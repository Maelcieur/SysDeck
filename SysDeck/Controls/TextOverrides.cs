using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Services;

namespace SysDeck.Controls;

/// <summary>
/// Remplace le texte ou la couleur d'un TextBlock en mémorisant l'original, pour pouvoir y revenir
/// dès que le réglage est effacé (même principe que <see cref="FontScaler"/> pour les tailles).
/// </summary>
public static class TextOverrides
{
    private static readonly DependencyProperty OriginalTextProperty = DependencyProperty.RegisterAttached(
        "OriginalText", typeof(string), typeof(TextOverrides), new PropertyMetadata(null));

    private static readonly DependencyProperty OriginalForegroundProperty = DependencyProperty.RegisterAttached(
        "OriginalForeground", typeof(object), typeof(TextOverrides), new PropertyMetadata(null));

    public static void ApplyText(TextBlock text, string? replacement)
    {
        var original = (string?)text.GetValue(OriginalTextProperty);
        if (!string.IsNullOrEmpty(replacement))
        {
            if (original is null) text.SetValue(OriginalTextProperty, text.Text);
            text.Text = replacement;
        }
        else if (original is not null)
        {
            text.Text = original;
            text.ClearValue(OriginalTextProperty);
        }
    }

    public static void ApplyColor(TextBlock text, string? hex)
    {
        var original = text.GetValue(OriginalForegroundProperty);
        if (!string.IsNullOrEmpty(hex))
        {
            // La valeur locale d'origine (souvent absente : couleur venue du style) est gardée telle quelle.
            if (original is null) text.SetValue(OriginalForegroundProperty, text.ReadLocalValue(TextBlock.ForegroundProperty));
            var brush = new SolidColorBrush(ThemeService.Parse(hex, Colors.White));
            brush.Freeze();
            text.Foreground = brush;
        }
        else if (original is not null)
        {
            if (original == DependencyProperty.UnsetValue) text.ClearValue(TextBlock.ForegroundProperty);
            else text.SetValue(TextBlock.ForegroundProperty, original);
            text.ClearValue(OriginalForegroundProperty);
        }
    }
}
