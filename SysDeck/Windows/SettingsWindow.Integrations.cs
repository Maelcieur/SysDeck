using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SysDeck.Controls;
using SysDeck.Infrastructure;
using SysDeck.Services;

namespace SysDeck;

// Page Applications connectées (noms de code « Integrations ») : une carte par application (OBS Studio, Twitch, Voicemod).
public partial class SettingsWindow
{
    private readonly DispatcherTimer _integrationsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _obsLoading;
    private string? _obsTestMessage;

    private enum AppStatus { Connected, Connecting, Error, Off }

    private void InitIntegrationsPage()
    {
        var obs = _widgetManager.Config.Obs;
        _obsLoading = true;
        ObsEnabledCheck.IsChecked = obs.Enabled;
        ObsHostBox.Text = obs.Host;
        ObsPortBox.Text = obs.Port.ToString();
        ObsPasswordBox.Password = obs.Password ?? "";
        _obsLoading = false;

        // Logo OBS noir : texte clair des Paramètres pour qu'il reste lisible sur le fond sombre.
        ObsLogo.Content = ShortcutIcons.CreateBrand("brand:obsstudio", 30, "#FFE7E8EB") ?? ShortcutIcons.CreateGlyph("", 26);
        TwitchLogo.Content = ShortcutIcons.CreateBrand("brand:twitch", 30) ?? ShortcutIcons.CreateGlyph("", 26);
        VoicemodLogo.Content = ShortcutIcons.CreateGlyph("", 26, "#FF2EE6B6");
        VoicemodEnabledCheck.IsChecked = _widgetManager.Config.Voicemod.Enabled;
        RefreshAppCards();

        _integrationsTimer.Tick += (_, _) =>
        {
            UpdateObsStatus();
            UpdateTwitchStatus();
            UpdateVoicemodStatus();
        };
        _widgetManager.Obs.StateChanged += UpdateObsStatus;
        _widgetManager.Twitch.StateChanged += UpdateTwitchStatus;
        _widgetManager.Voicemod.StateChanged += UpdateVoicemodStatus;
        UpdateVoicemodStatus();
        UpdateObsStatus();
        UpdateTwitchStatus();
    }

    /// <summary>La connexion peut échouer sans changement d'état : l'état en direct est relu chaque seconde tant que la page est visible.</summary>
    private void SetIntegrationsTimer(bool on)
    {
        if (on)
        {
            UpdateObsStatus();
            UpdateTwitchStatus();
            UpdateVoicemodStatus();
            _integrationsTimer.Start();
        }
        else _integrationsTimer.Stop();
    }

    private List<string> ShownApps => _widgetManager.Config.ConnectedApps ??= new();

