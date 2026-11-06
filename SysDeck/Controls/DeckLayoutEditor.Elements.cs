using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SysDeck.Deck;
using SysDeck.Services;
using Cursors = System.Windows.Input.Cursors;

namespace SysDeck.Controls;

/// <summary>
/// Mode éléments de l'éditeur Switch : un cadre pointillé par élément déplaçable des modules affichés.
/// Glisser déplace, molette redimensionne, clic droit masque ou réinitialise — comme le mode édition
/// du bureau, mais sur l'apparence propre à la Switch.
/// </summary>
public sealed partial class DeckLayoutEditor
{
    // Sans fond : seuls les cadres captent la souris, le reste passe aux cadres de l'horloge et des tuiles dessous.
    private readonly Canvas _elementOverlay = new()
    {
        Width = DeckScreen.ScreenWidth,
        Height = DeckScreen.ScreenHeight
    };

    // Les éléments bougent avec les données (textes plus longs, listes) : les cadres suivent régulièrement.
    private readonly DispatcherTimer _elementRefresh = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private sealed record ElementBox(ModuleCard Card, FrameworkElement Element, string Key);

    private ElementBox? _elementDrag;

    private IEnumerable<string> HiddenKeys(ModuleCard card)
        => card.MovableKeys.Where(k => _deck!.GetAppearance(card.ModuleId).GetElement(k).Hidden).ToList();

