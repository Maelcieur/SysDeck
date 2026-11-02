using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SysDeck.Services;
using static SysDeck.Controls.DrawingHelpers;

namespace SysDeck.Controls;

/// <summary>
/// Jauge circulaire (arc de 270°) ou demi-cercle (180°), animée, remplie d'un dégradé qui suit l'arc :
/// la couleur de début correspond au minimum de l'échelle, celle de fin au maximum. Aucun Effect WPF
/// (les fenêtres de widgets utilisent AllowsTransparency) : le halo est dessiné à la main.
/// </summary>
public sealed class RadialGauge : FrameworkElement
{
    // WPF n'a pas de dégradé conique : l'arc de valeur est découpé en petits segments de couleur interpolée.
    private const double SegmentDegrees = 3;
    private const int TickCount = 24;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RadialGauge), new PropertyMetadata(0.0, OnValueChanged));

    /// <summary>Valeur actuellement dessinée : suit <see cref="Value"/> avec une animation adoucie.</summary>
    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnDisplayValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SweepAngleProperty = DependencyProperty.Register(
        nameof(SweepAngle), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(270.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(
        nameof(StartColor), typeof(Color), typeof(RadialGauge),
        new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE0, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(
        nameof(EndColor), typeof(Color), typeof(RadialGauge),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8B, 0x5C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RadialGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowTicksProperty = DependencyProperty.Register(
        nameof(ShowTicks), typeof(bool), typeof(RadialGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    // Réglages d'apparence alimentés par le thème (global ou propre au module) via le style implicite.
    public static readonly DependencyProperty ThicknessScaleProperty = DependencyProperty.Register(
        nameof(ThicknessScale), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TicksEnabledProperty = DependencyProperty.Register(
        nameof(TicksEnabled), typeof(bool), typeof(RadialGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowEnabledProperty = DependencyProperty.Register(
        nameof(GlowEnabled), typeof(bool), typeof(RadialGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Multiplicateur de durée des animations ; 0 = pas d'animation.</summary>
    public static readonly DependencyProperty AnimationScaleProperty = DependencyProperty.Register(
        nameof(AnimationScale), typeof(double), typeof(RadialGauge), new PropertyMetadata(1.0));

    /// <summary>"Smooth", "Elastic", "Bounce" ou "Linear".</summary>
    public static readonly DependencyProperty EasingProperty = DependencyProperty.Register(
        nameof(Easing), typeof(string), typeof(RadialGauge), new PropertyMetadata("Smooth"));

    /// <summary>Style des graduations : "Lines", "Dense", "Majors", "Dots", "Inner" ou "Numbers".</summary>
    public static readonly DependencyProperty TickDesignProperty = DependencyProperty.Register(
        nameof(TickDesign), typeof(string), typeof(RadialGauge),
        new FrameworkPropertyMetadata("Lines", FrameworkPropertyMetadataOptions.AffectsRender));

    public string TickDesign { get => (string)GetValue(TickDesignProperty); set => SetValue(TickDesignProperty, value); }
    /// <summary>Style de dessin : "Smooth", "Segments", "Thin", "Dots", "Needle", "Pie", "Neon", "Rays", "Double" ou "Capsule".</summary>
    public static readonly DependencyProperty DesignProperty = DependencyProperty.Register(
        nameof(Design), typeof(string), typeof(RadialGauge),
        new FrameworkPropertyMetadata("Smooth", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Design { get => (string)GetValue(DesignProperty); set => SetValue(DesignProperty, value); }
    public static readonly DependencyProperty IntroAnimationProperty = DependencyProperty.Register(
        nameof(IntroAnimation), typeof(bool), typeof(RadialGauge), new PropertyMetadata(true));

    public double ThicknessScale { get => (double)GetValue(ThicknessScaleProperty); set => SetValue(ThicknessScaleProperty, value); }
    public bool TicksEnabled { get => (bool)GetValue(TicksEnabledProperty); set => SetValue(TicksEnabledProperty, value); }
    public bool GlowEnabled { get => (bool)GetValue(GlowEnabledProperty); set => SetValue(GlowEnabledProperty, value); }
    public double AnimationScale { get => (double)GetValue(AnimationScaleProperty); set => SetValue(AnimationScaleProperty, value); }
    public string Easing { get => (string)GetValue(EasingProperty); set => SetValue(EasingProperty, value); }
    public bool IntroAnimation { get => (bool)GetValue(IntroAnimationProperty); set => SetValue(IntroAnimationProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double SweepAngle { get => (double)GetValue(SweepAngleProperty); set => SetValue(SweepAngleProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Color StartColor { get => (Color)GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }
    public Color EndColor { get => (Color)GetValue(EndColorProperty); set => SetValue(EndColorProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public bool ShowTicks { get => (bool)GetValue(ShowTicksProperty); set => SetValue(ShowTicksProperty, value); }

    /// <summary>Levé à chaque image de l'animation, pour afficher un compteur qui défile avec l'arc.</summary>
    public event Action<double>? DisplayValueChanged;

    private bool _hasAppeared;

    public RadialGauge()
    {
        // Premier affichage : l'arc se remplit depuis zéro, plus lentement que les mises à jour suivantes.
        Loaded += (_, _) =>
        {
            if (_hasAppeared) return;
            _hasAppeared = true;
            AnimateTo(Value, IntroAnimation ? TimeSpan.FromMilliseconds(1100) : TimeSpan.Zero);
        };
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RadialGauge)d;
        if (gauge._hasAppeared)
            gauge.AnimateTo((double)e.NewValue, TimeSpan.FromMilliseconds(650));
    }

    private static void OnDisplayValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RadialGauge)d).DisplayValueChanged?.Invoke((double)e.NewValue);

    private void AnimateTo(double target, TimeSpan duration)
    {
        var scaled = duration * Math.Max(0, AnimationScale);
        // Écart de moins de 1 % de l'échelle (bruit de mesure) : invisible animé, mais 26 redessins à chaque relevé.
        var tiny = Math.Abs(target - DisplayValue) <= Math.Max(0, Maximum) * 0.01;
        if (scaled <= TimeSpan.FromMilliseconds(16) || tiny)
        {
            BeginAnimation(DisplayValueProperty, null);
            DisplayValue = target;
            return;
        }

        var animation = new DoubleAnimation(target, scaled)
        {
            EasingFunction = Easing switch
            {
                "Elastic" => new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 },
                "Bounce" => new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 2, Bounciness = 3 },
                _ => ThemeService.CreateEasing(Easing)
            }
        };
        // 40 i/s suffisent à l'œil et ménagent le CPU d'un outil de surveillance censé rester discret.
        Timeline.SetDesiredFrameRate(animation, 40);
        BeginAnimation(DisplayValueProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var sweep = Math.Clamp(SweepAngle, 10, 360);
        var t = Thickness * Math.Clamp(ThicknessScale, 0.2, 3);
        var ticks = ShowTicks && TicksEnabled;
        var glow = GlowEnabled;
        // Marge autour de l'axe de l'anneau : le halo déborde d'une épaisseur, les graduations un peu plus.
        var pad = Math.Max(glow ? t : t / 2 + 1, !ticks ? 0 : TickDesign == "Numbers" ? t / 2 + 10 : t / 2 + 7);

        double r, cx, cy;
        if (sweep <= 180)
        {
            r = Math.Min(w / 2 - pad, h - 2 * pad);
            cx = w / 2;
            cy = pad + r;
        }
        else
        {
            r = Math.Min(w, h) / 2 - pad;
            cx = w / 2;
            cy = h / 2;
        }
        if (r <= 1) return;

        var g = new Frame(cx, cy, r, t, 90 + (360 - sweep) / 2, sweep,
            sweep * (Maximum > 0 ? Math.Clamp(DisplayValue / Maximum, 0, 1) : 0), glow, StartColor, EndColor);

        if (ticks) DrawTicks(dc, g);

        switch (Design)
        {
            case "Segments": DrawSegments(dc, g); break;
            case "Thin": DrawSmooth(dc, g with { T = Math.Max(1.5, t * 0.35) }, tipRadius: Math.Max(2.2, t * 0.55)); break;
            case "Dots": DrawDots(dc, g); break;
            case "Needle": DrawNeedle(dc, g); break;
            case "Pie": DrawPie(dc, g); break;
            case "Neon": DrawNeon(dc, g); break;
            case "Rays": DrawRays(dc, g); break;
            case "Double": DrawDouble(dc, g); break;
            case "Capsule": DrawCapsule(dc, g); break;
            default: DrawSmooth(dc, g, tipRadius: t * 0.26); break;
        }
    }

    // ---------- Cache de rendu ----------
    // La jauge est redessinée jusqu'à 40 fois par seconde pendant une animation : plumes, pinceaux et arcs figés
    // sont réutilisés d'une image à l'autre au lieu d'être recréés (c'était l'essentiel des allocations de SysDeck).
    // Les clés contiennent toutes les entrées du dessin : rien à invalider, chaque cache est vidé au-delà de sa borne.
    private const int CacheLimit = 512;
    private readonly Dictionary<Color, Brush> _brushes = new();
    private readonly Dictionary<(Color, double, bool), Pen> _colorPens = new();
    private readonly Dictionary<(Brush, double, bool), Pen> _brushPens = new();
    private readonly Dictionary<(double, double, double, double, double), Geometry> _arcs = new();
    private readonly Dictionary<(Color, Color), Brush> _gradients = new();

    private static TValue Cached<TKey, TValue>(Dictionary<TKey, TValue> cache, TKey key, Func<TValue> create) where TKey : notnull
    {
        if (cache.TryGetValue(key, out var value)) return value;
        if (cache.Count >= CacheLimit) cache.Clear();
        return cache[key] = create();
    }

    private Brush BrushOf(Color color) => Cached(_brushes, color, () => Frozen(new SolidColorBrush(color)));

    private Pen PenOf(Color color, double width, bool round = false) =>
        Cached(_colorPens, (color, width, round), () => MakePen(BrushOf(color), width, round));

    private Pen PenOf(Brush brush, double width, bool round = false) =>
        Cached(_brushPens, (brush, width, round), () => MakePen(brush, width, round));

    private static Pen MakePen(Brush brush, double width, bool round)
    {
        var pen = new Pen(brush, width);
        if (round) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        return Frozen(pen);
    }

    private Geometry ArcOf(double cx, double cy, double r, double from, double to) =>
        Cached(_arcs, (cx, cy, r, from, to), () => Arc(cx, cy, r, from, to));

    private Brush RadialOf(Color inner, Color outer) =>
        Cached(_gradients, (inner, outer), () => Frozen(new RadialGradientBrush(inner, outer)));

    // Les segments du dégradé sont posés sur une grille fixe partant du début de l'arc : seul le dernier,
    // partiel, change avec la valeur ; les autres (plume, couleur, arc) sont identiques d'une image à l'autre.
    private static int SegmentCount(Frame g) => Math.Max(1, (int)Math.Ceiling(g.ValueSweep / SegmentDegrees - 1e-9));

    /// <summary>Angles d'un segment ; <paramref name="overlap"/> fait empiéter les segments pleins sur le suivant.</summary>
    private static (double A0, double A1, double Mid) Segment(Frame g, int i, int count, double overlap)
    {
        var a0 = g.Start + i * SegmentDegrees;
        return (a0, i == count - 1 ? g.End : a0 + SegmentDegrees + overlap, a0 + SegmentDegrees / 2);
    }

    /// <summary>Géométrie commune à tous les styles : centre, rayon, épaisseur, angles (en degrés).</summary>
    private sealed record Frame(double Cx, double Cy, double R, double T, double Start, double Sweep, double ValueSweep,
        bool Glow, Color StartColor, Color EndColor)
    {
        public double End => Start + ValueSweep;
        public Point At(double angle, double radius)
        {
            var rad = angle * Math.PI / 180;
            return new Point(Cx + radius * Math.Cos(rad), Cy + radius * Math.Sin(rad));
        }
        public Color ColorAt(double angle) => Lerp(StartColor, EndColor, (angle - Start) / Sweep);
    }

    /// <summary>
    /// Graduations ; celles déjà atteintes par la valeur s'allument à la couleur du dégradé.
    /// Styles : "Lines" (traits), "Dense" (traits fins et serrés), "Majors" (grands traits seuls),
    /// "Dots" (points), "Inner" (traits à l'intérieur de l'anneau), "Numbers" (traits et valeurs).
    /// </summary>
    private void DrawTicks(DrawingContext dc, Frame g)
    {
        var design = TickDesign;
        var count = design == "Dense" ? TickCount * 2 : TickCount;
        var majorEvery = design == "Dense" ? 12 : 6;
        var dimBrush = BrushOf(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        var outerEdge = g.R + g.T / 2 + 3;
        var innerEdge = g.R - g.T / 2 - 3;

        Brush BrushAt(double angle, bool lit) => lit ? BrushOf(WithAlpha(g.ColorAt(angle), 0xD0)) : dimBrush;

        for (var i = 0; i <= count; i++)
        {
            var major = i % majorEvery == 0;
            if (design == "Majors" && !major) continue;

            var angle = g.Start + g.Sweep * i / count;
            var lit = angle <= g.End + 0.01 && g.ValueSweep > 0;
            var brush = BrushAt(angle, lit);

            switch (design)
            {
                case "Dots":
                {
                    var radius = major ? 1.6 : 1.0;
                    dc.DrawEllipse(brush, null, g.At(angle, outerEdge + 2), radius, radius);
                    break;
                }
                case "Inner":
                {
                    var length = major ? 4.5 : 2.5;
                    dc.DrawLine(PenOf(brush, major ? 1.4 : 1.1), g.At(angle, innerEdge), g.At(angle, innerEdge - length));
                    break;
                }
                default:
                {
                    var length = design switch { "Majors" => 6, "Dense" => major ? 4.5 : 2, _ => major ? 4.5 : 2.5 };
                    var thickness = design == "Dense" && !major ? 0.8 : major ? 1.4 : 1.1;
                    dc.DrawLine(PenOf(brush, thickness), g.At(angle, outerEdge), g.At(angle, outerEdge + length));
                    break;
                }
            }

            // Valeurs aux graduations principales (0, 25 %, 50 %… du maximum de la jauge).
            if (design == "Numbers" && major)
            {
                var value = Maximum * i / count;
                var label = new FormattedText(value.ToString(value >= 10 || value == 0 ? "0" : "0.#"),
                    System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 6.5, lit ? brush : BrushOf(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                // Juste au-delà des traits ; les valeurs peuvent déborder un peu du cadre de la jauge plutôt que de la rétrécir.
                var at = g.At(angle, outerEdge + 9.5);
                dc.DrawText(label, new Point(at.X - label.Width / 2, at.Y - label.Height / 2));
            }
        }
    }
    private void DrawTrack(DrawingContext dc, Frame g, double thickness)
    {
        dc.DrawGeometry(null, PenOf(TrackBrush, thickness, round: true), ArcOf(g.Cx, g.Cy, g.R, g.Start, g.Start + g.Sweep));
    }

    /// <summary>Style « Lisse » (et « Fine » avec une épaisseur réduite) : arc continu au dégradé, bout lumineux.</summary>
    private void DrawSmooth(DrawingContext dc, Frame g, double tipRadius)
    {
        DrawTrack(dc, g, g.T);
        if (g.ValueSweep < 0.05) return;

        var count = SegmentCount(g);

        // Halo : même arc, plus épais et très transparent.
        for (var i = 0; g.Glow && i < count; i++)
        {
            var (a0, a1, mid) = Segment(g, i, count, overlap: 0);
            dc.DrawGeometry(null, PenOf(WithAlpha(g.ColorAt(mid), 0x2A), g.T * 2), ArcOf(g.Cx, g.Cy, g.R, a0, a1));
        }

        // Arc de valeur. Chaque segment empiète légèrement sur le suivant pour masquer les jointures
        // d'anticrénelage ; les extrémités arrondies sont des disques posés aux deux bouts.
        for (var i = 0; i < count; i++)
        {
            var (a0, a1, mid) = Segment(g, i, count, overlap: 0.8);
            dc.DrawGeometry(null, PenOf(g.ColorAt(mid), g.T), ArcOf(g.Cx, g.Cy, g.R, a0, a1));
        }

        var tipColor = g.ColorAt(g.End);
        dc.DrawEllipse(BrushOf(g.StartColor), null, g.At(g.Start, g.R), g.T / 2, g.T / 2);
        dc.DrawEllipse(BrushOf(tipColor), null, g.At(g.End, g.R), g.T / 2, g.T / 2);
        DrawTip(dc, g, tipColor, tipRadius);
    }

    /// <summary>Point lumineux au bout de l'arc.</summary>
    private void DrawTip(DrawingContext dc, Frame g, Color color, double radius)
    {
        var tip = g.At(g.End, g.R);
        if (g.Glow)
            dc.DrawEllipse(RadialOf(WithAlpha(color, 0x90), WithAlpha(color, 0x00)), null, tip,
                Math.Max(g.T * 1.25, radius * 2.4), Math.Max(g.T * 1.25, radius * 2.4));
        dc.DrawEllipse(BrushOf(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), null, tip, radius, radius);
    }

    /// <summary>Style « Segments » : blocs séparés façon afficheur LED, allumés jusqu'à la valeur.</summary>
    private void DrawSegments(DrawingContext dc, Frame g)
    {
        var blocks = Math.Max(8, (int)Math.Round(g.Sweep / 10));
        var slot = g.Sweep / blocks;
        var gap = Math.Min(2.6, slot * 0.35);
        var thickness = g.T * 1.15;
        var dim = TrackBrush;

        for (var i = 0; i < blocks; i++)
        {
            var a0 = g.Start + i * slot + gap / 2;
            var a1 = a0 + slot - gap;
            // Le bloc s'allume dès que la valeur dépasse sa moitié : la jauge ne paraît jamais « en retard ».
            var lit = g.ValueSweep > 0.05 && a0 + (a1 - a0) / 2 <= g.End;
            var geometry = ArcOf(g.Cx, g.Cy, g.R, a0, a1);
            if (lit)
            {
                var color = g.ColorAt((a0 + a1) / 2);
                if (g.Glow) dc.DrawGeometry(null, PenOf(WithAlpha(color, 0x30), thickness * 1.9), geometry);
                dc.DrawGeometry(null, PenOf(color, thickness), geometry);
            }
            else
            {
                dc.DrawGeometry(null, PenOf(dim, thickness), geometry);
            }
        }
    }

    /// <summary>Style « Pointillés » : une rangée de points, le dernier point allumé plus gros.</summary>
    private void DrawDots(DrawingContext dc, Frame g)
    {
        var count = Math.Max(10, (int)Math.Round(g.Sweep / 9));
        var radius = Math.Max(1.4, g.T * 0.42);
        var lastLit = -1;
        for (var i = 0; i <= count; i++)
            if (g.ValueSweep > 0.05 && g.Start + g.Sweep * i / count <= g.End + 0.01) lastLit = i;

        for (var i = 0; i <= count; i++)
        {
            var angle = g.Start + g.Sweep * i / count;
            var point = g.At(angle, g.R);
            if (i <= lastLit)
            {
                var color = g.ColorAt(angle);
                var size = i == lastLit ? radius * 1.55 : radius;
                if (g.Glow) dc.DrawEllipse(BrushOf(WithAlpha(color, 0x35)), null, point, size * 1.9, size * 1.9);
                dc.DrawEllipse(BrushOf(color), null, point, size, size);
            }
            else
            {
                dc.DrawEllipse(TrackBrush, null, point, radius * 0.75, radius * 0.75);
            }
        }
    }

    /// <summary>Style « Compteur » : arc fin et aiguille, comme un compte-tours.</summary>
    private void DrawNeedle(DrawingContext dc, Frame g)
    {
        var thin = g with { T = Math.Max(1.5, g.T * 0.45) };
        DrawSmoothArcOnly(dc, thin);

        // L'aiguille part de l'intérieur de l'anneau (le chiffre central reste lisible) jusqu'au-delà de l'arc.
        var angle = g.End;
        var color = g.ColorAt(angle);
        var from = g.At(angle, g.R * 0.74);
        var to = g.At(angle, g.R + g.T * 0.3);
        if (g.Glow)
            dc.DrawLine(PenOf(WithAlpha(color, 0x40), 6, round: true), from, to);
        dc.DrawLine(Frozen(new Pen(BrushOf(color), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Triangle }), from, to);
        dc.DrawEllipse(BrushOf(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), null, from, 2.2, 2.2);
    }

    private void DrawSmoothArcOnly(DrawingContext dc, Frame g)
    {
        DrawTrack(dc, g, g.T);
        if (g.ValueSweep < 0.05) return;
        var count = SegmentCount(g);
        for (var i = 0; i < count; i++)
        {
            var (a0, a1, mid) = Segment(g, i, count, overlap: 0.8);
            dc.DrawGeometry(null, PenOf(g.ColorAt(mid), g.T, round: true), ArcOf(g.Cx, g.Cy, g.R, a0, a1));
        }
    }

    /// <summary>Style « Secteur » : part de disque translucide jusqu'à la valeur, bordée d'un arc fin.</summary>
    private void DrawPie(DrawingContext dc, Frame g)
    {
        var outer = g.R + g.T / 2;
        dc.DrawGeometry(BrushOf(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), null, Sector(g.Cx, g.Cy, outer, g.Start, g.Start + g.Sweep));

        if (g.ValueSweep >= 0.05)
        {
            var tipColor = g.ColorAt(g.End);
            dc.DrawGeometry(RadialOf(WithAlpha(g.StartColor, 0x20), WithAlpha(tipColor, 0x70)), null, Sector(g.Cx, g.Cy, outer, g.Start, g.End));
        }
        DrawSmoothArcOnly(dc, g with { T = Math.Max(1.5, g.T * 0.4), R = outer });
    }

    /// <summary>Style « Néon » : tube fin presque blanc entouré de plusieurs halos colorés, comme une enseigne.</summary>
    private void DrawNeon(DrawingContext dc, Frame g)
    {
        var core = Math.Max(1.6, g.T * 0.32);
        DrawTrack(dc, g, core);
        if (g.ValueSweep < 0.05) return;

        var count = SegmentCount(g);
        // Halos du plus large au plus serré, puis le tube : couleur du dégradé éclaircie vers le blanc au centre.
        var layers = g.Glow
            ? new (double Width, byte Alpha, double White)[] { (g.T * 2.6, 0x14, 0), (g.T * 1.6, 0x26, 0), (g.T * 0.9, 0x60, 0.15), (core, 0xFF, 0.55) }
            : new (double Width, byte Alpha, double White)[] { (g.T * 0.9, 0x70, 0.1), (core, 0xFF, 0.55) };
        foreach (var (width, alpha, white) in layers)
        {
            for (var i = 0; i < count; i++)
            {
                var (a0, a1, mid) = Segment(g, i, count, overlap: 0.8);
                var color = WithAlpha(Lerp(g.ColorAt(mid), Colors.White, white), alpha);
                dc.DrawGeometry(null, PenOf(color, width, round: true), ArcOf(g.Cx, g.Cy, g.R, a0, a1));
            }
        }
        DrawTip(dc, g, g.ColorAt(g.End), core * 0.9);
    }

    /// <summary>Style « Rayons » : traits rayonnants, de plus en plus longs vers la valeur, façon égaliseur.</summary>
    private void DrawRays(DrawingContext dc, Frame g)
    {
        var count = Math.Max(16, (int)Math.Round(g.Sweep / 6));
        var inner = g.R - g.T * 0.6;
        var width = Math.Max(1.4, Math.Min(g.T * 0.45, 2 * Math.PI * g.R * g.Sweep / 360 / count * 0.55));
        for (var i = 0; i <= count; i++)
        {
            var angle = g.Start + g.Sweep * i / count;
            var lit = g.ValueSweep > 0.05 && angle <= g.End + 0.01;
            if (!lit)
            {
                dc.DrawLine(PenOf(TrackBrush, width, round: true), g.At(angle, inner), g.At(angle, inner + g.T * 0.5));
                continue;
            }
            // Les rayons s'allongent en approchant du bout de la valeur.
            var progress = g.ValueSweep <= 0 ? 0 : (angle - g.Start) / g.ValueSweep;
            var length = g.T * (0.7 + 0.9 * progress);
            var color = g.ColorAt(angle);
            var from = g.At(angle, inner);
            var to = g.At(angle, inner + length);
            if (g.Glow)
                dc.DrawLine(PenOf(WithAlpha(color, 0x30), width * 2.4, round: true), from, to);
            dc.DrawLine(PenOf(color, width, round: true), from, to);
        }
    }

    /// <summary>Style « Double » : arc de valeur et, à l'intérieur, un second arc fin qui le suit en écho.</summary>
    private void DrawDouble(DrawingContext dc, Frame g)
    {
        var outer = g with { T = Math.Max(2, g.T * 0.6) };
        DrawSmoothArcOnly(dc, outer);
        var echo = g with { R = g.R - g.T * 1.15, T = Math.Max(1.2, g.T * 0.25), Glow = false };
        if (echo.R <= 2) return;
        DrawTrack(dc, echo, echo.T);
        if (g.ValueSweep < 0.05) return;
        var faded = echo with { StartColor = WithAlpha(g.StartColor, 0x90), EndColor = WithAlpha(g.EndColor, 0x90) };
        DrawSmoothArcOnly(dc, faded);
        DrawTip(dc, outer, g.ColorAt(g.End), outer.T * 0.45);
    }

    /// <summary>Style « Capsule » : rail épais et translucide, valeur plus fine posée à l'intérieur.</summary>
    private void DrawCapsule(DrawingContext dc, Frame g)
    {
        var rail = g.T * 1.45;
        dc.DrawGeometry(null, PenOf(TrackBrush, rail, round: true), ArcOf(g.Cx, g.Cy, g.R, g.Start, g.Start + g.Sweep));
        if (g.ValueSweep < 0.05) return;
        DrawSmoothArcOnly(dc, g with { T = g.T * 0.7 });
        dc.DrawEllipse(BrushOf(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), null, g.At(g.End, g.R), g.T * 0.22, g.T * 0.22);
    }
}
