using System.Collections.Concurrent;

namespace Watcher.Core;

public sealed class MonitorStatus
{
    public bool Running { get; init; }
    public MonitorEngine Engine { get; init; }
    public string Message { get; init; } = "";
    public bool ProcessAttribution => Engine == MonitorEngine.Etw;
}

/// <summary>
/// Chef d'orchestre de la capture : choisit le moteur, applique la portee et les
/// exclusions, puis pousse les evenements retenus dans une file bornee que l'IHM vide.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private const int QueueCapacity = 200_000;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<FileEvent> _queue = new();

    private IEventSource? _source;
    private IgnoreMatcher _ignore = IgnoreMatcher.Empty;
    private HashSet<string> _ignoredProcesses = new(StringComparer.OrdinalIgnoreCase);
    private string[] _roots = Array.Empty<string>();
    private bool _watchEverything;
    private bool _ignoreDirectories = true;

    private long _accepted, _dropped, _filtered;

    public MonitorStatus Status { get; private set; } = new() { Message = "Arretee" };
    public event Action<MonitorStatus>? StatusChanged;

    public long AcceptedCount => Interlocked.Read(ref _accepted);
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long FilteredCount => Interlocked.Read(ref _filtered);
    public int PendingCount => _queue.Count;

    /// <summary>Vide la file de capture. Appele par l'IHM a intervalle regulier.</summary>
    public int Drain(int max, Action<FileEvent> consume)
    {
        var n = 0;
        while (n < max && _queue.TryDequeue(out var e))
        {
            consume(e);
            n++;
        }
        return n;
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _accepted, 0);
        Interlocked.Exchange(ref _dropped, 0);
        Interlocked.Exchange(ref _filtered, 0);
    }

    public void Start(AppSettings settings)
    {
        lock (_gate)
        {
            StopCore();

            _ignore = new IgnoreMatcher(settings.IgnorePatterns);
            _ignoredProcesses = settings.IgnoredProcesses
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _ignoreDirectories = settings.IgnoreDirectoryEvents;
            (_roots, _watchEverything) = ResolveRoots(settings);

            if (_roots.Length == 0)
            {
                Publish(false, MonitorEngine.None,
                    settings.Scope == ScopeMode.None
                        ? "Portee vide : aucun disque selectionne"
                        : "Aucune cible valide dans la selection");
                return;
            }

            // ETW d'abord : c'est le seul moteur qui voit les lectures et les processus.
            if (settings.EnableProcessAttribution && Elevation.IsElevated)
            {
                try
                {
                    var etw = new EtwEventSource(settings);
                    etw.Start(_roots, OnEvent);
                    _source = etw;
                    Publish(true, MonitorEngine.Etw,
                        $"Surveillance active — moteur ETW noyau, {_roots.Length} cible(s)");
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Moteur ETW indisponible, repli sur FileSystemWatcher : {ex.Message}");
                }
            }

            try
            {
                var fsw = new FswEventSource();
                fsw.Start(_roots, OnEvent);
                _source = fsw;

                var why = !Elevation.IsElevated
                    ? "sans elevation : lectures et processus indisponibles"
                    : !settings.EnableProcessAttribution
                        ? "attribution des processus desactivee"
                        : "moteur ETW indisponible";
                Publish(true, MonitorEngine.FileSystemWatcher,
                    $"Surveillance active — FileSystemWatcher ({why}), {_roots.Length} cible(s)");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Demarrage de la surveillance impossible : {ex.Message}");
                Publish(false, MonitorEngine.None, $"Echec du demarrage : {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
            Publish(false, MonitorEngine.None, "Surveillance arretee");
        }
    }

    private void StopCore()
    {
        if (_source is null) return;
        try { _source.Dispose(); } catch (Exception ex) { AppLogger.Warn($"Arret du moteur : {ex.Message}"); }
        _source = null;
        while (_queue.TryDequeue(out _)) { }
    }

    /// <summary>Point chaud : appele des dizaines de milliers de fois par seconde avec ETW.</summary>
    private void OnEvent(FileEvent e)
    {
        var path = e.Path;

        if (AppPaths.IsOwnPath(path)) return;

        // Filtre par processus d'abord : une simple recherche dans un ensemble, bien moins
        // couteuse que les comparaisons de chemin qui suivent.
        if (_ignoredProcesses.Count > 0 && e.ProcessName is { Length: > 0 } proc
                                        && _ignoredProcesses.Contains(proc))
        {
            Interlocked.Increment(ref _filtered);
            return;
        }

        if (!InScope(path)) { Interlocked.Increment(ref _filtered); return; }
        if (_ignoreDirectories && !HasExtension(path)) { Interlocked.Increment(ref _filtered); return; }
        if (_ignore.IsIgnored(path)) { Interlocked.Increment(ref _filtered); return; }

        // File bornee : sous rafale, on prefere perdre des evenements et compter les pertes
        // plutot que de laisser la memoire grimper sans limite.
        if (_queue.Count >= QueueCapacity)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(e);
        Interlocked.Increment(ref _accepted);
    }

    private bool InScope(string path)
    {
        if (_watchEverything) return true;

        foreach (var root in _roots)
        {
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            // Une racine se termine par « \ » seulement pour un volume (« C:\ ») : deja une frontiere.
            if (root.EndsWith('\\')) return true;
            if (path.Length == root.Length || path[root.Length] == '\\') return true;
        }
        return false;
    }

    private static bool HasExtension(string path)
    {
        for (var i = path.Length - 1; i >= 0; i--)
        {
            var c = path[i];
            if (c == '\\') return false;
            if (c == '.') return i < path.Length - 1;
        }
        return false;
    }

    /// <summary>Traduit la portee configuree en liste de racines exploitables par les moteurs.</summary>
    private static (string[] Roots, bool Everything) ResolveRoots(AppSettings settings)
    {
        switch (settings.Scope)
        {
            case ScopeMode.None:
                return (Array.Empty<string>(), false);

            case ScopeMode.All:
            {
                var drives = DriveEnumerator.FixedDrives().Select(d => d.RootPath).ToArray();
                return (drives, true);
            }

            default:
            {
                var picked = settings.WatchedPaths
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Length == 3 && p[1] == ':' ? p : p.TrimEnd('\\'))
                    .Where(p => Directory.Exists(p) || File.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // Une racine de volume dans la selection rend inutiles ses sous-chemins.
                var minimal = picked
                    .Where(p => !picked.Any(other =>
                        !ReferenceEquals(other, p) &&
                        other.Length < p.Length &&
                        p.StartsWith(other.EndsWith('\\') ? other : other + "\\",
                            StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

                return (minimal, false);
            }
        }
    }

    private void Publish(bool running, MonitorEngine engine, string message)
    {
        Status = new MonitorStatus { Running = running, Engine = engine, Message = message };
        AppLogger.Info(message);
        StatusChanged?.Invoke(Status);
    }

    public void Dispose()
    {
        lock (_gate) StopCore();
    }
}
