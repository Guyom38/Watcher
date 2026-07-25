using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Watcher.Core;

/// <summary>Une ligne agregee : un fichier, son nombre d'acces et ses accedants.</summary>
public sealed class FileActivityEntry : INotifyPropertyChanged
{
    private readonly Dictionary<string, int> _processes = new(StringComparer.OrdinalIgnoreCase);

    public FileActivityEntry(string path, DateTime first)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(FileName)) FileName = path;
        Directory = System.IO.Path.GetDirectoryName(path) ?? path;
        Drive = path.Length >= 2 && path[1] == ':' ? path.Substring(0, 2).ToUpperInvariant() : "?";
        Extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        FirstSeen = first;
        _lastSeen = first;
    }

    public string Path { get; }
    public string FileName { get; }
    public string Directory { get; }
    public string Drive { get; }
    public string Extension { get; }
    public DateTime FirstSeen { get; }

    private DateTime _lastSeen;
    public DateTime LastSeen { get => _lastSeen; private set => Set(ref _lastSeen, value); }

    private int _count;
    public int Count { get => _count; private set => Set(ref _count, value); }

    private int _reads, _writes, _deletes;
    public int Reads { get => _reads; private set => Set(ref _reads, value); }
    public int Writes { get => _writes; private set => Set(ref _writes, value); }
    public int Deletes { get => _deletes; private set => Set(ref _deletes, value); }

    private string _lastAction = "";
    public string LastAction { get => _lastAction; private set => Set(ref _lastAction, value); }

    private string _processSummary = "—";
    /// <summary>Processus accedants, du plus actif au moins actif. « — » sans attribution.</summary>
    public string ProcessSummary { get => _processSummary; private set => Set(ref _processSummary, value); }

    private string _lastProcess = "—";
    public string LastProcess { get => _lastProcess; private set => Set(ref _lastProcess, value); }

    private int _lastPid;
    public int LastPid { get => _lastPid; private set => Set(ref _lastPid, value); }

    private bool _isTargeted;
    /// <summary>Ce fichier releve-t-il d'une cible epinglee (affiche une etoile).</summary>
    public bool IsTargeted { get => _isTargeted; set => Set(ref _isTargeted, value); }

    public string TargetMark => _isTargeted ? "★" : "";

    public string LastSeenDate => LastSeen.ToString("dd/MM/yyyy");
    public string LastSeenTime => LastSeen.ToString("HH:mm:ss");

    public void Apply(in FileEvent e)
    {
        Count++;
        LastSeen = e.Time;
        LastAction = e.ActionLabel;

        switch (e.Action)
        {
            case FileAction.Read: Reads++; break;
            case FileAction.Write or FileAction.Create: Writes++; break;
            case FileAction.Delete: Deletes++; break;
        }

        if (!string.IsNullOrEmpty(e.ProcessName))
        {
            var key = e.ProcessName;
            _processes[key] = _processes.TryGetValue(key, out var c) ? c + 1 : 1;

            LastProcess = key;
            LastPid = e.ProcessId;

            // Le resume n'est recalcule qu'a l'apparition d'un nouvel accedant :
            // le classement bouge peu et ce chemin est tres sollicite.
            if (c == 0) RebuildSummary();
        }

        OnPropertyChanged(nameof(LastSeenDate));
        OnPropertyChanged(nameof(LastSeenTime));
    }

    private void RebuildSummary()
    {
        var top = _processes.OrderByDescending(p => p.Value).Take(3).Select(p => p.Key);
        var s = string.Join(", ", top);
        if (_processes.Count > 3) s += $" (+{_processes.Count - 3})";
        ProcessSummary = s.Length == 0 ? "—" : s;
    }

    /// <summary>Detail complet des accedants, pour le volet d'inspection.</summary>
    public IEnumerable<(string Process, int Count)> ProcessBreakdown()
        => _processes.OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value));

    /// <summary>Noms des processus ayant touche ce fichier.</summary>
    public IEnumerable<string> ProcessNames => _processes.Keys;

    public bool UsedBy(string processName) => _processes.ContainsKey(processName);

    public int AccessesBy(string processName)
        => _processes.TryGetValue(processName, out var c) ? c : 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(IsTargeted)) OnPropertyChanged(nameof(TargetMark));
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Une ligne du flux en direct : un evenement, non agrege.</summary>
public sealed record LiveEvent(DateTime Time, string Action, string FileName, string Directory, string Process, int Pid)
{
    public string Date => Time.ToString("dd/MM/yyyy");
    public string Clock => Time.ToString("HH:mm:ss");
    public string ProcessDisplay => Pid > 0 ? $"{Process} ({Pid})" : Process;
}
