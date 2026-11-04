using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysDeck.Deck;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Controls;

/// <summary>
/// Éditeur de la disposition de l'écran Switch : aperçu en direct de l'écran (1280x720) et, par-dessus,
/// un cadre par élément affiché. Glisser déplace, la poignée en bas à droite redimensionne
/// (aimantation à 10 px, toujours dans l'écran), clic droit pour passer devant ou masquer.
/// Les modules (sauf le lecteur média) n'ont pas de cadre : on place directement leurs éléments, où l'on veut.
/// </summary>
public sealed partial class DeckLayoutEditor : Border
{
    private const double Snap = 10;
    private const double MinSize = DeckScreen.MinElementSize;
    private const double HandleSize = 34;

    private readonly Canvas _overlay = new() { Width = DeckScreen.ScreenWidth, Height = DeckScreen.ScreenHeight, Background = Brushes.Transparent };
    private DeckController? _deck;
    private string? _selected;

    // Glissement en cours
    private FrameworkElement? _dragFrame;
    private DeckElement? _dragElement;
    private bool _resizing;
    private Point _dragStart;
    private Rect _startRect;

    /// <summary>Un élément a été déplacé, redimensionné ou masqué (à enregistrer).</summary>
    public event Action? LayoutChanged;

    /// <summary>Un élément a été masqué depuis le menu (la liste des éléments doit suivre).</summary>
    public event Action? VisibilityChanged;

    /// <summary>Élément sélectionné dans l'aperçu (identifiant du module), null si aucun.</summary>
    public string? SelectedId => _selected;

    public DeckLayoutEditor()
    {
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        Background = Brushes.Black;
        ClipToBounds = true;
        _overlay.MouseMove += Overlay_MouseMove;
        _overlay.MouseLeftButtonUp += (_, _) => EndDrag();
        _overlay.LostMouseCapture += (_, _) => EndDrag();
        InitElementMode();
        InitActionDrop();
    }

