using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SysDeck.Infrastructure;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Écran de chargement noir posé sur TOUS les écrans pendant les bascules (entrée / sortie du Mode console),
/// pour ne jamais montrer le bureau Windows. Une fenêtre par écran ; quand Windows allume ou éteint un écran,
/// les fenêtres sont reconstruites sur les nouveaux écrans.
/// </summary>
public sealed class ConsoleSplash
{
    private readonly List<Window> _windows = new();
    private readonly DispatcherTimer _watch;
    private string _message;
    private string _signature = "";
    private bool _closed;

    private ConsoleSplash(string message)
    {
        _message = message;
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _watch.Tick += (_, _) => { if (Signature() != _signature) Rebuild(); };
    }

    /// <summary>Affiche l'écran de chargement sur tous les écrans actifs.</summary>
    public static ConsoleSplash Show(string message)
    {
        var s = new ConsoleSplash(message);
        s.Rebuild();
        s._watch.Start();
        return s;
    }

    public void SetMessage(string message)
    {
        _message = message;
        foreach (var w in _windows)
            if (w.Tag is TextBlock t) t.Text = message;
    }

    /// <summary>Fondu puis fermeture.</summary>
    public async Task CloseAsync(int fadeMs = 350)
    {
        if (_closed) return;
        _closed = true;
        _watch.Stop();
        foreach (var w in _windows)
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(fadeMs)));
        await Task.Delay(fadeMs + 30);
        CloseAll();
    }

    private void CloseAll()
    {
        foreach (var w in _windows)
            try { w.Close(); } catch { }
        _windows.Clear();
    }

    private void Rebuild()
    {
        if (_closed) return;
        try
        {
            var monitors = Monitors();
            _signature = Signature(monitors);
            var old = _windows.ToList();
            _windows.Clear();
            foreach (var r in monitors) _windows.Add(Create(r));
            // Les anciennes fenêtres sont fermées APRÈS l'ouverture des nouvelles : pas de trou visible.
            foreach (var w in old) try { w.Close(); } catch { }
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : écran de chargement", ex);
        }
    }

    private Window Create(RECT r)
    {
        var message = new TextBlock
        {
            Text = _message, Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA3, 0xB5)), FontSize = 30,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 34, 0, 0),
        };
        var root = new Grid { Width = 1920, Height = 1080 };
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(new TextBlock
        {
            Text = "SysDeck", Foreground = Brushes.White, FontSize = 86, FontWeight = FontWeights.Light,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        center.Children.Add(Spinner());
        center.Children.Add(message);
        root.Children.Add(center);

        var w = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x07, 0x09, 0x0E)), Topmost = true, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = r.Left, Top = r.Top, Width = 200, Height = 200,
            Content = new Viewbox { Stretch = Stretch.Uniform, Child = root }, Tag = message, Title = "SysDeck",
            Cursor = System.Windows.Input.Cursors.None,
        };
        w.Show();
        var hwnd = new WindowInteropHelper(w).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top - 1 /* pas « plein écran exact » : évite la resynchro VRR de la TV */, 0x0010 | 0x0040);
        // 2e passe : un changement de DPI entre écrans peut redimensionner la fenêtre après le 1er placement.
        w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            SetWindowPos(hwnd, new IntPtr(-1), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top - 1 /* pas « plein écran exact » : évite la resynchro VRR de la TV */, 0x0010 | 0x0040));
        return w;
    }

    private static FrameworkElement Spinner()
    {
        var arc = new Ellipse
        {
            Width = 64, Height = 64, StrokeThickness = 6, Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x5B, 0x8D, 0xEF)),
            StrokeDashArray = new DoubleCollection { 7, 4 }, StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var rot = new RotateTransform();
        arc.RenderTransform = rot;
        rot.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        return new Grid { Margin = new Thickness(0, 46, 0, 0), Children = { arc } };
    }

    // ---------- Écrans ----------

    private static string Signature() => Signature(Monitors());

    private static string Signature(List<RECT> monitors) =>
        string.Join(";", monitors.Select(m => $"{m.Left},{m.Top},{m.Right},{m.Bottom}"));

    private static List<RECT> Monitors()
    {
        var list = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT rc, IntPtr data) =>
        {
            list.Add(rc);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
