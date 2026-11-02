using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SysDeck.Controls;

/// <summary>
/// Jauge en ligne horizontale : piste discrète et remplissage au dégradé du thème, animé comme
/// <see cref="RadialGauge"/> (montée depuis zéro à l'apparition, transitions adoucies ensuite).
/// </summary>
public sealed class LinearGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LinearGauge), new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(LinearGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LinearGauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(
        nameof(StartColor), typeof(Color), typeof(LinearGauge),
        new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE0, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(
        nameof(EndColor), typeof(Color), typeof(LinearGauge),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8B, 0x5C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowEnabledProperty = DependencyProperty.Register(
        nameof(GlowEnabled), typeof(bool), typeof(LinearGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Multiplicateur de durée des animations ; 0 = pas d'animation.</summary>
    public static readonly DependencyProperty AnimationScaleProperty = DependencyProperty.Register(
        nameof(AnimationScale), typeof(double), typeof(LinearGauge), new PropertyMetadata(1.0));

    /// <summary>
    /// Style de dessin, comme les jauges circulaires : "Segments", "Thin", "Dots", "Neon", "Rays" (barres d'égaliseur)
    /// et "Capsule" ont leur version en ligne ; les autres sont lisses.
    /// </summary>
    public static readonly DependencyProperty DesignProperty = DependencyProperty.Register(
        nameof(Design), typeof(string), typeof(LinearGauge),
        new FrameworkPropertyMetadata("Smooth", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Design { get => (string)GetValue(DesignProperty); set => SetValue(DesignProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Color StartColor { get => (Color)GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }
    public Color EndColor { get => (Color)GetValue(EndColorProperty); set => SetValue(EndColorProperty, value); }
    public bool GlowEnabled { get => (bool)GetValue(GlowEnabledProperty); set => SetValue(GlowEnabledProperty, value); }
    public double AnimationScale { get => (double)GetValue(AnimationScaleProperty); set => SetValue(AnimationScaleProperty, value); }

    private bool _hasAppeared;

    public LinearGauge()
    {
        Loaded += (_, _) =>
        {
            if (_hasAppeared) return;
            _hasAppeared = true;
            AnimateTo(Value, TimeSpan.FromMilliseconds(900));
        };
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (LinearGauge)d;
        if (gauge._hasAppeared)
            gauge.AnimateTo((double)e.NewValue, TimeSpan.FromMilliseconds(650));
    }

    private void AnimateTo(double target, TimeSpan duration)
    {
        var scaled = duration * Math.Max(0, AnimationScale);
        if (scaled <= TimeSpan.FromMilliseconds(16))
        {
            BeginAnimation(DisplayValueProperty, null);
            DisplayValue = target;
            return;
        }

        var animation = new DoubleAnimation(target, scaled) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(animation, 40);
        BeginAnimation(DisplayValueProperty, animation);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, 6);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var fraction = Maximum > 0 ? Math.Clamp(DisplayValue / Maximum, 0, 1) : 0;
        var track = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        track.Freeze();

        switch (Design)
        {
            case "Segments":
                RenderBlocks(dc, w, h, fraction, track, round: false);
                return;
            case "Dots":
                RenderBlocks(dc, w, h, fraction, track, round: true);
                return;
            case "Thin":
                RenderThin(dc, w, h, fraction, track);
                return;
            case "Neon":
                RenderNeon(dc, w, h, fraction, track);
                return;
            case "Rays":
                RenderBars(dc, w, h, fraction, track);
                return;
            case "Capsule":
                RenderCapsule(dc, w, h, fraction, track);
                return;
        }

        var radius = h / 2;
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), radius, radius);
        if (fraction <= 0) return;
        var fillWidth = Math.Max(h, w * fraction); // jamais plus étroit qu'un point rond

        // Dégradé étalé sur toute la largeur : la couleur au bout indique où en est la jauge.
        var fill = new LinearGradientBrush(StartColor, EndColor, new Point(0, 0), new Point(w / fillWidth, 0));
        fill.Freeze();

        if (GlowEnabled)
        {
            var glow = new SolidColorBrush(Color.FromArgb(0x38, EndColor.R, EndColor.G, EndColor.B));
            glow.Freeze();
            dc.DrawRoundedRectangle(glow, null, new Rect(-1.5, -1.5, fillWidth + 3, h + 3), radius + 1.5, radius + 1.5);
        }
        dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, fillWidth, h), radius, radius);
    }

    private Color ColorAt(double x) => DrawingHelpers.Lerp(StartColor, EndColor, x);

    /// <summary>« Segments » (blocs) ou « Pointillés » (points) allumés jusqu'à la valeur.</summary>
    private void RenderBlocks(DrawingContext dc, double w, double h, double fraction, Brush track, bool round)
    {
        var count = Math.Max(8, (int)(w / (round ? h * 1.8 : h * 2.2)));
        var slot = w / count;
        var gap = round ? 0 : Math.Min(3, slot * 0.3);
        for (var i = 0; i < count; i++)
        {
            var x = i * slot + gap / 2;
            var lit = fraction > 0.001 && (i + 0.5) / count <= fraction;
            Brush brush = lit ? new SolidColorBrush(ColorAt((i + 0.5) / count)) : track;
            brush.Freeze();
            if (round)
            {
                var center = new Point(x + slot / 2, h / 2);
                var r = lit ? h / 2 : h / 2.8;
                dc.DrawEllipse(brush, null, center, r, r);
            }
            else
            {
                if (lit && GlowEnabled)
                {
                    var glow = new SolidColorBrush(DrawingHelpers.WithAlpha(ColorAt((i + 0.5) / count), 0x35));
                    glow.Freeze();
                    dc.DrawRoundedRectangle(glow, null, new Rect(x - 1.5, -1.5, slot - gap + 3, h + 3), 2.5, 2.5);
                }
                dc.DrawRoundedRectangle(brush, null, new Rect(x, 0, slot - gap, h), 1.5, 1.5);
            }
        }
    }

    /// <summary>« Fine » : trait fin et point lumineux à la valeur.</summary>
    private void RenderThin(DrawingContext dc, double w, double h, double fraction, Brush track)
    {
        var line = Math.Max(1.5, h * 0.35);
        var y = h / 2 - line / 2;
        dc.DrawRoundedRectangle(track, null, new Rect(0, y, w, line), line / 2, line / 2);
        if (fraction <= 0.001) return;

        var x = w * fraction;
        var fill = new LinearGradientBrush(StartColor, ColorAt(fraction), 0);
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, new Rect(0, y, x, line), line / 2, line / 2);

        var tipColor = ColorAt(fraction);
        if (GlowEnabled)
        {
            var halo = new RadialGradientBrush(DrawingHelpers.WithAlpha(tipColor, 0x90), DrawingHelpers.WithAlpha(tipColor, 0));
            halo.Freeze();
            dc.DrawEllipse(halo, null, new Point(x, h / 2), h * 1.3, h * 1.3);
        }
        var dot = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
        dot.Freeze();
        dc.DrawEllipse(dot, null, new Point(x, h / 2), h * 0.45, h * 0.45);
    }

    /// <summary>« Néon » : tube fin presque blanc dans un halo coloré.</summary>
    private void RenderNeon(DrawingContext dc, double w, double h, double fraction, Brush track)
    {
        var line = Math.Max(1.5, h * 0.4);
        var y = h / 2 - line / 2;
        dc.DrawRoundedRectangle(track, null, new Rect(0, y, w, line), line / 2, line / 2);
        if (fraction <= 0.001) return;

        var x = Math.Max(line, w * fraction);
        if (GlowEnabled)
        {
            foreach (var (grow, alpha) in new[] { (h * 0.9, (byte)0x1C), (h * 0.45, (byte)0x40) })
            {
                var halo = new LinearGradientBrush(DrawingHelpers.WithAlpha(StartColor, alpha), DrawingHelpers.WithAlpha(ColorAt(fraction), alpha), 0);
                halo.Freeze();
                dc.DrawRoundedRectangle(halo, null, new Rect(-grow / 2, y - grow / 2, x + grow, line + grow), (line + grow) / 2, (line + grow) / 2);
            }
        }
        var core = new LinearGradientBrush(DrawingHelpers.Lerp(StartColor, Colors.White, 0.55), DrawingHelpers.Lerp(ColorAt(fraction), Colors.White, 0.55), 0);
        core.Freeze();
        dc.DrawRoundedRectangle(core, null, new Rect(0, y, x, line), line / 2, line / 2);
    }

    /// <summary>« Rayons » en ligne : barres verticales d'égaliseur, plus hautes en approchant de la valeur.</summary>
    private void RenderBars(DrawingContext dc, double w, double h, double fraction, Brush track)
    {
        var count = Math.Max(10, (int)(w / Math.Max(3, h * 0.9)));
        var slot = w / count;
        var bar = Math.Max(1.2, slot * 0.55);
        for (var i = 0; i < count; i++)
        {
            var x = i * slot + (slot - bar) / 2;
            var position = (i + 0.5) / count;
            var lit = fraction > 0.001 && position <= fraction;
            var height = lit ? h * (0.45 + 0.55 * position / fraction) : h * 0.3;
            Brush brush = lit ? new SolidColorBrush(ColorAt(position)) : track;
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, new Rect(x, h - height, bar, height), bar / 2, bar / 2);
        }
    }

    /// <summary>« Capsule » : rail épais translucide, valeur plus fine posée à l'intérieur.</summary>
    private void RenderCapsule(DrawingContext dc, double w, double h, double fraction, Brush track)
    {
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        if (fraction <= 0.001) return;
        var inset = Math.Max(1, h * 0.22);
        var inner = h - inset * 2;
        var x = Math.Max(inner, (w - inset * 2) * fraction);
        var fill = new LinearGradientBrush(StartColor, ColorAt(fraction), 0);
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, new Rect(inset, inset, x, inner), inner / 2, inner / 2);
    }
}
