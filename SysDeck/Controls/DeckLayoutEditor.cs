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
    private readonly HashSet<string> _selectedElements = new(StringComparer.Ordinal);
    private readonly Dictionary<DeckElement, Rect> _startRects = new();
    private readonly List<System.Windows.Shapes.Line> _alignmentGuides = new();

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
        ClearAlignmentGuides();
        _overlay.Children.Clear();
        if (_deck is null) return;
        _selectedElements.RemoveWhere(id => _deck.Settings.Find(id) is not { Visible: true });
        if (_selected is not null && !_selectedElements.Contains(_selected))
            _selected = _selectedElements.FirstOrDefault();
        if (_deck.Settings.SnapToGrid)
            _overlay.Children.Add(new LayoutGrid { Width = DeckScreen.ScreenWidth, Height = DeckScreen.ScreenHeight });
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
        var selected = _selectedElements.Contains(element.Id);
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
        frame.MouseLeave += (_, _) => label.Opacity = _selectedElements.Contains(element.Id) || isModule ? 1 : 0;
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

        var controlClick = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (controlClick)
        {
            if (!_selectedElements.Add(element.Id)) _selectedElements.Remove(element.Id);
            _selected = _selectedElements.Contains(element.Id) ? element.Id : _selectedElements.FirstOrDefault();
            RefreshSelectionStyle();
            if (!_selectedElements.Contains(element.Id))
            {
                e.Handled = true;
                return;
            }
        }
        else if (!_selectedElements.Contains(element.Id) || _selectedElements.Count > 1)
        {
            _selectedElements.Clear();
            _selectedElements.Add(element.Id);
            _selected = element.Id;
            RefreshSelectionStyle();
        }

        _resizing = e.OriginalSource is DependencyObject source && IsInResizeHandle(source, frame);
        _dragFrame = frame;
        _dragElement = element;
        _dragStart = e.GetPosition(_overlay);
        _startRect = new Rect(element.X, element.Y, element.Width, element.Height);
        _startRects.Clear();
        foreach (var selectedId in _selectedElements)
            if (_deck?.Settings.Find(selectedId) is { } selectedElement)
                _startRects[selectedElement] = new Rect(selectedElement.X, selectedElement.Y, selectedElement.Width, selectedElement.Height);
        // Clic sur une tuile du bloc d'applications : on déplace la tuile (pas le bloc) ; un simple clic la sélectionne,
        // un double-clic ouvre sa configuration.
        if (!controlClick && _selectedElements.Count == 1 && !_resizing && TryBeginTileDrag(element, _dragStart))
        {
            _dragFrame = null;
            _dragElement = null;
            _startRects.Clear();
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
        if (controlClick && _selectedElements.Count > 1) SelectTile(null);
        _overlay.CaptureMouse();
        e.Handled = true;
    }

    private void RefreshSelectionStyle()
    {
        foreach (var frame in _overlay.Children.OfType<Border>())
        {
            var selected = frame.Tag is DeckElement element && _selectedElements.Contains(element.Id);
            frame.BorderThickness = new Thickness(selected ? 4 : 2);
            frame.BorderBrush = AccentBrush(selected ? (byte)0xFF : (byte)0x99);
            frame.Background = new SolidColorBrush(Color.FromArgb(selected ? (byte)0x22 : (byte)0x08, 0x00, 0xE0, 0xFF));
            if (frame.Child is Grid { Children: [Border label, ..] })
                label.Opacity = selected || frame.IsMouseOver || label.Tag is GripTag ? 1 : 0;
        }
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
            foreach (var (element, start) in _startRects)
            {
                element.Width = Math.Clamp(SnapValue(start.Width + delta.X), MinSize, W * 2);
                element.Height = Math.Clamp(SnapValue(start.Height + delta.Y), MinSize, H * 2);
                element.Scale = null;
                _deck.Place(element.Id);
                if (_overlay.Children.OfType<Border>().FirstOrDefault(candidate => candidate.Tag == element) is { } resizeFrame)
                    Place(resizeFrame, element);
            }
        }
        else
        {
            var x = _startRect.X + delta.X;
            var y = _startRect.Y + delta.Y;
            if (_deck.Settings.SnapToGrid)
            {
                x = Round(x);
                y = Round(y);
            }
            var (snapX, snapY) = SnapToAlignment(x, y);
            var (moveX, moveY) = ClampGroupDelta(snapX - _startRect.X, snapY - _startRect.Y, W, H);
            foreach (var (element, start) in _startRects)
            {
                element.X = start.X + moveX;
                element.Y = start.Y + moveY;
                _deck.Place(element.Id);
                if (_overlay.Children.OfType<Border>().FirstOrDefault(candidate => candidate.Tag == element) is { } moveFrame)
                    Place(moveFrame, element);
            }
        }
        SyncGridLines();
    }

    private (double X, double Y) SnapToAlignment(double x, double y)
    {
        ClearAlignmentGuides();
        if (_deck?.Settings.SnapToGrid != true || _dragElement is null) return (x, y);
        const double tolerance = 10;
        var moving = new Rect(x, y, _dragElement.Width, _dragElement.Height);
        double? bestX = null, bestY = null, guideX = null, guideY = null;
        foreach (var target in _deck.Settings.Elements.Where(e => e.Visible && !_selectedElements.Contains(e.Id)))
        {
            var other = new Rect(target.X, target.Y, target.Width, target.Height);
            foreach (var sourceX in new[] { moving.Left, moving.Left + moving.Width / 2, moving.Right })
            foreach (var targetX in new[] { other.Left, other.Left + other.Width / 2, other.Right })
            {
                var difference = targetX - sourceX;
                if (Math.Abs(difference) <= tolerance && (bestX is null || Math.Abs(difference) < Math.Abs(bestX.Value)))
                {
                    bestX = difference;
                    guideX = targetX;
                }
            }
            foreach (var sourceY in new[] { moving.Top, moving.Top + moving.Height / 2, moving.Bottom })
            foreach (var targetY in new[] { other.Top, other.Top + other.Height / 2, other.Bottom })
            {
                var difference = targetY - sourceY;
                if (Math.Abs(difference) <= tolerance && (bestY is null || Math.Abs(difference) < Math.Abs(bestY.Value)))
                {
                    bestY = difference;
                    guideY = targetY;
                }
            }
        }
        if (bestX is { } dx) x += dx;
        if (bestY is { } dy) y += dy;
        if (guideX is { } gx) AddAlignmentGuide(gx, vertical: true);
        if (guideY is { } gy) AddAlignmentGuide(gy, vertical: false);
        return (x, y);
    }

    private (double X, double Y) ClampGroupDelta(double x, double y, double width, double height)
    {
        var minX = double.NegativeInfinity;
        var maxX = double.PositiveInfinity;
        var minY = double.NegativeInfinity;
        var maxY = double.PositiveInfinity;
        foreach (var (element, start) in _startRects)
        {
            minX = Math.Max(minX, KeepVisible - start.Right);
            maxX = Math.Min(maxX, width - KeepVisible - start.Left);
            minY = Math.Max(minY, KeepVisible - start.Bottom);
            maxY = Math.Min(maxY, height - KeepVisible - start.Top);
        }
        return (Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }

    private void AddAlignmentGuide(double coordinate, bool vertical)
    {
        var line = new System.Windows.Shapes.Line
        {
            X1 = vertical ? coordinate : 0,
            X2 = vertical ? coordinate : DeckScreen.ScreenWidth,
            Y1 = vertical ? 0 : coordinate,
            Y2 = vertical ? DeckScreen.ScreenHeight : coordinate,
            Stroke = AccentBrush(0xDD),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            IsHitTestVisible = false
        };
        _alignmentGuides.Add(line);
        _overlay.Children.Add(line);
    }

    private void ClearAlignmentGuides()
    {
        foreach (var line in _alignmentGuides) _overlay.Children.Remove(line);
        _alignmentGuides.Clear();
    }

    private double SnapValue(double value) => _deck?.Settings.SnapToGrid == true ? Round(value) : value;

    private static double Round(double value) => Math.Round(value / Snap) * Snap;

    private void EndDrag()
    {
        if (_tileDrag is not null)
        {
            EndTileDrag();
            return;
        }
        if (_dragFrame is null) return;
        var moved = _startRects.Any(pair => pair.Value != new Rect(pair.Key.X, pair.Key.Y, pair.Key.Width, pair.Key.Height));
        _dragFrame = null;
        _dragElement = null;
        _startRects.Clear();
        ClearAlignmentGuides();
        if (_overlay.IsMouseCaptured) _overlay.ReleaseMouseCapture();
        if (moved) LayoutChanged?.Invoke();
    }

    private sealed class LayoutGrid : FrameworkElement
    {
        private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);

        protected override void OnRender(DrawingContext drawingContext)
        {
            const double spacing = 40;
            for (var x = spacing; x < DeckScreen.ScreenWidth; x += spacing)
                drawingContext.DrawLine(GridPen, new Point(x, 0), new Point(x, DeckScreen.ScreenHeight));
            for (var y = spacing; y < DeckScreen.ScreenHeight; y += spacing)
                drawingContext.DrawLine(GridPen, new Point(0, y), new Point(DeckScreen.ScreenWidth, y));
        }
    }
}
