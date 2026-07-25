namespace Watcher.Core;

public enum TargetKind
{
    Folder,
    File,
    /// <summary>Cible designee par nom de processus, tous chemins confondus.</summary>
    Process
}

/// <summary>
/// Une cible suivie de pres : dossier, fichier ou processus epingle par l'utilisateur.
/// Contrairement a la portee de capture, qui dit ce que les moteurs observent, une cible
/// sert a isoler et suivre un element precis dans l'onglet « Surveillance ciblee ».
/// </summary>
public sealed class WatchTarget
{
    public string Path { get; set; } = "";
    public TargetKind Kind { get; set; } = TargetKind.Folder;
    public DateTime AddedAt { get; set; } = DateTime.Now;

    public bool IsProcess => Kind == TargetKind.Process;

    public string DisplayName
    {
        get
        {
            if (Kind == TargetKind.Process) return Path;

            var trimmed = Path.TrimEnd('\\');
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    public string KindLabel => Kind switch
    {
        TargetKind.Folder => "Dossier",
        TargetKind.File => "Fichier",
        _ => "Processus"
    };

    /// <summary>Ce chemin releve-t-il de cette cible ? Toujours faux pour une cible processus.</summary>
    public bool CoversPath(string path)
    {
        if (path.Length == 0 || Kind == TargetKind.Process) return false;

        if (Kind == TargetKind.File)
            return string.Equals(path, Path, StringComparison.OrdinalIgnoreCase);

        var root = Path.TrimEnd('\\');
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        // Frontiere de dossier : « D:\Jeux » ne doit pas couvrir « D:\JeuxVideo ».
        return path.Length == root.Length || path[root.Length] == '\\';
    }

    /// <summary>Ce processus releve-t-il de cette cible ?</summary>
    public bool CoversProcess(string? processName)
        => Kind == TargetKind.Process
           && !string.IsNullOrEmpty(processName)
           && string.Equals(processName, Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Ensemble de cibles fige, pour tester chemins et processus sans reallouer.</summary>
public sealed class WatchTargetSet
{
    public static readonly WatchTargetSet Empty = new(Array.Empty<WatchTarget>());

    private readonly WatchTarget[] _paths;
    private readonly HashSet<string> _processes;

    public WatchTargetSet(IEnumerable<WatchTarget> targets)
    {
        var list = targets.Where(t => !string.IsNullOrWhiteSpace(t.Path)).ToArray();
        _paths = list.Where(t => !t.IsProcess).ToArray();
        _processes = list.Where(t => t.IsProcess)
            .Select(t => t.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEmpty => _paths.Length == 0 && _processes.Count == 0;

    public bool CoversPath(string path)
    {
        foreach (var t in _paths)
            if (t.CoversPath(path)) return true;
        return false;
    }

    public bool CoversProcess(string? processName)
        => processName is { Length: > 0 } && _processes.Contains(processName);

    public bool CoversAnyProcess(IEnumerable<string> processNames)
    {
        if (_processes.Count == 0) return false;
        foreach (var p in processNames)
            if (_processes.Contains(p)) return true;
        return false;
    }
}

/// <summary>Compteurs d'une cible, recalcules a partir du tableau d'activite.</summary>
public sealed record WatchTargetStats(
    int Accesses,
    int Files,
    DateTime? LastSeen,
    string TopProcess)
{
    public static readonly WatchTargetStats None = new(0, 0, null, "—");

    public string AccessLabel => $"{Accesses:N0} acces";
    public string FileLabel => $"{Files:N0} fichier(s)";
    public string LastSeenLabel => LastSeen is null ? "aucun acces" : $"dernier {LastSeen:dd/MM HH:mm:ss}";
}
