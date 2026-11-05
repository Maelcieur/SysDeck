using System.Windows;
using System.Windows.Controls;
using SysDeck.Controls;
using SysDeck.Deck;

namespace SysDeck;

// Page Switch › vue Manettes : les deux Joy-Con dessinés ; une touche choisie reçoit une action de la bibliothèque
// (ou une commande : page, curseur, volume), commune à toutes les pages ou propre à la page affichée.
public partial class SettingsWindow
{
    private JoyConDiagram? _joyConDiagram;
    private JoyConButton? _joyConButton;
    private bool _joyConView;
    private bool _joyConLoading;

    private void InitJoyConView()
    {
        _joyConDiagram = new JoyConDiagram { Describe = DescribeJoyCon };
        _joyConDiagram.Selected += button => InspectJoyCon(button);
        _joyConDiagram.ActionDropped += (button, data) =>
        {
            InspectJoyCon(button);
            if (data is DeckAction action) AssignJoyCon(action);
        };
        JoyConDiagramHost.Content = _joyConDiagram;
        foreach (var mode in Enum.GetValues<JoyConStickMode>()) JoyConStickBox.Items.Add(JoyConInfo.Label(mode));

        void OnState(DeckProtocol.JoyConState state)
        {
            _joyConDiagram.ShowState(state);
            ShowJoyConStatus(state);
        }
        // Switch branchée ou débranchée, payload sans Joy-Con : le texte d'état suit aussi sans message des manettes.
        void OnStatus(DeckStatus _) => ShowJoyConStatus(Deck.JoyCon);
        Deck.JoyConChanged += OnState;
        Deck.StatusChanged += OnStatus;
        Closed += (_, _) =>
        {
            Deck.JoyConChanged -= OnState;
            Deck.StatusChanged -= OnStatus;
        };
        ShowJoyConStatus(Deck.JoyCon);
        _joyConDiagram.ShowState(Deck.JoyCon);
        _joyConDiagram.RefreshLabels();
    }

