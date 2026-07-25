using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Watcher.Controls;

/// <summary>
/// Fond decoratif : nappes sinusoidales superposees qui derivent lentement, plus deux
/// halos flous. Le rendu est recalcule a cadence fixe et s'interrompt des que l'element
/// n'est plus visible, pour ne rien couter quand la fenetre est dans la zone de notification.
/// </summary>
public sealed class WaveBackground : FrameworkElement
{
    private sealed record Layer(
        double Amplitude,
        double Wavelength,
        double Speed,
        double VerticalPosition,
        double Phase,
        double Drift,
        Color Tint,
        double Opacity);

    private static readonly Layer[] Layers =
    {
        new(0.075, 1.35, 0.030, 0.52, 0.0, 0.018, Color.FromRgb(0x1E, 0x3A, 0x8A), 0.55),
        new(0.060, 1.90, 0.045, 0.60, 1.7, 0.024, Color.FromRgb(0x1D, 0x4E, 0xD8), 0.50),
        new(0.052, 2.60, 0.062, 0.69, 3.1, 0.030, Color.FromRgb(0x0E, 0xA5, 0xE9), 0.42),
        new(0.040, 3.40, 0.085, 0.79, 4.6, 0.036, Color.FromRgb(0x22, 0xD3, 0xEE), 0.32),
        new(0.030, 4.60, 0.110, 0.89, 0.9, 0.042, Color.FromRgb(0x67, 0xE8, 0xF9), 0.22)
    };

    private static readonly Brush BaseFill = CreateBaseFill();
    private readonly Brush[] _layerFills;
    private readonly Brush _glowA, _glowB;

    private readonly DispatcherTimer _timer;
    private double _time;

    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(
        nameof(IsAnimated), typeof(bool), typeof(WaveBackground),
        new PropertyMetadata(true, OnIsAnimatedChanged));

    public bool IsAnimated
    {
        get => (bool)GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    public static readonly DependencyProperty ReducedQualityProperty = DependencyProperty.Register(
        nameof(ReducedQuality), typeof(bool), typeof(WaveBackground),
        new PropertyMetadata(false, OnReducedQualityChanged));

    /// <summary>
    /// Mode econome : moitie moins d'images par seconde, trois nappes au lieu de cinq et
    /// un pas de trace plus grossier. Destine au rendu logiciel, ou chaque pixel est
    /// rasterise par le processeur : mesure a environ 64 % d'un coeur en qualite pleine
    /// contre 14 % en rendu materiel.
    /// </summary>
    public bool ReducedQuality
    {
        get => (bool)GetValue(ReducedQualityProperty);
        set => SetValue(ReducedQualityProperty, value);
    }

    private static void OnReducedQualityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (WaveBackground)d;
        self._timer.Interval = TimeSpan.FromMilliseconds(self.ReducedQuality ? 80 : 40);
        self.InvalidateVisual();
    }

    public WaveBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;

        _layerFills = Layers.Select(CreateLayerFill).ToArray();
        _glowA = CreateGlow(Color.FromRgb(0x2D, 0x6C, 0xF0), 0.30);
        _glowB = CreateGlow(Color.FromRgb(0x0E, 0xA5, 0xE9), 0.22);

        // 25 images/s : le mouvement voulu est tres lent, inutile de viser 60.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        // Le temps avance selon l'intervalle reel : le mouvement garde la meme vitesse
        // apparente, quelle que soit la cadence de rafraichissement.
        _timer.Tick += (_, _) =>
        {
            _time += _timer.Interval.TotalSeconds;
            InvalidateVisual();
        };

        IsVisibleChanged += (_, _) => UpdateTimer();
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
    }

    private static void OnIsAnimatedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WaveBackground)d).UpdateTimer();

    private void UpdateTimer()
    {
        var shouldRun = IsVisible && IsAnimated;
        if (shouldRun == _timer.IsEnabled) return;

        if (shouldRun) _timer.Start();
        else _timer.Stop();

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(BaseFill, null, new Rect(0, 0, w, h));

        // Halos : deux ellipses molles qui derivent en Lissajous, sous les vagues.
        DrawGlow(dc, _glowA, w, h, 0.30 + 0.16 * Math.Sin(_time * 0.11),
            0.22 + 0.10 * Math.Cos(_time * 0.08), 0.62);
        DrawGlow(dc, _glowB, w, h, 0.72 + 0.14 * Math.Cos(_time * 0.09),
            0.34 + 0.12 * Math.Sin(_time * 0.13), 0.48);

        // En mode econome on ne garde que les trois nappes du fond : les deux derniere
        // sont les plus transparentes, leur absence se remarque a peine.
        var count = ReducedQuality ? 3 : Layers.Length;
        for (var i = 0; i < count; i++)
            dc.DrawGeometry(_layerFills[i], null, BuildWave(Layers[i], w, h));
    }

    private static void DrawGlow(DrawingContext dc, Brush brush, double w, double h,
        double cx, double cy, double radiusRatio)
    {
        var r = Math.Max(w, h) * radiusRatio;
        dc.DrawEllipse(brush, null, new Point(cx * w, cy * h), r, r * 0.75);
    }

    /// <summary>
    /// Nappe fermee : la crete est la somme de deux sinusoides desaccordees (ce qui evite
    /// la repetition visuelle d'une sinusoide pure), refermee sur le bas de la surface.
    /// </summary>
    private StreamGeometry BuildWave(Layer layer, double w, double h)
    {
        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };

        var amp = layer.Amplitude * h;
        var baseY = layer.VerticalPosition * h + Math.Sin(_time * layer.Drift + layer.Phase) * amp * 0.6;
        var k = 2 * Math.PI * layer.Wavelength / Math.Max(w, 1);
        var t = _time * layer.Speed * 2 * Math.PI;

        // Pas adaptatif : assez fin pour rester lisse, assez large pour rester econome.
        var step = ReducedQuality
            ? Math.Max(10.0, w / 110.0)
            : Math.Max(4.0, w / 260.0);

        using (var ctx = geo.Open())
        {
            var first = true;
            for (var x = 0.0; x <= w + step; x += step)
            {
                var y = baseY
                        + Math.Sin(x * k + t + layer.Phase) * amp
                        + Math.Sin(x * k * 0.47 - t * 1.31 + layer.Phase) * amp * 0.45;

                var p = new Point(x, y);
                if (first) { ctx.BeginFigure(p, isFilled: true, isClosed: true); first = false; }
                else ctx.LineTo(p, isStroked: false, isSmoothJoin: true);
            }

            ctx.LineTo(new Point(w + step, h), false, false);
            ctx.LineTo(new Point(0, h), false, false);
        }

        geo.Freeze();
        return geo;
    }

    private static Brush CreateBaseFill()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.35, 1)
        };
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x14, 0x28), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x07, 0x0E, 0x1E), 0.55));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x04, 0x07, 0x12), 1.0));
        b.Freeze();
        return b;
    }

    private static Brush CreateLayerFill(Layer layer)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        b.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)(255 * layer.Opacity), layer.Tint.R, layer.Tint.G, layer.Tint.B), 0.0));
        b.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)(255 * layer.Opacity * 0.35), layer.Tint.R, layer.Tint.G, layer.Tint.B), 0.45));
        b.GradientStops.Add(new GradientStop(
            Color.FromArgb(0, layer.Tint.R, layer.Tint.G, layer.Tint.B), 1.0));
        b.Freeze();
        return b;
    }

    private static Brush CreateGlow(Color color, double opacity)
    {
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * opacity * 0.35), color.R, color.G, color.B), 0.45));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
        b.Freeze();
        return b;
    }
}
