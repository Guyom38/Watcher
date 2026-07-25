using System.Runtime.InteropServices;
using System.Text;

namespace Watcher.Core;

/// <summary>
/// Met les chemins sous une forme unique et comparable.
///
/// Sans cela, une cible saisie sous une forme et un evenement rapporte sous une autre
/// ne se rencontrent jamais, et la cible reste muette sans aucun message d'erreur.
/// Le cas le plus courant est le nom court 8.3 : la variable %TEMP% vaut souvent
/// « C:\Users\UTILIS~1\AppData\Local\Temp », et un chemin colle depuis un raccourci ou
/// une invite de commande peut contenir « PROGRA~1 » au lieu de « Program Files ».
/// </summary>
public static class PathNormalizer
{
    /// <summary>
    /// Retourne le chemin en forme longue et absolue, sans antislash final
    /// (sauf pour une racine de volume, ou il est conserve : « C:\ »).
    /// Retourne une chaine vide si l'entree est vide.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var p = path.Trim().Trim('"');

        try
        {
            // Resout les segments relatifs et unifie les separateurs.
            p = Path.GetFullPath(p);
        }
        catch
        {
            // Chemin syntaxiquement invalide : on le garde tel quel plutot que d'echouer.
        }

        p = ToLongPath(p);

        // Une racine de volume garde son antislash : « C:\ » et non « C: ».
        if (p.Length == 3 && p[1] == ':' && p[2] == '\\') return p;
        return p.TrimEnd('\\');
    }

    /// <summary>
    /// Developpe les composants 8.3 en noms complets. Sans effet si le chemin n'existe
    /// pas encore sur le disque : l'API a besoin des entrees reelles pour resoudre.
    /// </summary>
    private static string ToLongPath(string path)
    {
        try
        {
            var needed = GetLongPathName(path, null, 0);
            if (needed == 0) return path;

            var sb = new StringBuilder((int)needed + 1);
            var written = GetLongPathName(path, sb, (uint)sb.Capacity);
            if (written == 0 || written > sb.Capacity) return path;

            var result = sb.ToString();
            return result.Length > 0 ? result : path;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Les deux chemins designent-ils le meme emplacement ?</summary>
    public static bool AreSame(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string lpszShortPath, StringBuilder? lpszLongPath, uint cchBuffer);
}
