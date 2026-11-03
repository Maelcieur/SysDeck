using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SysDeck.Services;

namespace SysDeck;

public partial class SettingsWindow : Window
{
    private readonly WidgetManager _widgetManager;

    // Les réglages s'appliquent en direct ; l'écriture sur disque est regroupée (curseurs = rafales de changements).
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private readonly List<(ModuleAppearance Appearance, PropertyChangedEventHandler Handler)> _moduleSubscriptions = new();
    private bool _confirmReset;
    private bool _confirmPowerReset;

    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["cpu"] = "",
        ["gpu"] = "",
        ["ram"] = "",
        ["network"] = "",
        ["disk"] = "",
        ["media"] = "",
        ["volumemixer"] = "",
        ["mouse"] = "",
        ["audio"] = "",
        ["shortcuts"] = "",
        ["power"] = "",
        ["claude"] = "",
        ["visualizer"] = "\uE9D9",
    };

    private sealed record WidgetRow(string Id, string Label, string Glyph, bool IsVisible, bool CanCustomize);

    // ToString = libellé : affiché tel quel par la liste des modules, même si la liaison sur Label échoue.
    public sealed record ModuleRow(string Id, string Label, ModuleAppearance Appearance)
    {
        public override string ToString() => Label;
    }

    public SettingsWindow(WidgetManager widgetManager)
    {
        InitializeComponent();
        _widgetManager = widgetManager;
        ShowVersion();
        var config = widgetManager.Config;

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _widgetManager.SaveConfig();
        };

        // Page Modules
        WidgetsList.ItemsSource = WidgetManager.AllWidgets
            .Select(w => new WidgetRow(w.Id, w.Label, Glyphs.GetValueOrDefault(w.Id, ""),
                widgetManager.IsWidgetShown(w.Id), w.Id != "shortcuts"))
            .ToList();

        // Page Thème global : un éditeur par sous-onglet, tous sur le même thème
        GlobalThemeEditor.DataContext = GlobalCardEditor.DataContext = GlobalGaugesEditor.DataContext
            = GlobalAnimationsEditor.DataContext = widgetManager.Theme;
        widgetManager.Theme.PropertyChanged += OnGlobalThemeChanged;

        // Page Personnaliser (la barre de raccourcis a sa propre page)
        var modules = WidgetManager.AllWidgets
            .Where(w => w.Id != "shortcuts")
            .Select(w => new ModuleRow(w.Id, w.Label, widgetManager.GetAppearance(w.Id)))
            .ToList();
        // Chaque module a toujours ses réglages de style prêts (copie du thème global) : les onglets
        // de style affichent alors des valeurs réelles dès qu'un groupe est activé, sans case vide.
        foreach (var module in modules.Where(m => m.Appearance.Theme is null))
            module.Appearance.Theme = widgetManager.Theme.Clone();
        foreach (var module in modules)
        {
            var id = module.Id;
            PropertyChangedEventHandler handler = (_, e) => OnModuleChanged(id, module.Appearance, e.PropertyName);
            module.Appearance.PropertyChanged += handler;
            _moduleSubscriptions.Add((module.Appearance, handler));
        }
        ModuleSelector.ItemsSource = modules;
        EnergyPanel.DataContext = config.Power;
        config.Power.PropertyChanged += OnPowerSettingsChanged;
        ModuleSelector.SelectedIndex = 0;

        // Page Barre de raccourcis
        DockPanelOpacitySlider.Value = config.DockPanelOpacity;
        DockBorderOpacitySlider.Value = config.DockBorderOpacity;
        (config.DockOrientation == "Vertical" ? OrientationVertical : OrientationHorizontal).IsChecked = true;
        var directionRadio = config.DockAnimationDirection switch
        {
            "Down" => DirDown,
            "Left" => DirLeft,
            "Right" => DirRight,
            _ => DirUp
        };
        directionRadio.IsChecked = true;
        IconSizeSlider.Value = config.DockIconSize;
        IconSpacingSlider.Value = config.DockIconSpacing;

        InitDeckPage();
        InitProfilesPage();
        InitIntegrationsPage();
        InitPluginsPage();

        // Divers
        GeminiApiKeyBox.Password = config.GeminiApiKey ?? "";
        EmojiHotkeyBox.Display(Interop.HotkeyGesture.Parse(config.EmojiHotkey));
        EmojiAppearanceCard.DataContext = config.EmojiPicker;
        config.EmojiPicker.PropertyChanged += OnEmojiPickerSettingsChanged;
        AlertsPanel.DataContext = config.Alerts;
        config.Alerts.PropertyChanged += OnAlertSettingsChanged;
        EmojiHotkeyStatus.Text = config.EmojiHotkey is null ? "Aucun raccourci : le sélecteur d'émojis est désactivé."
            : widgetManager.IsEmojiHotkeyActive ? "Actif."
            : "Ce raccourci est déjà utilisé par une autre application : choisis-en un autre.";
        LockWidgetsCheck.IsChecked = config.WidgetsLocked;
        SetNavCollapsed(config.SettingsNavCollapsed, save: false);
        LayoutEditCheck.IsChecked = widgetManager.LayoutEditMode;
        widgetManager.LayoutEditModeChanged += OnLayoutEditModeChanged;
        widgetManager.DesktopStateChanged += OnDesktopStateChanged;

        Closed += (_, _) =>
        {
            FlushApply(); // dernier changement encore en attente d'application
            _widgetManager.LayoutEditModeChanged -= OnLayoutEditModeChanged;
            _widgetManager.DesktopStateChanged -= OnDesktopStateChanged;
            _widgetManager.Deck.StatusChanged -= ShowDeckStatus;
            _widgetManager.Theme.PropertyChanged -= OnGlobalThemeChanged;
            _widgetManager.Config.Power.PropertyChanged -= OnPowerSettingsChanged;
            _widgetManager.Config.EmojiPicker.PropertyChanged -= OnEmojiPickerSettingsChanged;
            _widgetManager.Config.Alerts.PropertyChanged -= OnAlertSettingsChanged;
            _widgetManager.SuspendHotkeys(false); // fermeture en pleine saisie : les raccourcis reviennent
            _integrationsTimer.Stop();
            _twitchLoginCts?.Cancel();
            _widgetManager.Obs.StateChanged -= UpdateObsStatus;
            _widgetManager.Twitch.StateChanged -= UpdateTwitchStatus;
            _widgetManager.Voicemod.StateChanged -= UpdateVoicemodStatus;
            foreach (var (appearance, handler) in _moduleSubscriptions)
                appearance.PropertyChanged -= handler;
            foreach (var (style, handler) in _textSubscriptions)
                style.PropertyChanged -= handler;
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                _widgetManager.SaveConfig();
            }
        };
    }

    /// <summary>Version affichée sous le nom (celle du projet, reprise par l'installateur).</summary>
    private void ShowVersion()
    {
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---------- Navigation ----------

    /// <summary>Vrai : l'entrée « Modules » du menu affiche la page Personnaliser d'un module au lieu de la liste.</summary>
    private bool _customizing;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton nav) return;
        if (nav != NavModules) _customizing = false; // quitter Modules ferme la personnalisation
        ShowPageFor(nav);
    }

    /// <summary>Cliquer « Modules » alors qu'on personnalise un module revient à la liste.</summary>
    private void NavModules_Click(object sender, RoutedEventArgs e)
    {
        if (!_customizing) return;
        _customizing = false;
        ShowPageFor(NavModules);
    }

    /// <summary>Fil d'Ariane de Personnaliser : retour à la liste des modules.</summary>
    private void CustomizeBack_Click(object sender, RoutedEventArgs e)
    {
        _customizing = false;
        ShowPageFor(NavModules);
    }

    /// <summary>Va à une page du menu ; <paramref name="customize"/> ouvre Personnaliser (sous l'entrée « Modules »).</summary>
    private void NavigateTo(RadioButton nav, bool customize = false)
    {
        if (customize)
        {
            _customizing = true;
            nav = NavModules;
        }
        else
        {
            _customizing = false;
        }

        if (nav.IsChecked == true) ShowPageFor(nav);
        else nav.IsChecked = true;
    }

    private void ShowPageFor(RadioButton nav)
    {
        if (!IsInitialized || PageModules is null) return;

        var pages = new Dictionary<RadioButton, FrameworkElement>
        {
            [NavModules] = _customizing ? PageCustomize : PageModules,
            [NavTheme] = PageTheme,
            [NavProfiles] = PageProfiles,
            [NavDock] = PageDock,
            [NavSwitch] = PageSwitch,
            [NavIntegrations] = PageIntegrations,
            [NavPlugins] = PagePlugins,
            [NavCorrection] = PageCorrection,
            [NavEmoji] = PageEmoji,
            [NavAlerts] = PageAlerts,
            [NavGeneral] = PageGeneral,
            [NavEnergy] = PageEnergy,
        };
        var target = pages[nav];
        foreach (var page in pages.Values.Append(PageCustomize).Append(PageModules).Distinct())
            page.Visibility = page == target ? Visibility.Visible : Visibility.Collapsed;

        if (target == PageProfiles) RefreshProfilesList(); // un profil a pu être ajouté ou retiré depuis la dernière visite
        if (target == PageEnergy) UpdateLifetimeDetails();
        if (target == PageGeneral) UpdateFpsStatus();
        if (target == PagePlugins) RefreshPluginsList();
        SetIntegrationsTimer(target == PageIntegrations);
        if (target != PageSwitch) ClosePluginPage(); // la page de réglages d'un plugin ne reste pas ouverte en arrière-plan
        else if (_inspected is not null) ShowPluginPage(_inspected);
        if (target is ScrollViewer scroller) scroller.ScrollToTop();
        else CustomizeScroller.ScrollToTop(); // Personnaliser : en-tête fixe, seul l'onglet défile
    }

    /// <summary>Onglets de Personnaliser : un seul panneau visible à la fois.</summary>
    private void CustomizeTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || TabSize is null) return;

        var tabs = new Dictionary<RadioButton, FrameworkElement>
        {
            [TabSizeButton] = TabSize,
            [TabElementsButton] = TabElements,
            [TabTextsButton] = TabTexts,
            [TabButtonsButton] = TabButtons,
            [TabStyleButton] = TabStyle,
        };
        foreach (var (button, panel) in tabs)
            panel.Visibility = button == sender ? Visibility.Visible : Visibility.Collapsed;
        CustomizeScroller.ScrollToTop();

        // La liste des éléments dépend du module affiché sur le bureau : rafraîchie à chaque visite.
        if (sender == TabElementsButton)
        {
            BuildElementRows();
            BuildAudioDeviceRows(); // un périphérique a pu être branché ou une application vue depuis la dernière visite
            BuildAppIconRows();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Modules ----------

    private void WidgetCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not CheckBox { Tag: string id } checkBox)
            return;

        _widgetManager.ToggleWidget(id, checkBox.IsChecked == true);
    }

    private void CustomizeModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) ShowModule(id);
    }

    /// <summary>Ouvre la personnalisation de ce module, sous l'entrée « Modules » (aussi depuis le menu clic droit du bureau).</summary>
    public void ShowModule(string id)
    {
        ModuleSelector.SelectedItem = ModuleSelector.Items.Cast<ModuleRow>().FirstOrDefault(m => m.Id == id);
        NavigateTo(NavModules, customize: true);
    }

    /// <summary>Module masqué ou verrouillage changé depuis le bureau : la liste de la page Modules suit.</summary>
    private void OnDesktopStateChanged()
    {
        WidgetsList.ItemsSource = WidgetManager.AllWidgets
            .Select(w => new WidgetRow(w.Id, w.Label, Glyphs.GetValueOrDefault(w.Id, ""),
                _widgetManager.IsWidgetShown(w.Id), w.Id != "shortcuts"))
            .ToList();
        LockWidgetsCheck.IsChecked = _widgetManager.Config.WidgetsLocked;
    }

    private void LockWidgets_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _widgetManager.SetWidgetsLocked(LockWidgetsCheck.IsChecked == true);
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Quit();
}