    private void ShowElement(ModuleCard card, string key)
    {
        _deck!.GetAppearance(card.ModuleId).GetElement(key).Hidden = false;
        card.ApplyElementLayouts();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceElementBoxes);
        ElementLayoutChanged?.Invoke(card.ModuleId);
    }
    private Point _elementDragStart;
    private double _elementStartX, _elementStartY;

    /// <summary>Un élément d'un module a été modifié sur l'aperçu (module concerné).</summary>
    public event Action<string>? ElementLayoutChanged;

    /// <summary>Élément d'un module cliqué dans l'aperçu (module, clé de l'élément), annoncé au relâchement.</summary>
    public event Action<string, string>? ModuleElementSelected;

    private void InitElementMode()
    {
        _elementRefresh.Tick += (_, _) =>
        {
            PlaceElementBoxes();
            RefreshFrames();
        };
        _elementOverlay.MouseMove += ElementOverlay_MouseMove;
        _elementOverlay.MouseLeftButtonUp += (_, _) => EndElementDrag();
        _elementOverlay.LostMouseCapture += (_, _) => EndElementDrag();
        Unloaded += (_, _) => _elementRefresh.Stop();
        Loaded += (_, _) => _elementRefresh.Start();
        // Clic droit sur une zone vide : réafficher les éléments masqués de n'importe quel module.
        _overlay.ContextMenu = BuildHiddenModulesMenu();
    }

    /// <summary>Menu « Éléments masqués » par module affiché (rempli à l'ouverture).</summary>
    private ContextMenu BuildHiddenModulesMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var card in Cards)
            {
                var keys = HiddenKeys(card).ToList();
                if (keys.Count == 0) continue;
                var moduleItem = new MenuItem { Header = $"{LabelFor(card.ModuleId)} — éléments masqués" };
                foreach (var key in keys)
                {
                    var item = new MenuItem { Header = Movable.LabelFor(key) };
                    item.Click += (_, _) => ShowElement(card, key);
                    moduleItem.Items.Add(item);
                }
                menu.Items.Add(moduleItem);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Aucun élément masqué", IsEnabled = false });
        };
        return menu;
    }

    private IEnumerable<ModuleCard> Cards
        => _deck is null ? Enumerable.Empty<ModuleCard>()
            : _deck.Settings.Elements.Where(e => e.Visible).Select(e => _deck.Screen.CardOf(e.Id)).OfType<ModuleCard>();

    /// <summary>Recrée les cadres des éléments (modules ajoutés ou retirés, éléments recréés).</summary>
    private bool _elementBoxesPending;

    /// <summary>Reconstruit les cadres une fois la mise en page faite (une seule fois même si plusieurs demandes arrivent).</summary>
    private void ScheduleElementBoxes()
    {
        if (_elementBoxesPending) return;
        _elementBoxesPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _elementBoxesPending = false;
            RebuildElementBoxes();
        });
    }

    public void RebuildElementBoxes()
    {
        _elementOverlay.Children.Clear();
        if (_deck is null) return;

        foreach (var card in Cards)
            foreach (var element in card.MovableElements)
            {
                var box = new ElementBox(card, element, Movable.GetKey(element)!);
                var frame = new Border
                {
                    Tag = box,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(4),
                    Cursor = Cursors.SizeAll,
                    ToolTip = $"{Movable.LabelFor(box.Key)} — glisser : déplacer · molette : taille · clic droit : options",
                    ContextMenu = BuildElementMenu(box)
                };
                frame.MouseEnter += (_, _) => StyleBox(frame, hover: true);
                frame.MouseLeave += (_, _) => StyleBox(frame, hover: false);
                frame.MouseLeftButtonDown += ElementBox_MouseLeftButtonDown;
                frame.MouseWheel += ElementBox_MouseWheel;
                _elementOverlay.Children.Add(frame);
            }
        PlaceElementBoxes();
    }

    private void PlaceElementBoxes()
    {
        if (_deck is null) return;
        foreach (var frame in _elementOverlay.Children.OfType<Border>())
        {
            if (frame.Tag is not ElementBox box) continue;
            // Élément retiré de l'écran entre-temps (liste recréée, module qui change de disposition) : les cadres sont refaits.
            if (!box.Element.IsDescendantOf(_deck.Screen))
            {
                frame.Visibility = Visibility.Collapsed;
                ScheduleElementBoxes();
                continue;
            }

            // Élément désactivé par une option (titre décoché…) : aucun cadre.
            if (!Movable.IsDisplayed(box.Element)) { frame.Visibility = Visibility.Collapsed; continue; }

            var bounds = box.Element.TransformToAncestor(_deck.Screen).TransformBounds(Movable.ContentBounds(box.Element));
            bounds.Inflate(3, 3);
            Canvas.SetLeft(frame, bounds.X);
            Canvas.SetTop(frame, bounds.Y);
            frame.Width = Math.Max(8, bounds.Width);
            frame.Height = Math.Max(8, bounds.Height);
            frame.Visibility = Visibility.Visible;
            // Un élément masqué n'a plus de cadre : il se réaffiche par le menu « Éléments masqués ».
            if (Layout(box).Hidden) { frame.Visibility = Visibility.Collapsed; continue; }
            StyleBox(frame, frame.IsMouseOver || _elementDrag == box);
        }
    }

    /// <summary>Pointillés bleus (les éléments masqués n'ont plus de cadre).</summary>
    private void StyleBox(Border frame, bool hover)
    {
        if (frame.Tag is not ElementBox box) return;
        var hidden = Layout(box).Hidden;
        var color = hidden ? Color.FromRgb(0xFF, 0x5C, 0x6C) : Color.FromRgb(0x00, 0xE0, 0xFF);
        frame.BorderBrush = new SolidColorBrush(Color.FromArgb(hover ? (byte)0xFF : (byte)0xA0, color.R, color.G, color.B));
        frame.Background = new SolidColorBrush(Color.FromArgb(hover ? (byte)0x40 : hidden ? (byte)0x18 : (byte)0x10, color.R, color.G, color.B));
    }

    private ElementLayout Layout(ElementBox box) => _deck!.GetAppearance(box.Card.ModuleId).GetElement(box.Key);

    private void ApplyElement(ElementBox box)
    {
        box.Card.ApplyElementLayouts();
        // La position réelle n'est connue qu'après la mise en page : les cadres suivent juste après.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceElementBoxes);
        ElementLayoutChanged?.Invoke(box.Card.ModuleId);
    }

    private void ElementBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ElementBox box }) return;
        var layout = Layout(box);
        _elementDrag = box;
        _elementDragStart = e.GetPosition(_elementOverlay);
        _elementStartX = layout.X;
        _elementStartY = layout.Y;
        _elementOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void ElementOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_elementDrag is not { } box || _deck is null) return;
        if (VisualTreeHelper.GetParent(box.Element) is not Visual parent || !parent.IsDescendantOf(_deck.Screen)) return;

        // Déplacement mesuré sur l'écran, converti dans le repère du parent de l'élément (mises à l'échelle des cartes).
        var toParent = _deck.Screen.TransformToDescendant(parent);
        if (toParent is null) return;
        var delta = toParent.Transform(e.GetPosition(_elementOverlay)) - toParent.Transform(_elementDragStart);

        var layout = Layout(box);
        layout.X = Math.Round(_elementStartX + delta.X);
        layout.Y = Math.Round(_elementStartY + delta.Y);
        box.Card.ApplyElementLayouts();
        PlaceElementBoxes();
    }

    private void EndElementDrag()
    {
        if (_elementDrag is not { } box) return;
        _elementDrag = null;
        if (_elementOverlay.IsMouseCaptured) _elementOverlay.ReleaseMouseCapture();
        ApplyElement(box);
        ModuleElementSelected?.Invoke(box.Card.ModuleId, box.Key);
    }

    private void ElementBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ElementBox box }) return;
        var layout = Layout(box);
        layout.Scale = Math.Round(Math.Clamp(layout.Scale * (e.Delta > 0 ? 1.08 : 1 / 1.08), 0.3, 4), 3);
        ApplyElement(box);
        e.Handled = true;
    }

    private ContextMenu BuildElementMenu(ElementBox box)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, Action<ElementLayout> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                action(Layout(box));
                ApplyElement(box);
            };
            return item;
        }

        var toggleVisibility = Item("Masquer cet élément", l => l.Hidden = true);

        menu.Items.Add(Item("Agrandir", l => l.Scale = Math.Min(4, Math.Round(l.Scale * 1.15, 3))));
        menu.Items.Add(Item("Réduire", l => l.Scale = Math.Max(0.3, Math.Round(l.Scale / 1.15, 3))));
        menu.Items.Add(Item("Taille d'origine", l => l.Scale = 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Position d'origine", l => l.X = l.Y = 0));
        menu.Items.Add(toggleVisibility);
        menu.Items.Add(new Separator());
        Movable.AddHiddenMenu(menu, () => HiddenKeys(box.Card), k => ShowElement(box.Card, k));
        menu.Items.Add(Item("Tout réinitialiser pour cet élément", l => { l.X = l.Y = 0; l.Scale = 1; l.Hidden = false; }));
        return menu;
    }
}
