using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using static SysDeck.Controls.DrawingHelpers;

namespace SysDeck.Controls;

/// <summary>
/// Réglage de volume (0–100) en plusieurs formes : "Vertical", "Horizontal", "Circle" (bouton rotatif),
/// "Half" (demi-cercle) ou "Segments" (barres empilées façon égaliseur). Clic, glisser ou molette.
/// </summary>
public sealed class VolumeControl : FrameworkElement, ITouchDraggable
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(VolumeControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(string), typeof(VolumeControl),
        new FrameworkPropertyMetadata("Vertical", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(
        nameof(StartColor), typeof(Color), typeof(VolumeControl),
        new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE0, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(
        nameof(EndColor), typeof(Color), typeof(VolumeControl),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8B, 0x5C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Shape { get => (string)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public Color StartColor { get => (Color)GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }
    public Color EndColor { get => (Color)GetValue(EndColorProperty); set => SetValue(EndColorProperty, value); }

    /// <summary>Valeur modifiée par l'utilisateur (clic, glisser, molette).</summary>
    public event EventHandler? UserValueChanged;
    public event EventHandler? DragStarted;
    public event EventHandler? DragCompleted;

    private const int SegmentCount = 10;
    private bool _dragging;

    public VolumeControl() => Cursor = System.Windows.Input.Cursors.Hand;

    protected override Size MeasureOverride(Size availableSize) => Shape switch
    {
        "Horizontal" => new Size(110, 22),
        "Circle" => new Size(62, 62),
        "Half" => new Size(76, 46),
        "Segments" => new Size(34, 90),
        _ => new Size(26, 90)
    };

    private Brush Gradient(double angle) { var b = new LinearGradientBrush(StartColor, EndColor, angle); b.Freeze(); return b; }

    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var f = Math.Clamp(Value / 100, 0, 1);
        // Fond transparent : toute la zone répond à la souris, pas seulement les parties dessinées.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        switch (Shape)
        {
            case "Horizontal":
            {
                const double pad = 9, t = 5;
                var y = h / 2;
                dc.DrawRoundedRectangle(TrackBrush, null, new Rect(pad, y - t / 2, w - 2 * pad, t), t / 2, t / 2);
                var x = pad + (w - 2 * pad) * f;
                if (x > pad) dc.DrawRoundedRectangle(Gradient(0), null, new Rect(pad, y - t / 2, x - pad, t), t / 2, t / 2);
                DrawThumb(dc, new Point(x, y));
                break;
            }
            case "Circle":
                DrawArc(dc, w, h, 270, f, t: 6, centerY: h / 2);
                DrawText(dc, $"{Value:0}", 13, new Point(w / 2, h / 2));
                break;
            case "Half":
                DrawArc(dc, w, h, 180, f, t: 6, centerY: h - 6);
                DrawText(dc, $"{Value:0}", 12, new Point(w / 2, h - 13));
                break;
            case "Segments":
            {
                const double gap = 2.5;
                var segH = (h - gap * (SegmentCount - 1)) / SegmentCount;
                var lit = (int)Math.Round(f * SegmentCount);
                for (var i = 0; i < SegmentCount; i++)
                {
                    // Segment 0 en bas ; couleur selon la hauteur, du début à la fin du dégradé.
                    var top = h - (i + 1) * segH - i * gap;
                    var color = i < lit ? DrawingHelpers.Lerp(StartColor, EndColor, i / (double)(SegmentCount - 1)) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
                    dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(color)), null, new Rect(4, top, w - 8, segH), 2, 2);
                }
                break;
            }
            default: // Vertical
            {
                const double pad = 9, t = 4;
                var x = w / 2;
                dc.DrawRoundedRectangle(TrackBrush, null, new Rect(x - t / 2, pad, t, h - 2 * pad), t / 2, t / 2);
                var y = h - pad - (h - 2 * pad) * f;
                if (y < h - pad) dc.DrawRoundedRectangle(Gradient(270), null, new Rect(x - t / 2, y, t, h - pad - y), t / 2, t / 2);
                DrawThumb(dc, new Point(x, y));
                break;
            }
        }
    }

    private void DrawThumb(DrawingContext dc, Point center)
    {
        var ring = new Pen(Gradient(0), 2.5);
        ring.Freeze();
        dc.DrawEllipse(Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x29))), ring, center, 7.5, 7.5);
        dc.DrawEllipse(Brushes.White, null, center, 2.5, 2.5);
    }

    private void DrawArc(DrawingContext dc, double w, double h, double sweep, double f, double t, double centerY)
    {
        var r = sweep > 180 ? Math.Min(w, h) / 2 - t : Math.Min(w / 2, centerY) - t;
        var c = new Point(w / 2, centerY);
        var start = 90 + (360 - sweep) / 2;

        var track = new Pen(TrackBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(c.X, c.Y, r, start, start + sweep));
        if (f > 0.005)
        {
            var value = new Pen(Gradient(0), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, value, Arc(c.X, c.Y, r, start, start + sweep * f));
        }

        var tip = start + sweep * f;
        var p = new Point(c.X + r * Math.Cos(tip * Math.PI / 180), c.Y + r * Math.Sin(tip * Math.PI / 180));
        dc.DrawEllipse(Brushes.White, null, p, t * 0.45, t * 0.45);
    }

    private void DrawText(DrawingContext dc, string text, double size, Point center)
    {
        var font = TryFindResource("NumberFont") as FontFamily ?? new FontFamily("Segoe UI");
        var brush = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    // ---------- Souris ----------

    private double ValueFromPoint(Point p)
    {
        double w = ActualWidth, h = ActualHeight;
        switch (Shape)
        {
            case "Horizontal":
                return (p.X - 9) / (w - 18) * 100;
            case "Segments":
                return (1 - p.Y / h) * 100;
            case "Circle":
                return AngleValue(p, new Point(w / 2, h / 2), 135, 270);
            case "Half":
                return AngleValue(p, new Point(w / 2, h - 6), 180, 180);
            default:
                return (1 - (p.Y - 9) / (h - 18)) * 100;
        }
    }

    /// <summary>Position angulaire sur un arc ; en dehors de l'arc, la valeur va à l'extrémité la plus proche.</summary>
    private static double AngleValue(Point p, Point c, double start, double sweep)
    {
        var angle = Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI;
        var rel = ((angle - start) % 360 + 360) % 360;
        if (rel > sweep)
            rel = rel < sweep + (360 - sweep) / 2 ? sweep : 0;
        return rel / sweep * 100;
    }

    private void SetFromUser(double value)
    {
        value = Math.Round(Math.Clamp(value, 0, 100));
        if (Math.Abs(value - Value) < 0.5) return;
        // SetCurrentValue : garde la liaison de données éventuelle sur Value.
        SetCurrentValue(ValueProperty, value);
        UserValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = CaptureMouse();
        if (_dragging) DragStarted?.Invoke(this, EventArgs.Empty);
        SetFromUser(ValueFromPoint(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromUser(ValueFromPoint(e.GetPosition(this)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        DragCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // Capture perdue sans relâchement du bouton (Alt+Tab, fenêtre UAC…) : sans ceci le glisser resterait
    // « en cours » et le mixeur ne rafraîchirait plus sa liste.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging) return;
        _dragging = false;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    // Doigt sur l'écran de la Switch : même comportement que le glisser à la souris.
    public void TouchStart(Point position)
    {
        _dragging = true;
        DragStarted?.Invoke(this, EventArgs.Empty);
        SetFromUser(ValueFromPoint(position));
    }

    public new void TouchMove(Point position)
    {
        if (_dragging) SetFromUser(ValueFromPoint(position));
    }

    public void TouchEnd()
    {
        if (!_dragging) return;
        _dragging = false;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetFromUser(Value + (e.Delta > 0 ? 5 : -5));
        e.Handled = true;
    }
}
