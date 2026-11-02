using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SysDeck.Controls;

namespace SysDeck;

/// <summary>
/// Propriétés attachées lues par l'index de recherche des Paramètres (voir SettingsWindow.Search.cs).
/// Posées sur une carte (<c>Border</c> au style Card) pour enrichir ce que l'index déduit tout seul
/// de son titre et de ses textes.
/// </summary>
public static class SettingsSearch
{
    /// <summary>Mots-clés ou synonymes (séparés par des espaces) qui mènent à la carte, ex. « prix tarif kwh ».</summary>
    public static readonly DependencyProperty KeywordsProperty = DependencyProperty.RegisterAttached(
        "Keywords", typeof(string), typeof(SettingsSearch), new PropertyMetadata(null));

    public static string? GetKeywords(DependencyObject d) => (string?)d.GetValue(KeywordsProperty);
    public static void SetKeywords(DependencyObject d, string? value) => d.SetValue(KeywordsProperty, value);

    /// <summary>
    /// Module auquel la carte est propre (onglet Général de Personnaliser) : le résultat sélectionne ce module,
    /// sinon la carte n'apparaîtrait pas (elle est masquée pour les autres modules).
    /// </summary>
    public static readonly DependencyProperty ModuleProperty = DependencyProperty.RegisterAttached(
        "Module", typeof(string), typeof(SettingsSearch), new PropertyMetadata(null));

    public static string? GetModule(DependencyObject d) => (string?)d.GetValue(ModuleProperty);
    public static void SetModule(DependencyObject d, string? value) => d.SetValue(ModuleProperty, value);

    /// <summary>Titre à utiliser quand la carte n'a pas de titre visible (ou pour en imposer un autre).</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(SettingsSearch), new PropertyMetadata(null));

    public static string? GetTitle(DependencyObject d) => (string?)d.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject d, string? value) => d.SetValue(TitleProperty, value);

    /// <summary>Vrai : le texte de cet élément n'est pas indexé (bandeaux répétés, libellés sans intérêt).</summary>
    public static readonly DependencyProperty IgnoreProperty = DependencyProperty.RegisterAttached(
        "Ignore", typeof(bool), typeof(SettingsSearch), new PropertyMetadata(false));

    public static bool GetIgnore(DependencyObject d) => (bool)d.GetValue(IgnoreProperty);
    public static void SetIgnore(DependencyObject d, bool value) => d.SetValue(IgnoreProperty, value);

    /// <summary>Minuscules sans accents : « Électricité » et « electricite » se retrouvent.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c == '’' ? '\'' : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Mots normalisés entourés d'espaces (ponctuation = séparateur) : une recherche « cle » trouve « clé API »
    /// mais pas « cercle » ; on compare donc des débuts de mots.
    /// </summary>
    public static string Tokenize(string? text)
    {
        var sb = new StringBuilder(" ");
        foreach (var c in Normalize(text))
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return sb.Append(' ').ToString();
    }

    /// <summary>
    /// Textes visibles d'un élément (descriptions, libellés, puces) pour la correspondance. Les listes générées
    /// et les éditeurs de thème sont ignorés : ces derniers sont indexés carte par carte à part.
    /// </summary>
    public static void CollectText(DependencyObject root, StringBuilder sb)
    {
        if (root is ThemeEditor or ItemsControl || GetIgnore(root)) return;

        switch (root)
        {
            case SettingRow row:
                // Libellé et description de la ligne, puis son contrôle (texte de puces, etc.).
                sb.Append(row.Label).Append(' ').Append(row.Description).Append(' ');
                if (row.Content is DependencyObject inner) CollectText(inner, sb);
                else if (row.Content is string rowText) sb.Append(rowText).Append(' ');
                return;
            case TextBlock textBlock:
                sb.Append(textBlock.Text).Append(' ');
                return;
            case ContentControl { Content: string text }:
                sb.Append(text).Append(' ');
                return;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject element) CollectText(element, sb);
            else if (child is string text) sb.Append(text).Append(' ');
        }
    }
}

/// <summary>Contour animé d'une seconde environ, posé par-dessus un élément pour le repérer après une recherche.</summary>
internal sealed class FlashAdorner : Adorner
{
    private readonly Pen _pen;
    private readonly double _radius;

    private FlashAdorner(FrameworkElement element, Brush brush) : base(element)
    {
        IsHitTestVisible = false;
        // Un adorneur plutôt qu'un changement de bordure : les cartes intégrées n'ont plus de bordure.
        _pen = new Pen(brush, 2.5);
        _radius = (element as Border)?.CornerRadius.TopLeft + 3 ?? 8;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = AdornedElement.RenderSize;
        drawingContext.DrawRoundedRectangle(null, _pen, new Rect(-3, -3, size.Width + 6, size.Height + 6), _radius, _radius);
    }

    public static void Flash(FrameworkElement element)
    {
        var layer = AdornerLayer.GetAdornerLayer(element);
        if (layer is null) return;

        var brush = element.TryFindResource("UiAccent") as Brush ?? Brushes.White;
        var adorner = new FlashAdorner(element, brush) { Opacity = 0 };
        layer.Add(adorner);

        // Deux pulsations puis extinction, le tout en ~1,2 s.
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var (seconds, value) in new[] { (0.0, 0.0), (0.15, 1.0), (0.4, 0.25), (0.65, 1.0), (1.2, 0.0) })
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(value, TimeSpan.FromSeconds(seconds)));
        animation.Completed += (_, _) => layer.Remove(adorner);
        adorner.BeginAnimation(OpacityProperty, animation);
    }
}
