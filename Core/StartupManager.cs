using Microsoft.Win32;

namespace Watcher.Core;

/// <summary>Inscription au demarrage de session via HKCU\...\Run (aucun droit admin requis).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Watcher";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (k is null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                k.SetValue(ValueName, $"\"{exe}\" --tray");
                AppLogger.Info("Lancement au demarrage de Windows active");
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLogger.Info("Lancement au demarrage de Windows desactive");
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Modification du demarrage automatique impossible : {ex.Message}");
            return false;
        }
    }
}