    public void Attach(DeckController deck)
    {
        _deck = deck;
        // Un module change ses éléments (lecteur qui change de disposition, disque branché) : nouveaux cadres.
        deck.Screen.AddHandler(Movable.ElementsChangedEvent, new RoutedEventHandler((_, _) => ScheduleElementBoxes()), handledEventsToo: true);
        // Aperçu : pinceau qui reproduit en direct le visuel de l'écran Switch (rendu par la carte graphique).
        var preview = new System.Windows.Shapes.Rectangle
        {
            Width = DeckScreen.ScreenWidth,
            Height = DeckScreen.ScreenHeight,
            Fill = new VisualBrush(deck.Screen)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, DeckScreen.ScreenWidth, DeckScreen.ScreenHeight)
            }
        };
        Child = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = new Grid
            {
                Width = DeckScreen.ScreenWidth,
                Height = DeckScreen.ScreenHeight,
                Children = { preview, _overlay, _elementOverlay }
            }
        };
        Rebuild();
    }

    /// <summary>Recrée les cadres (éléments ajoutés ou retirés).</summary>
    public void Rebuild()
    {
        _overlay.Children.Clear();
        if (_deck is null) return;
        // Chaque élément de l'écran a un cadre pour le déplacer d'un bloc ; les éléments internes des modules
        // ont en plus leurs propres cadres (couche au-dessus).
        foreach (var element in _deck.Settings.Elements.Where(e => e.Visible))
        {
            if (element.Id == "apps") AddGridLines(element);
            _overlay.Children.Add(BuildFrame(element));
        }
        SyncGridLines();
        // Un module tout juste ajouté n'est pas encore chargé : ses éléments sont cherchés après sa mise en page.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, RebuildElementBoxes);
    }

    private FrameworkElement BuildFrame(DeckElement element)
    {
        var selected = element.Id == _selected;
        // Module : ses éléments couvrent presque toute la carte, l'étiquette (au-dessus de la carte) sert de poignée.
        var isModule = _deck?.Screen.CardOf(element.Id) is not null;
        var label = new Border
        {
            Tag = isModule ? GripTag : null,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x11, 0x17)),
            CornerRadius = new CornerRadius(0, 0, 10, 0),
            Padding = new Thickness(10, 4, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = (isModule ? "✥  " : "") + LabelFor(element.Id), Foreground = Brushes.White, FontSize = 22 }
        };
        if (isModule) label.CornerRadius = new CornerRadius(10, 10, 10, 0);

        var handle = new Border
        {
            Width = HandleSize,
            Height = HandleSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(10, 0, 8, 0),
            Background = AccentBrush(0xEE),
            Cursor = Cursors.SizeNWSE,
            Tag = "resize",
            Child = new TextBlock
            {
                Text = "", // agrandir
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var frame = new Border
        {
            Tag = element,
            BorderThickness = new Thickness(selected ? 4 : 2),
            BorderBrush = AccentBrush(selected ? (byte)0xFF : (byte)0x99),
            Background = new SolidColorBrush(Color.FromArgb(selected ? (byte)0x22 : (byte)0x08, 0x00, 0xE0, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.SizeAll,
            Child = new Grid { Children = { label, handle } },
            ContextMenu = BuildMenu(element),
            ToolTip = $"{LabelFor(element.Id)} — glisser : déplacer · coin : redimensionner · clic droit : options"
        };
        // Coin de redimensionnement d'un module à moitié dehors : le coin de la carte est souvent couvert par un élément.
        if (isModule) handle.Margin = new Thickness(0, 0, -HandleSize / 2, -HandleSize / 2);
        // Étiquette au survol ou sur l'élément sélectionné seulement : elle cacherait le titre du module.
        // Celle d'un module, posée au-dessus de la carte, reste visible : c'est la poignée pour la déplacer.
        label.Opacity = selected || isModule ? 1 : 0;
        frame.MouseEnter += (_, _) => label.Opacity = 1;
        frame.MouseLeave += (_, _) => label.Opacity = element.Id == _selected || isModule ? 1 : 0;
        Place(frame, element);
        frame.MouseLeftButtonDown += Frame_MouseLeftButtonDown;
        return frame;
    }

    private static Brush AccentBrush(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, 0x00, 0xE0, 0xFF));

    private static string LabelFor(string id)
        => DeckController.AllElements.FirstOrDefault(e => e.Id == id)?.Label ?? id;

    /// <summary>Cadres recalés sur les réglages : un module en taille automatique change de taille quand ses éléments bougent.</summary>
    private void RefreshFrames()
    {
        foreach (var frame in _overlay.Children.OfType<Border>())
            if (frame != _dragFrame && frame.Tag is DeckElement element)
                Place(frame, element);
        SyncGridLines();
    }

    private const string GripTag = "grip";
    private const double KeepVisible = 60; // part minimale d'un élément restant à l'écran (px de la Switch)
    private const double GripHeight = 42;

    private static void Place(FrameworkElement frame, DeckElement element)
    {
        // Poignée d'un module au-dessus de la carte, ou dedans si la carte touche le haut ou dépasse à gauche.
        if (frame is Border { Child: Grid { Children: [Border { Tag: GripTag } grip, ..] } })
            grip.Margin = new Thickness(Math.Max(0, -element.X), element.Y >= GripHeight ? -GripHeight : Math.Max(0, -element.Y), 0, 0); // toujours à l'écran
        Canvas.SetLeft(frame, element.X);
        Canvas.SetTop(frame, element.Y);
        frame.Width = element.Width;
        frame.Height = element.Height;
        // Même ordre que sur l'écran : le dernier de la liste passe devant.
    }

    private ContextMenu BuildMenu(DeckElement element)
    {
        var front = new MenuItem { Header = "Mettre au premier plan" };
        front.Click += (_, _) =>
        {
            _deck?.BringToFront(element.Id);
            Rebuild();
            LayoutChanged?.Invoke();
        };
        var hide = new MenuItem { Header = "Retirer de l'écran" };
        hide.Click += (_, _) =>
        {
            _deck?.SetVisible(element.Id, false);
            Rebuild();
            LayoutChanged?.Invoke();
            VisibilityChanged?.Invoke();
        };
        var menu = new ContextMenu { Items = { front, hide } };
        // Même si tous les éléments du module sont masqués (plus aucun cadre en mode éléments), on peut les réafficher d'ici.
        Movable.AddHiddenMenu(menu,
            () => _deck?.Screen.CardOf(element.Id) is ModuleCard card ? HiddenKeys(card) : Enumerable.Empty<string>(),
            k => { if (_deck?.Screen.CardOf(element.Id) is ModuleCard card) ShowElement(card, k); });
        return menu;
    }

    // ---------- Glisser ----------

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeckElement element } frame) return;

        _resizing = e.OriginalSource is DependencyObject source && IsInResizeHandle(source, frame);
        _dragFrame = frame;
        _dragElement = element;
        _dragStart = e.GetPosition(_overlay);
        _startRect = new Rect(element.X, element.Y, element.Width, element.Height);

        if (_selected != element.Id)
        {
            _selected = element.Id;
            // Restyle sans recréer le cadre saisi : la capture de la souris doit rester sur l'overlay.
            foreach (var other in _overlay.Children.OfType<Border>())
            {
                var isSelected = other.Tag == element;
                other.BorderThickness = new Thickness(isSelected ? 4 : 2);
                other.BorderBrush = AccentBrush(isSelected ? (byte)0xFF : (byte)0x99);
                other.Background = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)0x22 : (byte)0x08, 0x00, 0xE0, 0xFF));
                if (other.Child is Grid { Children: [Border label, ..] }) label.Opacity = isSelected || other.IsMouseOver || label.Tag is GripTag ? 1 : 0;
            }
        }
        // Clic sur une tuile du bloc d'applications : on déplace la tuile (pas le bloc) ; un simple clic la sélectionne,
        // un double-clic ouvre sa configuration.
        if (!_resizing && TryBeginTileDrag(element, _dragStart))
        {
            _dragFrame = null;
            _dragElement = null;
            if (e.ClickCount == 2 && _tileDrag is { } item)
            {
                SelectTile(item);
                _tileDrag = null;
                e.Handled = true;
                Dispatcher.BeginInvoke(() => TileActivated?.Invoke(item));
                return;
            }
        }
        else SelectTile(null);
        _overlay.CaptureMouse();
        e.Handled = true;
    }

    private static bool IsInResizeHandle(DependencyObject source, FrameworkElement frame)
    {
        for (var d = source; d is not null && d != frame; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: "resize" }) return true;
        return false;
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_tileDrag is not null)
        {
            UpdateTileDrag(e.GetPosition(_overlay));
            return;
        }
        if (_dragFrame is null || _dragElement is null || _deck is null) return;
        var delta = e.GetPosition(_overlay) - _dragStart;
        const double W = DeckScreen.ScreenWidth, H = DeckScreen.ScreenHeight;

        if (_resizing)
        {
            // Peut dépasser de l'écran (la partie dehors est coupée) ; borne large pour rester manipulable.
            _dragElement.Width = Math.Clamp(Round(_startRect.Width + delta.X), MinSize, W * 2);
            _dragElement.Height = Math.Clamp(Round(_startRect.Height + delta.Y), MinSize, H * 2);
            _dragElement.Scale = null; // module en taille automatique : le cadre tiré donne la nouvelle échelle
        }
        else
        {
            // Dépassement de l'écran permis, mais une partie reste visible pour pouvoir le rattraper.
            _dragElement.X = Math.Clamp(Round(_startRect.X + delta.X), KeepVisible - _dragElement.Width, W - KeepVisible);
            _dragElement.Y = Math.Clamp(Round(_startRect.Y + delta.Y), KeepVisible - _dragElement.Height, H - KeepVisible);
        }

        _deck.Place(_dragElement.Id);
        Place(_dragFrame, _dragElement); // après : un module en taille automatique a pu ajuster son cadre
        SyncGridLines();
    }

    private static double Round(double value) => Math.Round(value / Snap) * Snap;

    private void EndDrag()
    {
        if (_tileDrag is not null)
        {
            EndTileDrag();
            return;
        }
        if (_dragFrame is null) return;
        var moved = _dragElement is not null && _startRect != new Rect(_dragElement.X, _dragElement.Y, _dragElement.Width, _dragElement.Height);
        _dragFrame = null;
        _dragElement = null;
        if (_overlay.IsMouseCaptured) _overlay.ReleaseMouseCapture();
        if (moved) LayoutChanged?.Invoke();
    }
}
