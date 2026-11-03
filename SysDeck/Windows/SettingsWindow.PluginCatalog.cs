using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysDeck.Infrastructure;
using SysDeck.Plugins;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck;

// Page Plugins › Catalogue : recherche sur GitHub, fiche de chaque plugin, installation et mises à jour.
public partial class SettingsWindow
{
    private CancellationTokenSource? _catalogSearch;
    private bool _catalogBusy;
    private bool _catalogLoaded;

    private void PluginsTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || PluginsInstalledPanel is null || PluginsCatalogPanel is null) return;
        var catalog = PluginsTabCatalog.IsChecked == true;
        PluginsInstalledPanel.Visibility = catalog ? Visibility.Collapsed : Visibility.Visible;
        PluginsCatalogPanel.Visibility = catalog ? Visibility.Visible : Visibility.Collapsed;
        // Première ouverture : les plus populaires, sans avoir à chercher.
        if (catalog && !_catalogLoaded) _ = SearchCatalogAsync();
        if (!catalog) RefreshPluginsList();
    }

    private void CatalogSearch_Click(object sender, RoutedEventArgs e) => _ = SearchCatalogAsync();

    private void CatalogSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = SearchCatalogAsync();
    }

    private async Task SearchCatalogAsync()
    {
        _catalogSearch?.Cancel();
        var cts = _catalogSearch = new CancellationTokenSource();
        _catalogLoaded = true;
        CatalogSearchButton.IsEnabled = false;
        CatalogStatusText.Text = "Recherche sur GitHub…";
        CatalogList.Children.Clear();
        try
        {
            var results = await PluginCatalog.SearchAsync(CatalogSearchBox.Text, cts.Token);
            if (cts.IsCancellationRequested) return;
            CatalogStatusText.Text = results.Count == 0
                ? "Aucun plugin trouvé. Essaie un autre mot, ou installe un fichier depuis l'onglet Installés."
                : $"{results.Count} plugin(s). « Vérifié » : testé avec SysDeck ; les autres viennent de la communauté, installe-les seulement si tu fais confiance à l'auteur.";
            foreach (var entry in results) CatalogList.Children.Add(BuildCatalogCard(entry));
        }
        catch (CatalogException ex)
        {
            CatalogStatusText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // nouvelle recherche lancée entre-temps
        }
        finally
        {
            if (_catalogSearch == cts) CatalogSearchButton.IsEnabled = true;
        }
    }

    private FrameworkElement BuildCatalogCard(CatalogEntry entry)
    {
        var installedId = _widgetManager.Config.Plugins.Sources
            .FirstOrDefault(s => string.Equals(s.Value.Repo, entry.Repo, StringComparison.OrdinalIgnoreCase)).Key;
        var installed = installedId is not null && _widgetManager.PluginHost.Find(installedId) is not null;

        var badges = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (entry.Verified) badges.Children.Add(Badge("Vérifié", Color.FromRgb(0x58, 0xB7, 0x7D)));
        badges.Children.Add(entry.Kind == CatalogKind.SysDeck
            ? Badge("Plugin SysDeck", Color.FromRgb(0x5B, 0x8D, 0xEF))
            : Badge("Plugin Stream Deck", Color.FromRgb(0xE5, 0xA0, 0x3E)));
        if (installed) badges.Children.Add(Badge("Installé", Color.FromRgb(0x9A, 0xA0, 0xB4)));

        var meta = new List<string> { entry.Owner };
        if (entry.Stars > 0) meta.Add($"★ {entry.Stars}");
        if (entry.Updated is { } updated) meta.Add($"mis à jour le {updated.ToLocalTime():d}");
        if (entry.License is { Length: > 0 } license && license != "NOASSERTION") meta.Add(license);

        var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        info.Children.Add(new TextBlock { Text = entry.Name, Foreground = (Brush)FindResource("UiText"), FontSize = 14.5, FontWeight = FontWeights.SemiBold });
        info.Children.Add(new TextBlock { Text = string.Join(" · ", meta), FontSize = 11.5, Foreground = (Brush)FindResource("Muted") });
        info.Children.Add(badges);
        if (entry.Description is { } description)
            info.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = (Brush)FindResource("UiText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        var install = new Button { Content = installed ? "Réinstaller" : "Installer", Style = (Style)FindResource("PrimaryButton"), MinWidth = 110 };
        install.Click += async (_, _) => await InstallFromCatalogAsync(entry, install);
        var github = new Button { Content = "Voir sur GitHub", Margin = new Thickness(0, 8, 0, 0) };
        github.Click += (_, _) => OpenUrl(entry.Url);
        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { install, github } };
        DockPanel.SetDock(actions, Dock.Right);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new DockPanel { Children = { actions, info } }
        };
    }

    private static Border Badge(string text, Color color) => new()
    {
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 1, 8, 2),
        Margin = new Thickness(0, 0, 6, 0),
        Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) }
    };

    /// <summary>Dernière version publiée du dépôt : téléchargée puis installée (avertissement d'abord pour la communauté).</summary>
    private async Task InstallFromCatalogAsync(CatalogEntry entry, Button button)
    {
        if (_catalogBusy) return;
        var warning = entry.Verified
            ? $"Installer « {entry.Name} » de {entry.Owner} ?"
            : $"« {entry.Name} » vient de la communauté (dépôt {entry.Repo}) : SysDeck ne l'a pas vérifié.\n\n" +
              "Un plugin est un programme : il s'exécute sur ce PC avec tes droits et peut lire tes fichiers. " +
              "N'installe que si tu fais confiance à l'auteur. Continuer ?";
        if (MessageBox.Show(this, warning, "Installer un plugin", MessageBoxButton.YesNo,
                entry.Verified ? MessageBoxImage.Question : MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _catalogBusy = true;
        var label = button.Content;
        button.IsEnabled = false;
        button.Content = "Téléchargement…";
        string? file = null;
        try
        {
            var release = await PluginCatalog.GetLatestReleaseAsync(entry.Repo, CancellationToken.None);
            if (release is null)
            {
                if (MessageBox.Show(this, "Ce dépôt ne publie aucun fichier de plugin à installer (.sysdeckplugin, .streamDeckPlugin ou .zip).\n\nOuvrir sa page GitHub ?",
                        "Installer un plugin", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenUrl(entry.Url);
                return;
            }
            file = await PluginCatalog.DownloadAsync(release, CancellationToken.None);
            button.Content = "Installation…";
            var manifest = _widgetManager.PluginHost.Install(file);
            _widgetManager.Config.Plugins.Sources[manifest.Id] = new PluginSource { Repo = entry.Repo, Tag = release.Tag };
            ScheduleSave();
            MessageBox.Show(this, InstalledMessage(manifest, release.Tag), "Plugin installé", MessageBoxButton.OK, MessageBoxImage.Information);
            button.Content = "Réinstaller";
            label = button.Content;
            RefreshDeckActions();
        }
        catch (Exception ex) when (IsCatalogError(ex))
        {
            MessageBox.Show(this, CatalogErrorText(ex), "Installer un plugin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (file is not null) PluginCatalog.TryDelete(file);
            button.Content = label;
            button.IsEnabled = true;
            _catalogBusy = false;
        }
    }

    private static string InstalledMessage(PluginManifest manifest, string? tag)
    {
        var text = $"« {manifest.Name} » {tag ?? manifest.Version} est installé ({manifest.Actions.Count} action(s)).\n" +
                   "Ses actions sont dans la bibliothèque de la page Switch.";
        if (manifest.SkippedActions > 0) text += $"\n{manifest.SkippedActions} action(s) pour les molettes du Stream Deck + ne sont pas disponibles.";
        if (manifest.IsElgato && manifest.Actions.Any(a => a.PropertyInspectorPath is not null))
            text += "\nSes réglages s'affichent dans l'inspecteur de la tuile (page Switch).";
        return text;
    }

    /// <summary>Plugins installés depuis GitHub : nouvelle version publiée ? Proposée une par une.</summary>
    private async void CheckPluginUpdates_Click(object sender, RoutedEventArgs e)
    {
        var sources = _widgetManager.Config.Plugins.Sources
            .Where(s => _widgetManager.PluginHost.Find(s.Key) is not null).ToList();
        if (sources.Count == 0)
        {
            MessageBox.Show(this, "Aucun plugin installé depuis le catalogue : les mises à jour ne sont suivies que pour ceux-là.",
                "Plugins", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var button = (Button)sender;
        button.IsEnabled = false;
        var updated = 0;
        try
        {
            foreach (var (id, source) in sources)
            {
                var release = await PluginCatalog.GetLatestReleaseAsync(source.Repo, CancellationToken.None);
                if (release is null || release.Tag == source.Tag) continue;
                var name = _widgetManager.PluginHost.Find(id)?.Name ?? id;
                if (MessageBox.Show(this, $"« {name} » : version {release.Tag} disponible (installée : {source.Tag}). Mettre à jour ?",
                        "Mise à jour", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    continue;
                var file = await PluginCatalog.DownloadAsync(release, CancellationToken.None);
                try
                {
                    var manifest = _widgetManager.PluginHost.Install(file);
                    _widgetManager.Config.Plugins.Sources[manifest.Id] = new PluginSource { Repo = source.Repo, Tag = release.Tag };
                    ScheduleSave(); // enregistrée tout de suite : une erreur sur le plugin suivant ne la perd pas
                    updated++;
                }
                finally
                {
                    PluginCatalog.TryDelete(file);
                }
            }
            MessageBox.Show(this, updated == 0 ? "Tous les plugins sont à jour." : $"{updated} plugin(s) mis à jour.", "Plugins",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (IsCatalogError(ex))
        {
            var done = updated > 0 ? $"\n\n{updated} plugin(s) ont déjà été mis à jour." : "";
            MessageBox.Show(this, CatalogErrorText(ex) + done, "Mise à jour", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.IsEnabled = true;
            RefreshPluginsList();
        }
    }

    /// <summary>Erreurs attendues d'un téléchargement ou d'une installation (réseau, délai, réponse de GitHub, fichier invalide).</summary>
    private static bool IsCatalogError(Exception ex)
        => ex is CatalogException or InvalidDataException or System.Net.Http.HttpRequestException or IOException
            or TaskCanceledException or System.Text.Json.JsonException or UnauthorizedAccessException;

    private static string CatalogErrorText(Exception ex) => ex switch
    {
        CatalogException or InvalidDataException => ex.Message,
        TaskCanceledException => "GitHub ne répond pas (délai dépassé) : réessaie dans un instant.",
        System.Net.Http.HttpRequestException => $"GitHub est injoignable : {ex.Message}",
        System.Text.Json.JsonException => "Réponse de GitHub illisible : réessaie plus tard.",
        _ => $"Opération impossible : {ex.Message}"
    };

    private static void OpenUrl(string url)
    {
        if (PluginProtocol.SafeUrl(url) is not { } uri) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn("Ouverture du lien impossible", ex); }
    }
}
