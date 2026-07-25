namespace Watcher.Core;

/// <summary>Emplacements de stockage de l'application (config, journaux, exports).</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watcher");

    public static string LogDirectory { get; } = Path.Combine(Root, "logs");
    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");
    public static string ExportDirectory { get; } = Path.Combine(Root, "exports");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ExportDirectory);
    }

    /// <summary>
    /// Vrai si le chemin appartient a Watcher lui-meme. Ces chemins sont toujours exclus
    /// de la surveillance : sans cela, l'ecriture du journal declencherait un evenement
    /// qui serait journalise a son tour, en boucle.
    /// </summary>
    public static bool IsOwnPath(string path)
        => path.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
}
