using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Models;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Controls;

/// <summary>
/// Icône d'une tuile ou d'une ligne de liste : symbole Segoe, émoji ou image. Priorité :
/// <see cref="ShortcutItem.IconGlyph"/>, puis <see cref="ShortcutItem.CustomIconPath"/>, puis l'icône détectée du programme.
/// </summary>
public static class ShortcutIcons
{
    public const string SymbolFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>Symboles proposés, par catégorie : glyphe Segoe Fluent Icons / MDL2 et libellé (vérifiés par un test).</summary>
    public static readonly IReadOnlyList<(string Category, IReadOnlyList<(string Glyph, string Name)> Items)> Library =
        new (string, IReadOnlyList<(string, string)>)[]
        {
            ("Médias", new (string, string)[]
            {
                ("\uE768", "Lecture"), ("\uE769", "Pause"), ("\uE71A", "Stop"), ("\uE893", "Suivant"), ("\uE892", "Précédent"),
                ("\uE8B1", "Aléatoire"), ("\uE8EE", "Répéter"), ("\uE8ED", "Répéter un titre"), ("\uE714", "Vidéo"), ("\uE8B2", "Films"),
                ("\uE8D6", "Musique"), ("\uE7F6", "Casque"), ("\uE7F5", "Haut-parleurs"), ("\uE722", "Caméra"), ("\uE91B", "Photo"),
                ("\uE7F4", "Écran"), ("\uE790", "Couleurs")
            }),
            ("Son", new (string, string)[]
            {
                ("\uE767", "Volume"), ("\uE74F", "Muet"), ("\uE993", "Volume bas"), ("\uE994", "Volume moyen"), ("\uE995", "Volume fort"),
                ("\uE720", "Micro"), ("\uF781", "Micro coupé")
            }),
            ("Édition", new (string, string)[]
            {
                ("\uE8C8", "Copier"), ("\uE77F", "Coller"), ("\uE8C6", "Couper"), ("\uE7A7", "Annuler"), ("\uE7A6", "Rétablir"),
                ("\uE74E", "Enregistrer"), ("\uE70F", "Modifier"), ("\uE8B3", "Tout sélectionner"), ("\uE8DD", "Gras"), ("\uE8DB", "Italique"),
                ("\uE8DC", "Souligné"), ("\uE8D2", "Police"), ("\uE7E6", "Surligner"), ("\uE721", "Rechercher"), ("\uE8A3", "Zoom avant"),
                ("\uE71F", "Zoom arrière"), ("\uE924", "Capture d'écran"), ("\uE749", "Imprimer"), ("\uE710", "Ajouter"), ("\uE738", "Retirer"),
                ("\uE74D", "Corbeille"), ("\uE73E", "Valider"), ("\uE711", "Fermer")
            }),
            ("Système", new (string, string)[]
            {
                ("\uE713", "Paramètres"), ("\uE7E8", "Alimentation"), ("\uE708", "Nuit"), ("\uE706", "Luminosité"), ("\uE770", "PC"),
                ("\uE7F8", "Portable"), ("\uE9D9", "Diagnostic"), ("\uE72E", "Verrou"), ("\uE785", "Déverrouillé"), ("\uE8D7", "Clé"),
                ("\uE7BA", "Attention"), ("\uE946", "Information"), ("\uE81C", "Historique"), ("\uE823", "Horloge"), ("\uE916", "Chronomètre"),
                ("\uE787", "Calendrier"), ("\uEA8F", "Notification"), ("\uE72C", "Actualiser"), ("\uE895", "Synchroniser"), ("\uE7C4", "Vue des tâches"),
                ("\uE740", "Plein écran"), ("\uE73F", "Fenêtré"), ("\uE921", "Réduire"), ("\uE922", "Agrandir"), ("\uE71D", "Applications"),
                ("\uE80F", "Accueil"), ("\uE700", "Menu"), ("\uE712", "Plus"), ("\uE765", "Clavier"), ("\uE962", "Souris")
            }),
            ("Réseau", new (string, string)[]
            {
                ("\uE701", "Wi-Fi"), ("\uE702", "Bluetooth"), ("\uE704", "VPN"), ("\uE709", "Mode avion"), ("\uE774", "Navigateur"),
                ("\uE753", "Nuage"), ("\uE839", "Ethernet"), ("\uE968", "Réseau"), ("\uE71B", "Lien"), ("\uE72D", "Partager"),
                ("\uE896", "Télécharger"), ("\uE898", "Téléverser")
            }),
            ("Jeux et loisirs", new (string, string)[]
            {
                ("\uE7FC", "Jeu"), ("\uE990", "Console"), ("\uE945", "Éclair"), ("\uE734", "Étoile"), ("\uE735", "Étoile pleine"),
                ("\uEB51", "Cœur"), ("\uEB52", "Cœur plein"), ("\uE7C1", "Drapeau"), ("\uE8EF", "Calculatrice"), ("\uE9CA", "Thermomètre"),
                ("\uE707", "Repère"), ("\uE909", "Monde"), ("\uE7BE", "Études"), ("\uE719", "Boutique"), ("\uE7BF", "Panier"),
                ("\uE825", "Banque"), ("\uE8F1", "Bibliothèque"), ("\uE736", "Livre"), ("\uE771", "Personnaliser")
            }),
            ("Communication", new (string, string)[]
            {
                ("\uE715", "Courrier"), ("\uE8BD", "Discussion"), ("\uE717", "Téléphone"), ("\uE77B", "Contact"), ("\uE716", "Personnes"),
                ("\uE910", "Arobase"), ("\uE724", "Envoyer un message"), ("\uE718", "Épingle")
            }),
            ("Fichiers", new (string, string)[]
            {
                ("\uE8B7", "Dossier"), ("\uE838", "Dossier ouvert"), ("\uE8A5", "Document"), ("\uE8E5", "Ouvrir"), ("\uE7C3", "Page"),
                ("\uE7B8", "Archive"), ("\uE8D4", "Profil"), ("\uE8A7", "Nouvelle fenêtre")
            }),
            ("Flèches", new (string, string)[]
            {
                ("\uE72B", "Retour"), ("\uE72A", "Avancer"), ("\uE74A", "Haut"), ("\uE74B", "Bas"), ("\uE70E", "Chevron haut"),
                ("\uE70D", "Chevron bas"), ("\uE76B", "Chevron gauche"), ("\uE76C", "Chevron droit"), ("\uE8AB", "Basculer"), ("\uE7AD", "Pivoter")
            })
        };

