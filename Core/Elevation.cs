using System.Diagnostics;
using System.Security.Principal;

namespace Watcher.Core;

public static class Elevation
{
    private static bool? _cached;

    public static bool IsElevated
    {
        get
        {
            if (_cached.HasValue) return _cached.Value;
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                _cached = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { _cached = false; }
            return _cached.Value;
        }
    }

    /// <summary>
    /// Relance l'executable courant avec une demande d'elevation.
    /// Retourne faux si l'utilisateur refuse l'invite UAC.
    /// </summary>
    public static bool RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Relance en administrateur annulee ou impossible : {ex.Message}");
            return false;
        }
    }
}
