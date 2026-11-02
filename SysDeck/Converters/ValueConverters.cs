using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PathToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ShortcutItem { CustomIconPath: { Length: > 0 } custom } => IconExtractor.GetCustomIcon(custom),
        ShortcutItem item => IconExtractor.GetIcon(item.Path),
        string path => IconExtractor.GetIcon(path),
        _ => null
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visible quand l'image (icône du programme ou image choisie) doit s'afficher : aucun symbole choisi,
/// ou logo de marque inconnu. Masquée sinon (le symbole, l'émoji ou le logo prend la place).
/// </summary>
public sealed class ImageFallbackVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string { Length: > 0 } glyph && (!Controls.BrandIcons.IsBrand(glyph) || Controls.BrandIcons.Resolve(glyph) is not null)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MuteGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "\U0001F507" : "\U0001F50A";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Clé de réglage du bouton muet (onglet Boutons) : « muted » quand l'application est coupée, « sound » sinon.</summary>
/// <summary>Clé du bouton d'une sortie audio : « active » pour la sortie par défaut, « use » pour les autres.</summary>
public sealed class AudioUseKeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "active" : "use";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MuteKeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "muted" : "sound";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToOrientationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Orientation.Vertical : Orientation.Horizontal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SpacingToMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        double spacing = values.Length > 0 && values[0] is double d ? d : 6;
        bool vertical = values.Length > 1 && values[1] is bool b && b;
        double half = spacing / 2;
        return vertical ? new Thickness(0, half, 0, half) : new Thickness(half, 0, half, 0);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Coche un RadioButton quand la valeur liée vaut le paramètre ; le cocher écrit le paramètre.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter : System.Windows.Data.Binding.DoNothing;
}

/// <summary>Couleur hexadécimale → pinceau uni.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    // Le convertisseur est rappelé à chaque évaluation de la liaison (champs de couleur saisis en direct) :
    // un pinceau par couleur, figé, plutôt qu'un nouveau à chaque fois. Cache borné pour la saisie libre.
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string ?? "";
        if (Cache.TryGetValue(hex, out var cached)) return cached;

        var brush = new SolidColorBrush(ThemeService.Parse(hex, Colors.Transparent));
        brush.Freeze();
        if (Cache.Count > 256) Cache.Clear();
        Cache[hex] = brush;
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Deux couleurs hexadécimales (début, fin) → dégradé horizontal.</summary>
public sealed class HexGradientConverter : IMultiValueConverter
{
    private static readonly Dictionary<(string, string), LinearGradientBrush> Cache = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var start = values.ElementAtOrDefault(0) as string ?? "";
        var end = values.ElementAtOrDefault(1) as string ?? "";
        if (Cache.TryGetValue((start, end), out var cached)) return cached;

        var brush = new LinearGradientBrush(
            ThemeService.Parse(start, ThemeService.DefaultGaugeStart),
            ThemeService.Parse(end, ThemeService.DefaultGaugeEnd), 0);
        brush.Freeze();
        if (Cache.Count > 256) Cache.Clear();
        Cache[(start, end)] = brush;
        return brush;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Chemin d'image → pinceau de remplissage (aperçu de l'image de fond d'un module).</summary>
public sealed class ImagePathToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => WidgetWindow.LoadImage(value as string) is { } image ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Multiplicateur → texte "×1,5".</summary>
public sealed class MultiplierConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"×{d:0.0#}" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Nombre ↔ texte à la française ("0,1952") ; la saisie accepte aussi le point.</summary>
public sealed class DecimalTextConverter : IValueConverter
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d.ToString("0.####", Fr) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => double.TryParse((value as string)?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? d
            : DependencyProperty.UnsetValue;
}

/// <summary>Date ↔ texte "28/01/2026" ; vide = null (valeur par défaut).</summary>
public sealed class DateTextConverter : IValueConverter
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime d ? d.ToString("dd/MM/yyyy", Fr) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return DateTime.TryParse(text, Fr, DateTimeStyles.None, out var d) && d <= DateTime.Now ? d.Date : DependencyProperty.UnsetValue;
    }
}

/// <summary>Texte vide ou null → visible (indication affichée dans une zone de saisie vide).</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Texte → majuscules (titres de section des Paramètres, saisis en casse normale dans le XAML).</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.ToUpper(CultureInfo.GetCultureInfo("fr-FR")) ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Heure décimale → texte "22 h 30".</summary>
public sealed class HourConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double d) return "";
        var minutes = (int)Math.Round(d * 60);
        return $"{minutes / 60} h {minutes % 60:00}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Opacité 0–1 → texte "75 %".</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:0} %" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