    /// <summary>Tous les symboles, sans doublon.</summary>
    public static IEnumerable<(string Glyph, string Name)> Symbols
        => Library.SelectMany(c => c.Items).DistinctBy(i => i.Glyph);

    /// <summary>Vrai pour un glyphe de police de symboles (zone d'usage privé), faux pour un émoji.</summary>
    public static bool IsSymbol(string glyph) => glyph.Length == 1 && glyph[0] is >= '\uE000' and <= '\uF8FF';

    /// <summary>Contrôle de <paramref name="size"/> pixels de côté montrant l'icône de la tuile (null s'il n'y en a aucune).</summary>
    public static FrameworkElement? Create(ShortcutItem item, double size)
    {
        if (item.DisplayImage is { } image) return new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
        if (!string.IsNullOrEmpty(item.IconGlyph))
        {
            // Marque inconnue : on retombe sur l'icône par défaut (image choisie, sinon icône du programme).
            if (!BrandIcons.IsBrand(item.IconGlyph)) return CreateGlyph(item.IconGlyph, size, item.IconColor);
            if (CreateBrand(item.IconGlyph, size, item.IconColor) is { } brand) return brand;
        }

        var custom = !string.IsNullOrWhiteSpace(item.CustomIconPath);
        var source = custom ? IconExtractor.GetCustomIcon(item.CustomIconPath!) : IconExtractor.GetIcon(item.Path);
        if (source is null) return null;
        if (custom && !string.IsNullOrWhiteSpace(item.ImageTint)) return CreateTinted(source, size, item.ImageTint);
        return new Image { Source = source, Width = size, Height = size };
    }

