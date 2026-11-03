using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Infrastructure;
using SysDeck.Plugins;
using SysDeck.Services;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck;

// Page Plugins : liste des plugins installés (état, activer, redémarrer, journal, désinstaller) et installation.
public partial class SettingsWindow
{
    private void InitPluginsPage()
    {
        var host = _widgetManager.PluginHost;
        void OnChanged()
        {
            if (PagePlugins.Visibility == Visibility.Visible) RefreshPluginsList();
        }
        host.StateChanged += OnChanged;
        Closed += (_, _) => host.StateChanged -= OnChanged;
    }

    /// <summary>Une carte par plugin installé, dans l'ordre alphabétique.</summary>
    private void RefreshPluginsList()
    {
        var plugins = _widgetManager.PluginHost.Plugins;
        PluginsList.Children.Clear();
        PluginsEmptyCard.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var plugin in plugins) PluginsList.Children.Add(BuildPluginCard(plugin));
    }

    private FrameworkElement BuildPluginCard(PluginInfo plugin)
    {
        var manifest = plugin.Manifest;
        FrameworkElement logo = manifest?.IconPath is { } iconPath && IconExtractor.GetCustomIcon(iconPath) is { } icon
            ? new Image { Source = icon, Width = 30, Height = 30 }
            : ShortcutIcons.CreateGlyph(TileRuntime.PluginGlyph, 26, "#FF9AA0B4");
        logo.VerticalAlignment = VerticalAlignment.Top;

        var (pillText, pillColor) = plugin.Status switch
        {
            PluginStatus.Running => ("En marche", Color.FromRgb(0x58, 0xB7, 0x7D)),
            PluginStatus.Starting => ("Démarrage…", Color.FromRgb(0xE5, 0xA0, 0x3E)),
            PluginStatus.Crashed => ("En erreur", Color.FromRgb(0xE5, 0x70, 0x6B)),
            PluginStatus.Invalid => ("Invalide", Color.FromRgb(0xE5, 0x70, 0x6B)),
            PluginStatus.Disabled => ("Désactivé", Color.FromRgb(0x9A, 0xA0, 0xB4)),
            _ => ("Arrêté", Color.FromRgb(0x9A, 0xA0, 0xB4))
        };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0x26, pillColor.R, pillColor.G, pillColor.B)),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(pillColor), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = pillText, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(pillColor), Margin = new Thickness(6, 0, 0, 0) }
                }
            }
        };

        var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        info.Children.Add(new TextBlock { Text = plugin.Name, Foreground = (Brush)FindResource("UiText"), FontSize = 15, FontWeight = FontWeights.SemiBold });
        var source = _widgetManager.Config.Plugins.Sources.TryGetValue(plugin.Id, out var s) ? s : null;
        var subtitle = manifest is null ? plugin.Id
            : string.Join(" · ", new[]
            {
                $"version {source?.Tag ?? manifest.Version}", manifest.Author, manifest.IsElgato ? "plugin Stream Deck" : "plugin SysDeck",
                source is null ? manifest.Id : $"github.com/{source.Repo}"
            }.Where(t => !string.IsNullOrEmpty(t)));
        info.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(pill);
        if (manifest?.Description is { } description)
            info.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = (Brush)FindResource("UiText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        if (manifest is not null)
            info.Children.Add(new TextBlock
            {
                Text = "Actions : " + string.Join(", ", manifest.Actions.Select(a => a.Name)),
                FontSize = 11.5,
                Foreground = (Brush)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        if (plugin.Detail is { } detail)
            info.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = (Brush)FindResource("UiDanger"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (manifest is not null)
        {
            var enabled = new CheckBox
            {
                Style = (Style)FindResource("ToggleSwitch"),
                Content = "Activé",
                IsChecked = plugin.Status != PluginStatus.Disabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            enabled.Click += (_, _) => _widgetManager.PluginHost.SetEnabled(plugin.Id, enabled.IsChecked == true);
            buttons.Children.Add(enabled);
            if (plugin.Status != PluginStatus.Disabled)
                buttons.Children.Add(PluginButton("Redémarrer", () => _widgetManager.PluginHost.Restart(plugin.Id)));
        }
        buttons.Children.Add(PluginButton("Journal", () => OpenPluginLog(plugin)));
        buttons.Children.Add(PluginButton("Dossier", () => OpenFolder(plugin.Folder)));
        var remove = PluginButton("Désinstaller", () => UninstallPlugin(plugin));
        remove.Style = (Style)FindResource("DangerButton");
        buttons.Children.Add(remove);
        info.Children.Add(buttons);

        var row = new DockPanel { Children = { logo, info } };
        return new Border
        {
            Style = (Style)FindResource("RowCard"),
            Margin = new Thickness(0, 16, 0, 0),
            Child = new Border { Style = (Style)FindResource("RowBorder"), BorderThickness = new Thickness(0), Padding = new Thickness(0, 14, 0, 14), Child = row }
        };
    }

    private static Button PluginButton(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    private void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Installer un plugin",
            Filter = "Plugins (*.sysdeckplugin;*.streamDeckPlugin;*.zip)|*.sysdeckplugin;*.streamDeckPlugin;*.zip|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        if (MessageBox.Show(this,
                "Un plugin est un programme : il s'exécute sur ce PC avec tes droits et peut lire tes fichiers.\n\n" +
                "N'installe que des plugins dont tu connais l'auteur. Continuer ?",
                "Installer un plugin", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var manifest = _widgetManager.PluginHost.Install(dialog.FileName);
            _widgetManager.Config.Plugins.Sources.Remove(manifest.Id); // installé à la main : plus suivi sur GitHub
            ScheduleSave();
            RefreshPluginsList();
            RefreshDeckActions();
            MessageBox.Show(this, InstalledMessage(manifest, null), "Plugin installé", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(this, ex.Message, "Installer un plugin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UninstallPlugin(PluginInfo plugin)
    {
        if (MessageBox.Show(this, $"Désinstaller « {plugin.Name} » ?\nSes tuiles restent sur la Switch, atténuées, jusqu'à ce que tu le réinstalles ou les retires.",
                "Plugins", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            _widgetManager.PluginHost.Uninstall(plugin.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Désinstallation incomplète : {ex.Message}", "Plugins", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshPluginsList();
    }

    private void OpenPluginLog(PluginInfo plugin)
    {
        var path = PluginLog.PathOf(plugin.Id);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "Ce plugin n'a encore rien écrit dans son journal.", "Plugins", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn("Ouverture du journal du plugin impossible", ex); }
    }

    private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(PluginHost.Dir);
        OpenFolder(PluginHost.Dir);
    }

    private static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn("Ouverture du dossier impossible", ex); }
    }
}
