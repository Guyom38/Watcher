using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Watcher.Core;

namespace Watcher;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Couleur de pastille selon le type d'acces, pour lire le tableau d'un coup d'oeil.</summary>
public sealed class ActionToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Read = Frozen(0x38, 0xBD, 0xF8);
    private static readonly SolidColorBrush Write = Frozen(0xFB, 0xBF, 0x24);
    private static readonly SolidColorBrush Create = Frozen(0x34, 0xD3, 0x99);
    private static readonly SolidColorBrush Delete = Frozen(0xF8, 0x71, 0x71);
    private static readonly SolidColorBrush Rename = Frozen(0xA7, 0x8B, 0xFA);
    private static readonly SolidColorBrush Other = Frozen(0x93, 0xA7, 0xC4);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Lecture" => Read,
            "Ecriture" => Write,
            "Creation" => Create,
            "Suppression" => Delete,
            "Renommage" => Rename,
            _ => Other
        };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var brush = value is LogLevel l
            ? l switch
            {
                LogLevel.Error => Color.FromRgb(0xF8, 0x71, 0x71),
                LogLevel.Warn => Color.FromRgb(0xFB, 0xBF, 0x24),
                LogLevel.Info => Color.FromRgb(0x38, 0xBD, 0xF8),
                _ => Color.FromRgb(0x63, 0x74, 0x8F)
            }
            : Color.FromRgb(0x93, 0xA7, 0xC4);

        var b = new SolidColorBrush(brush);
        b.Freeze();
        return b;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Formate les grands nombres pour les vignettes de statistiques.</summary>
public sealed class CompactNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var n = value switch
        {
            long l => l,
            int i => i,
            _ => 0L
        };

        if (n < 1_000) return n.ToString(CultureInfo.CurrentCulture);
        if (n < 1_000_000) return (n / 1_000d).ToString("0.#", culture) + " k";
        if (n < 1_000_000_000) return (n / 1_000_000d).ToString("0.##", culture) + " M";
        return (n / 1_000_000_000d).ToString("0.##", culture) + " G";
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
