using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Services;

namespace SysDeck.Controls;

/// <param name="Disc">Bouton rond au dégradé (lecture / pause) : le fond remplace le dégradé, le survol (baisse d'opacité) n'est pas réglable.</param>
public sealed record ButtonPartInfo(string Key, string Label, double IconSize = 14, bool Disc = false)
{
    public bool HasHover => !Disc;
}

/// <summary>
/// Marque un bouton d'un module (ButtonPart.Key="hide"…) pour qu'il puisse être masqué, changer de fond, de
/// couleur de texte, de couleur au survol et d'icône depuis l'onglet « Boutons » de Personnaliser (voir
/// <see cref="ButtonPartStyle"/>). Sans réglage, le bouton garde exactement son rendu d'origine.
/// </summary>
/// <remarks>
/// Couleurs : appliquées en redéfinissant, dans les ressources du bouton seul, les pinceaux que son style
/// utilise déjà (ButtonBrush = fond, AccentBrush = survol, TextBrush et GaugeGradientBrush = texte et icônes) ;
/// le survol, le style et le modèle d'origine continuent donc de fonctionner tels quels.
/// Lecture / pause : deux clés (« play » quand rien ne joue, « pause » pendant la lecture) car le bouton
/// change d'icône ; la clé est mise à jour par le widget, chaque état a donc son propre réglage.
/// Muet : « muted » (application coupée, le bouton montre 🔇) et « sound » (son actif, le bouton montre 🔊).
/// </remarks>
public static class ButtonPart
{
    // ---------- Clé ----------

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ButtonPart), new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    /// <summary>La clé d'un bouton a changé (lecture ↔ pause, muet ↔ son) : la carte du module réapplique le style.</summary>
    public static readonly RoutedEvent KeyChangedEvent = EventManager.RegisterRoutedEvent(
        "KeyChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ButtonPart));

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is not null && d is UIElement element)
            element.RaiseEvent(new RoutedEventArgs(KeyChangedEvent));
    }

    // ---------- Affichage (masquage demandé par l'utilisateur + visibilité voulue par le widget) ----------

    public static readonly DependencyProperty HiddenProperty = DependencyProperty.RegisterAttached(
        "Hidden", typeof(bool), typeof(ButtonPart), new PropertyMetadata(false, OnVisibilityInputChanged));

    public static readonly DependencyProperty WantedProperty = DependencyProperty.RegisterAttached(
        "Wanted", typeof(bool), typeof(ButtonPart), new PropertyMetadata(true, OnVisibilityInputChanged));

    public static bool GetHidden(DependencyObject d) => (bool)d.GetValue(HiddenProperty);
    public static void SetHidden(DependencyObject d, bool value) => d.SetValue(HiddenProperty, value);

    /// <summary>Visibilité voulue par le widget lui-même (ex. « Fermer Spotify » seulement si Spotify joue) ; sans effet si le bouton est masqué.</summary>
    public static bool GetWanted(DependencyObject d) => (bool)d.GetValue(WantedProperty);
    public static void SetWanted(DependencyObject d, bool value) => d.SetValue(WantedProperty, value);

    private static void OnVisibilityInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
            element.Visibility = GetWanted(element) && !GetHidden(element) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Couleur de l'icône du bouton rond (lecture / pause), lue par le widget ----------

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.RegisterAttached(
        "IconBrush", typeof(Brush), typeof(ButtonPart), new PropertyMetadata(null, OnIconBrushChanged));

    public static Brush? GetIconBrush(DependencyObject d) => (Brush?)d.GetValue(IconBrushProperty);
    public static void SetIconBrush(DependencyObject d, Brush? value) => d.SetValue(IconBrushProperty, value);

    public static readonly RoutedEvent IconBrushChangedEvent = EventManager.RegisterRoutedEvent(
        "IconBrushChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ButtonPart));

    private static void OnIconBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element) element.RaiseEvent(new RoutedEventArgs(IconBrushChangedEvent));
    }

    // ---------- Boutons personnalisables ----------

    /// <summary>Boutons personnalisables de chaque module, dans l'ordre d'affichage.</summary>
    public static readonly Dictionary<string, ButtonPartInfo[]> ByModule = new()
    {
        ["audio"] = new ButtonPartInfo[]
        {
            new("active", "Sortie par défaut (en cours d'utilisation)"),
            new("use", "Autres sorties (bouton « Utiliser »)"),
            new("hide", "Bouton ✕ (masquer le périphérique)", 12),
        },
        ["volumemixer"] = new ButtonPartInfo[]
        {
            new("muted", "Muet (application coupée)"),
            new("sound", "Son (application active)"),
            new("hide", "Bouton ✕ (masquer l'application)", 12),
        },
        ["media"] = new ButtonPartInfo[]
        {
            new("previous", "Précédent", 12),
            new("play", "Lecture (rien ne joue)", 16, Disc: true),
            new("pause", "Pause (pendant la lecture)", 16, Disc: true),
            new("next", "Suivant", 12),
            new("stopSpotify", "Fermer Spotify", 12),
        },
    };

    // ---------- Application d'un style ----------

    private sealed record Saved(object? Content, string? Glyph, object? Icon);

    private static readonly DependencyProperty SavedProperty = DependencyProperty.RegisterAttached(
        "Saved", typeof(Saved), typeof(ButtonPart), new PropertyMetadata(null));

    private static readonly DependencyProperty TouchedProperty = DependencyProperty.RegisterAttached(
        "Touched", typeof(bool), typeof(ButtonPart), new PropertyMetadata(false));

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    /// <summary>Pinceau figé et partagé pour une couleur (même instance d'un appel à l'autre : aucune notification inutile).</summary>
    private static Brush? BrushOf(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        if (!Brushes.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush(ThemeService.Parse(hex, Colors.White));
            brush.Freeze();
            Brushes[hex] = brush;
        }
        return brush;
    }

    private static void SetResource(ResourceDictionary resources, string key, Brush? brush)
    {
        if (brush is null) resources.Remove(key);
        else resources[key] = brush;
    }

    /// <summary>
    /// Applique <paramref name="style"/> au bouton (null ou par défaut : rend son aspect d'origine, s'il avait été modifié).
    /// Peut être appelée autant de fois que nécessaire.
    /// </summary>
    public static void Apply(Button button, ButtonPartInfo info, ButtonPartStyle? style)
    {
        var custom = style is { IsDefault: false };
        if (!custom && !(bool)button.GetValue(TouchedProperty)) return; // rien n'a jamais été changé : on ne touche à rien
        button.SetValue(TouchedProperty, custom);

        var resources = button.Resources;
        if (info.Disc)
        {
            // Disque au dégradé : le fond remplace le dégradé ; l'icône est dessinée par le widget (IconBrush).
            SetResource(resources, "GaugeGradientBrush", BrushOf(style?.Background));
            SetIconBrush(button, BrushOf(style?.Foreground));
        }
        else
        {
            SetResource(resources, "ButtonBrush", BrushOf(style?.Background));
            SetResource(resources, "AccentBrush", BrushOf(style?.HoverBackground));
            var ink = BrushOf(style?.Foreground);
            SetResource(resources, "TextBrush", ink);
            SetResource(resources, "GaugeGradientBrush", ink); // icônes au dégradé du jeu « Néon »
        }

        ApplyGlyph(button, info, style);
        SetHidden(button, style?.Hidden == true);
    }

    private static void ApplyGlyph(Button button, ButtonPartInfo info, ButtonPartStyle? style)
    {
        var saved = (Saved?)button.GetValue(SavedProperty);
        var glyph = style?.Glyph;

        // Déjà affichée : rien à refaire (évite de recréer l'icône à chaque rafraîchissement).
        if (!string.IsNullOrEmpty(glyph) && !BrandIcons.IsBrand(glyph) && saved is not null && saved.Glyph == glyph && ReferenceEquals(button.Content, saved.Icon))
            return;

        if (!string.IsNullOrEmpty(glyph) && ShortcutIcons.CreateCustom(glyph, info.IconSize, BrandIcons.IsBrand(glyph) ? style?.Foreground : null) is { } icon)
        {
            // On garde le contenu d'origine, une seule fois, pour pouvoir le rétablir.
            var original = saved is null ? button.Content : saved.Content;
            button.SetValue(SavedProperty, new Saved(original, glyph, icon));
            button.Content = icon;
        }
        else if (saved is not null)
        {
            button.Content = saved.Content;
            button.ClearValue(SavedProperty);
        }
    }
}
