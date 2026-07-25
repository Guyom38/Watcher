using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watcher.Core;

public enum ScopeMode
{
    /// <summary>Tous les disques fixes, integralement.</summary>
    All,
    /// <summary>Aucune cible : la surveillance ne remonte rien.</summary>
    None,
    /// <summary>Uniquement les chemins coches dans l'arborescence.</summary>
    Specific
}

public sealed class AppSettings
{
    /// <summary>La surveillance doit-elle etre active au demarrage du service.</summary>
    public bool MonitoringEnabled { get; set; }

    public ScopeMode Scope { get; set; } = ScopeMode.All;

    /// <summary>Racines surveillees quand <see cref="Scope"/> vaut <see cref="ScopeMode.Specific"/>.</summary>
    public List<string> WatchedPaths { get; set; } = new();

    /// <summary>
    /// Motifs d'exclusion. Un motif sans joker est traite comme un prefixe de chemin
    /// (fichier ou dossier entier) ; sinon les jokers * et ? s'appliquent au chemin complet.
    /// </summary>
    public List<string> IgnorePatterns { get; set; } = new(DefaultIgnorePatterns);

    /// <summary>
    /// Noms de processus dont les acces sont ecartes a la source (« explorer.exe »...).
    /// Filtre le plus efficace : il s'applique avant toute analyse de chemin.
    /// N'a d'effet qu'avec le moteur ETW, seul a connaitre l'origine des acces.
    /// </summary>
    public List<string> IgnoredProcesses { get; set; } = new();

    /// <summary>
    /// Dossiers, fichiers et processus epingles, suivis dans l'onglet « Surveillance ciblee ».
    /// Ajouter une cible de chemin garantit aussi qu'elle entre dans la portee de capture.
    /// </summary>
    public List<WatchTarget> WatchTargets { get; set; } = new();

    // --- Capture ---
    public bool TrackReads { get; set; } = true;
    public bool TrackWrites { get; set; } = true;
    public bool TrackDeletes { get; set; } = true;
    public bool TrackRenames { get; set; } = true;

    /// <summary>Tente d'ouvrir une session ETW noyau pour attribuer chaque acces a un processus.</summary>
    public bool EnableProcessAttribution { get; set; } = true;

    /// <summary>Ignore les chemins sans extension (souvent des dossiers ou des pipes).</summary>
    public bool IgnoreDirectoryEvents { get; set; } = true;

    // --- Rendu / stockage ---
    public int MaxActivityEntries { get; set; } = 20_000;
    public int LiveFeedSize { get; set; } = 300;
    public bool LogToFile { get; set; } = true;

    // --- Application ---
    public bool StartMinimized { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public bool AnimatedBackground { get; set; } = true;

    /// <summary>
    /// Force le rendu WPF sur le processeur au lieu du GPU. A activer quand la fenetre
    /// s'affiche en noir : sous forte pression sur la memoire video (jeu en cours) ou
    /// apres une perte du peripherique Direct3D, WPF n'arrive plus a allouer sa surface
    /// de rendu materielle et ne dessine plus rien.
    /// </summary>
    public bool SoftwareRendering { get; set; }

    public static readonly string[] DefaultIgnorePatterns =
    {
        @"C:\Windows\WinSxS",
        @"C:\Windows\Prefetch",
        @"C:\Windows\SoftwareDistribution",
        @"C:\Windows\Temp",
        @"C:\Windows\System32\config",
        @"C:\$Recycle.Bin",
        @"*\pagefile.sys",
        @"*\swapfile.sys",
        @"*\hiberfil.sys",
        @"*\AppData\Local\Temp\*",
        @"*\AppData\Local\Microsoft\Windows\Explorer\*",
        @"*\AppData\Local\Packages\*\AC\*",
        @"*\System Volume Information\*",
        @"*.etl",
        @"*.tmp"
    };

    /// <summary>
    /// Ramene les chemins de la configuration a leur forme canonique. Repare notamment
    /// un fichier ecrit a la main avec des noms courts 8.3, qui ne correspondraient a
    /// aucun evenement. Les motifs a joker sont laisses intacts : ce ne sont pas des
    /// chemins reels et les normaliser detruirait les jokers.
    /// </summary>
    public void NormalizePaths()
    {
        WatchedPaths = WatchedPaths
            .Select(PathNormalizer.Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var t in WatchTargets)
            if (!t.IsProcess)
                t.Path = PathNormalizer.Normalize(t.Path);

        IgnorePatterns = IgnorePatterns
            .Select(p => p.Contains('*') || p.Contains('?') ? p.Trim() : PathNormalizer.Normalize(p))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions),
            SettingsStore.JsonOptions)!;
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly object Gate = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s is not null)
                {
                    s.NormalizePaths();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Lecture des parametres impossible, retour aux valeurs par defaut : {ex.Message}");
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureCreated();
            lock (Gate)
            {
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ecriture des parametres impossible : {ex.Message}");
        }
    }
}
