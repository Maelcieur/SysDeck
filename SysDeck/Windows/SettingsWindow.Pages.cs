using System.ComponentModel;
using System.Windows;
using SysDeck.Services;

namespace SysDeck;

// Pages Énergie, Barre de raccourcis, Correction de texte, Émojis, Alertes et Général (FPS, éléments masqués).
public partial class SettingsWindow
{
    private void OnPowerSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        ScheduleSave();
        UpdateLifetimeDetails();
    }

    /// <summary>D'où vient le cumul depuis la mise en service : mesuré, journal Windows, extrapolé.</summary>
    private void UpdateLifetimeDetails()
    {
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        if (_widgetManager.GetPowerLifetime() is not { } life)
        {
            LifetimeDetailsText.Text = "Date de mise en service inconnue : indique-la ci-dessus.";
            return;
        }

        var logLine = life.LogFrom is { } logFrom
            ? $"Journal Windows (depuis le {logFrom.ToString("d MMMM yyyy", fr)}) : {life.LoggedHours.ToString("N0", fr)} h allumé"
            : "Journal Windows : en cours de lecture ou indisponible";
        LifetimeDetailsText.Text =
            $"Depuis le {life.Since.ToString("d MMMM yyyy", fr)} : ≈ {life.Cost.ToString("N2", fr)} € · {life.Kwh.ToString("N0", fr)} kWh · {life.Hours.ToString("N0", fr)} h\n" +
            $"Dont jour : {life.DayHours.ToString("N0", fr)} h · soir : {life.NightHours.ToString("N0", fr)} h\n" +
            $"{logLine}\n" +
            $"Extrapolé avant le journal : {life.ExtrapolatedHours.ToString("N0", fr)} h\n" +
            $"Mesuré par SysDeck : {life.MeasuredHours.ToString("N1", fr)} h\n" +
            $"Puissance moyenne appliquée au passé : {life.PastAverageWatts.ToString("N0", fr)} W";
    }

    private void ResetPower_Click(object sender, RoutedEventArgs e)
    {
        // Confirmation en deux clics, sans boîte de dialogue.
        if (!_confirmPowerReset)
        {
            _confirmPowerReset = true;
            ResetPowerButton.Content = "Cliquer à nouveau pour confirmer";
            return;
        }

        _confirmPowerReset = false;
        ResetPowerButton.Content = "Réinitialiser session, jour et mois";
        _widgetManager.ResetPowerUsage();
    }

    // ---------- Barre de raccourcis ----------

    private void DockOpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _refreshingFromProfile) return;
        _widgetManager.SetDockOpacity(DockPanelOpacitySlider.Value, DockBorderOpacitySlider.Value);
    }

    private void DockStyle_RadioChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _refreshingFromProfile) return;
        ApplyDockStyleFromControls();
    }

    private void DockStyle_SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _refreshingFromProfile) return;
        ApplyDockStyleFromControls();
    }

    private void ApplyDockStyleFromControls()
    {
        var orientation = OrientationVertical.IsChecked == true ? "Vertical" : "Horizontal";
        var direction = DirDown.IsChecked == true ? "Down"
            : DirLeft.IsChecked == true ? "Left"
            : DirRight.IsChecked == true ? "Right"
            : "Up";

        _widgetManager.SetDockStyle(orientation, direction, IconSizeSlider.Value, IconSpacingSlider.Value);
    }

    // ---------- Correction de texte ----------

    private void SaveGeminiKey_Click(object sender, RoutedEventArgs e)
    {
        _widgetManager.Config.GeminiApiKey = GeminiApiKeyBox.Password;
        _widgetManager.SaveConfig();
        MessageBox.Show(this, "Clé API enregistrée.", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- Général : mesure des FPS ----------

    private bool _fpsPermissionGranted;

    private void UpdateFpsStatus()
    {
        if (FpsService.HasPermission)
        {
            FpsStatusText.Text = "Mesure autorisée. Lance un jeu : ses FPS s'affichent dans la tuile du module.";
            FpsPermissionButton.Visibility = Visibility.Collapsed;
        }
        else if (_fpsPermissionGranted)
        {
            FpsStatusText.Text = "Compte ajouté. Déconnecte-toi de Windows puis reconnecte-toi (ou redémarre) pour que l'autorisation prenne effet.";
            FpsPermissionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            FpsStatusText.Text = "Mesure non autorisée : la tuile FPS reste vide.";
            FpsPermissionButton.Visibility = Visibility.Visible;
        }
    }

    private async void FpsPermission_Click(object sender, RoutedEventArgs e)
    {
        FpsPermissionButton.IsEnabled = false;
        FpsStatusText.Text = "Confirme dans la fenêtre de Windows (mot de passe administrateur si demandé)…";
        try
        {
            _fpsPermissionGranted = await FpsService.RequestPermissionAsync();
            if (!_fpsPermissionGranted)
            {
                FpsStatusText.Text = "Autorisation non accordée (confirmation refusée ou échec). Tu peux réessayer.";
                return;
            }
            UpdateFpsStatus();
        }
        finally
        {
            FpsPermissionButton.IsEnabled = true;
        }
    }

    // ---------- Émojis ----------

    private void EmojiHotkeyBox_GestureCaptured(Interop.HotkeyGesture gesture)
    {
        if (_widgetManager.SetEmojiHotkey(gesture))
        {
            EmojiHotkeyBox.Display(gesture);
            EmojiHotkeyStatus.Text = $"Actif : appuie sur {gesture} dans n'importe quelle application.";
        }
        else
        {
            EmojiHotkeyBox.Display(Interop.HotkeyGesture.Parse(_widgetManager.Config.EmojiHotkey));
            EmojiHotkeyStatus.Text = $"{gesture} est déjà utilisé par Windows ou une autre application : choisis une autre combinaison.";
        }
    }

    private void EmojiHotkeyBox_CaptureStateChanged(bool capturing) => _widgetManager.SuspendHotkeys(capturing);

    private void ClearEmojiHotkey_Click(object sender, RoutedEventArgs e)
    {
        _widgetManager.SetEmojiHotkey(null);
        EmojiHotkeyBox.Display(null);
        EmojiHotkeyStatus.Text = "Aucun raccourci : le sélecteur d'émojis est désactivé.";
    }

    private void ClearRecentEmojis_Click(object sender, RoutedEventArgs e) => _widgetManager.ClearRecentEmojis();

    private void ResetEmojiAppearance_Click(object sender, RoutedEventArgs e) => _widgetManager.Config.EmojiPicker.Reset();

    private void OnEmojiPickerSettingsChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    // ---------- Alertes ----------

    private void OnAlertSettingsChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    private void ResetAlertLook_Click(object sender, RoutedEventArgs e)
    {
        var a = _widgetManager.Config.Alerts;
        var defaults = new AlertSettings();
        a.BackgroundColor = defaults.BackgroundColor;
        a.BackgroundOpacity = defaults.BackgroundOpacity;
        a.CornerRadius = defaults.CornerRadius;
        a.AutoAccent = defaults.AutoAccent;
        a.AccentColor = defaults.AccentColor;
        a.ShowBorder = defaults.ShowBorder;
        a.ShowGlow = defaults.ShowGlow;
        a.Position = defaults.Position;
        a.Scale = defaults.Scale;
    }

    private void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<AlertKind>(tag, out var kind))
            _widgetManager.ShowSampleAlert(kind);
    }

    // ---------- Général : éléments masqués ----------

    private void ResetHiddenAudio_Click(object sender, RoutedEventArgs e) => _widgetManager.ResetHiddenAudioDevices();

    private void ResetHiddenVolumeMixerApps_Click(object sender, RoutedEventArgs e) => _widgetManager.ResetHiddenVolumeMixerApps();

}
