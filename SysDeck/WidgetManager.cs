using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SysDeck.Deck;
using SysDeck.Infrastructure;
using SysDeck.Interop;
using SysDeck.Models;
using SysDeck.Services;
using SysDeck.Widgets;

namespace SysDeck;

public sealed record WidgetDescriptor(string Id, string Label);

/// <summary>
/// Chef d'orchestre des modules : crée et ferme leurs fenêtres, les alimente (capteurs, audio, médias…)
/// et expose leurs réglages à la fenêtre Paramètres.
/// </summary>
public sealed partial class WidgetManager : IDisposable, IProfileHost
{
    public static readonly WidgetDescriptor[] AllWidgets =
    {
        new("cpu", "Processeur"),
        new("gpu", "Carte graphique"),
        new("ram", "Mémoire"),
        new("network", "Réseau"),
        new("disk", "Disques"),
        new("media", "Lecture en cours"),
        new("volumemixer", "Volume par application"),
        new("mouse", "Souris"),
        new("audio", "Sortie audio"),
        new("power", "Consommation électrique"),
        new("claude", "Claude (quota)"),
        new("visualizer", "Visualiseur audio"),
        new("shortcuts", "Raccourcis"),
    };

    /// <summary>Construction du contenu et position par défaut (première apparition) de chaque module.</summary>
    private readonly Dictionary<string, (Func<UIElement> Build, Func<Point> DefaultPosition)> _registry;

    private readonly ConfigStore _configStore = new();
    private readonly AppConfig _config;

    private readonly HardwareMonitorService _hardware = new();
    private readonly NetworkMonitorService _network = new();
    private readonly DiskMonitorService _disk = new();
    private readonly MediaSessionService _media = new();
    private readonly VolumeMixerService _volumeMixer = new();
    private readonly AudioService _audio = new();
    private readonly LogitechBatteryService _mouseBattery = new();
    private readonly PowerUsageService _power;
    private readonly ClaudeUsageService _claude = new();
    private readonly ObsService _obs = new();
    private readonly TwitchService _twitch = new();
    private readonly VoicemodService _voicemod = new(VoicemodService.LoadEmbeddedKey());
    private readonly FpsService _fps = new();
    // Les FPS bougent vite : rafraîchis plus souvent que les autres capteurs, seulement si le module est affiché.
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ShortcutStore _shortcutStore = new();
    private readonly ObservableCollection<ShortcutItem> _shortcuts = new();

    private readonly DispatcherTimer _hardwareTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _volumeMixerTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _mouseTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    // Garde anti-chevauchement : une seule lecture de fond en cours par source.
    private bool _hardwareBusy, _networkBusy, _diskBusy, _mixerBusy, _mixerForcePending;
    private bool _disposed;
    private bool _mousePollInProgress;
    private int _mouseFailureCount;
    private const int MouseFailureThreshold = 5; // ~2,5 min d'échecs consécutifs avant de considérer la souris absente

    private readonly Dictionary<string, WidgetWindow> _windows = new();
    private readonly DeckController _deck;

    /// <summary>
    /// Fenêtre invisible : reçoit le raccourci global et les messages système, et sert de propriétaire
    /// aux boîtes de dialogue (une boîte possédée par un widget, rattaché au bureau, ne s'affiche jamais).
    /// </summary>
    private readonly Window _hiddenOwner;
    private GlobalHotkey? _correctionHotkey;
    private GlobalHotkey? _emojiHotkey;
    private EmojiPickerWindow? _emojiPicker;
    private const int MaxRecentEmojis = 32;
    private readonly TextCorrectionService _textCorrection;
    private readonly uint _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private DispatcherTimer? _recreateTimer;

