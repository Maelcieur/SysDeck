using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SysDeck.Controls;

/// <summary>
/// Courbes de température du processeur et de la carte graphique sur un même graphe (échelle 20-100 °C,
/// repères à 40, 60 et 80 °C). Chaque courbe a sa couleur ; une valeur absente coupe la courbe.
/// </summary>
public sealed class TemperatureChart : FrameworkElement
{
    public const int Capacity = 120;
    private const double MinT = 20, MaxT = 100;

    private readonly List<(double? Cpu, double? Gpu)> _points = new();

    public static readonly DependencyProperty CpuColorProperty = DependencyProperty.Register(nameof(CpuColor), typeof(Color),
        typeof(TemperatureChart), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE0, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GpuColorProperty = DependencyProperty.Register(nameof(GpuColor), typeof(Color),
        typeof(TemperatureChart), new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x5A, 0x36), FrameworkPropertyMetadataOptions.AffectsRender));

    public Color CpuColor { get => (Color)GetValue(CpuColorProperty); set => SetValue(CpuColorProperty, value); }
    public Color GpuColor { get => (Color)GetValue(GpuColorProperty); set => SetValue(GpuColorProperty, value); }

    public int Count => _points.Count;

    public void Push(double? cpu, double? gpu)
    {
        _points.Add((cpu, gpu));
        if (_points.Count > Capacity) _points.RemoveRange(0, _points.Count - Capacity);
        InvalidateVisual();
    }

    public void Load(IEnumerable<(double? Cpu, double? Gpu)> history)
    {
        _points.Clear();
        foreach (var p in history) _points.Add(p);
        if (_points.Count > Capacity) _points.RemoveRange(0, _points.Count - Capacity);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        const double left = 22; // place des graduations
        var plot = w - left;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), 1);
        grid.Freeze();
        var labelBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        labelBrush.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var t in new[] { 40.0, 60, 80 })
        {
            var y = Y(t, h);
            dc.DrawLine(grid, new Point(left, y), new Point(w, y));
            var text = new FormattedText(t.ToString("0", CultureInfo.InvariantCulture) + "°", CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, labelBrush, dpi);
            dc.DrawText(text, new Point(0, y - text.Height / 2));
        }

        if (_points.Count < 2) return;
        var dx = plot / (Capacity - 1);
        var offset = Capacity - _points.Count;
        Series(dc, i => _points[i].Gpu, GpuColor, left, dx, offset, h);
        Series(dc, i => _points[i].Cpu, CpuColor, left, dx, offset, h);
    }

    private void Series(DrawingContext dc, Func<int, double?> value, Color color, double left, double dx, int offset, double h)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        using (var a = area.Open())
        {
            var run = new List<Point>();
            void Flush()
            {
                if (run.Count >= 2)
                {
                    g.BeginFigure(run[0], false, false);
                    g.PolyLineTo(run.Skip(1).ToList(), true, true);
                    a.BeginFigure(new Point(run[0].X, h), true, true);
                    a.PolyLineTo(run.Append(new Point(run[^1].X, h)).ToList(), false, true);
                }
                run.Clear();
            }
            for (var i = 0; i < _points.Count; i++)
            {
                if (value(i) is { } t) run.Add(new Point(left + (offset + i) * dx, Y(t, h)));
                else Flush();
            }
            Flush();
        }
        line.Freeze();
        area.Freeze();
        var fill = new LinearGradientBrush(DrawingHelpers.WithAlpha(color, 0x50), DrawingHelpers.WithAlpha(color, 0x00), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private static double Y(double t, double h) => 2 + (1 - (Math.Clamp(t, MinT, MaxT) - MinT) / (MaxT - MinT)) * (h - 4);
}
