using System.Windows;
using System.Windows.Media;
using SysDeck.Services;

namespace SysDeck.Controls;

/// <summary>Icône météo dessinée (soleil, lune, nuages, pluie, neige, orage, brouillard), sans police ni image.</summary>
public sealed class WeatherIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(WeatherKind),
        typeof(WeatherIcon), new FrameworkPropertyMetadata(WeatherKind.Clear, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(nameof(IsDay), typeof(bool),
        typeof(WeatherIcon), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public WeatherKind Kind { get => (WeatherKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool IsDay { get => (bool)GetValue(IsDayProperty); set => SetValue(IsDayProperty, value); }

    private static readonly Brush Sun = Frozen(Color.FromRgb(0xFF, 0xC4, 0x3D));
    private static readonly Brush Moon = Frozen(Color.FromRgb(0xDD, 0xE3, 0xF0));
    private static readonly Brush CloudLight = Frozen(Color.FromRgb(0xE9, 0xEC, 0xF2));
    private static readonly Brush CloudDark = Frozen(Color.FromRgb(0x9A, 0xA3, 0xB5));
    private static readonly Brush Water = Frozen(Color.FromRgb(0x4F, 0xA8, 0xFF));
    private static readonly Brush Bolt = Frozen(Color.FromRgb(0xFF, 0xD2, 0x3F));

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        // Dessin dans un carré de 48 unités, centré.
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 48, size / 48));
        switch (Kind)
        {
            case WeatherKind.Clear:
                Celestial(dc, 24, 24, 11);
                break;
            case WeatherKind.PartlyCloudy:
                Celestial(dc, 17, 17, 8);
                Cloud(dc, 26, 30, 1, CloudLight);
                break;
            case WeatherKind.Cloudy:
                Cloud(dc, 30, 22, 0.75, CloudDark);
                Cloud(dc, 22, 29, 1, CloudLight);
                break;
            case WeatherKind.Fog:
                Cloud(dc, 24, 20, 0.9, CloudDark);
                var fog = new Pen(CloudLight, 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(fog, new Point(8, 32), new Point(40, 32));
                dc.DrawLine(fog, new Point(12, 38), new Point(36, 38));
                break;
            case WeatherKind.Drizzle:
            case WeatherKind.Rain:
                Cloud(dc, 24, 21, 1, CloudLight);
                var drop = new Pen(Water, Kind == WeatherKind.Rain ? 2.6 : 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var drops = Kind == WeatherKind.Rain ? new[] { 15.0, 23, 31 } : new[] { 18.0, 28 };
                foreach (var x in drops) dc.DrawLine(drop, new Point(x + 2, 33), new Point(x - 1, 41));
                break;
            case WeatherKind.Snow:
                Cloud(dc, 24, 21, 1, CloudLight);
                foreach (var (x, y) in new[] { (15.0, 35.0), (24, 40), (33, 35), (19, 43), (29, 44) })
                    dc.DrawEllipse(Moon, null, new Point(x, y), 2, 2);
                break;
            case WeatherKind.Storm:
                Cloud(dc, 24, 20, 1, CloudDark);
                var bolt = new StreamGeometry();
                using (var g = bolt.Open())
                {
                    g.BeginFigure(new Point(26, 27), true, true);
                    g.PolyLineTo(new[] { new Point(18, 38), new Point(23, 38), new Point(20, 46), new Point(30, 34), new Point(25, 34), new Point(28, 27) }, true, true);
                }
                bolt.Freeze();
                dc.DrawGeometry(Bolt, null, bolt);
                break;
        }
        dc.Pop();
        dc.Pop();
    }

    /// <summary>Soleil (avec rayons) le jour, croissant de lune la nuit.</summary>
    private void Celestial(DrawingContext dc, double cx, double cy, double r)
    {
        if (IsDay)
        {
            dc.DrawEllipse(Sun, null, new Point(cx, cy), r * 0.62, r * 0.62);
            var ray = new Pen(Sun, r * 0.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                dc.DrawLine(ray, new Point(cx + Math.Cos(a) * r * 0.85, cy + Math.Sin(a) * r * 0.85),
                    new Point(cx + Math.Cos(a) * r * 1.15, cy + Math.Sin(a) * r * 1.15));
            }
            return;
        }
        var moon = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx, cy), r * 0.9, r * 0.9),
            new EllipseGeometry(new Point(cx + r * 0.5, cy - r * 0.35), r * 0.75, r * 0.75));
        dc.DrawGeometry(Moon, null, moon);
    }

    /// <summary>Nuage (trois bosses sur une base arrondie), centré en (cx, cy).</summary>
    private static void Cloud(DrawingContext dc, double cx, double cy, double scale, Brush fill)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(cx - 8 * scale, cy + 1 * scale), 7 * scale, 7 * scale));
        g.Children.Add(new EllipseGeometry(new Point(cx + 1 * scale, cy - 3 * scale), 9.5 * scale, 9.5 * scale));
        g.Children.Add(new EllipseGeometry(new Point(cx + 10 * scale, cy + 2 * scale), 6.5 * scale, 6.5 * scale));
        g.Children.Add(new RectangleGeometry(new Rect(cx - 14 * scale, cy + 1 * scale, 30 * scale, 7.5 * scale), 3.7 * scale, 3.7 * scale));
        dc.DrawGeometry(fill, null, g);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
