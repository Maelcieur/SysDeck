using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SysDeck.ConsoleMode;
using SysDeck.Infrastructure;

namespace SysDeck;

// Page Mode console : thème de l'interface TV (galerie avec aperçus animés), intro, aperçus sur l'écran du PC.
public partial class SettingsWindow
{
    private void InitConsoleAppearance()
    {
        ConsoleIntroCheck.IsChecked = ConsoleConfig.IntroEnabled;
        ConsoleSoundsCheck.IsChecked = ConsoleConfig.UiSounds;
        ConsoleIntroStyleBox.SelectedIndex = ConsoleConfig.IntroStyle switch { "auto" => 0, "elegant" => 1, _ => 2 };
        ConsoleVibrationCheck.IsChecked = ConsoleConfig.Vibration;
        ConsoleIntroFolderBox.Text = ConsoleConfig.IntroFolder;
        BuildConsoleThemeGallery();
    }

    private void BuildConsoleThemeGallery()
    {
        ConsoleThemeGallery.Children.Clear();
        var current = ConsoleThemes.Find(ConsoleConfig.ThemeId);
        ConsoleThemeName.Text = $"{current.Name} — {current.Era}";
        var accent = Color.FromRgb(0x5B, 0x8D, 0xEF);
        foreach (var theme in ConsoleThemes.All)
        {
            var t = theme;
            var on = t.Id == current.Id;
            var panel = new StackPanel { Width = 176, Margin = new Thickness(0, 0, 12, 12), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, ToolTip = $"{t.Name} — {t.Era}" };
            panel.Children.Add(new Border
            {
                Child = ConsoleWindow.ThemeCard(t, 160, 100), CornerRadius = new CornerRadius(10), Padding = new Thickness(2),
                BorderThickness = new Thickness(3), BorderBrush = on ? new SolidColorBrush(accent) : Brushes.Transparent,
                Effect = on ? new DropShadowEffect { Color = accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.7 } : null,
            });
            panel.Children.Add(new TextBlock { Text = t.Name, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(4, 6, 0, 0) });
            panel.MouseLeftButtonUp += (_, _) => SetConsoleTheme(t.Id);
            ConsoleThemeGallery.Children.Add(panel);
        }
    }

    private void SetConsoleTheme(string id)
    {
        if (ConsoleConfig.ThemeId == id) return;
        ConsoleConfig.ThemeId = id;
        ScheduleSave();
        BuildConsoleThemeGallery();
    }

    private void ConsoleIntroStyle_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_consoleLoading || ConsoleIntroStyleBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        ConsoleConfig.IntroStyle = (string)item.Tag;
        ScheduleSave();
    }

    private void ConsoleSounds_Changed(object sender, RoutedEventArgs e)
    {
        if (_consoleLoading) return;
        ConsoleConfig.UiSounds = ConsoleSoundsCheck.IsChecked == true;
        ScheduleSave();
    }

    private void ConsoleVibration_Changed(object sender, RoutedEventArgs e)
    {
        if (_consoleLoading) return;
        ConsoleConfig.Vibration = ConsoleVibrationCheck.IsChecked == true;
        ScheduleSave();
    }

    private void ConsoleIntro_Changed(object sender, RoutedEventArgs e)
    {
        if (_consoleLoading) return;
        ConsoleConfig.IntroEnabled = ConsoleIntroCheck.IsChecked == true;
        ScheduleSave();
    }

    private void ConsoleIntroBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier de l'intro personnalisée (images + sons du même nom)" };
        if (!string.IsNullOrWhiteSpace(ConsoleConfig.IntroFolder)) dialog.InitialDirectory = ConsoleConfig.IntroFolder;
        if (dialog.ShowDialog(this) != true) return;
        ConsoleConfig.IntroFolder = dialog.FolderName;
        ConsoleIntroFolderBox.Text = dialog.FolderName;
        ScheduleSave();
    }

    private void ConsoleIntroClear_Click(object sender, RoutedEventArgs e)
    {
        ConsoleConfig.IntroFolder = "";
        ConsoleIntroFolderBox.Text = "";
        ScheduleSave();
    }

    /// <summary>Bornes (pixels physiques) de l'écran qui affiche les Paramètres.</summary>
    private Rect CurrentScreenBounds()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var b = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        return new Rect(b.X, b.Y, b.Width, b.Height);
    }

    /// <summary>Ouvre l'interface TV sur cet écran, sans toucher aux écrans ni au son (aperçu du thème).</summary>
    private void ConsolePreviewUi_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetManager.IsConsoleModeActive) return;
        try
        {
            var window = new ConsoleWindow(new ConsoleLibrary(), new ConsoleUiOptions(ConsoleConfig.ThemeId, IntroEnabled: false, Preview: true, Sounds: ConsoleConfig.UiSounds, Vibration: ConsoleConfig.Vibration, IntroStyle: ConsoleConfig.IntroStyle));
            window.ExitRequested += (_, _) => window.Close();
            window.ThemeChosen += SetConsoleTheme;
            window.ShowOn(CurrentScreenBounds());
        }
        catch (Exception ex)
        {
            Log.Warn("Aperçu du mode console impossible", ex);
        }
    }

    /// <summary>Joue l'intro en plein écran sur cet écran (Échap, Entrée ou clic pour passer).</summary>
    private async void ConsolePreviewIntro_Click(object sender, RoutedEventArgs e)
    {
        var bounds = CurrentScreenBounds();
        var host = new Grid { Background = Brushes.Black };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
            Background = Brushes.Black, Content = host, Title = "Intro du mode console", Cursor = System.Windows.Input.Cursors.None,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Width = 200, Height = 200,
        };
        using var cts = new CancellationTokenSource();
        window.PreviewKeyDown += (_, _) => cts.Cancel();
        window.MouseLeftButtonUp += (_, _) => cts.Cancel();
        window.Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0x0040);
        window.Activate();
        try
        {
            await ConsoleIntro.PlayAsync(host, ConsoleThemes.Find(ConsoleConfig.ThemeId),
                string.IsNullOrWhiteSpace(ConsoleConfig.IntroFolder) ? null : ConsoleConfig.IntroFolder, cts.Token, null, ConsoleConfig.IntroStyle);
        }
        finally
        {
            window.Close();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