    /// <summary>Image recolorée d'une seule couleur : sa transparence sert de masque à un aplat de <paramref name="color"/>.</summary>
    public static FrameworkElement CreateTinted(ImageSource source, double size, string color)
    {
        var brush = new SolidColorBrush(ThemeService.Parse(color, Colors.White));
        brush.Freeze();
        var mask = new ImageBrush(source) { Stretch = Stretch.Uniform };
        mask.Freeze();
        return new System.Windows.Shapes.Rectangle { Width = size, Height = size, Fill = brush, OpacityMask = mask };
    }

    /// <summary>Vrai pour un chemin de fichier image (icône importée), faux pour un symbole ou un émoji.</summary>
    public static bool IsImagePath(string value) => !IsSymbol(value) && !BrandIcons.IsBrand(value) && System.IO.Path.IsPathRooted(value);

    /// <summary>
    /// Logo « brand:slug » de <paramref name="size"/> pixels : couleur <paramref name="color"/> si fournie, sinon couleur
    /// officielle (couleur du texte du thème pour un logo noir). Null si le slug est inconnu.
    /// </summary>
    public static FrameworkElement? CreateBrand(string? value, double size, string? color = null)
    {
        if (BrandIcons.Resolve(value) is not { Geometry: { } geometry } brand) return null;

        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(color)) path.Fill = new SolidColorBrush(ThemeService.Parse(color, Colors.White));
        else if (brand.IsDark) path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
        else path.Fill = new SolidColorBrush(ThemeService.Parse(brand.OfficialColor, Colors.White));
        return new Grid { Width = size, Height = size, Children = { path } };
    }

    /// <summary>Logo sous forme d'image (liste, dock) ; mêmes couleurs que <see cref="CreateBrand"/> (blanc pour un logo noir). Null si inconnu.</summary>
    public static ImageSource? CreateBrandImage(string? value, string? color = null)
    {
        if (BrandIcons.Resolve(value) is not { Geometry: { } geometry } brand) return null;

        var fill = !string.IsNullOrWhiteSpace(color) ? ThemeService.Parse(color, Colors.White)
            : brand.IsDark ? Colors.White
            : ThemeService.Parse(brand.OfficialColor, Colors.White);
        var brush = new SolidColorBrush(fill);
        brush.Freeze();
        var image = new DrawingImage(new GeometryDrawing(brush, null, geometry));
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Icône d'un réglage à valeur unique (symbole Segoe, émoji ou chemin d'image) ; null si vide ou image introuvable.
    /// <paramref name="color"/> ne s'applique qu'aux symboles.
    /// </summary>
    public static FrameworkElement? CreateCustom(string? value, double size, string? color = null)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (BrandIcons.IsBrand(value)) return CreateBrand(value, size, color);
        if (!IsImagePath(value)) return CreateGlyph(value, size, color);

        var source = IconExtractor.GetCustomIcon(value);
        return source is null ? null : new Image { Source = source, Width = size, Height = size };
    }

    /// <summary>
    /// Symbole (police ; couleur <paramref name="color"/>, sinon celle du texte du thème) ou émoji (image en couleur,
    /// comme le sélecteur d'émojis ; la couleur ne s'y applique pas).
    /// </summary>
    public static FrameworkElement CreateGlyph(string glyph, double size, string? color = null)
    {
        if (BrandIcons.IsBrand(glyph))
            return CreateBrand(glyph, size, color) ?? new Grid { Width = size, Height = size }; // slug inconnu : vide, sans exception

        if (!IsSymbol(glyph))
        {
            var emoji = new Image { Width = size, Height = size };
            Emoji.Wpf.Image.SetSource(emoji, glyph);
            return emoji;
        }

        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(SymbolFont),
            FontSize = size * 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(color)) text.Foreground = new SolidColorBrush(ThemeService.Parse(color, Colors.White));
        else text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return new Grid { Width = size, Height = size, Children = { text } };
    }
}
