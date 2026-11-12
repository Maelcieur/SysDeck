using System.Windows;
using System.Windows.Controls;
using SysDeck.Infrastructure;
using SysDeck.Services.ConsoleMode;

namespace SysDeck;

// Page Mode console : TV par le réseau (recherche, appairage, allumage automatique) et combinaison de la manette.
public partial class SettingsWindow
{
    private void InitConsoleTv()
    {
        ConsoleTvAutoPowerCheck.IsChecked = ConsoleConfig.TvAutoPower;
        ConsoleTvAutoOffCheck.IsChecked = ConsoleConfig.TvAutoOff;
        ConsoleTvHostBox.Text = ConsoleConfig.TvRemoteHost;
        ConsoleTvHdmiBox.SelectedIndex = Math.Clamp(ConsoleConfig.TvHdmiInput, 0, 4);
        ConsoleComboBox.Items.Clear();
        foreach (var (id, label) in GamepadComboWatcher.Combos)
            ConsoleComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        ConsoleComboBox.SelectedItem = ConsoleComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == ConsoleConfig.StartCombo)
                                       ?? ConsoleComboBox.Items[0];
        UpdateConsoleTvStatus();
    }

    private void UpdateConsoleTvStatus()
    {
        var paired = _widgetManager.TvRemote.IsPaired;
        ConsoleTvRemoteStatus.Text = paired
            ? $"Appairée avec {ConsoleConfig.TvRemoteHost}."
            : "Pas encore appairée : sans appairage, View ⧉ + LT / RT règlent le volume de Windows.";
        ConsoleTvForgetButton.Visibility = paired ? Visibility.Visible : Visibility.Collapsed;
    }

    private string ConsoleTvHost()
    {
        var text = (ConsoleTvHostBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ConsoleTvHostBox.Text;
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d+\.\d+\.\d+\.\d+");
        return m.Success ? m.Value : (text ?? "").Trim();
    }

    private async void ConsoleTvFind_Click(object sender, RoutedEventArgs e)
    {
        ConsoleTvFindButton.IsEnabled = false;
        ConsoleTvRemoteStatus.Text = "Recherche des TV sur le réseau…";
        try
        {
            var found = await TvRemoteService.DiscoverAsync();
            ConsoleTvHostBox.Items.Clear();
            foreach (var tv in found) ConsoleTvHostBox.Items.Add(new ComboBoxItem { Content = tv.Name, Tag = tv.Host });
            if (found.Count > 0) ConsoleTvHostBox.SelectedIndex = 0;
            ConsoleTvRemoteStatus.Text = found.Count == 0
                ? "Aucune Google TV trouvée : vérifie qu'elle est allumée et sur le même réseau."
                : $"{found.Count} TV trouvée(s). Choisis la tienne puis « Appairer ».";
        }
        finally
        {
            ConsoleTvFindButton.IsEnabled = true;
        }
    }

    private async void ConsoleTvPair_Click(object sender, RoutedEventArgs e)
    {
        var host = ConsoleTvHost();
        if (string.IsNullOrWhiteSpace(host)) { ConsoleTvRemoteStatus.Text = "Choisis ou tape d'abord l'adresse de la TV."; return; }
        ConsoleTvPairButton.IsEnabled = false;
        ConsoleTvRemoteStatus.Text = "Connexion à la TV…";
        try
        {
            await _widgetManager.TvRemote.StartPairingAsync(host);
            ConsoleTvCodeBox.Text = "";
            ConsoleTvCodePanel.Visibility = Visibility.Visible;
            ConsoleTvCodeBox.Focus();
            ConsoleTvRemoteStatus.Text = "Un code s'affiche sur la TV : tape-le ci-dessus.";
        }
        catch (Exception ex)
        {
            Log.Warn("TV réseau : appairage impossible", ex);
            ConsoleTvRemoteStatus.Text = $"Appairage impossible ({ex.Message}). La TV est-elle allumée ?";
        }
        finally
        {
            ConsoleTvPairButton.IsEnabled = true;
        }
    }

    private async void ConsoleTvCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await _widgetManager.TvRemote.FinishPairingAsync(ConsoleTvCodeBox.Text))
            {
                ConsoleTvCodePanel.Visibility = Visibility.Collapsed;
                UpdateConsoleTvStatus();
            }
            else
            {
                ConsoleTvRemoteStatus.Text = "Code incorrect : relance « Appairer » pour avoir un nouveau code.";
                ConsoleTvCodePanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("TV réseau : code refusé", ex);
            ConsoleTvRemoteStatus.Text = $"La TV a refusé le code ({ex.Message}).";
            ConsoleTvCodePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ConsoleTvCodeCancel_Click(object sender, RoutedEventArgs e)
    {
        _widgetManager.TvRemote.CancelPairing();
        ConsoleTvCodePanel.Visibility = Visibility.Collapsed;
        UpdateConsoleTvStatus();
    }

    private void ConsoleTvForget_Click(object sender, RoutedEventArgs e)
    {
        _widgetManager.TvRemote.Forget();
        UpdateConsoleTvStatus();
    }

    private void ConsoleTvHdmi_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_consoleLoading || ConsoleTvHdmiBox.SelectedIndex < 0) return;
        if (ConsoleConfig.TvHdmiInput != ConsoleTvHdmiBox.SelectedIndex) ConsoleConfig.TvInputCommand = "";   // autre port : méthode à retrouver
        ConsoleConfig.TvHdmiInput = ConsoleTvHdmiBox.SelectedIndex;
        ScheduleSave();
    }

    // ---------- Assistant « Trouver la méthode » ----------

    private List<(string Label, string Command)> _wizardCandidates = new();
    private int _wizardIndex;

    /// <summary>
    /// Essaie une à une les méthodes connues pour passer sur l'entrée du PC. Avant chaque essai, la TV revient à son
    /// écran d'accueil pour que le changement soit visible (bandeau « HDMI n », même sans image du PC).
    /// </summary>
    private async void ConsoleTvWizard_Click(object sender, RoutedEventArgs e)
    {
        var n = ConsoleTvHdmiBox.SelectedIndex;
        if (n < 1) { ConsoleTvRemoteStatus.Text = "Choisis d'abord l'entrée HDMI du PC."; return; }
        if (!_widgetManager.TvRemote.IsPaired) { ConsoleTvRemoteStatus.Text = "Appaire d'abord la TV (et allume-la)."; return; }
        _wizardCandidates = TvRemoteService.InputCandidates(n);
        _wizardIndex = -1;
        ConsoleTvWizardPanel.Visibility = Visibility.Visible;
        await NextWizardStepAsync();
    }

    private async Task NextWizardStepAsync()
    {
        var n = ConsoleTvHdmiBox.SelectedIndex;
        _wizardIndex++;
        if (_wizardIndex >= _wizardCandidates.Count)
        {
            ConsoleTvWizardText.Text = "Aucune méthode connue ne fonctionne avec cette TV : la TV s'allumera, mais il faudra choisir l'entrée à la main.";
            ConsoleTvWizardYes.IsEnabled = ConsoleTvWizardNo.IsEnabled = false;
            return;
        }
        var (label, command) = _wizardCandidates[_wizardIndex];
        ConsoleTvWizardYes.IsEnabled = ConsoleTvWizardNo.IsEnabled = false;
        ConsoleTvWizardText.Text = $"Essai {_wizardIndex + 1}/{_wizardCandidates.Count} : retour à l'accueil de la TV…";
        if (!await _widgetManager.TvRemote.HomeAsync())
        {
            ConsoleTvWizardText.Text = "La TV ne répond pas : allume-la puis relance « Trouver la méthode ».";
            return;
        }
        await Task.Delay(2500);
        await _widgetManager.TvRemote.RunInputCommandAsync(command);
        ConsoleTvWizardText.Text = $"Essai {_wizardIndex + 1}/{_wizardCandidates.Count} ({label}) : la TV est-elle passée sur HDMI {n} ? " +
                                   "(bandeau « HDMI », ou « pas de signal » sur cette entrée)";
        ConsoleTvWizardYes.IsEnabled = ConsoleTvWizardNo.IsEnabled = true;
    }

    private void ConsoleTvWizardYes_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardIndex < 0 || _wizardIndex >= _wizardCandidates.Count) return;
        var (label, command) = _wizardCandidates[_wizardIndex];
        ConsoleConfig.TvInputCommand = command;
        ScheduleSave();
        ConsoleTvWizardPanel.Visibility = Visibility.Collapsed;
        ConsoleTvRemoteStatus.Text = $"Méthode retenue : {label}. La TV passera sur le PC en mode console.";
    }

    private async void ConsoleTvWizardNo_Click(object sender, RoutedEventArgs e) => await NextWizardStepAsync();

    private void ConsoleTvWizardCancel_Click(object sender, RoutedEventArgs e)
    {
        ConsoleTvWizardPanel.Visibility = Visibility.Collapsed;
        UpdateConsoleTvStatus();
    }

    private async void ConsoleTvHdmiTest_Click(object sender, RoutedEventArgs e)
    {
        var n = ConsoleTvHdmiBox.SelectedIndex;
        if (n < 1) { ConsoleTvRemoteStatus.Text = "Choisis d'abord l'entrée HDMI du PC."; return; }
        if (!_widgetManager.TvRemote.IsPaired) { ConsoleTvRemoteStatus.Text = "Appaire d'abord la TV."; return; }
        ConsoleTvRemoteStatus.Text = await _widgetManager.TvRemote.SelectPcInputAsync(string.IsNullOrWhiteSpace(ConsoleConfig.TvInputCommand) ? n : null)
            ? $"Entrée HDMI {n} demandée à la TV."
            : "La TV ne répond pas (est-elle allumée ?).";
    }

    private void ConsoleTvAutoPower_Changed(object sender, RoutedEventArgs e)
    {
        if (_consoleLoading) return;
        ConsoleConfig.TvAutoPower = ConsoleTvAutoPowerCheck.IsChecked == true;
        ScheduleSave();
    }

    private void ConsoleTvAutoOff_Changed(object sender, RoutedEventArgs e)
    {
        if (_consoleLoading) return;
        ConsoleConfig.TvAutoOff = ConsoleTvAutoOffCheck.IsChecked == true;
        ScheduleSave();
    }

    private void ConsoleCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_consoleLoading || ConsoleComboBox.SelectedItem is not ComboBoxItem item) return;
        ConsoleConfig.StartCombo = (string)item.Tag;
        ScheduleSave();
    }
}