    public WidgetManager()
    {
        _config = _configStore.Load();
        _power = new PowerUsageService(_config.Power);

        ThemeService.ApplyTheme(Application.Current.Resources, _config.Theme!);
        ThemeService.ApplyDockOpacity(_config.DockPanelOpacity, _config.DockBorderOpacity);

        foreach (var item in _shortcutStore.Load())
            _shortcuts.Add(item);

        var corrector = new GeminiCorrector(() => _config.GeminiApiKey);
        _textCorrection = new TextCorrectionService(corrector, ShowWarning);

        _registry = new()
        {
            ["cpu"] = (() => new CpuWidgetContent(), () => new Point(40, 40)),
            ["gpu"] = (() => new GpuWidgetContent(), () => new Point(40, 220)),
            ["ram"] = (() => new RamWidgetContent(), () => new Point(40, 400)),
            ["mouse"] = (() => new MouseWidgetContent(), () => new Point(40, 580)),
            ["network"] = (() => new NetworkWidgetContent(), () => new Point(300, 220)),
            ["disk"] = (BuildDiskContent, () => new Point(300, 400)),
            ["media"] = (BuildMediaContent, () => new Point(620, 220)),
            ["volumemixer"] = (BuildVolumeMixerContent, () => new Point(620, 400)),
            ["audio"] = (BuildAudioContent, () => new Point(300, 40)),
            ["power"] = (() => new PowerWidgetContent(), () => new Point(300, 580)),
            ["claude"] = (() => new ClaudeWidgetContent(), () => new Point(620, 40)),
            ["visualizer"] = (BuildVisualizerContent, () => new Point(620, 580)),
            ["shortcuts"] = (BuildShortcutsContent,
                () => new Point(SystemParameters.WorkArea.Width / 2 - 150, SystemParameters.WorkArea.Height - 110)),
        };

        // Abonnement unique : le module Lecture en cours peut être masqué puis réaffiché sans le dupliquer.
        _media.NowPlayingChanged += OnNowPlayingChanged;
        _claude.Updated += PushClaudeUsage;
        _claude.Updated += s =>
        {
            if (s.State == ClaudeUsageState.Ok && !s.NetworkError) ShowAlerts(Alerts.CheckClaude(s.SessionPercent, s.WeekPercent));
        };
        _config.Alerts.PropertyChanged += (_, _) => OnAlertSettingsChanged();
        TileRuntime.Obs = _obs;
        _obs.StateChanged += OnObsStateChanged;
        TileRuntime.Twitch = _twitch;
        _twitch.StateChanged += OnTwitchStateChanged;
        TileRuntime.Voicemod = _voicemod;
        _voicemod.StateChanged += OnVoicemodStateChanged;

        _hiddenOwner = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Visibility = Visibility.Hidden
        };
        _hiddenOwner.SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(_hiddenOwner).Handle)?.AddHook(OwnerWndProc);
            _correctionHotkey = new GlobalHotkey(_hiddenOwner, id: 1);
            _correctionHotkey.Pressed += () => _ = _textCorrection.RunAsync();
            if (!_correctionHotkey.Register(HotkeyGesture.CtrlSpace))
                Log.Warn("Raccourci Ctrl+Espace déjà utilisé par une autre application : correction IA indisponible.");

            _emojiHotkey = new GlobalHotkey(_hiddenOwner, id: 2);
            _emojiHotkey.Pressed += ShowEmojiPicker;
            if (HotkeyGesture.Parse(_config.EmojiHotkey) is { } emojiGesture && !_emojiHotkey.Register(emojiGesture))
                Log.Warn($"Raccourci des émojis ({emojiGesture}) déjà utilisé par une autre application.");
        };
        // force la création du handle natif sans jamais afficher la fenêtre
        new WindowInteropHelper(_hiddenOwner).EnsureHandle();

        CreateWidgets();

        // Écran de la Switch : ses modules sont des instances propres, construites et alimentées comme celles du bureau.
        _deck = new DeckController(_config, () => _configStore.Save(_config), BuildDeckModule, GetAppearance,
            ApplyModuleSettings, _shortcuts, LaunchShortcut);
        _deck.ContentsChanged += () =>
        {
            UpdateSensorDemand();
            RefreshAll();
        };
        if (_lastArtwork is not null) _deck.SetMediaArtwork(_lastArtwork);
        UpdateSensorDemand();

        _fpsTimer.Tick += (_, _) => RefreshFps();
        _fpsTimer.Start();

        _hardwareTimer.Tick += (_, _) => RefreshSensors();
        _hardwareTimer.Start();
        RefreshSensors();

        RefreshAudioDevices();

        _volumeMixerTimer.Tick += (_, _) => RefreshVolumeMixer();
        _volumeMixerTimer.Start();
        RefreshVolumeMixer();

        _mouseTimer.Tick += (_, _) => RefreshMouseBattery();
        _mouseTimer.Start();
        ApplyObsSettings();
        ApplyTwitchSettings();
        ApplyVoicemodSettings();
        StartTileTimers();
        RefreshMouseBattery();
    }

    // ---------- Fenêtres des modules ----------

    private void CreateWidgets()
    {
        foreach (var widget in AllWidgets)
            if (IsVisible(widget.Id))
                ShowWidget(widget.Id);
        UpdateSensorDemand();
    }

    /// <summary>Contenus vivants d'un type de module : celui du bureau et celui de la Switch.</summary>
    private IEnumerable<T> Live<T>() where T : UIElement
        => _windows.Values.Select(w => w.ModuleContent)
            .Concat(_deck?.ModuleContents ?? Enumerable.Empty<UIElement>())
            .OfType<T>();

    /// <summary>Les lectures coûteuses ne tournent que si un module (bureau ou Switch) les affiche.</summary>
    private void UpdateSensorDemand()
    {
        _hardware.MemoryEnabled = Live<RamWidgetContent>().Any();
        if (!Live<GpuWidgetContent>().Any()) _fps.Stop();
        // Une alerte activée garde la lecture en route même si le module n'est pas affiché.
        _claude.Enabled = Live<ClaudeWidgetContent>().Any() || _config.Alerts.AnyClaude;
    }

    /// <summary>Envoie le dernier quota Claude connu à tous les modules affichés (bureau et Switch).</summary>
    private void PushClaudeUsage(ClaudeUsageSnapshot snapshot)
    {
        foreach (var content in Live<ClaudeWidgetContent>()) content.Update(snapshot);
    }

    /// <summary>Enregistre une modification de la configuration puis la sauvegarde (évite de répéter le couple mutation + Save).</summary>
    private void UpdateConfig(Action<AppConfig> change)
    {
        change(_config);
        _configStore.Save(_config);
    }

    private void RefreshAll()
    {
        RefreshSensors();
        RefreshAudioDevices();
        RefreshVolumeMixer();
        RefreshMouseBattery();
    }

    /// <summary>Module construit pour l'écran de la Switch (la barre de raccourcis y est remplacée par des tuiles).</summary>
    private UIElement? BuildDeckModule(string id)
    {
        if (id == "shortcuts" || !_registry.TryGetValue(id, out var entry)) return null;
        try
        {
            return entry.Build();
        }
        catch (Exception ex)
        {
            Log.Error($"Création du module « {id} » pour la Switch impossible", ex);
            return null;
        }
    }

    private bool IsVisible(string id) => _config.Layout.FirstOrDefault(w => w.Id == id)?.Visible ?? true;

    private void ShowWidget(string id)
    {
        if (_windows.ContainsKey(id) || !_registry.TryGetValue(id, out var entry)) return;

        UIElement content;
        try
        {
            content = entry.Build();
        }
        catch (Exception ex)
        {
            // Un module défaillant ne doit pas empêcher les autres de s'afficher.
            Log.Error($"Création du module « {id} » impossible", ex);
            return;
        }

        var position = entry.DefaultPosition();
        var window = new WidgetWindow(id, content, _configStore, _config, position.X, position.Y, useDefaultChrome: id != "shortcuts");
        window.ApplyAppearance(GetAppearance(id));
        window.SetEditMode(LayoutEditMode);
        window.ContextMenu = BuildWidgetMenu(id);
        window.ResizeRequested += factor => ResizeWidget(id, factor);
        if (id == "media") window.SetMediaArtwork(_lastArtwork);
        window.Closed += (_, _) => OnWidgetClosed(id, window);
        _windows[id] = window;
        window.Show();
    }

    /// <summary>
    /// Fenêtre fermée sans qu'on l'ait demandé : le bureau qui l'hébergeait a disparu (Explorer
    /// redémarré). On l'oublie ; elle sera recréée dès que le nouveau bureau sera prêt.
    /// </summary>
    private void OnWidgetClosed(string id, WidgetWindow window)
    {
        if (App.IsExiting || !_windows.TryGetValue(id, out var current) || current != window) return;
        _windows.Remove(id);
        ReleaseContent(id);
        Log.Warn($"Module « {id} » fermé par le système (redémarrage d'Explorer ?)");
    }

    public bool IsWidgetShown(string id) => _windows.ContainsKey(id);

    public void ToggleWidget(string id, bool visible)
    {
        var existing = _config.Layout.FirstOrDefault(w => w.Id == id);
        if (existing is null)
        {
            existing = new WidgetPosition { Id = id };
            _config.Layout.Add(existing);
        }
        UpdateConfig(_ => existing.Visible = visible);

        if (visible)
        {
            ShowWidget(id);
            RefreshAll();
        }
        else
        {
            CloseWidget(id);
        }
        UpdateSensorDemand();
    }

    private void CloseWidget(string id)
    {
        if (!_windows.Remove(id, out var window)) return;
        try { window.Close(); }
        catch (InvalidOperationException) { /* déjà fermée par le système */ }
        ReleaseContent(id);
    }

    /// <summary>Oublie le contenu d'un module masqué : ses informations ne sont plus lues jusqu'à son retour.</summary>
    private void ReleaseContent(string id)
    {
        if (id == "shortcuts")
        {
            _shortcutsContent?.Detach();
            _shortcutsContent = null;
        }
        UpdateSensorDemand();
    }

    // ---------- Redémarrage d'Explorer ----------

    private IntPtr OwnerWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Diffusé à toutes les fenêtres quand la barre des tâches (donc le bureau) est recréée.
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
            ScheduleWidgetsRecreation();
        return IntPtr.Zero;
    }

    /// <summary>
    /// Les widgets sont rattachés au bureau d'Explorer : quand Explorer redémarre, ils disparaissent
    /// avec lui. On les recrée une fois le nouveau bureau en place (délai : Explorer finit de démarrer).
    /// </summary>
    private void ScheduleWidgetsRecreation()
    {
        _recreateTimer?.Stop();
        _recreateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _recreateTimer.Tick += (_, _) =>
        {
            _recreateTimer.Stop();
            Log.Info("Explorer redémarré : recréation des modules");
            foreach (var id in _windows.Keys.ToList())
                CloseWidget(id);
            CreateWidgets();
            RefreshAll();
        };
        _recreateTimer.Start();
    }

    // ---------- Contenu des modules ----------

    private UIElement BuildDiskContent()
    {
        var disk = new DiskWidgetContent();
        disk.SetGaugeStyle(GetAppearance("disk").DiskStyle);
        return disk;
    }

    private UIElement BuildVisualizerContent()
    {
        var visualizer = new VisualizerWidgetContent();
        ApplyVisualizerSettings(visualizer);
        return visualizer;
    }

    private void ApplyVisualizerSettings(VisualizerWidgetContent visualizer)
    {
        var a = GetAppearance("visualizer");
        visualizer.ApplySettings(a.VisualizerStyle, a.VisualizerBars, a.VisualizerSensitivity);
    }

    private System.Windows.Media.ImageSource? _lastArtwork;

    private UIElement BuildMediaContent()
    {
        var media = new MediaWidgetContent();
        media.SetIconSet(GetAppearance("media").MediaIconSet);
        media.PlayPauseRequested += () => _ = _media.TogglePlayPauseAsync();
        media.NextRequested += () => _ = _media.NextAsync();
        media.PreviousRequested += () => _ = _media.PreviousAsync();
        media.StopSpotifyRequested += () => _ = _media.StopSpotifyAsync();
        if (_media.Current is { } current) media.Update(current);
        _ = _media.InitializeAsync();
        return media;
    }

    private void OnNowPlayingChanged(NowPlayingInfo info)
    {
        foreach (var media in Live<MediaWidgetContent>()) media.Update(info);
        // La pochette peut aussi servir de fond au module (réglage « Fond de lecture »).
        _lastArtwork = info.Thumbnail;
        if (_windows.TryGetValue("media", out var mediaWindow))
            mediaWindow.SetMediaArtwork(info.Thumbnail);
        // Peut arriver pendant le constructeur, avant la création de _deck : la pochette est alors donnée plus bas.
        _deck?.SetMediaArtwork(info.Thumbnail);
    }

    private UIElement BuildVolumeMixerContent()
    {
        var mixer = new VolumeMixerWidgetContent { VolumeShape = GetAppearance("volumemixer").VolumeStyle };
        mixer.VolumeChangeRequested += (pid, percent) => _volumeMixer.SetVolume(pid, percent);
        mixer.MuteToggleRequested += info =>
        {
            _volumeMixer.SetMuted(info.ProcessId, !info.Muted);
            RefreshVolumeMixer(force: true);
        };
        mixer.ChangeIconRequested += ChooseAppIcon;
        mixer.HideRequested += info =>
        {
            UpdateConfig(c =>
            {
                if (!c.HiddenVolumeMixerApps.Contains(info.ProcessName))
                    c.HiddenVolumeMixerApps.Add(info.ProcessName);
            });
            RefreshVolumeMixer(force: true);
        };
        return mixer;
    }

    private UIElement BuildAudioContent()
    {
        var audio = new AudioWidgetContent();
        audio.SetDefaultRequested += device =>
        {
            try
            {
                _audio.SetDefaultDevice(device.Id);
                RefreshAudioDevices();
            }
            catch (Exception ex)
            {
                Log.Warn($"Changement de sortie audio vers « {device.Name} » impossible", ex);
                ShowWarning($"Impossible de changer la sortie audio :\n{ex.Message}");
            }
        };
        audio.HideRequested += device =>
        {
            UpdateConfig(c =>
            {
                if (!c.HiddenAudioDeviceIds.Contains(device.Id))
                    c.HiddenAudioDeviceIds.Add(device.Id);
            });
            RefreshAudioDevices();
        };
        return audio;
    }

    private ShortcutsDockContent? _shortcutsContent;
    private UIElement BuildShortcutsContent()
    {
        _shortcutsContent = new ShortcutsDockContent();
        _shortcutsContent.SetShortcuts(_shortcuts);
        _shortcutsContent.LaunchRequested += item => LaunchTile(item, fromDesktop: true);
        _shortcutsContent.RemoveRequested += item =>
        {
            _shortcuts.Remove(item);
            CustomIconStore.TryDelete(item.CustomIconPath);
            _shortcutStore.Save(_shortcuts);
            _deck.RefreshShortcuts(); // la Switch affiche aussi ce raccourci
        };
        _shortcutsContent.AddRequested += () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choisir un exécutable ou un raccourci",
                Filter = "Exécutables et raccourcis (*.exe;*.lnk)|*.exe;*.lnk|Tous les fichiers (*.*)|*.*"
            };
            if (dialog.ShowDialog(_hiddenOwner) != true) return;
            AddShortcut(dialog.FileName);
        };
        _shortcutsContent.FileDropped += AddShortcut;
        _shortcutsContent.ReorderRequested += () => _shortcutStore.Save(_shortcuts);
        _shortcutsContent.AddCustomRequested += () =>
        {
            var dialog = new AddCustomShortcutWindow();
            if (dialog.ShowDialog() != true || dialog.Result is null) return;

            var item = dialog.Result;
            // Copie l'icône choisie dans le dossier géré par SysDeck : si le fichier d'origine est
            // ensuite déplacé ou supprimé par l'utilisateur, l'icône du raccourci reste intacte.
            if (!string.IsNullOrWhiteSpace(item.CustomIconPath))
            {
                try { item.CustomIconPath = CustomIconStore.SaveCopy(item.CustomIconPath); }
                catch (Exception ex)
                {
                    Log.Warn("Copie de l'icône personnalisée impossible", ex);
                    item.CustomIconPath = null;
                }
            }

            _shortcuts.Add(item);
            _shortcutStore.Save(_shortcuts);
        };
        _shortcutsContent.ChangeIconRequested += item =>
        {
            // Même fenêtre de choix que partout : symbole, logo de marque, émoji ou image (propriétaire invisible : centrée sur l'écran).
            var chooser = new IconChooserWindow(_hiddenOwner, item.Name, CreateEmojiPickerForChoice, item)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            if (chooser.ShowDialog() != true || chooser.Result is not { } choice) return;

            var previousIconPath = item.CustomIconPath;
            item.IconColor = choice.IconColor;
            item.TileColor = choice.TileColor;
            if (choice.Kind != IconChoiceKind.Keep)
            {
                string? newImage = null;
                if (choice.Kind == IconChoiceKind.Image)
                {
                    try
                    {
                        newImage = CustomIconStore.SaveCopy(choice.Value!);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Copie de l'icône impossible", ex);
                        ShowWarning($"Impossible d'utiliser cette icône :\n{ex.Message}");
                        return;
                    }
                }
                item.CustomIconPath = newImage;
                item.IconGlyph = choice.Kind == IconChoiceKind.Glyph ? choice.Value : null;
                // Nettoie l'ancienne copie gérée par SysDeck (si elle existe) — jamais un fichier en
                // dehors de notre propre dossier de copies.
                CustomIconStore.TryDelete(previousIconPath);
            }

            _shortcutStore.Save(_shortcuts);
            _shortcutsContent?.RefreshIcon(item);
            _deck.RefreshShortcuts();
        };
        ApplyDockStyle();
        return _shortcutsContent;
    }

    private void LaunchShortcut(ShortcutItem item) => LaunchTile(item, fromDesktop: false);

    /// <summary>Action d'une tuile hors raccourci clavier et application : commande, bascule muet, action OBS, Twitch ou Voicemod. Faux si ce n'en est pas une.</summary>
    private bool TryRunSpecialTile(ShortcutItem item, bool fromDesktop)
    {
        switch (item.Kind)
        {
            case TileKind.Command:
                if (CommandRunner.Run(item) is { } error)
                    ShowWarning($"Impossible d'exécuter « {item.Name} » :\n{error}");
                return true;
            case TileKind.MicMute:
            case TileKind.OutputMute:
                ToggleMute(item);
                return true;
            case TileKind.Obs:
                RunObsTile(item);
                return true;
            case TileKind.Twitch:
                RunTwitchTile(item, fromDesktop);
                return true;
            case TileKind.Voicemod:
                RunVoicemodTile(item);
                return true;
            default:
                return false;
        }
    }

    private void LaunchTile(ShortcutItem item, bool fromDesktop)
    {
        // Second appui exigé : le premier arme la tuile (rouge sur la Switch), le second dans les 3 s exécute.
        if (item.ConfirmTap && !TileRuntime.Confirm.Press(item))
        {
            OnTileArmed(item, fromDesktop);
            return;
        }
        if (TryRunSpecialTile(item, fromDesktop)) return;

        // Raccourci clavier : la combinaison part vers la fenêtre au premier plan, rien n'est lancé.
        if (item.IsHotkey)
        {
            SendHotkeyShortcut(item);
            return;
        }
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            Log.Warn($"Tuile « {item.Name} » sans programme ni raccourci clavier : ignorée.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Path,
                Arguments = item.Arguments,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Lancement de « {item.Name} » ({item.Path}) impossible", ex);
            ShowWarning($"Impossible de lancer « {item.Name} » :\n{ex.Message}");
        }
    }

    private void SendHotkeyShortcut(ShortcutItem item)
    {
        // Une touche seule est permise ici (multimédia, F5…), contrairement aux raccourcis globaux.
        if (HotkeyGesture.Parse(item.Hotkey, requireModifier: false) is not { } gesture)
        {
            Log.Warn($"Raccourci clavier illisible pour « {item.Name} » : {item.Hotkey}");
            return;
        }
        try
        {
            InputSimulator.SendHotkey(gesture);
        }
        catch (Exception ex)
        {
            Log.Warn($"Envoi du raccourci {gesture} (« {item.Name} ») impossible", ex);
        }
    }

    private void ApplyDockStyle()
    {
        if (_shortcutsContent is null) return;

        _shortcutsContent.IconSize = _config.DockIconSize;
        _shortcutsContent.IconSpacing = _config.DockIconSpacing;
        _shortcutsContent.IsVertical = _config.DockOrientation == "Vertical";
        _shortcutsContent.SetAnimationDirection(Enum.TryParse<DockAnimationDirection>(_config.DockAnimationDirection, out var dir)
            ? dir
            : DockAnimationDirection.Up);
    }

    public void SetDockStyle(string orientation, string direction, double iconSize, double iconSpacing)
    {
        UpdateConfig(c =>
        {
            c.DockOrientation = orientation;
            c.DockAnimationDirection = direction;
            c.DockIconSize = iconSize;
            c.DockIconSpacing = iconSpacing;
        });
        ApplyDockStyle();
    }

    private void AddShortcut(string path)
    {
        if (_shortcuts.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        _shortcuts.Add(new ShortcutItem
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Path = path
        });
        _shortcutStore.Save(_shortcuts);
    }

    // ---------- Émojis ----------

    private void ShowEmojiPicker()
    {
        if (_emojiPicker is null)
        {
            _emojiPicker = new EmojiPickerWindow(() => _config.RecentEmojis, _config.EmojiPicker);
            _emojiPicker.EmojiInserted += text =>
            {
                UpdateConfig(c =>
                {
                    c.RecentEmojis.Remove(text);
                    c.RecentEmojis.Insert(0, text);
                    if (c.RecentEmojis.Count > MaxRecentEmojis)
                        c.RecentEmojis.RemoveRange(MaxRecentEmojis, c.RecentEmojis.Count - MaxRecentEmojis);
                });
            };
        }
        _emojiPicker.ShowNearCursor();
    }

    /// <summary>
    /// Change le raccourci des émojis (null = désactivé). Renvoie faux si la combinaison est déjà prise
    /// par une autre application : l'ancien raccourci est alors remis en place.
    /// </summary>
    public bool SetEmojiHotkey(HotkeyGesture? gesture)
    {
        if (_emojiHotkey is null) return false;
        var previous = HotkeyGesture.Parse(_config.EmojiHotkey);

        if (gesture is null)
        {
            _emojiHotkey.Unregister();
            UpdateConfig(c => c.EmojiHotkey = null);
            return true;
        }

        if (!_emojiHotkey.Register(gesture))
        {
            if (previous is not null) _emojiHotkey.Register(previous);
            return false;
        }
        UpdateConfig(c => c.EmojiHotkey = gesture.Serialize());
        return true;
    }

    /// <summary>Vrai si le raccourci des émojis est actif (enregistré auprès de Windows).</summary>
    public bool IsEmojiHotkeyActive => _emojiHotkey?.IsRegistered == true;

    /// <summary>
    /// Suspend les raccourcis globaux pendant la saisie d'une nouvelle combinaison dans les Paramètres :
    /// sinon, une combinaison déjà active serait interceptée au lieu d'être saisie.
    /// </summary>
    public void SuspendHotkeys(bool suspended)
    {
        if (suspended)
        {
            _correctionHotkey?.Unregister();
            _emojiHotkey?.Unregister();
            return;
        }
        _correctionHotkey?.Register(HotkeyGesture.CtrlSpace);
        if (HotkeyGesture.Parse(_config.EmojiHotkey) is { } gesture) _emojiHotkey?.Register(gesture);
    }

    public void ClearRecentEmojis()
    {
        UpdateConfig(c => c.RecentEmojis.Clear());
    }

    /// <summary>Message d'avertissement toujours visible, même déclenché depuis un widget du bureau.</summary>
    private void ShowWarning(string message)
        => MessageBox.Show(_hiddenOwner, message, "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---------- Rafraîchissement ----------

    /// <summary>
    /// Une passe de lecture des capteurs lourds. Chaque source a son propre garde anti-chevauchement :
    /// si sa lecture précédente n'est pas finie, la passe est simplement sautée pour elle.
    /// </summary>
    private void RefreshSensors()
    {
        RefreshHardware();
        RefreshNetwork();
        RefreshDisks();
    }

    // Les lectures (LibreHardwareMonitor, cartes réseau, disques) peuvent prendre plusieurs centaines de
    // millisecondes : elles se font sur un thread de fond, et seule la mise à jour des widgets (avec des
    // valeurs déjà lues) revient sur le thread de l'interface, grâce au contexte de l'await.
    private async void RefreshHardware()
    {
        if (_hardwareBusy || _disposed) return;
        _hardwareBusy = true;
        try
        {
            await RefreshHardwareCoreAsync();
        }
        finally
        {
            _hardwareBusy = false;
        }
    }

    private async Task RefreshHardwareCoreAsync()
    {
        HardwareSnapshot? snapshot = null;
        try
        {
            snapshot = await Task.Run(() => _hardware.Read());
            if (_disposed) return;
            foreach (var cpu in Live<CpuWidgetContent>())
                cpu.Update(snapshot.CpuName, snapshot.CpuLoad, snapshot.CpuTemperature, snapshot.CpuClockMhz);
            foreach (var gpu in Live<GpuWidgetContent>())
                gpu.Update(snapshot.GpuName, snapshot.GpuLoad, snapshot.GpuTemperature,
                    snapshot.GpuCoreClockMhz, snapshot.GpuMemoryClockMhz, snapshot.GpuMemoryUsedMb / 1024f);
            foreach (var ram in Live<RamWidgetContent>())
                ram.Update(snapshot.RamLoad, snapshot.RamUsedGb, snapshot.RamTotalGb);
            ShowAlerts(Alerts.CheckTemperatures(snapshot.CpuTemperature, snapshot.GpuTemperature));
        }
        catch (Exception ex)
        {
            Log.WarnOnce("refresh:hardware", "Rafraîchissement processeur / carte graphique / mémoire en échec", ex);
        }

        // Le service ne lit qu'aux 2 min ; ici on remet à jour les « Réinit. dans… » et les modules nouvellement affichés.
        if (_claude.Latest.State != ClaudeUsageState.Pending) PushClaudeUsage(_claude.Latest);

        // Compté même module masqué, pour que les cumuls du jour et du mois restent justes.
        if (snapshot is not null)
        {
            try
            {
                var power = _power.AddSample(_power.Estimate(snapshot));
                foreach (var content in Live<PowerWidgetContent>()) content.Update(power, _config.Power.GaugeMaxWatts);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("refresh:power", "Calcul de la consommation en échec", ex);
            }
        }
    }

    private async void RefreshNetwork()
    {
        if (_networkBusy || _disposed || !Live<NetworkWidgetContent>().Any()) return;
        _networkBusy = true;
        try
        {
            var net = await Task.Run(() => _network.Read());
            if (_disposed) return;
            // Liste relue après l'attente : un module a pu être masqué ou affiché entre-temps.
            foreach (var network in Live<NetworkWidgetContent>().ToList())
                network.Update(net.DownloadBytesPerSecond, net.UploadBytesPerSecond);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("refresh:network", "Lecture du débit réseau en échec", ex);
        }
        finally
        {
            _networkBusy = false;
        }
    }

    private async void RefreshDisks()
    {
        if (_diskBusy || _disposed || !Live<DiskWidgetContent>().Any()) return;
        _diskBusy = true;
        try
        {
            var drives = await Task.Run(() => _disk.Read());
            if (_disposed) return;
            foreach (var disk in Live<DiskWidgetContent>().ToList())
                disk.Update(drives);
        }
        catch (Exception ex)
        {
            // lecteur momentanément indisponible (ex : disque amovible retiré)
            Log.WarnOnce("refresh:disk", "Lecture des disques en échec", ex);
        }
        finally
        {
            _diskBusy = false;
        }
    }

    private void RefreshFps()
    {
        var gpus = Live<GpuWidgetContent>().ToList();
        if (gpus.Count == 0) return;
        try
        {
            _fps.EnsureRunning();
            var fps = _fps.ReadForegroundFps();
            foreach (var gpu in gpus) gpu.SetFps(fps, _fps.State);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("refresh:fps", "Lecture des FPS en échec", ex);
        }
    }

    public FpsState FpsState => _fps.State;

    private async void RefreshMouseBattery()
    {
        if (_mousePollInProgress || (!Live<MouseWidgetContent>().Any() && !_config.Alerts.MouseEnabled))
            return;

        _mousePollInProgress = true;
        try
        {
            var info = await Task.Run(() => _mouseBattery.TryReadBattery());
            if (_disposed) return;
            if (info is not null && Alerts.CheckMouse(info.Percent, info.Charging) is { } alert) ShowAlert(alert);
            if (!Live<MouseWidgetContent>().Any()) return; // module masqué pendant la lecture (ou lu pour l'alerte seule)

            if (info is null)
            {
                // Une souris sans fil rate régulièrement un sondage HID pendant qu'elle est en veille
                // (pas de mouvement) — un seul échec ne veut pas dire qu'elle est débranchée. On ne
                // cache le widget qu'après plusieurs échecs consécutifs, pour éviter qu'il disparaisse
                // sans raison apparente à chaque petit accroc de lecture.
                _mouseFailureCount++;
                if (_mouseFailureCount >= MouseFailureThreshold && _windows.TryGetValue("mouse", out var win))
                    win.Hide();
                return;
            }

            _mouseFailureCount = 0;
            if (_windows.TryGetValue("mouse", out var w) && !w.IsVisible)
                w.Show();

            foreach (var mouse in Live<MouseWidgetContent>()) mouse.Update(info.Percent, info.Charging);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("refresh:mouse", "Lecture de la batterie de la souris en échec", ex);
        }
        finally
        {
            _mousePollInProgress = false;
        }
    }

    public void RefreshAudioDevices()
    {
        var contents = Live<AudioWidgetContent>().ToList();
        if (contents.Count == 0) return;
        try
        {
            var devices = WithCustomNames(_audio.GetPlaybackDevices().Where(d => !_config.HiddenAudioDeviceIds.Contains(d.Id))).ToList();
            foreach (var content in contents)
            {
                // Même raison que pour le mixeur : ne pas recréer la liste si rien n'a changé.
                if (content.DevicesList.ItemsSource is IEnumerable<AudioDeviceInfo> shown && shown.SequenceEqual(devices))
                    continue;
                content.SetDevices(devices);
            }
        }
        catch (Exception ex)
        {
            Log.WarnOnce("refresh:audio", "Liste des sorties audio indisponible", ex);
        }
    }

    public void ResetHiddenAudioDevices()
    {
        UpdateConfig(c => c.HiddenAudioDeviceIds.Clear());
        RefreshAudioDevices();
    }

    public void ResetPowerUsage() => _power.Reset();

    public PowerLifetime? GetPowerLifetime() => _power.ComputeLifetime();

    public void ResetHiddenVolumeMixerApps()
    {
        UpdateConfig(c => c.HiddenVolumeMixerApps.Clear());
        RefreshVolumeMixer();
    }

    private async void RefreshVolumeMixer(bool force = false)
    {
        if (_disposed) return;
        if (_mixerBusy)
        {
            // Une action de l'utilisateur (muet, masquer) ne doit pas être perdue : on la rejoue à la fin.
            if (force) _mixerForcePending = true;
            return;
        }
        if (!Live<VolumeMixerWidgetContent>().Any()) return;

        _mixerBusy = true;
        try
        {
            // L'énumération COM des sessions audio et la lecture des processus sont lentes : thread de fond
            // (le pool est en MTA, ce que NAudio/WASAPI accepte). Le service est mono-lecteur grâce à _mixerBusy.
            var all = await Task.Run(() => _volumeMixer.GetSessions());
            if (_disposed) return;

            // Filtre sur le thread de l'interface : _config n'est pas prévue pour être lue depuis le fond.
            NoteSeenApps(all);
            var sessions = WithCustomIcons(all.Where(s => !_config.HiddenVolumeMixerApps.Contains(s.ProcessName))).ToList();
            foreach (var mixer in Live<VolumeMixerWidgetContent>().ToList())
            {
                // Réaffecter l'ItemsSource recrée tous les contrôles (survol et clics perdus) : on ne le fait
                // que si la liste a vraiment changé. Les enregistrements se comparent par valeur.
                if (!force && mixer.SessionsList.ItemsSource is IEnumerable<AudioSessionInfo> shown
                    && shown.SequenceEqual(sessions))
                    continue;
                mixer.Update(sessions, force);
            }
        }
        catch (Exception ex)
        {
            Log.WarnOnce("refresh:volumemixer", "Lecture des volumes par application en échec", ex);
        }
        finally
        {
            _mixerBusy = false;
            if (_mixerForcePending && !_disposed)
            {
                _mixerForcePending = false;
                RefreshVolumeMixer(force: true);
            }
        }
    }

    // ---------- Accès pour la fenêtre Paramètres ----------

    public AppConfig Config => _config;

    /// <summary>Service OBS (état de connexion, listes et test pour les Paramètres).</summary>
    public ObsService Obs => _obs;

    /// <summary>Service Twitch (connexion, recherche de catégories et état pour les Paramètres).</summary>
    public TwitchService Twitch => _twitch;

    /// <summary>Service Voicemod (état de connexion et listes de voix et de sons pour les Paramètres).</summary>
    public VoicemodService Voicemod => _voicemod;

    public ThemeSettings Theme => _config.Theme!;

    /// <summary>Apparence du module ; créée (avec les valeurs par défaut) si elle n'existe pas encore.</summary>
    public ModuleAppearance GetAppearance(string id)
    {
        if (!_config.Modules.TryGetValue(id, out var appearance))
        {
            appearance = new ModuleAppearance();
            _config.Modules[id] = appearance;
        }
        return appearance;
    }

    public void ApplyGlobalTheme()
    {
        ThemeService.ApplyTheme(Application.Current.Resources, Theme);
        // Un module qui ne personnalise qu'une partie du style garde le reste du thème global :
        // ses ressources (fusion des deux) doivent être recalculées.
        foreach (var (id, window) in _windows)
            window.ApplyAppearance(GetAppearance(id));
        _deck.ApplyAllAppearances();
    }

    // ---------- Profils et thèmes prêts à l'emploi ----------

    private ProfileService? _profiles;

    /// <summary>Profils enregistrés (liste, sauvegarde, application, export / import), liés à cette instance.</summary>
    public ProfileService Profiles => _profiles ??= new ProfileService(this);

    /// <summary>Instantané de la personnalisation courante (copie indépendante de la configuration).</summary>
    public Profile CaptureProfile() => Profile.Capture(_config);

    /// <summary>
    /// Applique un profil : les valeurs sont recopiées DANS les instances existantes (thème, apparences,
    /// sélecteur d'émojis) pour que la fenêtre Paramètres et les widgets, abonnés à ces instances, suivent.
    /// includeLayout : modules affichés et positions. La configuration est sauvegardée.
    /// </summary>
    public void ApplyProfile(Profile profile, bool includeLayout)
    {
        var backgroundsBefore = BackgroundPaths();
        profile.ApplyTo(_config, includeLayout);

        ThemeService.ApplyDockOpacity(_config.DockPanelOpacity, _config.DockBorderOpacity);
        ApplyDockStyle();
        ApplyGlobalTheme(); // réapplique aussi l'apparence de chaque module (bureau et Switch)
        _deck.ApplyBackground();
        _deck.ApplyClockOpacity();
        _deck.ApplyAppsOpacity();
        foreach (var (id, window) in _windows)
            ApplyModuleSettings(window.ModuleContent, GetAppearance(id));
        if (includeLayout) ApplyLayoutToWindows();

        // Les anciennes images de fond remplacées ne sont plus référencées : on retire nos copies orphelines.
        var backgroundsAfter = BackgroundPaths();
        foreach (var path in backgroundsBefore.Where(p => !backgroundsAfter.Contains(p)))
            BackgroundImageStore.TryDelete(path);

        SaveConfig();
    }

    /// <summary>
    /// Remplace le thème global par un thème prêt à l'emploi. resetModuleStyles : désactive les styles propres
    /// des modules (bureau et Switch) pour que tout suive le nouveau thème.
    /// </summary>
    public void ApplyThemePreset(ThemePreset preset, bool resetModuleStyles)
    {
        SettingsCopier.CopyInto(preset.CreateTheme(), Theme);

        if (resetModuleStyles)
        {
            foreach (var appearance in _config.Modules.Values.Concat(_config.Deck.Modules.Values))
            {
                appearance.UseCustomTheme = false;
                appearance.CustomCard = false;
                appearance.CustomGauges = false;
                appearance.CustomAnimations = false;
            }
        }

        ApplyGlobalTheme();
        SaveConfig();
    }

    /// <summary>Images de fond gérées par SysDeck actuellement utilisées (modules du bureau et de la Switch).</summary>
    private HashSet<string> BackgroundPaths()
        => _config.Modules.Values.Concat(_config.Deck.Modules.Values)
            .Select(a => a.BackgroundImage)
            .Append(_config.Deck.BackgroundImage)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Affiche ou masque les modules et les replace selon Config.Layout (les modules sans entrée reprennent leur position d'origine).</summary>
    private void ApplyLayoutToWindows()
    {
        foreach (var widget in AllWidgets)
        {
            if (!IsVisible(widget.Id))
            {
                CloseWidget(widget.Id);
                continue;
            }
            if (!_windows.TryGetValue(widget.Id, out var window))
            {
                ShowWidget(widget.Id); // lit sa position dans Config.Layout
                continue;
            }
            var saved = _config.Layout.FirstOrDefault(w => w.Id == widget.Id);
            var position = saved is null ? _registry[widget.Id].DefaultPosition() : new Point(saved.X, saved.Y);
            window.Left = position.X;
            window.Top = position.Y;
        }
        UpdateSensorDemand();
        RefreshAll();
    }

    /// <summary>Éléments déplaçables du module affiché (vide s'il est masqué).</summary>
    public IReadOnlyList<string> GetElementKeys(string id)
        => _windows.TryGetValue(id, out var window) ? window.MovableKeys : Array.Empty<string>();

    public void ApplyModuleAppearance(string id)
    {
        if (_windows.TryGetValue(id, out var window))
            window.ApplyAppearance(GetAppearance(id));

        if (_windows.TryGetValue(id, out var desktop))
            ApplyModuleSettings(desktop.ModuleContent, GetAppearance(id));
    }

    /// <summary>Applique l'apparence propre à la Switch d'un module (page Personnaliser, cible Switch).</summary>
    public void ApplyDeckAppearance(string id) => _deck.ApplyAppearance(id);

    /// <summary>Réglages propres à certains modules, pris dans l'apparence donnée (bureau ou Switch).</summary>
    private void ApplyModuleSettings(UIElement content, ModuleAppearance a)
    {
        switch (content)
        {
            case MediaWidgetContent media: media.SetIconSet(a.MediaIconSet); break;
            case VolumeMixerWidgetContent mixer: mixer.VolumeShape = a.VolumeStyle; break;
            case VisualizerWidgetContent visualizer: visualizer.ApplySettings(a.VisualizerStyle, a.VisualizerBars, a.VisualizerSensitivity); break;
            case DiskWidgetContent disk: disk.SetGaugeStyle(a.DiskStyle); break;
        }
    }

    /// <summary>Écran de la Switch : disposition, fond, luminosité, état de la liaison.</summary>
    public DeckController Deck => _deck;

    /// <summary>Taille actuelle de la carte du module, ou null s'il n'est pas affiché.</summary>
    public Size? GetModuleCardSize(string id)
        => _windows.TryGetValue(id, out var window) ? window.CurrentCardSize : null;

    public void SaveConfig() => _configStore.Save(_config);

    /// <summary>Mode édition de la disposition : les éléments des modules se déplacent à la souris.</summary>
    public bool LayoutEditMode { get; private set; }

    public event Action<bool>? LayoutEditModeChanged;

    public void SetLayoutEditMode(bool enabled)
    {
        if (LayoutEditMode == enabled) return;
        LayoutEditMode = enabled;
        foreach (var window in _windows.Values)
            window.SetEditMode(enabled);
        LayoutEditModeChanged?.Invoke(enabled);
    }

    public void SetDockOpacity(double panelOpacity, double borderOpacity)
    {
        UpdateConfig(c =>
        {
            c.DockPanelOpacity = panelOpacity;
            c.DockBorderOpacity = borderOpacity;
        });
        ThemeService.ApplyDockOpacity(panelOpacity, borderOpacity);
    }

    public void SetWidgetsLocked(bool locked) => UpdateConfig(c => c.WidgetsLocked = locked);

    public void Dispose()
    {
        _disposed = true; // les lectures de fond encore en cours n'écrivent plus dans l'interface
        if (_desktopSaveTimer is { IsEnabled: true }) SaveConfig(); // taille changée à la molette juste avant de quitter
        CloseToasts();
        _hardwareTimer.Stop();
        _mouseTimer.Stop();
        StopTileTimers();
        _volumeMixerTimer.Stop();
        _fpsTimer.Stop();
        try
        {
            _deck.Dispose(); // BYE : la Switch revient à son écran d'attente
            _fps.Dispose();
            _recreateTimer?.Stop();
            _correctionHotkey?.Dispose();
            _emojiHotkey?.Dispose();
            _emojiPicker?.Close();
            _hardware.Dispose();
            _claude.Dispose();
            TileRuntime.Obs = null;
            _obs.Dispose();
            TileRuntime.Twitch = null;
            _twitch.Dispose();
            TileRuntime.Voicemod = null;
            _voicemod.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Arrêt des services en échec", ex);
        }
        finally
        {
            // Quoi qu'il arrive plus haut : le cumul de consommation est sauvegardé et les fenêtres sont fermées.
            try { _power.Save(); }
            catch (Exception ex) { Log.Warn("Sauvegarde de la consommation en échec", ex); }
            foreach (var id in _windows.Keys.ToList())
                CloseWidget(id);
            _hiddenOwner.Close();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
