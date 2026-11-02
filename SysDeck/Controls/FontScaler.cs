using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SysDeck.Controls;

/// <summary>
/// Agrandit ou réduit tous les textes d'une fenêtre de module d'un même facteur, en gardant leurs
/// proportions : la taille d'origine de chaque texte est mémorisée, puis multipliée par le facteur.
/// Réappliquer est sans risque (on repart toujours de la taille d'origine) : la fenêtre le refait
/// quand ses listes régénèrent leurs éléments.
/// </summary>
public static class FontScaler
{
    private static readonly DependencyProperty BaseSizeProperty = DependencyProperty.RegisterAttached(
        "BaseSize", typeof(double), typeof(FontScaler), new PropertyMetadata(double.NaN));

    /// <summary>
    /// Applique <paramref name="scale"/> à tous les textes sous <paramref name="root"/>, multiplié pour
    /// chaque texte par <paramref name="partScale"/> (taille propre au texte, 1 par défaut).
    /// </summary>
    public static void ApplyAll(DependencyObject root, double scale, Func<TextBlock, double>? partScale = null)
    {
        if (root is TextBlock text) Apply(text, scale * (partScale?.Invoke(text) ?? 1));
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            ApplyAll(VisualTreeHelper.GetChild(root, i), scale, partScale);
    }

    private static void Apply(TextBlock text, double scale)
    {
        ApplySize(text, TextBlock.FontSizeProperty, scale);
        foreach (var inline in text.Inlines)
            ApplyInline(inline, scale);
    }

    private static void ApplyInline(Inline inline, double scale)
    {
        // Seuls les morceaux de texte qui ont leur propre taille sont touchés : les autres héritent déjà du TextBlock.
        if (!double.IsNaN((double)inline.GetValue(BaseSizeProperty))
            || inline.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue)
            ApplySize(inline, TextElement.FontSizeProperty, scale);

        if (inline is Span span)
            foreach (var child in span.Inlines)
                ApplyInline(child, scale);
    }

    private static void ApplySize(DependencyObject element, DependencyProperty fontSize, double scale)
    {
        var baseSize = (double)element.GetValue(BaseSizeProperty);
        if (double.IsNaN(baseSize))
        {
            if (Math.Abs(scale - 1) < 0.001) return; // rien à faire tant que la taille n'est pas modifiée
            baseSize = (double)element.GetValue(fontSize);
            element.SetValue(BaseSizeProperty, baseSize);
        }
        element.SetValue(fontSize, Math.Max(4, baseSize * scale));
    }
}
