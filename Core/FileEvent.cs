namespace Watcher.Core;

public enum FileAction
{
    Read,
    Write,
    Create,
    Delete,
    Rename
}

public enum MonitorEngine
{
    None,
    /// <summary>Session ETW noyau : lectures + ecritures + processus responsable. Requiert l'elevation.</summary>
    Etw,
    /// <summary>FileSystemWatcher : creations, ecritures, suppressions, renommages. Sans processus.</summary>
    FileSystemWatcher
}

/// <summary>Un acces fichier unitaire, tel que remonte par un moteur de capture.</summary>
public readonly record struct FileEvent(
    string Path,
    FileAction Action,
    DateTime Time,
    int ProcessId,
    string? ProcessName)
{
    public static readonly string[] ActionLabels = { "Lecture", "Ecriture", "Creation", "Suppression", "Renommage" };
    public string ActionLabel => ActionLabels[(int)Action];
}

public interface IEventSource : IDisposable
{
    MonitorEngine Engine { get; }
    /// <summary>Demarre la capture. Leve une exception en cas d'echec, avec un message affichable.</summary>
    void Start(IReadOnlyList<string> roots, Action<FileEvent> onEvent);
}