    private void SwitchView_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || SwitchScreenView is null || SwitchJoyConView is null) return;
        _joyConView = SwitchViewJoyCon.IsChecked == true;
        SwitchScreenView.Visibility = _joyConView ? Visibility.Collapsed : Visibility.Visible;
        SwitchJoyConView.Visibility = _joyConView ? Visibility.Visible : Visibility.Collapsed;
        JoyConInspector.Visibility = _joyConView ? Visibility.Visible : Visibility.Collapsed;
        SwitchViewHint.Text = _joyConView
            ? "Clique sur une touche des Joy-Con fixés sur la Switch, puis choisis son action (ou glisse-la depuis la bibliothèque)."
            : "Glisse une action de la bibliothèque sur une case, puis clique sur la tuile pour la régler.";
        if (_joyConView)
        {
            ClosePluginPage();
            _joyConDiagram?.RefreshLabels();
            InspectJoyCon(_joyConButton);
        }
        else if (_inspected is not null) ShowPluginPage(_inspected);
        RefreshDeckActions(); // la bibliothèque propose aussi les commandes de manette
    }

    private void ShowJoyConStatus(DeckProtocol.JoyConState state)
    {
        if (!Deck.Status.Connected)
        {
            JoyConStatusText.Text = "Switch non connectée : tu peux déjà choisir les actions, elles serviront dès que la Switch sera branchée.";
            return;
        }
        if (Deck.PayloadVersion < 6)
        {
            JoyConStatusText.Text = "Le payload de ta Switch est trop ancien pour les Joy-Con : copie le nouveau switchdeck.bin sur la carte SD " +
                                    "(sd:/bootloader/payloads/) et relance-le depuis hekate.";
            return;
        }
        if (!state.AnyConnected)
        {
            JoyConStatusText.Text = "Aucun Joy-Con détecté : fixe-les sur les côtés de la Switch (le Bluetooth n'est pas pris en charge).";
            return;
        }
        if (state.Lite)
        {
            JoyConStatusText.Text = "Commandes de la Switch Lite détectées. Appuie sur une touche : elle s'allume ici.";
            return;
        }
        string Side(string name, bool on, byte raw)
            => on ? $"{name} : {DeckProtocol.JoyConState.BatteryPercent(raw, true)} %{(DeckProtocol.JoyConState.BatteryCharging(raw) ? " (en charge)" : "")}" : $"{name} : absent";
        JoyConStatusText.Text = $"● {Side("Joy-Con gauche", state.LeftConnected, state.BatteryLeft)}  ·  {Side("Joy-Con droit", state.RightConnected, state.BatteryRight)}" +
                                "  ·  appuie sur une touche : elle s'allume ici.";
    }

    /// <summary>Étiquette d'une touche : action effective, « (cette page) » si la page la remplace ; stick : son rôle aussi.</summary>
    private string DescribeJoyCon(JoyConButton button)
    {
        var settings = Deck.Settings;
        var page = settings.Page.JoyCon;
        var text = JoyConInfo.Describe(JoyConInfo.Resolve(button, settings.JoyCon, page), settings.Pages);
        if (page?.Buttons.ContainsKey(button) == true) text += " (cette page)";
        if (button is JoyConButton.LStick or JoyConButton.RStick)
            text = $"{JoyConInfo.Label(JoyConInfo.ResolveStick(button == JoyConButton.LStick, settings.JoyCon, page))} · clic : {text}";
        return text;
    }

    private void InspectJoyCon(JoyConButton? button)
    {
        _joyConButton = button;
        JoyConInspectorEmpty.Visibility = button is null ? Visibility.Visible : Visibility.Collapsed;
        JoyConInspectorBody.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        if (button is not { } b) return;

        var settings = Deck.Settings;
        var page = settings.Page.JoyCon;
        var stick = b is JoyConButton.LStick or JoyConButton.RStick;
        var binding = JoyConInfo.Resolve(b, settings.JoyCon, page);
        _joyConLoading = true;
        JoyConInspectorTitle.Text = (stick ? (b == JoyConButton.LStick ? "STICK GAUCHE" : "STICK DROIT") : $"TOUCHE {JoyConInfo.Label(b)}").ToUpperInvariant();
        JoyConInspectorAction.Text = (stick ? "Clic : " : "") + JoyConInfo.Describe(binding, settings.Pages);
        JoyConPageCheck.Content = $"Propre à la page « {settings.Page.Name} »";
        JoyConPageCheck.IsChecked = page?.Buttons.ContainsKey(b) == true
                                    || stick && (b == JoyConButton.LStick ? page?.LeftStick : page?.RightStick) is not null;
        JoyConStickPanel.Visibility = stick ? Visibility.Visible : Visibility.Collapsed;
        JoyConTargetPagePanel.Visibility = !stick && binding?.Command == JoyConCommand.PageSelect
            ? Visibility.Visible : Visibility.Collapsed;
        JoyConTargetPageBox.Items.Clear();
        if (binding?.Command == JoyConCommand.PageSelect)
        {
            foreach (var targetPage in settings.Pages)
                JoyConTargetPageBox.Items.Add(new ComboBoxItem { Content = targetPage.Name, Tag = targetPage.Id });
            JoyConTargetPageBox.SelectedItem = JoyConTargetPageBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, binding.TargetPageId));
        }
        if (stick) JoyConStickBox.SelectedIndex = (int)JoyConInfo.ResolveStick(b == JoyConButton.LStick, settings.JoyCon, page);
        JoyConEditButton.Visibility = binding is { Command: JoyConCommand.Tile, Tile: not null } ? Visibility.Visible : Visibility.Collapsed;
        _joyConLoading = false;
        if (_joyConDiagram is not null) _joyConDiagram.SelectedButton = b;
    }

    /// <summary>Réglages modifiés : ceux de la page si la touche lui est propre, sinon ceux de toutes les pages.</summary>
    private JoyConSettings JoyConTarget()
    {
        var settings = Deck.Settings;
        if (JoyConPageCheck.IsChecked != true) return settings.JoyCon;
        return settings.Page.JoyCon ??= new JoyConSettings();
    }

    private void SetJoyConBinding(JoyConBinding? binding)
    {
        if (_joyConButton is not { } button) return;
        var target = JoyConTarget();
        // Commune : « aucune » efface ; propre à la page : « aucune » est gardée pour masquer l'action commune.
        if (binding is null && ReferenceEquals(target, Deck.Settings.JoyCon)) target.Buttons.Remove(button);
        else target.Buttons[button] = binding ?? new JoyConBinding();
        ScheduleSave();
        _joyConDiagram?.RefreshLabels();
        InspectJoyCon(button);
    }

    /// <summary>Action de la bibliothèque (clic ou glisser) donnée à la touche choisie.</summary>
    private void AssignJoyCon(DeckAction action)
    {
        if (_joyConButton is null)
        {
            MessageBox.Show(this, "Clique d'abord sur une touche des Joy-Con.", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (action.Command is { } command)
        {
            SetJoyConBinding(new JoyConBinding
            {
                Command = command,
                TargetPageId = command == JoyConCommand.PageSelect ? Deck.Settings.Page.Id : null
            });
            return;
        }
        if (action.Create()?.FirstOrDefault() is { } tile)
            SetJoyConBinding(new JoyConBinding { Command = JoyConCommand.Tile, Tile = tile });
    }

    private void JoyConEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_joyConButton is not { } button) return;
        var binding = JoyConInfo.Resolve(button, Deck.Settings.JoyCon, Deck.Settings.Page.JoyCon);
        if (binding?.Tile is not { } tile) return;
        EditDeckTile(tile);
        ScheduleSave();
        _joyConDiagram?.RefreshLabels();
        InspectJoyCon(button);
    }

    private void JoyConClear_Click(object sender, RoutedEventArgs e) => SetJoyConBinding(null);

    /// <summary>« Propre à cette page » : copie l'affectation commune dans la page, ou retire celle de la page.</summary>
    private void JoyConPage_Click(object sender, RoutedEventArgs e)
    {
        if (_joyConButton is not { } button) return;
        var settings = Deck.Settings;
        var stick = button is JoyConButton.LStick or JoyConButton.RStick;
        if (JoyConPageCheck.IsChecked == true)
        {
            var page = settings.Page.JoyCon ??= new JoyConSettings();
            if (settings.JoyCon.Buttons.TryGetValue(button, out var common))
                page.Buttons[button] = new JoyConBinding { Command = common.Command, Tile = common.Tile, TargetPageId = common.TargetPageId };
            else page.Buttons[button] = new JoyConBinding();
            if (stick)
            {
                if (button == JoyConButton.LStick) page.LeftStick = JoyConInfo.ResolveStick(true, settings.JoyCon, null);
                else page.RightStick = JoyConInfo.ResolveStick(false, settings.JoyCon, null);
            }
        }
        else if (settings.Page.JoyCon is { } page)
        {
            page.Buttons.Remove(button);
            if (button == JoyConButton.LStick) page.LeftStick = null;
            if (button == JoyConButton.RStick) page.RightStick = null;
            if (page.Buttons.Count == 0 && page.LeftStick is null && page.RightStick is null) settings.Page.JoyCon = null;
        }
        ScheduleSave();
        _joyConDiagram?.RefreshLabels();
        InspectJoyCon(button);
    }

    private void JoyConStick_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_joyConLoading || _joyConButton is not { } button || JoyConStickBox.SelectedIndex < 0) return;
        var mode = (JoyConStickMode)JoyConStickBox.SelectedIndex;
        var target = JoyConTarget();
        if (button == JoyConButton.LStick) target.LeftStick = mode;
        else if (button == JoyConButton.RStick) target.RightStick = mode;
        ScheduleSave();
        _joyConDiagram?.RefreshLabels();
    }

    private void JoyConTargetPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_joyConLoading || _joyConButton is not { } button
            || JoyConTargetPageBox.SelectedItem is not ComboBoxItem { Tag: string pageId }) return;
        var current = JoyConInfo.Resolve(button, Deck.Settings.JoyCon, Deck.Settings.Page.JoyCon);
        if (current?.Command != JoyConCommand.PageSelect || current.TargetPageId == pageId) return;
        SetJoyConBinding(new JoyConBinding { Command = JoyConCommand.PageSelect, TargetPageId = pageId });
    }

    /// <summary>Changement de page : les étiquettes et l'inspecteur montrent les affectations de la nouvelle page.</summary>
    private void RefreshJoyConForPage()
    {
        if (_joyConDiagram is null) return;
        _joyConDiagram.RefreshLabels();
        if (_joyConView) InspectJoyCon(_joyConButton);
    }

    /// <summary>Commandes de manette (catégorie « Manette ») puis actions de la bibliothèque, sans les plugins.</summary>
    private IEnumerable<DeckAction> JoyConActions()
    {
        const string category = "Manette", icon = "";
        foreach (var (command, glyph) in new[]
                 {
                     (JoyConCommand.PagePrevious, ""), (JoyConCommand.PageNext, ""), (JoyConCommand.PageSelect, ""),
                     (JoyConCommand.CursorUp, ""), (JoyConCommand.CursorDown, ""),
                     (JoyConCommand.CursorLeft, ""), (JoyConCommand.CursorRight, ""),
                     (JoyConCommand.CursorPress, ""), (JoyConCommand.VolumeUp, ""), (JoyConCommand.VolumeDown, "")
                 })
            yield return new DeckAction(category, icon, JoyConInfo.Label(command), glyph, "Commande de la Switch", () => null, Command: command);
        // Plugins écartés : une action de plugin a besoin d'une vraie tuile pour recevoir ses messages.
        foreach (var action in DeckActions().Where(a => !a.Plugin)) yield return action;
    }
}
