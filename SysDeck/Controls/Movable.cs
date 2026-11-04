using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Services;

namespace SysDeck.Controls;

/// <summary>
/// Marque un élément d'un module comme déplaçable (Movable.Key="title", "ring"…). Sa position, sa taille
/// et sa visibilité personnalisées sont appliquées par-dessus la mise en page d'origine, sous forme de
/// transformation de rendu : la mise en page de base reste intacte et « Réinitialiser » la retrouve.
/// </summary>
public static class Movable
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Movable), new PropertyMetadata(null));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["title"] = "Titre",
        ["name"] = "Nom du matériel",
        ["ring"] = "Cercle",
        ["value"] = "Pourcentage",
        ["temp"] = "Température",
        ["tiles"] = "Statistiques",
        ["total"] = "Total",
        ["list"] = "Liste",
        ["graph"] = "Graphe",
        ["rates"] = "Débits",
        ["status"] = "État",
        ["disc"] = "Pochette",
        ["track"] = "Morceau",
        ["controls"] = "Boutons",
        ["progress"] = "Barre de temps",
        ["session"] = "Coût depuis l'allumage",
        ["usageToday"] = "Heures du jour",
        ["lifetime"] = "Depuis la mise en service",
        ["projection"] = "Estimation mensuelle",
        ["details"] = "Détail des puissances",
        ["spectrum"] = "Barres",
        ["clock"] = "Fréquence",
        ["fps"] = "Images par seconde (FPS)",
        ["gpuClock"] = "Fréquence GPU",
        ["memClock"] = "Fréquence mémoire",
        ["vram"] = "Mémoire vidéo utilisée",
    };

    public static string LabelFor(string key)
        => key.StartsWith("drive:") ? $"Disque {key[6..]}" : Labels.GetValueOrDefault(key, key);

    /// <summary>
    /// Levé par un module quand ses éléments déplaçables changent (ex : un disque branché ou retiré),
    /// pour que la fenêtre les recherche à nouveau.
    /// </summary>
    public static readonly RoutedEvent ElementsChangedEvent = EventManager.RegisterRoutedEvent(
        "ElementsChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(Movable));

    /// <summary>
    /// Vrai si l'élément est réellement affiché : visible lui-même et via ses parents (module à l'écran,
    /// texte ou bloc non désactivé par une option) et d'une taille non nulle. Un élément désactivé n'a pas de cadre d'édition.
    /// </summary>
    public static bool IsDisplayed(FrameworkElement element)
        => element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0;

    /// <summary>
    /// Contour de ce qui est réellement dessiné (texte, jauge, image…), dans le repère de l'élément.
    /// L'emplacement réservé (souvent étiré sur toute la largeur) donnait des cadres d'édition trop grands.
    /// Les fonds transparents des panneaux sont ignorés ; sans contenu visible, on garde l'emplacement entier.
    /// </summary>
    public static Rect ContentBounds(FrameworkElement element)
    {
        var bounds = Rect.Empty;
        AddContent(element, element, ref bounds);
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
            return new Rect(element.RenderSize);
        // Un débordement éventuel reste encadré, mais pas plus loin que l'emplacement élargi de moitié.
        var limit = new Rect(element.RenderSize);
        limit.Inflate(element.ActualWidth / 2, element.ActualHeight / 2);
        bounds.Intersect(limit);
        return bounds.IsEmpty ? new Rect(element.RenderSize) : bounds;
    }

    /// <summary>
    /// Ajoute à <paramref name="bounds"/> (repère de <paramref name="root"/>) ce que <paramref name="node"/> affiche.
    /// Seul le texte compte pour son tracé réel ; tout ce qui est dessiné ou animé (jauge, graphe, barres, image,
    /// pochette qui tourne…) compte pour sa boîte fixe : la taille ne varie pas avec les valeurs ni les animations.
    /// </summary>
    internal static void AddContent(Visual root, Visual node, ref Rect bounds)
    {
        if (node is UIElement { Visibility: not Visibility.Visible } or UIElement { Opacity: <= 0.01 }) return;
        // Élément transformé à l'intérieur (pochette qui tourne, barres animées…) : on compte son emplacement,
        // pas sa forme tournée, sinon le cadre et la carte changeraient de taille à chaque image.
        if (!ReferenceEquals(node, root) && node is UIElement { RenderTransform: { } transform } ui
            && !transform.Value.IsIdentity && VisualTreeHelper.GetParent(node) is Visual parent)
        {
            var slot = new Rect((Point)VisualTreeHelper.GetOffset(node), ui.RenderSize);
            bounds.Union(ReferenceEquals(parent, root) ? slot : parent.TransformToAncestor(root).TransformBounds(slot));
            return;
        }
        if (node is TextBlock)
        {
            Add(root, node, VisualTreeHelper.GetContentBounds(node), ref bounds);
            return;
        }
        if (node is UIElement drawn && !IsContainer(node))
        {
            // Boîte fixe + tracé (graduations, halo qui débordent) : ce qui change avec la valeur reste dans la boîte.
            var box = new Rect(drawn.RenderSize);
            var ink = VisualTreeHelper.GetContentBounds(node);
            if (!ink.IsEmpty) box.Union(ink);
            Add(root, node, box, ref bounds);
            return;
        }
        if (!IsInvisibleBackground(node))
            Add(root, node, VisualTreeHelper.GetContentBounds(node), ref bounds);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (VisualTreeHelper.GetChild(node, i) is Visual child)
                AddContent(root, child, ref bounds);
    }

    private static void Add(Visual root, Visual node, Rect local, ref Rect bounds)
    {
        if (local.IsEmpty || local.Width <= 0 || local.Height <= 0) return;
        bounds.Union(ReferenceEquals(node, root) ? local : node.TransformToAncestor(root).TransformBounds(local));
    }

    /// <summary>Élément qui ne fait que contenir d'autres éléments (son contenu est examiné à part).</summary>
    internal static bool IsContainer(Visual node)
        => node is Panel or Decorator or ContentPresenter or ItemsPresenter
            // Contrôle avec modèle (bouton, liste…) ; sans enfant, il dessine lui-même (jauge) : c'est un dessin.
            || node is System.Windows.Controls.Control && VisualTreeHelper.GetChildrenCount(node) > 0
            || node is not UIElement; // visuels internes (Viewbox…)

    /// <summary>Panneau ou bordure qui ne dessine qu'un fond transparent (posé pour capter la souris).</summary>
    private static bool IsInvisibleBackground(Visual node) => node switch
    {
        Panel p => IsClear(p.Background),
        Border b => IsClear(b.Background) && (IsClear(b.BorderBrush) || b.BorderThickness == default),
        System.Windows.Controls.Control c => IsClear(c.Background) && (IsClear(c.BorderBrush) || c.BorderThickness == default),
        _ => false
    };

    private static bool IsClear(Brush? brush)
        => brush is null || brush.Opacity <= 0.01 || brush is SolidColorBrush { Color.A: 0 };

    /// <summary>Tous les éléments déplaçables sous <paramref name="root"/> (arbre visuel, sans entrer dans un élément déplaçable).</summary>
    public static List<FrameworkElement> FindAll(DependencyObject root)
    {
        var found = new List<FrameworkElement>();
        Walk(root, found);
        return found;
    }

    private static void Walk(DependencyObject node, List<FrameworkElement> found)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement fe && GetKey(fe) is not null)
                found.Add(fe);
            Walk(child, found);
        }
    }

    /// <summary>
    /// Ajoute au menu un sous-menu « Éléments masqués » : un item par clé masquée, cliquer le réaffiche.
    /// Mis à jour à chaque ouverture du menu, grisé quand il n'y en a aucun.
    /// </summary>
    public static void AddHiddenMenu(ContextMenu menu, Func<IEnumerable<string>> hiddenKeys, Action<string> show)
    {
        var root = new MenuItem { Header = "Éléments masqués", IsEnabled = false };
        menu.Opened += (_, _) =>
        {
            root.Items.Clear();
            foreach (var key in hiddenKeys())
            {
                var item = new MenuItem { Header = LabelFor(key) };
                var k = key;
                item.Click += (_, _) => show(k);
                root.Items.Add(item);
            }
            root.IsEnabled = root.Items.Count > 0;
        };
        menu.Items.Add(root);
    }

    /// <summary>Applique décalage, échelle et visibilité ; un élément masqué n'a plus ni rendu ni cadre d'édition (réaffichage par le menu « Éléments masqués »).</summary>
    public static void Apply(FrameworkElement element, ElementLayout? layout)
    {
        layout ??= new ElementLayout();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = layout.IsDefault
            ? Transform.Identity
            : new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(layout.Scale, layout.Scale),
                    new TranslateTransform(layout.X, layout.Y)
                }
            };
        element.Opacity = layout.Hidden ? 0 : 1;
        element.IsHitTestVisible = !layout.Hidden;
        ThemeService.ApplyAccent(element.Resources, layout.AccentStart, layout.AccentEnd);
    }
}
