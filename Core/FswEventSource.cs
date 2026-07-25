namespace Watcher.Core;

/// <summary>
/// Moteur de repli, sans elevation. FileSystemWatcher ne rapporte pas les lectures
/// (les notifications LastAccess sont desactivees par defaut sur NTFS) et n'expose
/// jamais le processus responsable : seules les modifications sont visibles.
/// </summary>
public sealed class FswEventSource : IEventSource
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private Action<FileEvent>? _onEvent;
    private volatile bool _disposed;

    public MonitorEngine Engine => MonitorEngine.FileSystemWatcher;

    public void Start(IReadOnlyList<string> roots, Action<FileEvent> onEvent)
    {
        _onEvent = onEvent;
        var started = 0;

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    AppLogger.Warn($"Racine surveillee introuvable, ignoree : {root}");
                    continue;
                }

                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024, // maximum accepte par l'API
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size
                                   | NotifyFilters.Attributes
                };

                w.Changed += (_, e) => Emit(e.FullPath, FileAction.Write);
                w.Created += (_, e) => Emit(e.FullPath, FileAction.Create);
                w.Deleted += (_, e) => Emit(e.FullPath, FileAction.Delete);
                w.Renamed += (_, e) => Emit(e.FullPath, FileAction.Rename);
                w.Error += (_, e) => OnWatcherError(w, e);

                w.EnableRaisingEvents = true;
                _watchers.Add(w);
                started++;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Surveillance de « {root} » impossible : {ex.Message}");
            }
        }

        if (started == 0)
            throw new InvalidOperationException("Aucune racine n'a pu etre surveillee.");

        AppLogger.Info($"Moteur FileSystemWatcher demarre sur {started} racine(s)");
    }

    private void Emit(string path, FileAction action)
    {
        if (_disposed) return;
        // Ce moteur n'a pas acces a l'origine de l'acces : PID 0, processus inconnu.
        _onEvent?.Invoke(new FileEvent(path, action, DateTime.Now, 0, null));
    }

    /// <summary>
    /// Le tampon interne a debordé : des evenements sont perdus. On relance la surveillance
    /// pour ne pas rester sur un watcher mort.
    /// </summary>
    private void OnWatcherError(FileSystemWatcher w, ErrorEventArgs e)
    {
        AppLogger.Warn($"Debordement du tampon sur « {w.Path} » ({e.GetException().Message}) — relance");
        if (_disposed) return;

        try
        {
            w.EnableRaisingEvents = false;
            w.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Relance de la surveillance de « {w.Path} » impossible : {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
        AppLogger.Info("Moteur FileSystemWatcher arrete");
    }
}
