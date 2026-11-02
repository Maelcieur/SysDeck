using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SysDeck.Services;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;

namespace SysDeck;

/// <summary>
/// Alerte affichée en bas à droite de l'écran, au-dessus de tout : carte sombre arrondie, pastille colorée
/// avec un émoji, titre, message, valeur mise en avant et barre de temps restant. Elle entre en glissant,
/// reste tant que la souris est dessus et se ferme d'un clic.
/// </summary>
public sealed class AlertToast : Window
{
    public const double ToastWidth = 400;
    private const double ShadowSpace = 18; // marge transparente réservée à l'ombre

    private readonly TimeSpan _duration;
    private readonly DispatcherTimer _closeTimer = new();
    private readonly ScaleTransform _progress = new(1, 1);
    private readonly TranslateTransform _slide = new(60, 0);
    private bool _closing;

    public AlertKind Kind { get; }

    /// <summary>Hauteur occupée à l'écran (ombre comprise), pour empiler les alertes.</summary>
    public double StackHeight => ActualHeight;

    private readonly AlertSettings _look;
    private readonly bool _fromLeft;

    public AlertToast(Alert alert, AlertSettings look)
    {
        Kind = alert.Kind;
        _look = look;
        _duration = TimeSpan.FromSeconds(Math.Clamp(look.DurationSeconds, 3, 60));
        _fromLeft = look.Position is "BottomLeft" or "TopLeft";
        _slide.X = _fromLeft ? -60 : 60;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // ne vole pas le clavier au jeu ou à l'application en cours
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = ToastWidth * Math.Clamp(look.Scale, 0.7, 1.5) + ShadowSpace * 2; // la marge d'ombre n'est pas mise à l'échelle
        FontFamily = new FontFamily("Segoe UI");
        Opacity = 0;

        var (start, end) = Accent(alert);
        Content = Build(alert, start, end);

        _closeTimer.Interval = _duration;
        _closeTimer.Tick += (_, _) => Dismiss();
        Loaded += (_, _) => Appear();
        MouseEnter += (_, _) => Pause();
        MouseLeave += (_, _) => Resume();
        MouseLeftButtonUp += (_, _) => Dismiss();
    }

    /// <summary>Couleurs selon le type d'alerte (rouge dès qu'elle est critique), ou couleur choisie dans les réglages.</summary>
    private (Color Start, Color End) Accent(Alert alert)
    {
        if (!_look.AutoAccent)
        {
            var c = ThemeService.Parse(_look.AccentColor, Color.FromRgb(0x00, 0xE0, 0xFF));
            return (c, Color.FromRgb((byte)Math.Min(255, c.R + 60), (byte)Math.Min(255, c.G + 60), (byte)Math.Min(255, c.B + 60)));
        }
        if (alert.Critical) return (Color.FromRgb(0xFF, 0x3B, 0x5C), Color.FromRgb(0xFF, 0x8A, 0x3D));
        return alert.Kind switch
        {
            AlertKind.CpuTemperature or AlertKind.GpuTemperature => (Color.FromRgb(0xFF, 0x8A, 0x00), Color.FromRgb(0xFF, 0xC8, 0x3D)),
            AlertKind.MouseBattery => (Color.FromRgb(0xFF, 0xB0, 0x20), Color.FromRgb(0xF5, 0xE0, 0x4C)),
            _ => (Color.FromRgb(0xA7, 0x7B, 0xFF), Color.FromRgb(0x5C, 0xC8, 0xFF))
        };
    }

