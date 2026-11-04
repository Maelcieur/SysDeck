using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SysDeck.Deck;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Controls;

/// <summary>
/// Les deux Joy-Con fixés sur la Switch, dessinés : chaque touche (et chaque stick) se clique pour lui choisir une
/// action ; une étiquette reliée par un trait montre l'action actuelle. Les touches s'allument quand on les presse,
/// et une action de la bibliothèque peut être glissée dessus.
/// </summary>
public sealed class JoyConDiagram : Viewbox
{
    private const double W = 960, H = 470;
    private static readonly Color Accent = Color.FromRgb(0x5B, 0x8D, 0xEF);
    private static readonly Brush BodyLeft = Frozen(Color.FromRgb(0x1F, 0x6F, 0xB8));
    private static readonly Brush BodyRight = Frozen(Color.FromRgb(0xC8, 0x3A, 0x3A));
    private static readonly Brush KeyFill = Frozen(Color.FromRgb(0x22, 0x24, 0x2C));
    private static readonly Brush KeyStroke = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LineBrush = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));

    private readonly Canvas _canvas = new() { Width = W, Height = H };
    private readonly Dictionary<JoyConButton, Shape> _keys = new();
    private readonly Dictionary<JoyConButton, (Border Box, TextBlock Action)> _labels = new();
    private readonly Border _leftBody, _rightBody;
    private JoyConButton? _selected;
    private uint _down;

    /// <summary>Touche ou stick cliqué (le stick est représenté par son clic : LStick / RStick).</summary>
    public event Action<JoyConButton>? Selected;

    /// <summary>Action de la bibliothèque glissée sur une touche.</summary>
    public event Action<JoyConButton, object>? ActionDropped;

    /// <summary>Texte de l'action d'une touche (étiquettes).</summary>
    public Func<JoyConButton, string> Describe { get; set; } = _ => "";

    public JoyConDiagram()
    {
        Stretch = Stretch.Uniform;
        Child = _canvas;

        // Corps : Joy-Con gauche (bleu), écran de la Switch, Joy-Con droit (rouge).
        _leftBody = Body(250, 70, new CornerRadius(80, 0, 0, 80), BodyLeft);
        _rightBody = Body(630, 70, new CornerRadius(0, 80, 80, 0), BodyRight);
        var console = new Border
        {
            Width = 220, Height = 340, Background = Frozen(Color.FromRgb(0x18, 0x19, 0x1E)),
            BorderBrush = KeyStroke, BorderThickness = new Thickness(1),
            Child = new Border
            {
                Margin = new Thickness(14, 22, 14, 22), Background = Frozen(Color.FromRgb(0x0B, 0x0C, 0x10)), CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Text = "SysDeck", Foreground = KeyStroke, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 }
            }
        };
        Place(console, 410, 70);

        // Touches du Joy-Con gauche (centres en coordonnées du dessin).
        Pill(JoyConButton.ZL, 270, 30, 80, 20, "ZL");
        Pill(JoyConButton.L, 270, 54, 80, 18, "L");
        Pill(JoyConButton.Minus, 372, 92, 26, 12, "−");
        Round(JoyConButton.LStick, 330, 150, 32, "");
        Round(JoyConButton.Up, 330, 225, 17, "▲");
        Round(JoyConButton.Down, 330, 295, 17, "▼");
        Round(JoyConButton.Left, 295, 260, 17, "◀");
        Round(JoyConButton.Right, 365, 260, 17, "▶");
        Square(JoyConButton.Capture, 352, 352, 22);

        // Joy-Con droit.
        Pill(JoyConButton.ZR, 610, 30, 80, 20, "ZR");
        Pill(JoyConButton.R, 610, 54, 80, 18, "R");
        Pill(JoyConButton.Plus, 648, 92, 26, 12, "+");
        Round(JoyConButton.X, 710, 118, 17, "X");
        Round(JoyConButton.B, 710, 186, 17, "B");
        Round(JoyConButton.Y, 676, 152, 17, "Y");
        Round(JoyConButton.A, 744, 152, 17, "A");
        Round(JoyConButton.RStick, 690, 265, 32, "");
        Round(JoyConButton.Home, 668, 352, 13, "⌂");

        // Étiquettes reliées aux touches, à gauche et à droite (SL / SR, cachés par les rails, ne sont pas proposés).
        var left = new[]
        {
            JoyConButton.ZL, JoyConButton.L, JoyConButton.Minus, JoyConButton.LStick, JoyConButton.Up, JoyConButton.Left,
            JoyConButton.Right, JoyConButton.Down, JoyConButton.Capture
        };
        var right = new[]
        {
            JoyConButton.ZR, JoyConButton.R, JoyConButton.Plus, JoyConButton.X, JoyConButton.Y, JoyConButton.A,
            JoyConButton.B, JoyConButton.RStick, JoyConButton.Home
        };
        for (var i = 0; i < left.Length; i++) Label(left[i], 6, 20 + i * 48, rightSide: false);
        for (var i = 0; i < right.Length; i++) Label(right[i], W - 186, 20 + i * 48, rightSide: true);
    }

    /// <summary>Touche choisie (entourée), null pour aucune.</summary>
    public JoyConButton? SelectedButton
    {
        get => _selected;
        set
        {
            _selected = value;
            Paint();
        }
    }

    /// <summary>Met à jour le texte des étiquettes (affectations changées, autre page).</summary>
    public void RefreshLabels()
    {
        foreach (var (button, (_, action)) in _labels) action.Text = Describe(button);
    }

    /// <summary>État reçu de la Switch : touches enfoncées allumées, Joy-Con absents atténués.</summary>
    public void ShowState(Deck.DeckProtocol.JoyConState state)
    {
        _down = state.Buttons;
        _leftBody.Opacity = state.LeftConnected ? 1 : 0.45;
        _rightBody.Opacity = state.RightConnected ? 1 : 0.45;
        Paint();
    }

    private void Paint()
    {
        foreach (var (button, key) in _keys)
        {
            var down = (_down & (1u << (int)button)) != 0;
            var selected = button == _selected;
            key.Fill = down ? new SolidColorBrush(Accent) : KeyFill;
            key.Stroke = selected ? new SolidColorBrush(Accent) : KeyStroke;
            key.StrokeThickness = selected ? 4 : 1.5;
            if (_labels.TryGetValue(button, out var label))
            {
                label.Box.BorderBrush = selected ? new SolidColorBrush(Accent) : KeyStroke;
                label.Box.BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }
    }

    // ---------- Construction ----------

    private Border Body(double x, double y, CornerRadius corners, Brush fill)
    {
        var body = new Border { Width = 160, Height = 340, CornerRadius = corners, Background = fill, BorderBrush = KeyStroke, BorderThickness = new Thickness(1) };
        Place(body, x, y);
        return body;
    }

    private void Round(JoyConButton button, double cx, double cy, double r, string text)
        => AddKey(button, new Ellipse { Width = r * 2, Height = r * 2 }, cx - r, cy - r, text);

    private void Pill(JoyConButton button, double cx, double cy, double w, double h, string text)
        => AddKey(button, new Rectangle { Width = w, Height = h, RadiusX = Math.Min(w, h) / 2, RadiusY = Math.Min(w, h) / 2 }, cx - w / 2, cy - h / 2, text);

    private void Square(JoyConButton button, double cx, double cy, double size)
        => AddKey(button, new Rectangle { Width = size, Height = size, RadiusX = 4, RadiusY = 4 }, cx - size / 2, cy - size / 2, "●");

    private void AddKey(JoyConButton button, Shape shape, double x, double y, string text)
    {
        shape.Fill = KeyFill;
        shape.Stroke = KeyStroke;
        shape.StrokeThickness = 1.5;
        var grid = new Grid
        {
            Cursor = Cursors.Hand,
            ToolTip = JoyConInfo.Label(button),
            Background = Brushes.Transparent,
            AllowDrop = true,
            Children =
            {
                shape,
                new TextBlock
                {
                    Text = text, Foreground = Brushes.White, FontSize = Math.Min(shape.Width, shape.Height) * 0.55,
                    FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
                }
            }
        };
        Wire(grid, button);
        Place(grid, x, y);
        Panel.SetZIndex(grid, 10);
        _keys[button] = shape;
    }

    private void Label(JoyConButton button, double x, double y, bool rightSide)
    {
        var action = new TextBlock { FontSize = 11.5, Foreground = Frozen(Color.FromRgb(0xC9, 0xCC, 0xD6)), TextTrimming = TextTrimming.CharacterEllipsis };
        var box = new Border
        {
            Width = 180, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 1, 8, 1),
            Background = Frozen(Color.FromRgb(0x1A, 0x1C, 0x23)), BorderBrush = KeyStroke, BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand, AllowDrop = true,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { new TextBlock { Text = JoyConInfo.Label(button), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }, action }
            }
        };
        Wire(box, button);
        Place(box, x, y);
        _labels[button] = (box, action);

        // Trait de l'étiquette à la touche.
        if (_keys.TryGetValue(button, out var key) && key.Parent is FrameworkElement keyHost)
        {
            var kx = Canvas.GetLeft(keyHost) + key.Width / 2;
            var ky = Canvas.GetTop(keyHost) + key.Height / 2;
            var line = new Line
            {
                X1 = rightSide ? x : x + box.Width, Y1 = y + box.Height / 2, X2 = kx, Y2 = ky,
                Stroke = LineBrush, StrokeThickness = 1, IsHitTestVisible = false
            };
            Panel.SetZIndex(line, 5);
            _canvas.Children.Add(line);
        }
    }

    private void Wire(FrameworkElement element, JoyConButton button)
    {
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SelectedButton = button;
            Selected?.Invoke(button);
        };
        element.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DeckLayoutEditor.ActionDataFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        element.Drop += (_, e) =>
        {
            if (e.Data.GetData(DeckLayoutEditor.ActionDataFormat) is not { } data) return;
            e.Handled = true;
            SelectedButton = button;
            Dispatcher.BeginInvoke(() => ActionDropped?.Invoke(button, data));
        };
    }

    private void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _canvas.Children.Add(element);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
