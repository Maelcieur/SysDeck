using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysDeck.Infrastructure;
using SysDeck.Interop;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using WpfImage = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Fond de console accroché AU BUREAU (comme les widgets) : il est toujours sous les fenêtres normales,
/// donc il ne gêne jamais le jeu, et c'est lui qu'on voit à la place du bureau Windows si le jeu se ferme ou se réduit.
/// </summary>
public sealed class ConsoleBackdrop
{
    private readonly Window _window;
    private readonly WpfImage _hero;
    private readonly TextBlock _title, _text;
    private Rect _bounds;

    public ConsoleBackdrop(ConsoleTheme theme)
    {
        _hero = new WpfImage { Stretch = Stretch.UniformToFill, Opacity = 0.35 };
        _title = new TextBlock { Foreground = theme.Brush(theme.Text), FontSize = 64, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _text = new TextBlock { Foreground = theme.Brush(theme.SubText), FontSize = 28, Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 1400 };
        center.Children.Add(_title);
        center.Children.Add(_text);
        var stage = new Grid { Width = 1920, Height = 1080 };
        stage.Children.Add(center);
        var root = new Grid { Background = new RadialGradientBrush(theme.Bg2, theme.Bg) { Center = new Point(0.75, 0.3), GradientOrigin = new Point(0.75, 0.3), RadiusX = 0.9, RadiusY = 0.9 } };
        root.Children.Add(_hero);
        root.Children.Add(new Rectangle { Fill = new RadialGradientBrush(theme.WithAlpha(theme.Bg, 0x50), theme.Bg) { RadiusX = 0.8, RadiusY = 0.8 } });
        root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = stage });

        _window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Background = theme.Brush(theme.Bg), Content = root, Title = "SysDeck - fond console",
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Width = 100, Height = 100,
        };
        _window.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(_window).Handle;
            if (DesktopWidgetHost.AttachToDesktop(h)) return;
            // Windows 11 24H2 et suivants : le calque du fond d'écran (WorkerW) est un ENFANT de Progman.
            var progman = FindWindow("Progman", null);
            var worker = progman == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            if (worker != IntPtr.Zero && SetParent(h, worker) != IntPtr.Zero)
            {
                Log.Info("Mode console : fond accroché au bureau (Windows 11 24H2).");
                return;
            }
            // Dernier recours : fenêtre normale qui ne prend jamais le focus, posée juste au-dessus du bureau.
            _topLevel = true;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 /* WS_EX_NOACTIVATE */);
            Log.Warn("Mode console : fond non accroché au bureau, posé juste au-dessus.");
        };
    }

    /// <summary>Affiche le fond sur l'écran donné (pixels physiques).</summary>
    public void Show(Rect boundsPx)
    {
        _bounds = boundsPx;
        try
        {
            if (!_window.IsVisible) _window.Show();
            Place();
            _window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Place);
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : fond de console", ex);
        }
    }

    public void SetGame(string title, string? heroPath, string message)
    {
        _title.Text = title;
        _text.Text = message;
        _hero.Source = Load(heroPath);
    }

    public void Close()
    {
        try { _window.Close(); } catch { }
    }

    /// <summary>Fenêtre enfant du bureau : coordonnées relatives à son parent (WorkerW).</summary>
    private void Place()
    {
        var h = new WindowInteropHelper(_window).Handle;
        if (h == IntPtr.Zero) return;
        var pt = new POINT { X = (int)_bounds.X, Y = (int)_bounds.Y };
        var parent = GetParent(h);
        if (parent != IntPtr.Zero) ScreenToClient(parent, ref pt);
        // Enfant du bureau : en bas de ses frères. Fenêtre normale : juste au-dessus du bureau (sous la fenêtre
        // qui précède Progman), jamais devant une application.
        var after = new IntPtr(1); // HWND_BOTTOM
        if (_topLevel)
        {
            var progman = FindWindow("Progman", null);
            var above = progman == IntPtr.Zero ? IntPtr.Zero : GetWindow(progman, 3 /* GW_HWNDPREV */);
            after = above != IntPtr.Zero && above != h ? above : new IntPtr(1);
        }
        SetWindowPos(h, after, pt.X, pt.Y, (int)_bounds.Width, (int)_bounds.Height, 0x0010 | 0x0040);
    }

    private static BitmapImage? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.DecodePixelWidth = 1920;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    private bool _topLevel;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
