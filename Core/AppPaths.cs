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

    /// <summary>
    /// Remplace le dossier de donnees local par la variable d'environnement
    /// correspondante. Plus court a lire dans l'interface, et evite d'afficher le nom
    /// du compte Windows — utile des qu'une capture d'ecran est partagee.
    /// </summary>
    public static string Abbreviate(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0 && path.StartsWith(local, StringComparison.OrdinalIgnoreCase))
            return "%LOCALAPPDATA%" + path.Substring(local.Length);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%" + path.Substring(profile.Length);

        return path;
    }
}
