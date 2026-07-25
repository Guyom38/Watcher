using System.Collections.Concurrent;
using System.Text;

namespace Watcher.Core;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogLine(DateTime Time, LogLevel Level, string Message)
{
    public string Stamp => Time.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public override string ToString() => $"{Stamp} [{Level.ToString().ToUpperInvariant(),-5}] {Message}";
}

/// <summary>
/// Journal de l'application : tampon en memoire pour l'onglet Journal, plus un
/// fichier quotidien ecrit par un thread dedie afin de ne jamais bloquer la capture.
/// </summary>
public static class AppLogger
{
    private const int MemoryCapacity = 5_000;

    private static readonly ConcurrentQueue<LogLine> Memory = new();
    private static readonly BlockingCollection<LogLine> ToDisk = new(new ConcurrentQueue<LogLine>(), 50_000);
    private static Thread? _writer;
    private static volatile bool _fileEnabled = true;

    public static event Action<LogLine>? Logged;

    public static bool FileEnabled
    {
        get => _fileEnabled;
        set => _fileEnabled = value;
    }

    public static string CurrentLogFile =>
        Path.Combine(AppPaths.LogDirectory, $"watcher-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Start()
    {
        if (_writer is not null) return;
        AppPaths.EnsureCreated();
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "Watcher.Log" };
        _writer.Start();
        Info($"Watcher demarre (PID {Environment.ProcessId}, elevation : {(Elevation.IsElevated ? "oui" : "non")})");
    }

    public static void Shutdown()
    {
        try
        {
            Info("Watcher arrete");
            ToDisk.CompleteAdding();
            _writer?.Join(2_000);
        }
        catch { /* arret au mieux */ }
    }

    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    private static void Write(LogLevel level, string message)
    {
        var line = new LogLine(DateTime.Now, level, message);

        Memory.Enqueue(line);
        while (Memory.Count > MemoryCapacity) Memory.TryDequeue(out _);

        if (_fileEnabled && !ToDisk.IsAddingCompleted)
        {
            // Si le disque ne suit pas, on abandonne la ligne plutot que de ralentir l'appelant.
            try { ToDisk.TryAdd(line); } catch { }
        }

        Logged?.Invoke(line);
    }

    public static IReadOnlyList<LogLine> Snapshot() => Memory.ToArray();

    private static void WriterLoop()
    {
        var buffer = new StringBuilder();
        try
        {
            foreach (var line in ToDisk.GetConsumingEnumerable())
            {
                buffer.Clear().AppendLine(line.ToString());

                // Vidange groupee : on absorbe ce qui est deja en file d'attente.
                var drained = 0;
                while (drained < 500 && ToDisk.TryTake(out var more))
                {
                    buffer.AppendLine(more.ToString());
                    drained++;
                }

                try { File.AppendAllText(CurrentLogFile, buffer.ToString(), Encoding.UTF8); }
                catch { /* fichier verrouille ou disque plein : on continue */ }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Supprime les journaux plus vieux que le nombre de jours indique.</summary>
    public static void Purge(int keepDays = 30)
    {
        try
        {
            var limit = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(AppPaths.LogDirectory, "watcher-*.log"))
                if (File.GetLastWriteTime(f) < limit)
                    File.Delete(f);
        }
        catch { }
    }
}
