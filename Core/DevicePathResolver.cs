using System.Runtime.InteropServices;
using System.Text;

namespace Watcher.Core;

/// <summary>
/// Les evenements ETW noyau exposent des chemins natifs
/// (« \Device\HarddiskVolume3\Users\... », « \SystemRoot\... ») et non des chemins Win32.
/// Cette classe construit la table de correspondance vers les lettres de lecteur.
/// </summary>
public static class DevicePathResolver
{
    private static readonly List<(string Device, string Drive)> Map = new();
    private static readonly string SystemRoot =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');

    static DevicePathResolver() => Refresh();

    public static void Refresh()
    {
        lock (Map)
        {
            Map.Clear();
            var sb = new StringBuilder(1024);

            foreach (var drive in DriveInfo.GetDrives())
            {
                var letter = drive.Name.TrimEnd('\\'); // « C: »
                sb.Clear();
                if (QueryDosDevice(letter, sb, sb.Capacity) == 0) continue;

                var device = sb.ToString().TrimEnd('\\');
                if (device.Length > 0)
                    Map.Add((device, letter));
            }

            // Les prefixes les plus longs d'abord : « \Device\HarddiskVolume10 » avant « \Device\HarddiskVolume1 ».
            Map.Sort((a, b) => b.Device.Length.CompareTo(a.Device.Length));
            AppLogger.Debug($"Table des peripheriques : {Map.Count} volume(s) resolu(s)");
        }
    }

    /// <summary>
    /// Convertit un chemin natif en chemin Win32. Retourne null si le chemin n'est
    /// pas rattachable a un volume monte (pipe nomme, socket, volume demonte...).
    /// </summary>
    public static string? ToWin32Path(string? nativePath)
    {
        if (string.IsNullOrEmpty(nativePath)) return null;

        // Deja un chemin Win32 : « C:\... »
        if (nativePath.Length >= 2 && nativePath[1] == ':') return nativePath;

        if (nativePath.StartsWith(@"\SystemRoot", StringComparison.OrdinalIgnoreCase))
            return SystemRoot + nativePath.Substring(@"\SystemRoot".Length);

        if (nativePath.StartsWith(@"\??\", StringComparison.Ordinal))
            nativePath = nativePath.Substring(4);
        if (nativePath.Length >= 2 && nativePath[1] == ':') return nativePath;

        lock (Map)
        {
            foreach (var (device, drive) in Map)
            {
                if (!nativePath.StartsWith(device, StringComparison.OrdinalIgnoreCase)) continue;
                if (nativePath.Length == device.Length) return drive + "\\";
                if (nativePath[device.Length] == '\\') return drive + nativePath.Substring(device.Length);
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}