    private static string IconFor(AlertKind kind) => kind switch
    {
        AlertKind.CpuTemperature or AlertKind.GpuTemperature => "🌡️",
        AlertKind.MouseBattery => "🪫",
        _ => "✨"
    };

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private UIElement Build(Alert alert, Color start, Color end)
    {
        var accent = new LinearGradientBrush(start, end, 0);

        // Pastille : halo dégradé + émoji en couleur.
        var icon = new Image { Width = 28, Height = 28 };
        Emoji.Wpf.Image.SetSource(icon, IconFor(alert.Kind));
        var badge = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(16),
            Background = new LinearGradientBrush(WithAlpha(start, 0x55), WithAlpha(end, 0x22), 45),
            BorderBrush = new SolidColorBrush(WithAlpha(start, 0x66)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = icon
        };

        var caption = new TextBlock
        {
            Text = alert.Critical ? "ALERTE CRITIQUE" : "ALERTE SYSDECK",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            Margin = new Thickness(0, 0, 0, 2)
        };
        var title = new TextBlock
        {
            Text = alert.Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var message = new TextBlock
        {
            Text = alert.Message,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9D, 0xB2)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        var texts = new StackPanel { Margin = new Thickness(14, 0, 10, 0), Children = { caption, title, message } };

        var value = new TextBlock
        {
            Text = alert.Value,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = _look.ShowGlow ? new DropShadowEffect { Color = start, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 } : null
        };

        var close = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8D, 0xA0)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -8, 0),
            ToolTip = "Fermer"
        };
        close.Click += (_, _) => Dismiss();

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(badge);
        row.Children.Add(texts);
        row.Children.Add(value);

        // Barre du temps restant, qui se vide de droite à gauche.
        _progress.CenterX = 0;
        var bar = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = accent,
            Margin = new Thickness(0, 14, 0, 0),
            RenderTransform = _progress
        };
        var track = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 14, 0, 0)
        };
        var progressGrid = new Grid { Children = { track, bar } };

        var body = new Grid { Children = { new StackPanel { Children = { row, progressGrid } }, close } };

        // Fond réglable : couleur et transparence, avec un léger dégradé plus sombre vers le bas.
        var bg = ThemeService.Parse(_look.BackgroundColor, Color.FromRgb(0x1A, 0x1C, 0x27));
        var alpha = (byte)Math.Round(Math.Clamp(_look.BackgroundOpacity, 0, 1) * 255);
        var darker = Color.FromArgb(alpha, (byte)(bg.R * 0.7), (byte)(bg.G * 0.7), (byte)(bg.B * 0.7));
        var radius = Math.Clamp(_look.CornerRadius, 0, 30);
        var scale = Math.Clamp(_look.Scale, 0.7, 1.5);

        var card = new Border
        {
            Width = ToastWidth,
            CornerRadius = new CornerRadius(radius),
            Background = new LinearGradientBrush(Color.FromArgb(alpha, bg.R, bg.G, bg.B), darker, 90),
            BorderBrush = _look.ShowBorder ? new LinearGradientBrush(WithAlpha(start, 0x70), Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0) : null,
            BorderThickness = new Thickness(_look.ShowBorder ? 1 : 0),
            Margin = new Thickness(ShadowSpace),
            Effect = _look.ShowGlow ? new DropShadowEffect { Color = WithAlpha(start, 0xFF), BlurRadius = 28, ShadowDepth = 4, Opacity = 0.35 } : null,
            Child = new Border { Padding = new Thickness(18, 16, 18, 14), Child = body },
            Cursor = Cursors.Hand,
            LayoutTransform = new ScaleTransform(scale, scale)
        };
        // Glissement sur un conteneur : la mise à l'échelle (LayoutTransform) et le mouvement restent indépendants.
        return new Border { Child = card, RenderTransform = _slide };
    }

    private void Appear()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_fromLeft ? -60 : 60, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } });
        StartCountdown();
    }

    private void StartCountdown()
    {
        _progress.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, _duration));
        _closeTimer.Start();
    }

    /// <summary>Souris dessus : l'alerte reste affichée, la barre se remplit à nouveau.</summary>
    private void Pause()
    {
        if (_closing) return;
        _closeTimer.Stop();
        _progress.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    private void Resume()
    {
        if (!_closing) StartCountdown();
    }

    /// <summary>Glisse vers la droite en s'effaçant, puis se ferme.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _closeTimer.Stop();
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_fromLeft ? -70 : 70, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Déplacement en douceur quand une alerte au-dessous disparaît.</summary>
    public void MoveTo(double left, double top, bool animate)
    {
        Left = left;
        if (!animate || !IsLoaded)
        {
            BeginAnimation(TopProperty, null);
            Top = top;
            return;
        }
        BeginAnimation(TopProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