    /// <summary>Affiche les cartes des applications ajoutées, dans l'ordre d'ajout, ou l'état vide.</summary>
    private void RefreshAppCards()
    {
        var shown = ShownApps;
        var panel = (Panel)ObsCard.Parent;
        var cards = new Dictionary<string, FrameworkElement> { [ConnectedApps.Obs] = ObsCard, [ConnectedApps.Twitch] = TwitchCard, [ConnectedApps.Voicemod] = VoicemodCard };
        foreach (var card in cards.Values) panel.Children.Remove(card);
        // Cartes affichées dans l'ordre d'ajout juste après l'état vide, les autres masquées à la suite.
        var at = panel.Children.IndexOf(AppsEmptyCard) + 1;
        foreach (var (id, card) in cards.OrderBy(c => shown.IndexOf(c.Key) is var i and >= 0 ? i : int.MaxValue))
        {
            card.Visibility = shown.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
            panel.Children.Insert(at++, card);
        }
        AppsEmptyCard.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddAppButton.IsEnabled = ConnectedApps.Addable(shown).Any(app => app.Available);
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddAppButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var app in ConnectedApps.Addable(ShownApps))
        {
            var item = new MenuItem { Header = app.Title, IsEnabled = app.Available };
            if (ShortcutIcons.CreateBrand(string.IsNullOrEmpty(app.BrandSlug) ? null : "brand:" + app.BrandSlug, 16,
                    app.Id == ConnectedApps.Obs ? "#FFE7E8EB" : null) is { } logo)
                item.Icon = logo;
            var id = app.Id;
            item.Click += (_, _) => AddApp(id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AddApp(string id)
    {
        if (!ConnectedApps.Add(ShownApps, id)) return;
        _widgetManager.SaveConfig();
        RefreshAppCards();
        var card = id switch { ConnectedApps.Obs => (FrameworkElement)ObsCard, ConnectedApps.Voicemod => VoicemodCard, _ => TwitchCard };
        card.BringIntoView();
        if (id == ConnectedApps.Obs && ObsEnabledCheck.IsChecked != true) ObsEnabledCheck.IsChecked = true;
        if (id == ConnectedApps.Voicemod && VoicemodEnabledCheck.IsChecked != true) VoicemodEnabledCheck.IsChecked = true;
    }

    private async void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        var id = (string)((FrameworkElement)sender).Tag;
        var title = ConnectedApps.Catalog.First(app => app.Id == id).Title;
        var detail = id switch
        {
            ConnectedApps.Twitch => "Le compte Twitch sera déconnecté et son jeton révoqué.",
            ConnectedApps.Voicemod => "La connexion à Voicemod sera désactivée.",
            _ => "La connexion à OBS sera désactivée."
        };
        if (MessageBox.Show(this, $"Retirer {title} des applications connectées ?\n{detail} Les tuiles existantes restent en place mais ne fonctionneront plus.",
                "SysDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        if (id == ConnectedApps.Obs) ObsEnabledCheck.IsChecked = false;
        else if (id == ConnectedApps.Voicemod) VoicemodEnabledCheck.IsChecked = false;
        else
        {
            _twitchLoginCts?.Cancel();
            if (_widgetManager.Config.Twitch.Enabled || _widgetManager.Twitch.IsConnected) await _widgetManager.Twitch.LogoutAsync();
            _twitchMessage = null;
        }
        ShownApps.Remove(id);
        _widgetManager.SaveConfig();
        RefreshAppCards();
        UpdateObsStatus();
        UpdateTwitchStatus();
        UpdateVoicemodStatus();
    }

    /// <summary>Libellé, couleur et fond translucide de chaque état (pinceaux figés, réutilisés à chaque seconde).</summary>
    private static readonly Dictionary<AppStatus, (string Text, Brush Color, Brush Background)> StatusLooks = new()
    {
        [AppStatus.Connected] = Look("Connectée", Color.FromRgb(0x58, 0xB7, 0x7D)),
        [AppStatus.Connecting] = Look("Connexion…", Color.FromRgb(0xE8, 0xA3, 0x3D)),
        [AppStatus.Error] = Look("Erreur", Color.FromRgb(0xE5, 0x70, 0x6B)),
        [AppStatus.Off] = Look("Déconnectée", Color.FromRgb(0x9A, 0x9D, 0xA7))
    };

    private static (string, Brush, Brush) Look(string text, Color color)
    {
        var solid = new SolidColorBrush(color);
        var soft = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        solid.Freeze();
        soft.Freeze();
        return (text, solid, soft);
    }

    private static void SetPill(Border pill, System.Windows.Shapes.Ellipse dot, TextBlock text, AppStatus status)
    {
        var (label, color, background) = StatusLooks[status];
        text.Text = label;
        text.Foreground = color;
        dot.Fill = color;
        pill.Background = background;
    }

    private (string Host, int Port) ObsAddress()
    {
        var host = string.IsNullOrWhiteSpace(ObsHostBox.Text) ? "127.0.0.1" : ObsHostBox.Text.Trim();
        var port = int.TryParse(ObsPortBox.Text.Trim(), out var value) && value is >= 1 and <= 65535 ? value : ObsSettings.DefaultPort;
        return (host, port);
    }

    private void UpdateObsStatus()
    {
        var obs = _widgetManager.Obs;
        var live = !_widgetManager.Config.Obs.Enabled ? "Désactivé."
            : obs.IsConnected ? $"Connecté à OBS ({obs.ObsVersion})."
            : obs.LastError is { Length: > 0 } error ? $"Non connecté : {error} Nouvelle tentative toutes les 5 s."
            : "Connexion en cours…";
        ObsStatusText.Text = _obsTestMessage is null ? live : $"{live}\nTest : {_obsTestMessage}";
        SetPill(ObsPill, ObsPillDot, ObsPillText,
            !_widgetManager.Config.Obs.Enabled ? AppStatus.Off
            : obs.IsConnected ? AppStatus.Connected
            : obs.LastError is { Length: > 0 } ? AppStatus.Error
            : AppStatus.Connecting);
    }

    private void ObsEnabled_Changed(object sender, RoutedEventArgs e) => ApplyObsSettings();

    private void ObsField_LostFocus(object sender, RoutedEventArgs e) => ApplyObsSettings();

    /// <summary>Enregistre les réglages saisis et relance la connexion s'ils ont changé.</summary>
    private void ApplyObsSettings()
    {
        if (_obsLoading || !IsLoaded) return;
        var settings = _widgetManager.Config.Obs;
        var enabled = ObsEnabledCheck.IsChecked == true;
        var (host, port) = ObsAddress();
        var password = ObsPasswordBox.Password.Length > 0 ? ObsPasswordBox.Password : null;
        ObsHostBox.Text = host;
        ObsPortBox.Text = port.ToString();

        if (enabled == settings.Enabled && host == settings.Host && port == settings.Port && password == settings.Password) return;
        settings.Enabled = enabled;
        settings.Host = host;
        settings.Port = port;
        settings.Password = password;
        _widgetManager.SaveConfig();
        _widgetManager.ApplyObsSettings();
        UpdateObsStatus();
    }

    private async void ObsTest_Click(object sender, RoutedEventArgs e)
    {
        ApplyObsSettings();
        var (host, port) = ObsAddress();
        ObsTestButton.IsEnabled = false;
        _obsTestMessage = "en cours…";
        UpdateObsStatus();
        try
        {
            var result = await ObsService.TestAsync(host, port, ObsPasswordBox.Password);
            _obsTestMessage = result.Outcome switch
            {
                ObsTestOutcome.Connected => $"connecté{(result.Version is null ? "" : $" à {result.Version}")} (obs-websocket {result.Detail}).",
                ObsTestOutcome.WrongPassword => "mot de passe incorrect.",
                _ => $"OBS injoignable ({result.Detail}). Vérifie qu'OBS est lancé et que le serveur WebSocket est activé (Outils → Paramètres du serveur WebSocket)."
            };
        }
        finally
        {
            ObsTestButton.IsEnabled = true;
            UpdateObsStatus();
        }
    }

    // ---------- Voicemod ----------

    private void VoicemodEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var enabled = VoicemodEnabledCheck.IsChecked == true;
        if (_widgetManager.Config.Voicemod.Enabled == enabled) return;
        _widgetManager.Config.Voicemod.Enabled = enabled;
        _widgetManager.SaveConfig();
        _widgetManager.ApplyVoicemodSettings();
        UpdateVoicemodStatus();
    }

    private void UpdateVoicemodStatus()
    {
        var voicemod = _widgetManager.Voicemod;
        var enabled = _widgetManager.Config.Voicemod.Enabled;
        VoicemodStatusText.Text = string.IsNullOrEmpty(voicemod.ClientKey) ? "Cette version de SysDeck n'a pas de clé Voicemod : l'intégration est indisponible."
            : !enabled ? "Désactivé."
            : voicemod.IsConnected ? "Connecté à Voicemod."
            : voicemod.AwaitingApproval ? "Voicemod demande d'autoriser SysDeck : accepte dans la fenêtre de Voicemod."
            : voicemod.LastError is { Length: > 0 } error ? $"Non connecté : {error} Nouvelle tentative toutes les 5 s."
            : "Connexion en cours…";
        SetPill(VoicemodPill, VoicemodPillDot, VoicemodPillText,
            !enabled ? AppStatus.Off
            : voicemod.IsConnected ? AppStatus.Connected
            : voicemod.AwaitingApproval ? AppStatus.Connecting
            : voicemod.LastError is { Length: > 0 } ? AppStatus.Error
            : AppStatus.Connecting);
    }

    // ---------- Twitch ----------

    private CancellationTokenSource? _twitchLoginCts;
    private TwitchDeviceCode? _twitchCode;
    private DateTime _twitchCodeExpiry;
    private string? _twitchMessage;

    private void UpdateTwitchStatus()
    {
        var twitch = _widgetManager.Twitch;
        var enabled = _widgetManager.Config.Twitch.Enabled;
        var loggingIn = _twitchLoginCts is not null;
        var hasAccount = twitch.IsConnected || enabled;
        TwitchConnectButton.Visibility = loggingIn || hasAccount ? Visibility.Collapsed : Visibility.Visible;
        TwitchCancelButton.Visibility = loggingIn ? Visibility.Visible : Visibility.Collapsed;
        TwitchDisconnectButton.Visibility = !loggingIn && hasAccount ? Visibility.Visible : Visibility.Collapsed;
        SetPill(TwitchPill, TwitchPillDot, TwitchPillText,
            twitch.IsConnected ? AppStatus.Connected
            : loggingIn ? AppStatus.Connecting
            : enabled ? (twitch.LastError is { Length: > 0 } ? AppStatus.Error : AppStatus.Connecting)
            : AppStatus.Off);

        if (loggingIn)
        {
            var remaining = Math.Max(0, (int)Math.Ceiling((_twitchCodeExpiry - DateTime.UtcNow).TotalMinutes));
            TwitchStatusText.Text = _twitchCode is null
                ? "Connexion à Twitch…"
                : $"Autorise SysDeck dans la page Twitch ouverte dans ton navigateur. Code à vérifier : {_twitchCode.UserCode} (expire dans {remaining} min).";
            return;
        }
        if (twitch.IsConnected)
        {
            var stream = twitch.Stream;
            var live = stream.Live ? $" En direct : {stream.Viewers} spectateur{(stream.Viewers > 1 ? "s" : "")}." : "";
            var reconnect = twitch.HasScope(TwitchService.EmotesScope) ? ""
                : "\nNouvelle fonction : déconnecte-toi puis reconnecte-toi pour utiliser tes émotes dans le clavier de la Switch.";
            TwitchStatusText.Text = $"Connecté en tant que {twitch.Login}.{live}{reconnect}";
        }
        else if (enabled)
            TwitchStatusText.Text = twitch.LastError is { Length: > 0 } error ? $"Non connecté : {error} Nouvelle tentative automatique." : "Connexion en cours…";
        else
            TwitchStatusText.Text = _twitchMessage ?? twitch.LastError ?? "Déconnecté.";
    }

    private async void TwitchConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_twitchLoginCts is not null) return;
        var cts = _twitchLoginCts = new CancellationTokenSource();
        _twitchCode = null;
        _twitchMessage = null;
        UpdateTwitchStatus();
        string? error;
        try
        {
            error = await _widgetManager.Twitch.LoginAsync(code =>
            {
                _twitchCode = code;
                _twitchCodeExpiry = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
                UpdateTwitchStatus();
                OpenTwitchPage(code.VerificationUri);
            }, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("Connexion à Twitch en échec", ex);
            error = "La connexion à Twitch a échoué.";
        }
        _twitchLoginCts = null;
        _twitchCode = null;
        cts.Dispose();
        _twitchMessage = error;
        UpdateTwitchStatus();
    }

    private static void OpenTwitchPage(string uri)
    {
        if (!TwitchService.IsSafeVerificationUri(uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Ouverture de la page d'autorisation Twitch impossible", ex);
        }
    }

    private void TwitchCancel_Click(object sender, RoutedEventArgs e) => _twitchLoginCts?.Cancel();

    private async void TwitchDisconnect_Click(object sender, RoutedEventArgs e)
    {
        TwitchDisconnectButton.IsEnabled = false;
        try
        {
            await _widgetManager.Twitch.LogoutAsync();
            _twitchMessage = "Déconnecté. Le jeton a été révoqué auprès de Twitch.";
        }
        finally
        {
            TwitchDisconnectButton.IsEnabled = true;
            UpdateTwitchStatus();
        }
    }
}
