using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Watcher.Core;

/// <summary>
/// Etat consolide affiche par l'IHM. Toutes les mutations se font sur le thread de l'IHM,
/// par lots, depuis <see cref="Ingest"/> : les collections observables ne supportent pas
/// la cadence brute de la capture.
/// </summary>
public sealed class ActivityStore : INotifyPropertyChanged
{
    private readonly Dictionary<string, FileActivityEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _processTotals = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<FileActivityEntry> Files { get; } = new();
    public ObservableCollection<LiveEvent> Live { get; } = new();

    public int MaxEntries { get; set; } = 20_000;
    public int LiveCapacity { get; set; } = 300;

    private long _totalEvents;
    public long TotalEvents { get => _totalEvents; private set => Set(ref _totalEvents, value); }

    public int UniqueFiles => _index.Count;
    public int UniqueProcesses => _processTotals.Count;

    private DateTime? _sessionStart;
    public DateTime? SessionStart { get => _sessionStart; set => Set(ref _sessionStart, value); }

    /// <summary>Le flux en direct est-il gele (l'utilisateur inspecte une ligne).</summary>
    public bool Paused { get; set; }

    private WatchTargetSet _targets = WatchTargetSet.Empty;

    /// <summary>Cibles epinglees. Les remplacer remarque toutes les lignes deja presentes.</summary>
    public WatchTargetSet Targets
    {
        get => _targets;
        set
        {
            _targets = value;
            foreach (var f in Files) f.IsTargeted = IsTargeted(f);
            Changed?.Invoke();
        }
    }

    /// <summary>Une ligne est ciblee par son chemin, ou par l'un de ses processus accedants.</summary>
    private bool IsTargeted(FileActivityEntry entry)
        => _targets.CoversPath(entry.Path) || _targets.CoversAnyProcess(entry.ProcessNames);

    public event Action? Changed;

    /// <summary>Applique un lot d'evenements. A appeler exclusivement depuis le thread de l'IHM.</summary>
    public void Ingest(IReadOnlyList<FileEvent> batch)
    {
        if (batch.Count == 0) return;

        foreach (var e in batch)
        {
            if (!_index.TryGetValue(e.Path, out var entry))
            {
                entry = new FileActivityEntry(e.Path, e.Time)
                {
                    IsTargeted = _targets.CoversPath(e.Path)
                };
                _index[e.Path] = entry;
                Files.Add(entry);
            }

            entry.Apply(e);

            // Une cible processus ne peut etre reconnue qu'apres coup : c'est l'evenement
            // qui apporte le nom du processus.
            if (!entry.IsTargeted && _targets.CoversProcess(e.ProcessName))
                entry.IsTargeted = true;

            if (!string.IsNullOrEmpty(e.ProcessName))
                _processTotals[e.ProcessName] =
                    _processTotals.TryGetValue(e.ProcessName, out var pc) ? pc + 1 : 1;

            if (!Paused)
            {
                Live.Insert(0, new LiveEvent(
                    e.Time, e.ActionLabel, entry.FileName, entry.Directory,
                    e.ProcessName ?? "—", e.ProcessId));

                while (Live.Count > LiveCapacity)
                    Live.RemoveAt(Live.Count - 1);
            }
        }

        TotalEvents += batch.Count;
        TrimIfNeeded();

        OnPropertyChanged(nameof(UniqueFiles));
        OnPropertyChanged(nameof(UniqueProcesses));
        Changed?.Invoke();
    }

    /// <summary>Retire les entrees les plus anciennes quand le plafond configure est franchi.</summary>
    private void TrimIfNeeded()
    {
        var excess = Files.Count - MaxEntries;
        if (excess <= 0) return;

        // Marge de 10 % pour ne pas rogner a chaque lot.
        excess += Math.Max(1, MaxEntries / 10);
        excess = Math.Min(excess, Files.Count);

        var victims = Files.OrderBy(f => f.LastSeen).Take(excess).ToList();
        foreach (var v in victims)
        {
            _index.Remove(v.Path);
            Files.Remove(v);
        }

        AppLogger.Debug($"Plafond d'entrees atteint : {victims.Count} ligne(s) la plus ancienne retiree(s)");
    }

    public void Clear()
    {
        _index.Clear();
        _processTotals.Clear();
        Files.Clear();
        Live.Clear();
        TotalEvents = 0;
        SessionStart = DateTime.Now;
        OnPropertyChanged(nameof(UniqueFiles));
        OnPropertyChanged(nameof(UniqueProcesses));
        Changed?.Invoke();
    }

    /// <summary>Retire du tableau toutes les lignes couvertes par le filtre d'exclusion fourni.</summary>
    public int PurgeIgnored(IgnoreMatcher matcher)
    {
        var victims = Files.Where(f => matcher.IsIgnored(f.Path)).ToList();
        foreach (var v in victims)
        {
            _index.Remove(v.Path);
            Files.Remove(v);
        }
        if (victims.Count > 0)
        {
            OnPropertyChanged(nameof(UniqueFiles));
            Changed?.Invoke();
        }
        return victims.Count;
    }

    public IEnumerable<(string Process, int Count)> TopProcesses(int n)
        => _processTotals.OrderByDescending(p => p.Value).Take(n).Select(p => (p.Key, p.Value));

    /// <summary>
    /// Compteurs de chaque cible, en un seul parcours du tableau. Appele quand l'onglet
    /// « Surveillance ciblee » est visible, pas a chaque lot d'evenements.
    /// </summary>
    public List<WatchTargetStats> ComputeTargetStats(IReadOnlyList<WatchTarget> targets)
    {
        var accesses = new int[targets.Count];
        var files = new int[targets.Count];
        var last = new DateTime?[targets.Count];
        var processes = new Dictionary<string, int>[targets.Count];

        foreach (var f in Files)
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];

                // Une cible processus ne compte que les acces de ce processus,
                // pas tous ceux du fichier.
                int hits;
                if (target.IsProcess)
                {
                    hits = f.AccessesBy(target.Path);
                    if (hits == 0) continue;
                }
                else
                {
                    if (!target.CoversPath(f.Path)) continue;
                    hits = f.Count;
                }

                accesses[i] += hits;
                files[i]++;
                if (last[i] is null || f.LastSeen > last[i]) last[i] = f.LastSeen;

                foreach (var (proc, count) in f.ProcessBreakdown())
                {
                    if (target.IsProcess && !string.Equals(proc, target.Path,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    var map = processes[i] ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    map[proc] = map.TryGetValue(proc, out var c) ? c + count : count;
                }
            }
        }

        var result = new List<WatchTargetStats>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            var top = processes[i] is { Count: > 0 } m
                ? m.OrderByDescending(p => p.Value).First().Key
                : "—";
            result.Add(new WatchTargetStats(accesses[i], files[i], last[i], top));
        }
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
