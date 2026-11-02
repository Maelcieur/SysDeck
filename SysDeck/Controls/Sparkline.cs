using System.Windows;
using System.Windows.Media;

namespace SysDeck.Controls;

/// <summary>
/// Mini-graphe d'historique à deux séries (ex : réception / envoi). La série principale est tracée
/// avec le dégradé des jauges et une zone remplie ; la secondaire en simple ligne de la couleur de fin.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private const int Capacity = 40;

    private readonly Queue<double> _primary = new();
    private readonly Queue<double> _secondary = new();

    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(
        nameof(StartColor), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE0, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(
        nameof(EndColor), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8B, 0x5C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Échelle verticale minimale, pour qu'un trafic quasi nul ne remplisse pas tout le graphe.</summary>
    public static readonly DependencyProperty MinimumScaleProperty = DependencyProperty.Register(
        nameof(MinimumScale), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color StartColor { get => (Color)GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }
    public Color EndColor { get => (Color)GetValue(EndColorProperty); set => SetValue(EndColorProperty, value); }
    public double MinimumScale { get => (double)GetValue(MinimumScaleProperty); set => SetValue(MinimumScaleProperty, value); }

    public void Push(double primary, double secondary)
    {
        Enqueue(_primary, primary);
        Enqueue(_secondary, secondary);
        InvalidateVisual();
    }

    private static void Enqueue(Queue<double> queue, double value)
    {
        queue.Enqueue(Math.Max(0, value));
        while (queue.Count > Capacity)
            queue.Dequeue();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Lignes de repère discrètes
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), 1);
        gridPen.Freeze();
        dc.DrawLine(gridPen, new Point(0, h - 0.5), new Point(w, h - 0.5));
        dc.DrawLine(gridPen, new Point(0, h / 2), new Point(w, h / 2));

        if (_primary.Count < 2) return;

        var max = Math.Max(MinimumScale, Math.Max(_primary.Max(), _secondary.Max())) * 1.15;
        var dx = w / (Capacity - 1);

        // Les points les plus récents sont collés à droite ; le graphe se remplit de droite à gauche.
        Point[] ToPoints(Queue<double> series)
        {
            var offset = Capacity - series.Count;
            return series.Select((v, i) => new Point((offset + i) * dx, h - 1 - v / max * (h - 3))).ToArray();
        }

        var secondary = ToPoints(_secondary);
        var secondaryPen = new Pen(new SolidColorBrush(DrawingHelpers.WithAlpha(EndColor, 0xB0)), 1.2) { LineJoin = PenLineJoin.Round };
        secondaryPen.Freeze();
        dc.DrawGeometry(null, secondaryPen, Polyline(secondary, closed: false, h));

        var primary = ToPoints(_primary);
        var fill = new LinearGradientBrush(DrawingHelpers.WithAlpha(StartColor, 0x66), DrawingHelpers.WithAlpha(StartColor, 0x00), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, Polyline(primary, closed: true, h));

        var stroke = new LinearGradientBrush(StartColor, EndColor, 0);
        stroke.Freeze();
        var primaryPen = new Pen(stroke, 1.7) { LineJoin = PenLineJoin.Round };
        primaryPen.Freeze();
        dc.DrawGeometry(null, primaryPen, Polyline(primary, closed: false, h));

        // Point sur la dernière valeur
        var last = primary[^1];
        dc.DrawEllipse(Brushes.White, null, last, 2, 2);
    }

    private static Geometry Polyline(Point[] points, bool closed, double height)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (closed)
            {
                ctx.BeginFigure(new Point(points[0].X, height), true, true);
                ctx.LineTo(points[0], false, false);
            }
            else
            {
                ctx.BeginFigure(points[0], false, false);
            }

            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);

            if (closed)
                ctx.LineTo(new Point(points[^1].X, height), false, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
