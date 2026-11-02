using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace SysDeck.Services;

/// <summary>
/// Traduit un <see cref="ThemeSettings"/> en ressources WPF. Appliqué aux ressources de l'application
/// pour le thème global, ou aux ressources d'une seule fenêtre de widget pour un module qui a son
/// propre style : les DynamicResource des widgets trouvent d'abord la valeur de leur fenêtre.
/// </summary>
public static class ThemeService
{
    public static readonly Color DefaultGaugeStart = Color.FromRgb(0x00, 0xE0, 0xFF);
    public static readonly Color DefaultGaugeEnd = Color.FromRgb(0x8B, 0x5C, 0xFF);

    // Couleur de fond par défaut des cartes ; le dégradé (plus clair en haut) en est dérivé.
    private static readonly Color PanelBaseColor = Color.FromRgb(0x23, 0x25, 0x30);
    private static readonly Color DefaultButtonColor = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);

    /// <summary>Dégradés proposés dans les Paramètres (nom, début, fin).</summary>
    public static (string Name, Color Start, Color End)[] GetGaugePresets()
    {
        var windows = GetWindowsAccentColor();
        return new[]
        {
            ("Néon", DefaultGaugeStart, DefaultGaugeEnd),
            ("Plasma", Color.FromRgb(0xFF, 0x8A, 0x00), Color.FromRgb(0xB4, 0x4C, 0xFF)),
            ("Braise", Color.FromRgb(0xFF, 0xC8, 0x00), Color.FromRgb(0xFF, 0x2E, 0x4D)),
            ("Toxique", Color.FromRgb(0x7C, 0xFF, 0x4F), Color.FromRgb(0x00, 0xC2, 0xFF)),
            ("Synthwave", Color.FromRgb(0xFF, 0x4F, 0xD8), Color.FromRgb(0x5C, 0x6C, 0xFF)),
            ("Glace", Color.FromRgb(0xE0, 0xF7, 0xFF), Color.FromRgb(0x2E, 0x8B, 0xFF)),
            ("Windows", windows, ShiftHue(windows, 45)),
        };
    }

    public static Color GetWindowsAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                byte a = (byte)((abgr >> 24) & 0xFF);
                byte b = (byte)((abgr >> 16) & 0xFF);
                byte g = (byte)((abgr >> 8) & 0xFF);
                byte r = (byte)(abgr & 0xFF);
                if (a > 0)
                    return Color.FromRgb(r, g, b);
            }
        }
        catch
        {
            // registre indisponible : on retombe sur la couleur par défaut
        }

        return DefaultGaugeStart;
    }

    /// <summary>Écrit toutes les ressources d'apparence de <paramref name="theme"/> dans <paramref name="target"/>.</summary>
    public static void ApplyTheme(ResourceDictionary target, ThemeSettings theme)
    {
        var start = Parse(theme.GaugeStartColor, DefaultGaugeStart);
        var end = Parse(theme.GaugeEndColor, DefaultGaugeEnd);
        var gradient = Frozen(new LinearGradientBrush(start, end, 0));

        target["GaugeStartColor"] = start;
        target["GaugeEndColor"] = end;
        target["GaugeGradientBrush"] = gradient;
        // Vertical, du bas (début) vers le haut (fin) : pour les curseurs de volume.
        target["GaugeGradientVerticalBrush"] = Frozen(new LinearGradientBrush(end, start, 90));

        // La couleur d'accent de l'interface (survols, menus, icônes) suit le dégradé.
        target["AccentBrush"] = gradient;
        target["AccentBrush2"] = gradient;
        target["AccentColor"] = start;

        target["TextBrush"] = Frozen(new SolidColorBrush(Parse(theme.TextColor, Color.FromRgb(0xE8, 0xE9, 0xEE))));
        target["SubTextBrush"] = Frozen(new SolidColorBrush(Parse(theme.SubTextColor, Color.FromRgb(0x9A, 0x9D, 0xAC))));

        target["TextFont"] = FontService.Resolve(theme.TextFont);
        target["NumberFont"] = FontService.Resolve(theme.NumberFont);

        // Fond des cartes : léger dégradé vertical, plus clair en haut, façon carte de tableau de bord.
        var panel = Parse(theme.PanelColor, PanelBaseColor);
        // Dégradé choisi : deux couleurs et un angle libres ; sinon le léger dégradé dérivé d'origine.
        target["PanelBrush"] = theme.PanelGradient
            ? Frozen(new LinearGradientBrush(
                WithAlpha(panel, theme.PanelOpacity), WithAlpha(Parse(theme.PanelColor2, panel), theme.PanelOpacity), theme.PanelGradientAngle))
            : Frozen(new LinearGradientBrush(
                WithAlpha(Mix(panel, Colors.White, 0.06), theme.PanelOpacity), WithAlpha(Mix(panel, Colors.Black, 0.3), theme.PanelOpacity), 90));
        target["ButtonBrush"] = Frozen(new SolidColorBrush(Parse(theme.ButtonColor, DefaultButtonColor)));

        // Bulles internes : tuiles (TileBrush) et lignes de listes, un peu plus marquées (TileStrongBrush).
        var tile = Parse(theme.TileColor, Colors.White);
        var tileOpacity = theme.ShowTiles ? Math.Clamp(theme.TileOpacity, 0, 1) : 0;
        target["TileBrush"] = Frozen(new SolidColorBrush(WithAlpha(tile, tileOpacity)));
        target["TileStrongBrush"] = Frozen(new SolidColorBrush(WithAlpha(tile, Math.Min(1, tileOpacity * 1.7))));
        target["TileBorderBrush"] = Frozen(new SolidColorBrush(WithAlpha(tile, tileOpacity * 0.75)));
        // Contour plus marqué en haut, comme un reflet de lumière sur l'arête de la carte.
        var border = Parse(theme.BorderColor, Colors.White);
        // Style « Accent » : le contour reprend le dégradé d'accent (gauche vers droite, comme les jauges).
        target["BorderBrush2"] = theme.BorderStyle == "Accent"
            ? Frozen(new LinearGradientBrush(WithAlpha(start, theme.BorderOpacity), WithAlpha(end, theme.BorderOpacity), 0))
            : Frozen(new LinearGradientBrush(
                WithAlpha(border, theme.BorderOpacity), WithAlpha(border, theme.BorderOpacity * 0.25), 90));
        target["CardBorderThickness"] = new Thickness(Math.Clamp(theme.BorderThickness, 0, 12));
        target["CardCornerRadius"] = new CornerRadius(theme.CornerRadius);

        target["GaugeSweepAngle"] = theme.GaugeShape switch
        {
            "Full" => 360.0,
            "Half" => 180.0,
            _ => 270.0
        };
        target["GaugeThicknessScale"] = theme.GaugeThickness;
        target["GaugeDesign"] = string.IsNullOrEmpty(theme.GaugeStyle) ? "Smooth" : theme.GaugeStyle;
        target["GaugeTicks"] = theme.ShowTicks;
        target["GaugeTickDesign"] = string.IsNullOrEmpty(theme.TickStyle) ? "Lines" : theme.TickStyle;
        target["GaugeGlow"] = theme.Glow;
        target["AnimationScale"] = theme.AnimationsEnabled ? theme.AnimationSpeed : 0.0;
        target["AnimationEasing"] = theme.AnimationEasing;
        target["IntroAnimation"] = theme.AnimationsEnabled && theme.IntroAnimation;
    }

    /// <summary>Applique l'opacité du fond et du contour de la barre de raccourcis, indépendamment des autres widgets.</summary>
    public static void ApplyDockOpacity(double panelOpacity, double borderOpacity)
    {
        var resources = Application.Current.Resources;
        resources["DockPanelBrush"] = new SolidColorBrush(WithAlpha(PanelBaseColor, panelOpacity));
        resources["DockBorderBrush"] = new SolidColorBrush(WithAlpha(Colors.White, borderOpacity));
    }

    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex)!;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Courbe d'animation choisie dans les réglages (AnimationEasing) ; null = linéaire.</summary>
    public static IEasingFunction? CreateEasing(string? name) => name switch
    {
        "Elastic" => new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 },
        "Bounce" => new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 2, Bounciness = 3 },
        "Back" => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 },   // dépasse un peu puis revient
        "Sine" => new SineEase { EasingMode = EasingMode.EaseInOut },                   // très douce, lente au départ
        "Snap" => new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 }, // presque instantanée puis se pose
        "Linear" => null,
        _ => new CubicEase { EasingMode = EasingMode.EaseOut }
    };

    /// <summary>Contour lumineux du survol « Glow » : dégradé d'accent plus opaque que le contour normal.</summary>
    public static Brush CreateHoverGlowBrush(ThemeSettings theme)
        => Frozen(new LinearGradientBrush(
            WithAlpha(Parse(theme.GaugeStartColor, DefaultGaugeStart), 0.95), WithAlpha(Parse(theme.GaugeEndColor, DefaultGaugeEnd), 0.95), 0));

    private static Color Mix(Color a, Color b, double t)
        => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Dégradé propre à un élément de module : posé dans ses ressources, il remplace celui du thème
    /// pour tout ce qu'il contient (jauges, graphes, boutons, accents).
    /// </summary>
    public static void ApplyAccent(ResourceDictionary target, string? startHex, string? endHex)
    {
        string[] keys = { "GaugeStartColor", "GaugeEndColor", "GaugeGradientBrush", "GaugeGradientVerticalBrush", "AccentBrush", "AccentBrush2", "AccentColor" };
        if (string.IsNullOrEmpty(startHex) && string.IsNullOrEmpty(endHex))
        {
            foreach (var key in keys) target.Remove(key);
            return;
        }

        // Une seule couleur choisie : elle sert aux deux bouts du dégradé.
        var start = Parse(startHex ?? endHex, DefaultGaugeStart);
        var end = Parse(endHex ?? startHex, DefaultGaugeEnd);
        var gradient = Frozen(new LinearGradientBrush(start, end, 0));
        target["GaugeStartColor"] = start;
        target["GaugeEndColor"] = end;
        target["GaugeGradientBrush"] = gradient;
        target["GaugeGradientVerticalBrush"] = Frozen(new LinearGradientBrush(end, start, 90));
        target["AccentBrush"] = gradient;
        target["AccentBrush2"] = gradient;
        target["AccentColor"] = start;
    }

    private static Color WithAlpha(Color color, double opacity)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Color ShiftHue(Color c, double degrees)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        double s = max == 0 ? 0 : d / max, v = max;

        h = ((h + degrees) % 360 + 360) % 360;
        double cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
        var (r1, g1, b1) = h switch
        {
            < 60 => (cc, x, 0.0),
            < 120 => (x, cc, 0.0),
            < 180 => (0.0, cc, x),
            < 240 => (0.0, x, cc),
            < 300 => (x, 0.0, cc),
            _ => (cc, 0.0, x)
        };
        return Color.FromRgb((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
