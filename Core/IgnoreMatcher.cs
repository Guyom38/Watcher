using System.Text.RegularExpressions;

namespace Watcher.Core;

/// <summary>
/// Filtre d'exclusion compile une fois par changement de configuration.
/// Deux formes de motifs :
///  - sans joker : prefixe de chemin (le fichier lui-meme ou tout le contenu du dossier) ;
///  - avec * ou ? : joker applique au chemin complet, insensible a la casse.
/// </summary>
public sealed class IgnoreMatcher
{
    private readonly string[] _prefixes;
    private readonly Regex[] _globs;

    public static readonly IgnoreMatcher Empty = new(Array.Empty<string>());

    public IgnoreMatcher(IEnumerable<string> patterns)
    {
        var prefixes = new List<string>();
        var globs = new List<Regex>();

        foreach (var raw in patterns)
        {
            var p = raw?.Trim();
            if (string.IsNullOrEmpty(p)) continue;

            if (p.Contains('*') || p.Contains('?'))
            {
                try { globs.Add(new Regex(GlobToRegex(p), RegexOptions.IgnoreCase | RegexOptions.Compiled)); }
                catch (Exception ex) { AppLogger.Warn($"Motif d'exclusion invalide ignore « {p} » : {ex.Message}"); }
            }
            else
            {
                prefixes.Add(p.TrimEnd('\\'));
            }
        }

        _prefixes = prefixes.ToArray();
        _globs = globs.ToArray();
    }

    public bool IsIgnored(string path)
    {
        foreach (var prefix in _prefixes)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            // Correspondance exacte, ou frontiere de dossier : « C:\Temp » ne doit pas couvrir « C:\Temporaire ».
            if (path.Length == prefix.Length || path[prefix.Length] == '\\') return true;
        }

        foreach (var glob in _globs)
            if (glob.IsMatch(path)) return true;

        return false;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var c in glob)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString())
            });
        }
        return sb.Append('$').ToString();
    }
}
