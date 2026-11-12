using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

using Rectangle = System.Windows.Shapes.Rectangle;
using Path = System.Windows.Shapes.Path;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Décor animé d'un thème, dessiné dans un Canvas 1920x1080 (mis à l'échelle par un Viewbox).
/// Uniquement des animations WPF en boucle (composées par la carte graphique) : rien n'est recalculé à chaque image.
/// </summary>
public static class ConsoleAmbientLayer
{
    private const double W = 1920, H = 1080;

    public static void Build(Canvas canvas, ConsoleTheme t)
    {
        canvas.Children.Clear();
        var rnd = new Random(42);
        switch (t.Ambient)
        {
            case ConsoleAmbient.Stars: Stars(canvas, t, rnd); break;
            case ConsoleAmbient.Bubbles: Bubbles(canvas, t, rnd); break;
            case ConsoleAmbient.Waves: Waves(canvas, t); break;
            case ConsoleAmbient.Cubes: Cubes(canvas, t, rnd); break;
            case ConsoleAmbient.Ribbons: Ribbons(canvas, t); break;
            case ConsoleAmbient.Particles: Particles(canvas, t, rnd); break;
            case ConsoleAmbient.Grid: Grid(canvas, t); break;
            case ConsoleAmbient.Ripples: Ripples(canvas, t); break;
            case ConsoleAmbient.Aurora: Aurora(canvas, t); break;
            case ConsoleAmbient.Snow: Snow(canvas, t, rnd); break;
            case ConsoleAmbient.Fireflies: Fireflies(canvas, t, rnd); break;
            case ConsoleAmbient.Petals: Petals(canvas, t, rnd); break;
            case ConsoleAmbient.Rain: Rain(canvas, t, rnd); break;
            case ConsoleAmbient.Hexagons: Hexagons(canvas, t); break;
            case ConsoleAmbient.Scanlines: Scanlines(canvas, t, rnd); break;
        }
    }

    private static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static DoubleAnimation Loop(double from, double to, double seconds, double beginOffset = 0, bool reverse = false) =>
        new(from, to, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever, AutoReverse = reverse,
            BeginTime = TimeSpan.FromSeconds(-beginOffset),     // décalage négatif : déjà en cours à l'ouverture
        };

    private static void Place(Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        c.Children.Add(e);
    }

    private static void Stars(Canvas c, ConsoleTheme t, Random r)
    {
        for (var i = 0; i < 90; i++)
        {
            var s = 1.5 + r.NextDouble() * 3;
            var e = new Ellipse { Width = s, Height = s, Fill = B(i % 7 == 0 ? A(t.Accent, 0xFF) : A(t.Text, 0xFF)) };
            e.BeginAnimation(UIElement.OpacityProperty, Loop(0.08, 0.7, 2 + r.NextDouble() * 4, r.NextDouble() * 6, true));
            Place(c, e, r.NextDouble() * W, r.NextDouble() * H);
        }
    }

