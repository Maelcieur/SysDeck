using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Models;

namespace SysDeck.Widgets;

public enum DockAnimationDirection { Up, Down, Left, Right }

public partial class ShortcutsDockContent : UserControl
{
    private const double MaxScale = 1.7;

    public event Action<ShortcutItem>? LaunchRequested;
    public event Action<ShortcutItem>? RemoveRequested;
    public event Action? AddRequested;
    public event Action? AddCustomRequested;
    public event Action<ShortcutItem>? ChangeIconRequested;
    public event Action<string>? FileDropped;
    public event Action? ReorderRequested;

    private DockAnimationDirection _direction = DockAnimationDirection.Up;
    private ObservableCollection<ShortcutItem>? _shortcuts;


    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(ShortcutsDockContent),
            new PropertyMetadata(50.0, OnIconSizeChanged));

    public static readonly DependencyProperty IconSpacingProperty =
        DependencyProperty.Register(nameof(IconSpacing), typeof(double), typeof(ShortcutsDockContent),
            new PropertyMetadata(6.0));

    public static readonly DependencyProperty IsVerticalProperty =
        DependencyProperty.Register(nameof(IsVertical), typeof(bool), typeof(ShortcutsDockContent),
            new PropertyMetadata(false));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double IconSpacing
    {
        get => (double)GetValue(IconSpacingProperty);
        set => SetValue(IconSpacingProperty, value);
    }

    public bool IsVertical
    {
        get => (bool)GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    public ShortcutsDockContent() => InitializeComponent();

    public void SetShortcuts(ObservableCollection<ShortcutItem> shortcuts)
    {
        Detach();
        _shortcuts = shortcuts;
        ShortcutsList.ItemsSource = shortcuts;
        UpdateEmptyState();
        shortcuts.CollectionChanged += OnShortcutsChanged;
    }

    /// <summary>
    /// Se désabonne de la liste partagée : sans ça, une barre masquée resterait référencée par la
    /// liste (fuite) et continuerait de réagir à chaque ajout de raccourci.
    /// </summary>
    public void Detach()
    {
        if (_shortcuts is not null)
            _shortcuts.CollectionChanged -= OnShortcutsChanged;
    }

    private void OnShortcutsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
        _labelItem = null; // les positions ont pu changer : l'étiquette sera repositionnée au prochain survol
    }

    private void UpdateEmptyState()
        => EmptyAddButton.Visibility = _shortcuts is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

    public void SetAnimationDirection(DockAnimationDirection direction)
    {
        _direction = direction;
        ApplyRenderOrigins();
    }

    private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShortcutsDockContent dock)
            dock.Dispatcher.BeginInvoke(dock.ApplyRenderOrigins);
    }

    /// <summary>
    /// Le point d'ancrage de la mise à l'échelle (RenderTransformOrigin) dépend de la direction choisie :
    /// l'icône doit grossir à l'opposé du bord d'où elle "sort" (ex : grossir vers le haut = ancrée en bas).
    /// </summary>
    private Point AnchorOrigin => _direction switch
    {
        DockAnimationDirection.Up => new Point(0.5, 1),
        DockAnimationDirection.Down => new Point(0.5, 0),
        DockAnimationDirection.Left => new Point(1, 0.5),
        DockAnimationDirection.Right => new Point(0, 0.5),
        _ => new Point(0.5, 1)
    };

    private void ApplyRenderOrigins()
    {
        var origin = AnchorOrigin;
        ForEachIconRoot(root => root.RenderTransformOrigin = origin);
        _labelItem = null; // taille ou direction changée : étiquette à repositionner
    }

    private void ForEachIconRoot(Action<FrameworkElement> action)
    {
        foreach (var item in ShortcutsList.Items)
        {
            if (ShortcutsList.ItemContainerGenerator.ContainerFromItem(item) is not ContentPresenter presenter)
                continue;
            if (VisualTreeHelper.GetChildrenCount(presenter) == 0)
                continue;
            if (VisualTreeHelper.GetChild(presenter, 0) is not FrameworkElement root)
                continue;

            action(root);
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ShortcutItem item })
            LaunchRequested?.Invoke(item);
    }

    private void LaunchContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ShortcutItem item })
            LaunchRequested?.Invoke(item);
    }

    private void RemoveContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ShortcutItem item })
            RemoveRequested?.Invoke(item);
    }

    private void AddContext_Click(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

    private void AddCustomContext_Click(object sender, RoutedEventArgs e) => AddCustomRequested?.Invoke();

    private void ChangeIconContext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ShortcutItem item })
            ChangeIconRequested?.Invoke(item);
    }

    /// <summary>
    /// Force le réaffichage d'une icône après un changement (ShortcutItem n'implémente pas
    /// INotifyPropertyChanged, et l'Image se lie à l'objet entier sans chemin de propriété précis —
    /// remplacer l'élément par lui-même au même index (Replace) ne suffit pas : comme c'est
    /// exactement la même référence, WPF n'a aucune garantie de régénérer le conteneur. Retirer puis
    /// réinsérer force une régénération complète du conteneur, donc une réévaluation du binding.
    /// </summary>
    public void RefreshIcon(ShortcutItem item)
    {
        if (_shortcuts is null) return;
        var index = _shortcuts.IndexOf(item);
        if (index < 0) return;

        _shortcuts.RemoveAt(index);
        _shortcuts.Insert(index, item);
    }

    // ---------- Magnification façon dock macOS ----------
    // Chaque icône grossit selon sa distance au curseur (effet de "voisinage"), avec une direction
    // d'animation configurable, plus une étiquette flottante affichant le nom de l'icône la plus proche.

    /// <summary>Une icône générée, avec sa position de repos et son facteur de proximité au curseur (0 à 1).</summary>
    private sealed record IconSlot(ShortcutItem? Item, FrameworkElement Root, Point Center, double Factor);

    // Icône dont l'étiquette est affichée : tant qu'elle ne change pas, ni texte ni mise en page à refaire.
    private ShortcutItem? _labelItem;

    private void Dock_MouseMove(object sender, MouseEventArgs e)
    {
        // Pendant un glisser (appui long), la souris est capturée par le Button de l'icône attrapée
        // et WPF ne fait plus remonter les MouseMove jusqu'ici (DockPill est un ancêtre du Button
        // capturé) : voir Icon_PreviewMouseMove, qui gère seul le survol pendant cette période.
        if (_isDragging) return;

        var slots = EnumerateIcons(e.GetPosition(DockPill)).ToList();
        var origin = AnchorOrigin;

        foreach (var slot in slots)
        {
            // Garantit le point d'ancrage même pour une icône générée après coup (réglage changé, ajout) :
            // sans ça elle garderait l'origine par défaut (0,0) et « sauterait ». Comparaison peu coûteuse,
            // donc faite ici plutôt que par un parcours complet à chaque mouvement.
            if (slot.Root.RenderTransformOrigin != origin)
                slot.Root.RenderTransformOrigin = origin;

            if (slot.Root.FindName("IconScale") is not ScaleTransform scale) continue;
            var lift = slot.Root.FindName("IconLift") as TranslateTransform;

            var value = 1 + (MaxScale - 1) * slot.Factor;
            scale.ScaleX = value;
            scale.ScaleY = value;

            if (lift is not null)
            {
                var offset = (value - 1) * (IconSize * 0.4);
                double dx = 0, dy = 0;
                switch (_direction)
                {
                    case DockAnimationDirection.Up: dy = -offset; break;
                    case DockAnimationDirection.Down: dy = offset; break;
                    case DockAnimationDirection.Left: dx = -offset; break;
                    case DockAnimationDirection.Right: dx = offset; break;
                }
                lift.X = dx;
                lift.Y = dy;
            }
        }

        var closest = PickClosest(slots);
        if (closest is { Factor: > 0.35, Item: not null })
            ShowNameLabel(closest.Item, closest.Center);
        else
            HideNameLabel();
    }

    /// <summary>
    /// Parcourt les icônes générées avec leur position de repos (le long de l'axe d'orientation du dock)
    /// et leur proximité à une position souris relative à DockPill. Seul point de calcul, partagé entre le
    /// survol normal (Dock_MouseMove) et la détection d'échange pendant un glisser (Icon_PreviewMouseMove),
    /// qui ne peut pas passer par Dock_MouseMove une fois la souris capturée.
    /// </summary>
    private IEnumerable<IconSlot> EnumerateIcons(Point mousePos)
    {
        bool vertical = IsVertical;
        double step = IconSize + IconSpacing;
        double influenceRadius = IconSize * 1.8;
        double originLeft = DockPill.BorderThickness.Left + DockPill.Padding.Left;
        double originTop = DockPill.BorderThickness.Top + DockPill.Padding.Top;
        double mouseAxis = vertical ? mousePos.Y : mousePos.X;

        int index = 0;
        foreach (var item in ShortcutsList.Items)
        {
            // L'index avance pour CHAQUE élément, même sans conteneur généré : sinon les icônes
            // suivantes seraient mesurées avec un centre décalé d'une case.
            int slotIndex = index++;

            if (ShortcutsList.ItemContainerGenerator.ContainerFromItem(item) is not ContentPresenter presenter
                || VisualTreeHelper.GetChildrenCount(presenter) == 0
                || VisualTreeHelper.GetChild(presenter, 0) is not FrameworkElement root)
                continue;

            // Décalage de IconSpacing/2 pour la marge de tête du tout premier élément ; axe de mesure
            // basé sur l'orientation (pas sur la direction d'animation).
            double centerAxis = (vertical ? originTop : originLeft) + IconSpacing / 2 + step * slotIndex + IconSize / 2;
            double centerCross = (vertical ? originLeft : originTop) + IconSize / 2;
            var center = vertical ? new Point(centerCross, centerAxis) : new Point(centerAxis, centerCross);
            var factor = Math.Max(0, 1 - Math.Abs(mouseAxis - centerAxis) / influenceRadius);

            yield return new IconSlot(item as ShortcutItem, root, center, factor);
        }
    }

    private static IconSlot? PickClosest(IEnumerable<IconSlot> slots)
    {
        IconSlot? best = null;
        foreach (var slot in slots)
            if (slot.Factor > (best?.Factor ?? 0))
                best = slot;
        return best;
    }

    /// <summary>Icône la plus proche du curseur (position relative à DockPill), ou null si aucune n'est dans la zone d'influence.</summary>
    private IconSlot? FindClosestIcon(Point mousePos) => PickClosest(EnumerateIcons(mousePos));

    private void ShowNameLabel(ShortcutItem item, Point centerInDock)
    {
        // Même icône que la fois précédente : texte et position inchangés, on évite de refaire une
        // mise en page complète (UpdateLayout) à chaque mouvement de souris.
        if (ReferenceEquals(_labelItem, item) && NameLabel.Visibility == Visibility.Visible) return;
        _labelItem = item;

        NameLabelText.Text = item.Name;
        NameLabel.Visibility = Visibility.Visible;
        NameLabel.UpdateLayout();

        // position dans le repère du Grid racine (celui qui contient à la fois le dock et l'étiquette) —
        // on part de DockPill, le même élément que celui utilisé comme origine (0,0) pour calculer
        // centerInDock, afin que les deux repères coïncident exactement.
        var dockToRoot = ((UIElement)DockPill).TranslatePoint(new Point(0, 0), (UIElement)NameLabel.Parent);
        var centerInRoot = new Point(dockToRoot.X + centerInDock.X, dockToRoot.Y + centerInDock.Y);

        double left, top;
        var labelW = NameLabel.ActualWidth;
        var labelH = NameLabel.ActualHeight;
        var gap = IconSize * 0.75;

        switch (_direction)
        {
            case DockAnimationDirection.Down:
                left = centerInRoot.X - labelW / 2;
                top = centerInRoot.Y + gap;
                break;
            case DockAnimationDirection.Left:
                left = centerInRoot.X - gap - labelW;
                top = centerInRoot.Y - labelH / 2;
                break;
            case DockAnimationDirection.Right:
                left = centerInRoot.X + gap;
                top = centerInRoot.Y - labelH / 2;
                break;
            default: // Up
                left = centerInRoot.X - labelW / 2;
                top = centerInRoot.Y - gap - labelH;
                break;
        }

        Canvas.SetLeft(NameLabel, left);
        Canvas.SetTop(NameLabel, top);
    }

    private void HideNameLabel()
    {
        _labelItem = null;
        NameLabel.Visibility = Visibility.Collapsed;
    }

    private void Dock_MouseLeave(object sender, MouseEventArgs e)
    {
        ForEachIconRoot(root =>
        {
            if (root.FindName("IconScale") is ScaleTransform scale)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
            if (root.FindName("IconLift") is TranslateTransform lift)
            {
                lift.X = 0;
                lift.Y = 0;
            }
        });

        HideNameLabel();
    }

    // ---------- Glisser-déposer ----------

    private void Dock_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropHighlight.BorderBrush = (Brush)FindResource("AccentBrush2");
    }

    private void Dock_DragLeave(object sender, DragEventArgs e)
    {
        DropHighlight.BorderBrush = Brushes.Transparent;
    }

    private void Dock_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.BorderBrush = Brushes.Transparent;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var path in paths)
            FileDropped?.Invoke(path);
    }
}
