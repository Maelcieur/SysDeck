using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SysDeck.Infrastructure;
using SysDeck.Models;
using SysDeck.Plugins;

namespace SysDeck;

// Inspecteur de la page Switch : page de réglages d'un plugin Stream Deck (Property Inspector), affichée par WebView2 et
// reliée au plugin comme dans le logiciel d'Elgato (connectElgatoStreamDeckSocket).
public partial class SettingsWindow
{
    private static Task<CoreWebView2Environment>? _webEnvironment;
    private WebView2? _pluginPage;
    private Task? _pluginPageInit;
    private InspectorLaunch? _pluginLaunch;

    /// <summary>Affiche (ou retire) la page de réglages du plugin de la tuile inspectée.</summary>
    private async void ShowPluginPage(ShortcutItem? item)
    {
        var host = _widgetManager.PluginHost;
        var action = item is { Kind: TileKind.Plugin } ? host.Find(item.PluginId)?.Manifest?.FindAction(item.PluginAction) : null;
        if (action?.PropertyInspectorPath is null)
        {
            ClosePluginPage();
            return;
        }
        DeckPluginPageHost.Visibility = Visibility.Visible;
        if (_pluginLaunch is { } open && open.Context == item!.PluginContext) return; // déjà affichée

        ClosePluginPage();
        DeckPluginPageHost.Visibility = Visibility.Visible;
        var launch = host.OpenInspector(item!);
        if (launch is null)
        {
            ShowPluginPageMessage("Le plugin ne tourne pas encore : ses réglages s'afficheront dès qu'il sera démarré (Paramètres › Plugins).");
            return;
        }
        _pluginLaunch = launch;
        try
        {
            // Une seule initialisation, partagée par les clics qui arrivent pendant qu'elle tourne ; refaite après un échec.
            if (_pluginPage is null || _pluginPageInit is { IsFaulted: true } or { IsCanceled: true })
            {
                _pluginPage = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x2D, 0x2D, 0x2D) };
                DeckPluginPageSlot.Content = _pluginPage;
                _pluginPageInit = InitPluginPageAsync(_pluginPage);
            }
            await _pluginPageInit!;
            if (_pluginLaunch != launch || _pluginPage?.CoreWebView2 is null) return; // autre tuile choisie pendant l'initialisation
            DeckPluginPageMessage.Visibility = Visibility.Collapsed;
            _pluginPage.Visibility = Visibility.Visible;
            _pluginPage.CoreWebView2.Navigate(new Uri(launch.Page).AbsoluteUri);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException
                                       or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warn("Page de réglages du plugin impossible à afficher", ex);
            ShowPluginPageMessage("Les réglages de ce plugin ont besoin du moteur Microsoft Edge WebView2, introuvable sur ce PC.");
        }
    }

    private async Task InitPluginPageAsync(WebView2 page)
    {
        await page.EnsureCoreWebView2Async(await WebEnvironmentAsync());
        var core = page.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationCompleted += PluginPage_NavigationCompleted;
        // Liens de la page (aide, compte…) : dans le navigateur, jamais dans SysDeck.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenUrl(e.Uri);
        };
    }

    /// <summary>Page chargée : on lui donne de quoi se connecter, comme le fait le logiciel du Stream Deck.</summary>
    private async void PluginPage_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_pluginLaunch is not { } launch || _pluginPage?.CoreWebView2 is not { } core || !e.IsSuccess) return;
        // Certaines pages définissent la fonction un peu après le chargement : quelques essais.
        var script = $$"""
            (function () {
              var tries = 0;
              var timer = setInterval(function () {
                if (typeof connectElgatoStreamDeckSocket === 'function') {
                  clearInterval(timer);
                  connectElgatoStreamDeckSocket({{launch.Port}}, {{JsonSerializer.Serialize(launch.Context)}}, {{JsonSerializer.Serialize(PluginHost.RegisterPropertyInspector)}},
                    {{JsonSerializer.Serialize(launch.Info)}}, {{JsonSerializer.Serialize(launch.ActionInfo)}});
                } else if (++tries > 40) {
                  clearInterval(timer);
                }
              }, 50);
            })();
            """;
        try { await core.ExecuteScriptAsync(script); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { Log.Warn("Connexion de la page de réglages impossible", ex); }
    }

    private void ShowPluginPageMessage(string text)
    {
        DeckPluginPageMessage.Text = text;
        DeckPluginPageMessage.Visibility = Visibility.Visible;
        if (_pluginPage is not null) _pluginPage.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ferme la page de réglages (autre tuile, page Switch quittée, fenêtre fermée) et prévient le plugin.</summary>
    private void ClosePluginPage()
    {
        if (_pluginLaunch is not null) _widgetManager.PluginHost.CloseInspector();
        _pluginLaunch = null;
        DeckPluginPageHost.Visibility = Visibility.Collapsed;
        DeckPluginPageMessage.Visibility = Visibility.Collapsed;
        if (_pluginPage?.CoreWebView2 is { } core)
        {
            try { core.Navigate("about:blank"); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Environnement WebView2 partagé : profil rangé dans les données de SysDeck (pas dans le dossier de l'application).</summary>
    private static Task<CoreWebView2Environment> WebEnvironmentAsync()
        => _webEnvironment ??= CoreWebView2Environment.CreateAsync(null, AppPaths.Folder("WebView2"));
}