    private static void Bubbles(Canvas c, ConsoleTheme t, Random r)
    {
        for (var i = 0; i < 26; i++)
        {
            var s = 20 + r.NextDouble() * 110;
            var e = new Ellipse
            {
                Width = s, Height = s, StrokeThickness = 2, Stroke = B(A(t.Accent, 0x50)),
                Fill = new RadialGradientBrush(A(t.Accent, 0x26), A(t.Accent, 0x04)),
            };
            var tr = new TranslateTransform();
            e.RenderTransform = tr;
            var dur = 14 + r.NextDouble() * 16;
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(H + 150, -200, dur, r.NextDouble() * dur));
            tr.BeginAnimation(TranslateTransform.XProperty, Loop(-30, 30, 3 + r.NextDouble() * 3, r.NextDouble() * 3, true));
            Place(c, e, r.NextDouble() * W, 0);
        }
    }

    private static Geometry Sine(double amp, double period, double y)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, y), false, false);
            for (double x = 0; x <= W * 2 + period; x += 24)
                ctx.LineTo(new Point(x, y + Math.Sin(x / period * Math.PI * 2) * amp), true, true);
        }
        g.Freeze();
        return g;
    }

    private static void Waves(Canvas c, ConsoleTheme t)
    {
        var specs = new[] { (60.0, 640.0, 760.0, 0x70, 3.0, 26.0), (90, 960, 820, 0x40, 2, 38), (40, 480, 900, 0x55, 2, 18), (120, 1280, 700, 0x28, 6, 52) };
        foreach (var (amp, period, y, alpha, thick, dur) in specs)
        {
            var p = new Path
            {
                Data = Sine(amp, period, y), StrokeThickness = thick, Stroke = B(A(t.Accent, (byte)alpha)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = thick * 1.5 },
            };
            var tr = new TranslateTransform();
            p.RenderTransform = tr;
            tr.BeginAnimation(TranslateTransform.XProperty, Loop(0, -period * 2, dur));
            Place(c, p, 0, 0);
        }
    }

    private static void Cubes(Canvas c, ConsoleTheme t, Random r)
    {
        for (var i = 0; i < 16; i++)
        {
            var s = 40 + r.NextDouble() * 140;
            var g = new Grid { Width = s, Height = s, RenderTransformOrigin = new Point(0.5, 0.5) };
            g.Children.Add(new Rectangle { StrokeThickness = 2, Stroke = B(A(t.Accent, 0x55)), RadiusX = t.Corner / 4, RadiusY = t.Corner / 4 });
            g.Children.Add(new Rectangle { Margin = new Thickness(s * 0.22), StrokeThickness = 1.5, Stroke = B(A(t.Accent, 0x30)), Fill = B(A(t.Accent, 0x0C)) });
            var tg = new TransformGroup();
            var rot = new RotateTransform();
            var tr = new TranslateTransform();
            tg.Children.Add(rot);
            tg.Children.Add(tr);
            g.RenderTransform = tg;
            rot.BeginAnimation(RotateTransform.AngleProperty, Loop(0, i % 2 == 0 ? 360 : -360, 30 + r.NextDouble() * 40, r.NextDouble() * 30));
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(-40, 40, 6 + r.NextDouble() * 6, r.NextDouble() * 6, true));
            Place(c, g, r.NextDouble() * W, r.NextDouble() * H);
        }
    }

    private static void Ribbons(Canvas c, ConsoleTheme t)
    {
        for (var i = 0; i < 3; i++)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(-200, 700 + i * 70), true, true);
                ctx.BezierTo(new Point(500, 300 + i * 90), new Point(1300, 1100 - i * 60), new Point(2200, 500 + i * 80), true, true);
                ctx.LineTo(new Point(2200, 560 + i * 80), true, true);
                ctx.BezierTo(new Point(1300, 1160 - i * 60), new Point(500, 360 + i * 90), new Point(-200, 760 + i * 70), true, true);
            }
            g.Freeze();
            var p = new Path
            {
                Data = g,
                Fill = new LinearGradientBrush(A(t.Accent, (byte)(0x50 - i * 0x12)), A(t.Accent, 0x00), 0),
            };
            var tr = new TranslateTransform();
            p.RenderTransform = tr;
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(-60, 60, 9 + i * 3, i * 2, true));
            tr.BeginAnimation(TranslateTransform.XProperty, Loop(-80, 80, 13 + i * 4, i * 3, true));
            Place(c, p, 0, 0);
        }
    }

    private static void Particles(Canvas c, ConsoleTheme t, Random r)
    {
        for (var i = 0; i < 70; i++)
        {
            var s = 2 + r.NextDouble() * 4;
            var e = new Ellipse { Width = s, Height = s, Fill = B(i % 4 == 0 ? t.Accent : t.Text) };
            var tr = new TranslateTransform();
            e.RenderTransform = tr;
            var dur = 18 + r.NextDouble() * 22;
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(0, -H - 100, dur, r.NextDouble() * dur));
            e.BeginAnimation(UIElement.OpacityProperty, Loop(0.05, 0.45, 3 + r.NextDouble() * 3, r.NextDouble() * 3, true));
            Place(c, e, r.NextDouble() * W, H + 20);
        }
    }

    private static void Grid(Canvas c, ConsoleTheme t)
    {
        const double horizon = 640;
        var stroke = B(A(t.Accent, 0x45));
        for (var i = -14; i <= 14; i++)
            Place(c, new Line { X1 = W / 2 + i * 30, Y1 = horizon, X2 = W / 2 + i * 260, Y2 = H + 40, Stroke = stroke, StrokeThickness = 1.5 }, 0, 0);
        // Lignes horizontales qui « avancent » vers le spectateur.
        var lines = new Canvas();
        for (var k = 0; k < 10; k++)
        {
            var y = horizon + Math.Pow(k / 10.0, 2) * (H - horizon + 80);
            lines.Children.Add(new Line { X1 = 0, X2 = W, Y1 = y, Y2 = y, Stroke = stroke, StrokeThickness = 1.5 });
        }
        var tr = new TranslateTransform();
        lines.RenderTransform = tr;
        tr.BeginAnimation(TranslateTransform.YProperty, Loop(0, 44, 1.6));
        lines.Clip = new RectangleGeometry(new Rect(0, horizon, W, H - horizon + 100));
        Place(c, lines, 0, 0);
        Place(c, new Rectangle
        {
            Width = W, Height = 3, Fill = B(A(t.Accent, 0xA0)),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 },
        }, 0, horizon - 1);
    }

    /// <summary>Voiles d'aurore : grandes bandes en dégradé qui ondulent lentement.</summary>
    private static void Aurora(Canvas c, ConsoleTheme t)
    {
        var tints = new[] { t.Accent, Color.FromRgb(0x5A, 0x8C, 0xFF), Color.FromRgb(0xB0, 0x6C, 0xFF) };
        for (var i = 0; i < 3; i++)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                var y = 160 + i * 110;
                ctx.BeginFigure(new Point(-300, y + 120), true, true);
                ctx.BezierTo(new Point(400, y - 120), new Point(1200, y + 260), new Point(2300, y), true, true);
                ctx.LineTo(new Point(2300, y + 340), true, true);
                ctx.BezierTo(new Point(1200, y + 520), new Point(400, y + 160), new Point(-300, y + 420), true, true);
            }
            g.Freeze();
            var brush = new LinearGradientBrush(A(tints[i], 0x00), A(tints[i], 0x00), 90);
            brush.GradientStops.Insert(1, new GradientStop(A(tints[i], (byte)(0x55 - i * 0x10)), 0.45));
            var p = new Path { Data = g, Fill = brush, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 40 } };
            var tr = new TranslateTransform();
            p.RenderTransform = tr;
            tr.BeginAnimation(TranslateTransform.XProperty, Loop(-160, 160, 14 + i * 5, i * 3, true));
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(-40, 40, 9 + i * 3, i * 2, true));
            p.BeginAnimation(UIElement.OpacityProperty, Loop(0.55, 1, 6 + i * 2, i, true));
            Place(c, p, 0, 0);
        }
    }

    /// <summary>Flocons qui tombent en se balançant.</summary>
    private static void Snow(Canvas c, ConsoleTheme t, Random r)
    {
        var flake = t.Light ? A(t.Accent, 0x90) : A(Colors.White, 0xC0);
        for (var i = 0; i < 70; i++)
        {
            var s = 3 + r.NextDouble() * 7;
            var e = new Ellipse { Width = s, Height = s, Fill = B(flake), Opacity = 0.4 + r.NextDouble() * 0.6 };
            var tr = new TranslateTransform();
            e.RenderTransform = tr;
            var dur = 10 + r.NextDouble() * 14;
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(-40, H + 40, dur, r.NextDouble() * dur));
            tr.BeginAnimation(TranslateTransform.XProperty, Loop(-25, 25, 2.5 + r.NextDouble() * 2.5, r.NextDouble() * 3, true));
            Place(c, e, r.NextDouble() * W, 0);
        }
    }

    /// <summary>Lucioles : points chauds qui dérivent et s'allument au hasard.</summary>
    private static void Fireflies(Canvas c, ConsoleTheme t, Random r)
    {
        for (var i = 0; i < 40; i++)
        {
            var s = 5 + r.NextDouble() * 6;
            var e = new Ellipse
            {
                Width = s * 4, Height = s * 4,
                Fill = new RadialGradientBrush(A(t.Accent, 0xFF), A(t.Accent, 0x00)) { GradientStops = { new GradientStop(A(t.Accent, 0xFF), 0), new GradientStop(A(t.Accent, 0x40), 0.25), new GradientStop(A(t.Accent, 0x00), 1) } },
            };
            var tr = new TranslateTransform();
            e.RenderTransform = tr;
            tr.BeginAnimation(TranslateTransform.XProperty, Loop(-60 - r.NextDouble() * 80, 60 + r.NextDouble() * 80, 6 + r.NextDouble() * 8, r.NextDouble() * 6, true));
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(-50 - r.NextDouble() * 60, 50 + r.NextDouble() * 60, 5 + r.NextDouble() * 7, r.NextDouble() * 5, true));
            e.BeginAnimation(UIElement.OpacityProperty, Loop(0, 0.9, 1.5 + r.NextDouble() * 2.5, r.NextDouble() * 4, true));
            Place(c, e, r.NextDouble() * W, H * 0.25 + r.NextDouble() * H * 0.75);
        }
    }

    /// <summary>Pétales qui tombent en tournoyant.</summary>
    private static void Petals(Canvas c, ConsoleTheme t, Random r)
    {
        for (var i = 0; i < 38; i++)
        {
            var s = 10 + r.NextDouble() * 12;
            var e = new Ellipse { Width = s, Height = s * 0.6, Fill = B(A(t.Accent, (byte)(0x70 + r.Next(0x60)))), RenderTransformOrigin = new Point(0.5, 0.5) };
            var tg = new TransformGroup();
            var rot = new RotateTransform();
            var tr = new TranslateTransform();
            tg.Children.Add(rot);
            tg.Children.Add(tr);
            e.RenderTransform = tg;
            var dur = 12 + r.NextDouble() * 12;
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(-40, H + 60, dur, r.NextDouble() * dur));
            tr.BeginAnimation(TranslateTransform.XProperty, Loop(-120, 120, 4 + r.NextDouble() * 4, r.NextDouble() * 4, true));
            rot.BeginAnimation(RotateTransform.AngleProperty, Loop(0, r.Next(2) == 0 ? 360 : -360, 3 + r.NextDouble() * 4, r.NextDouble() * 3));
            Place(c, e, r.NextDouble() * W, 0);
        }
    }

    /// <summary>Pluie de lignes lumineuses (ville néon).</summary>
    private static void Rain(Canvas c, ConsoleTheme t, Random r)
    {
        for (var i = 0; i < 60; i++)
        {
            var len = 60 + r.NextDouble() * 160;
            var line = new Rectangle
            {
                Width = 2, Height = len,
                Fill = new LinearGradientBrush(A(t.Accent, 0x00), A(t.Accent, (byte)(0x60 + r.Next(0x80))), 90),
            };
            var tr = new TranslateTransform();
            line.RenderTransform = tr;
            var dur = 1.2 + r.NextDouble() * 2.2;
            tr.BeginAnimation(TranslateTransform.YProperty, Loop(-len, H + len, dur, r.NextDouble() * dur));
            Place(c, line, r.NextDouble() * W, 0);
        }
    }

    /// <summary>Nid d'abeilles doré : hexagones dont la lueur passe en vague.</summary>
    private static void Hexagons(Canvas c, ConsoleTheme t)
    {
        const double size = 70;
        var w = Math.Sqrt(3) * size;
        var hex = Geometry.Parse(FormattableString.Invariant(
            $"M {w / 2},0 L {w},{size / 2} L {w},{size * 1.5} L {w / 2},{size * 2} L 0,{size * 1.5} L 0,{size / 2} Z"));
        hex.Freeze();
        for (var row = 0; row * size * 1.5 < H + size; row++)
            for (var col = -1; col * w < W + w; col++)
            {
                var x = col * w + (row % 2 == 1 ? w / 2 : 0);
                var y = row * size * 1.5 - size;
                var p = new Path { Data = hex, Stroke = B(A(t.Accent, 0x55)), StrokeThickness = 1.5, Fill = B(A(t.Accent, 0x10)), Opacity = 0.15 };
                // Vague de lumière qui traverse l'écran en diagonale.
                p.BeginAnimation(UIElement.OpacityProperty, Loop(0.12, 0.75, 2.5, (x + y) / 260.0 % 8, true));
                Place(c, p, x, y);
            }
    }

    /// <summary>Écran cathodique : lignes de balayage, bande lumineuse qui défile, parasites colorés de temps en temps.</summary>
    private static void Scanlines(Canvas c, ConsoleTheme t, Random r)
    {
        var cyan = Color.FromRgb(0x05, 0xD9, 0xE8);
        // Lignes de balayage (motif répété : un seul élément).
        var lines = new DrawingBrush(new GeometryDrawing(B(A(Colors.Black, 0x55)), null, new RectangleGeometry(new Rect(0, 0, 4, 1))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 4), ViewportUnits = BrushMappingMode.Absolute,
        };
        Place(c, new Rectangle { Width = W, Height = H, Fill = lines }, 0, 0);
        // Grille néon en perspective, discrète, en bas.
        for (var i = -10; i <= 10; i++)
            Place(c, new Line { X1 = W / 2 + i * 40, Y1 = 700, X2 = W / 2 + i * 300, Y2 = H + 40, Stroke = B(A(cyan, 0x30)), StrokeThickness = 1 }, 0, 0);
        // Bande lumineuse qui descend lentement.
        var band = new Rectangle { Width = W, Height = 140, Fill = new LinearGradientBrush(A(t.Accent, 0), A(t.Accent, 0x18), 90) };
        var bt = new TranslateTransform();
        band.RenderTransform = bt;
        bt.BeginAnimation(TranslateTransform.YProperty, Loop(-160, H + 20, 7));
        Place(c, band, 0, 0);
        // Parasites : barres colorées qui apparaissent un instant, à intervalles irréguliers.
        for (var i = 0; i < 7; i++)
        {
            var bar = new Rectangle
            {
                Width = 200 + r.NextDouble() * 900, Height = 2 + r.NextDouble() * 14,
                Fill = B(A(i % 2 == 0 ? t.Accent : cyan, 0x90)), Opacity = 0,
            };
            var period = 3.5 + r.NextDouble() * 6;
            var at = r.NextDouble() * period;
            var f = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(period), RepeatBehavior = RepeatBehavior.Forever };
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at + 0.05))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at + 0.09))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at + 0.14))));
            bar.BeginAnimation(UIElement.OpacityProperty, f);
            Place(c, bar, r.NextDouble() * (W - 300), r.NextDouble() * H);
        }
    }

    private static void Ripples(Canvas c, ConsoleTheme t)
    {
        for (var i = 0; i < 5; i++)
        {
            var e = new Ellipse { Width = 600, Height = 600, StrokeThickness = 3, Stroke = B(A(t.Accent, 0x60)), RenderTransformOrigin = new Point(0.5, 0.5) };
            var sc = new ScaleTransform();
            e.RenderTransform = sc;
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(0.1, 3.2, 10, i * 2));
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(0.1, 3.2, 10, i * 2));
            e.BeginAnimation(UIElement.OpacityProperty, Loop(0.9, 0, 10, i * 2));
            Place(c, e, W * 0.72 - 300, H * 0.62 - 300);
        }
    }
}
