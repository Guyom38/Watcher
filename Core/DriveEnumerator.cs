namespace Watcher.Core;

public sealed record DriveDescriptor(
    string RootPath,
    string Label,
    string Kind,
    long TotalBytes,
    long FreeBytes)
{
    public string Letter => RootPath.TrimEnd('\\');

    public string Display => string.IsNullOrWhiteSpace(Label)
        ? $"{Letter} ({Kind})"
        : $"{Letter} — {Label}";

    public double UsedRatio => TotalBytes > 0 ? 1.0 - (double)FreeBytes / TotalBytes : 0;

    public string Capacity => TotalBytes > 0
        ? $"{Format(TotalBytes - FreeBytes)} / {Format(TotalBytes)}"
        : "—";

    public static string Format(long bytes)
    {
        string[] u = { "o", "Ko", "Mo", "Go", "To", "Po" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}

public static class DriveEnumerator
{
    public static IEnumerable<DriveDescriptor> FixedDrives() => All().Where(d => d.Kind == "Disque fixe");

    public static List<DriveDescriptor> All()
    {
        var list = new List<DriveDescriptor>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;

                var kind = d.DriveType switch
                {
                    DriveType.Fixed => "Disque fixe",
                    DriveType.Removable => "Amovible",
                    DriveType.Network => "Reseau",
                    DriveType.CDRom => "Optique",
                    DriveType.Ram => "Disque RAM",
                    _ => "Inconnu"
                };

                // Les volumes reseau et optiques generent trop de bruit pour un interet limite.
                if (d.DriveType is DriveType.CDRom or DriveType.Network) continue;

                list.Add(new DriveDescriptor(d.RootDirectory.FullName, d.VolumeLabel, kind,
                    d.TotalSize, d.AvailableFreeSpace));
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Volume ignore ({d.Name}) : {ex.Message}");
            }
        }

        return list.OrderBy(x => x.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
