using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SysDeck.Services;

namespace SysDeck.Controls;

/// <summary>
/// Cadre d'édition posé sur un élément déplaçable : glisser pour déplacer, molette pour redimensionner,
/// clic droit pour réinitialiser ou masquer.
/// </summary>
public sealed class MovableAdorner : Adorner
{
    private readonly string _key;
    private readonly Func<string, ElementLayout> _getLayout;
    private readonly Action<string> _apply;
    private readonly Action _save;

    private Point _dragStart;
    private double _startX, _startY;
    private bool _dragging;

    private readonly Func<IEnumerable<string>> _hiddenKeys;
    private readonly Action<string> _show;

    public MovableAdorner(FrameworkElement adorned, string key, Func<string, ElementLayout> getLayout, Action<string> apply, Action save,
        Func<IEnumerable<string>> hiddenKeys, Action<string> show)
        : base(adorned)
    {
        _hiddenKeys = hiddenKeys;
        _show = show;
        _key = key;
        _getLayout = getLayout;
        _apply = apply;
        _save = save;
        // Retiré du visuel (fin du mode édition…) avec une sauvegarde en attente : on ne la perd pas.
        Unloaded += (_, _) => FlushPending();
        Cursor = System.Windows.Input.Cursors.SizeAll;
        ToolTip = $"{Movable.LabelFor(key)} — glisser : déplacer · molette : taille · clic droit : options";
        ContextMenu = BuildMenu();
        // Le contenu change (valeur plus longue…) sans que l'emplacement change : on suit son contour.
        Loaded += (_, _) => LayoutUpdated += OnLayoutUpdated;
        Unloaded += (_, _) => LayoutUpdated -= OnLayoutUpdated;
    }

    private Rect _lastBounds = Rect.Empty;

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (AdornedElement is FrameworkElement element && Movable.ContentBounds(element) != _lastBounds)
            InvalidateVisual();
    }

    private UIElement? AdornedParent => VisualTreeHelper.GetParent(AdornedElement) as UIElement;

    protected override void OnRender(DrawingContext dc)
    {
        var rect = _lastBounds = AdornedElement is FrameworkElement element
            ? Movable.ContentBounds(element)
            : new Rect(AdornedElement.RenderSize);
        rect.Inflate(2, 2);

        var stroke = (Brush?)TryFindResource("GaugeGradientBrush") ?? Brushes.White;
        var pen = new Pen(stroke, IsMouseOver || _dragging ? 1.8 : 1.1) { DashStyle = DashStyles.Dash };
        // Remplissage quasi transparent : rend tout le cadre cliquable, y compris les vides de l'élément.
        var fill = new SolidColorBrush(Color.FromArgb((byte)(IsMouseOver || _dragging ? 0x30 : 0x12), 0xFF, 0xFF, 0xFF));
        dc.DrawRoundedRectangle(fill, pen, rect, 4, 4);

        // Étiquette uniquement au survol : sur les petits modules, toutes affichées se chevaucheraient.
        if (!IsMouseOver && !_dragging) return;

        var label = new FormattedText(Movable.LabelFor(_key), CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var tag = new Rect(rect.Left, rect.Top - label.Height - 3, label.Width + 8, label.Height + 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x11, 0x18)), null, tag, 3, 3);
        dc.DrawText(label, new Point(tag.Left + 4, tag.Top + 1));
    }

    protected override void OnMouseEnter(MouseEventArgs e) => InvalidateVisual();
    protected override void OnMouseLeave(MouseEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (AdornedParent is not { } parent) return;
        var layout = _getLayout(_key);
        _dragStart = e.GetPosition(parent);
        _startX = layout.X;
        _startY = layout.Y;
        _dragging = CaptureMouse();
        e.Handled = true; // pas de déplacement de la fenêtre du widget
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || AdornedParent is not { } parent) return;
        var position = e.GetPosition(parent);
        var layout = _getLayout(_key);
        layout.X = Math.Round(_startX + position.X - _dragStart.X);
        layout.Y = Math.Round(_startY + position.Y - _dragStart.Y);
        _apply(_key);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        InvalidateVisual();
        SaveNow();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var layout = _getLayout(_key);
        layout.Scale = Math.Round(Math.Clamp(layout.Scale * (e.Delta > 0 ? 1.08 : 1 / 1.08), 0.3, 4), 3);
        _apply(_key);
        SaveSoon();
        e.Handled = true;
    }

    // Une molette envoie des dizaines de crans : on regroupe en une seule écriture du fichier de config.
    private DispatcherTimer? _saveTimer;

    private void SaveSoon()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick -= OnSaveTimer;
        _saveTimer.Tick += OnSaveTimer;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimer(object? sender, EventArgs e) => SaveNow();

    private void SaveNow()
    {
        _saveTimer?.Stop();
        _save();
    }

    private void FlushPending()
    {
        if (_saveTimer is { IsEnabled: true }) SaveNow();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, Action<ElementLayout> action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                action(_getLayout(_key));
                _apply(_key);
                SaveNow();
            };
            return item;
        }

        var toggleVisibility = Item("Masquer cet élément", l => l.Hidden = !l.Hidden);

        menu.Items.Add(Item("Agrandir", l => l.Scale = Math.Min(4, Math.Round(l.Scale * 1.15, 3))));
        menu.Items.Add(Item("Réduire", l => l.Scale = Math.Max(0.3, Math.Round(l.Scale / 1.15, 3))));
        menu.Items.Add(Item("Taille d'origine", l => l.Scale = 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Position d'origine", l => l.X = l.Y = 0));
        menu.Items.Add(toggleVisibility);
        menu.Items.Add(new Separator());
        // Les éléments masqués n'ont plus de cadre : on les réaffiche d'ici.
        Movable.AddHiddenMenu(menu, _hiddenKeys, _show);
        menu.Items.Add(Item("Tout réinitialiser pour cet élément", l => { l.X = l.Y = 0; l.Scale = 1; l.Hidden = false; }));
        return menu;
    }
}
