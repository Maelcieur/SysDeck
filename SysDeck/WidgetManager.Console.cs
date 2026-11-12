using System.Windows;
using SysDeck.ConsoleMode;
using SysDeck.Infrastructure;
using SysDeck.Interop;
using SysDeck.Services;
using SysDeck.Services.ConsoleMode;

namespace SysDeck;

// Mode console : bascule sur la TV (écrans, audio), interface manette plein écran, widgets du bureau masqués.
// L'écran de la Switch (Deck USB) n'est pas un écran Windows : il continue de fonctionner pendant le mode.
public sealed partial class WidgetManager
{
    private readonly ConsoleModeService _consoleService;
    private ConsoleWindow? _consoleWindow;
    private GlobalHotkey? _consoleHotkey;
    // Garde anti-appels simultanés (entrée et sortie prennent plusieurs secondes) ; une sortie demandée en cours d'entrée est reportée.
    private bool _consoleBusy, _consoleExitPending;
    private bool _desktopWidgetsHidden;
    private readonly List<WidgetWindow> _widgetsHiddenForConsole = new();
    // Applications réduites à l'entrée du mode console, rouvertes à la sortie.
    private List<IntPtr> _minimizedForConsole = new();

    private void RestoreConsoleMinimized()
    {
        var list = _minimizedForConsole;
        _minimizedForConsole = new();
        OpenAppWindows.RestoreAll(list);
    }

    /// <summary>Service du Mode console (liste des écrans pour les Paramètres, état).</summary>
    public ConsoleModeService ConsoleService => _consoleService;

    public bool IsConsoleModeActive => _consoleService.IsActive;

    /// <summary>Le Mode console vient d'être activé ou quitté (thread interface).</summary>
    public event Action? ConsoleModeChanged;

    // TV par le réseau (allumage, veille, volume) et combinaison de la manette qui lance le mode console.
    private TvRemoteService? _tvRemote;
    private GamepadComboWatcher? _comboWatcher;
    private DateTime _volumeAskedAt;

    /// <summary>TV par le réseau (appairage depuis les Paramètres).</summary>
    public TvRemoteService TvRemote => _tvRemote ??= new TvRemoteService(() => _config.Console, () => UpdateConfig(_ => { }));

    private void StartConsoleMode()
    {
        TvRemote.VolumeChanged += (level, max, muted) =>
        {
            // Volume reçu de la TV : affiché seulement juste après un appui sur LT / RT.
            if (DateTime.UtcNow - _volumeAskedAt > TimeSpan.FromSeconds(3)) return;
            Application.Current?.Dispatcher.BeginInvoke(() => _consoleWindow?.ShowVolume(level, max, muted));
        };
        _comboWatcher = new GamepadComboWatcher(() => _config.Console.StartCombo);
        _comboWatcher.Triggered += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _consoleBusy || _consoleWindow is not null || _consoleService.IsActive) return;
            _ = ToggleConsoleModeAsync();
        });
        _comboWatcher.VolumeStep += direction => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _consoleWindow is not { } w) return;
            _ = ChangeConsoleVolumeAsync(w, direction);
        });
        _comboWatcher.Start();
        TileRuntime.ConsoleActive = _consoleService.IsActive;
        _consoleService.StateChanged += (_, _) =>
        {
            // Levé depuis un thread de fond : l'interface suit sur le thread interface.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || _disposed) return;
            dispatcher.BeginInvoke(OnConsoleStateChanged);
        };
    }

    private void OnConsoleStateChanged()
    {
        if (_disposed) return;
        TileRuntime.ConsoleActive = _consoleService.IsActive;
        RefreshTiles();
        ConsoleModeChanged?.Invoke();
    }

    /// <summary>Si « Désactiver la TV hors mode console » est coché, la TV est désactivée au démarrage (sans effet si elle n'est pas branchée).</summary>
    public void EnsureTvDisabledIfConfigured()
    {
        var settings = _config.Console;
        if (!settings.DisableTvWhenIdle || string.IsNullOrWhiteSpace(settings.TvTargetId)) return;
        var tvId = settings.TvTargetId;
        _ = Task.Run(() =>
        {
            var result = _consoleService.EnsureTvDisabled(tvId);
            if (!result.Success) Log.Warn($"Mode console : la TV n'a pas pu être désactivée au démarrage ({result.Message}).");
        });
    }

    // ---------- Raccourci de secours ----------

    private void InitConsoleHotkey()
    {
        _consoleHotkey = new GlobalHotkey(_hiddenOwner, id: 3);
        _consoleHotkey.Pressed += () =>
        {
            if (_consoleService.IsActive || _consoleWindow is not null || _consoleBusy) _ = ExitConsoleModeAsync();
        };
        if (HotkeyGesture.Parse(_config.Console.EmergencyHotkey) is { } gesture && !_consoleHotkey.Register(gesture))
            Log.Warn($"Raccourci de secours du Mode console ({gesture}) déjà utilisé par une autre application.");
    }

    /// <summary>Change le raccourci de secours (null = valeur par défaut). Faux si la combinaison est déjà prise : l'ancienne est remise.</summary>
    public bool SetConsoleEmergencyHotkey(HotkeyGesture? gesture)
    {
        if (_consoleHotkey is null) return false;
        gesture ??= HotkeyGesture.Parse(ConsoleSettings.DefaultEmergencyHotkey)!;
        var previous = HotkeyGesture.Parse(_config.Console.EmergencyHotkey);
        if (!_consoleHotkey.Register(gesture))
        {
            if (previous is not null) _consoleHotkey.Register(previous);
            return false;
        }
        UpdateConfig(c => c.Console.EmergencyHotkey = gesture.Serialize());
        return true;
    }

    public bool IsConsoleHotkeyActive => _consoleHotkey?.IsRegistered == true;

    // ---------- Bascule ----------

    /// <summary>Entre dans le Mode console, ou le quitte s'il est actif. Ignoré si une bascule est déjà en cours.</summary>
    public async Task ToggleConsoleModeAsync()
    {
        if (_consoleBusy || _disposed) return;
        if (_consoleWindow is not null || _consoleService.IsActive)
        {
            await ExitConsoleModeAsync();
            return;
        }

        _consoleBusy = true;
        try
        {
            await EnterConsoleCoreAsync();
            if (_consoleExitPending && (_consoleWindow is not null || _consoleService.IsActive))
                await ExitConsoleCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Mode console : bascule en échec", ex);
            ShowWarning($"Le mode console a rencontré une erreur :\n{ex.Message}");
        }
        finally
        {
            _consoleBusy = false;
            _consoleExitPending = false;
        }
    }

    /// <summary>Quitte le Mode console (interface, écrans, audio, widgets). Pendant une entrée, la sortie est faite dès qu'elle se termine.</summary>
    public async Task ExitConsoleModeAsync()
    {
        if (_disposed) return;
        if (_consoleBusy)
        {
            _consoleExitPending = true;
            return;
        }
        _consoleBusy = true;
        try
        {
            await ExitConsoleCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Mode console : sortie en échec", ex);
        }
        finally
        {
            _consoleBusy = false;
            _consoleExitPending = false;
        }
    }

    private async Task EnterConsoleCoreAsync()
    {
        var settings = _config.Console;
        if (string.IsNullOrWhiteSpace(settings.TvTargetId))
        {
            ShowWarning("Aucune TV n'est configurée pour le mode console.\n\nChoisis ta TV dans Paramètres › Mode console.");
            return;
        }

        var options = new ConsoleModeOptions(settings.TvTargetId,
            string.IsNullOrWhiteSpace(settings.TvAudioDeviceId) ? null : settings.TvAudioDeviceId, settings.DisableTvWhenIdle);

        // Écran de chargement sur tous les écrans AVANT la bascule : le bureau n'est jamais visible.
        var splash = ConsoleSplash.Show("Passage en mode console…");
        HideDesktopWidgets();
        _minimizedForConsole = OpenAppWindows.MinimizeAll();
        if (settings.TvAutoPower && TvRemote.IsPaired)
        {
            splash.SetMessage("Allumage de la TV…");
            if (!await TvRemote.WakeAsync()) Log.Warn("Mode console : la TV ne s'est pas allumée par le réseau.");
            await TvRemote.SelectPcInputAsync();   // entrée HDMI du PC (si réglée)
            splash.SetMessage("Passage en mode console…");
        }
        var result = await _consoleService.EnterAsync(options);
        if (result.Success && TvRemote.IsPaired && settings.TvHdmiInput > 0)
        {
            // Quand l'image du PC arrive, certaines TV restent sur l'entrée précédente : on redemande l'entrée du PC.
            _ = Task.Delay(1500).ContinueWith(_ => TvRemote.SelectPcInputAsync());
        }
        if (result.Success)
        {
            // Les applications Chromium/Electron se rouvrent quand leur écran disparaît : on les re-réduit sur la TV.
            var minimized = _minimizedForConsole;
            OpenAppWindows.MinimizeAgain(minimized);
            _ = Task.Delay(2000).ContinueWith(_ => OpenAppWindows.MinimizeAgain(minimized));
        }
        if (_disposed) { await splash.CloseAsync(0); return; }

        if (!result.Success || result.Bounds is not { } bounds)
        {
            if (result.Success) await _consoleService.ExitAsync();
            RestoreDesktopWidgets();
            RestoreConsoleMinimized();
            await splash.CloseAsync();
            var error = result.Error ?? "L'écran de la TV est introuvable.";
            if (error.Contains("n'est pas connectée", StringComparison.OrdinalIgnoreCase))
                error += "\nAllume la TV (ou vérifie le câble HDMI), puis réessaie.";
            ShowWarning($"Impossible d'entrer en mode console :\n{error}");
            return;
        }

        try
        {
            var window = new ConsoleWindow(new ConsoleLibrary(),
                new ConsoleUiOptions(settings.ThemeId, settings.IntroEnabled, string.IsNullOrWhiteSpace(settings.IntroFolder) ? null : settings.IntroFolder,
                    string.IsNullOrWhiteSpace(settings.TvAudioDeviceId) ? null : settings.TvAudioDeviceId,
                    Sounds: settings.UiSounds, Vibration: settings.Vibration, IntroStyle: settings.IntroStyle));
            window.ThemeChosen += id => UpdateConfig(c => c.Console.ThemeId = id);
            window.VolumeRequested += direction => _ = ChangeConsoleVolumeAsync(window, direction);
            window.ExitRequested += (_, _) => _ = ExitConsoleModeAsync();
            window.Closed += (_, _) =>
            {
                // Fermée autrement que par nous (Alt+F4...) : on rebascule les écrans.
                if (ReferenceEquals(_consoleWindow, window)) _ = ExitConsoleModeAsync();
            };
            _consoleWindow = window;
            if (_comboWatcher != null) _comboWatcher.VolumeEnabled = true;   // View + LT / RT
            // La fenêtre console (avec son propre écran d'intro) passe au-dessus du chargement, qui peut alors partir.
            window.ShowOn(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            await Task.Delay(300);
            await splash.CloseAsync(0);
        }
        catch (Exception ex)
        {
            Log.Error("Mode console : interface TV impossible à afficher", ex);
            await splash.CloseAsync(0);
            await ExitConsoleCoreAsync();
            ShowWarning($"L'interface du mode console n'a pas pu s'afficher :\n{ex.Message}");
        }
    }

    private async Task ExitConsoleCoreAsync()
    {
        // Écran de chargement par-dessus tout pendant la remise en place des écrans, des icônes et des modules.
        if (_comboWatcher != null) _comboWatcher.VolumeEnabled = false;
        var splash = ConsoleSplash.Show("Retour au PC…");
        var window = _consoleWindow;
        _consoleWindow = null;
        if (window is not null)
        {
            try { window.Close(); }
            catch (Exception ex) { Log.Warn("Mode console : fermeture de l'interface TV", ex); }
        }

        var restored = await _consoleService.ExitAsync();
        RestoreDesktopWidgets();
        RestoreConsoleMinimized();
        if (_config.Console.TvAutoOff && TvRemote.IsPaired) _ = TvRemote.SleepAsync();
        await Task.Delay(400);
        await splash.CloseAsync(450);
        if (_disposed) return;
        if (!restored)
            ShowWarning("Les écrans n'ont pas pu être entièrement restaurés.\n\nSysDeck réessaiera au prochain démarrage. " +
                        "Sinon, rétablis tes écrans dans Paramètres Windows › Affichage.");
    }

    /// <summary>Gâchettes LT / RT : volume de la TV par le réseau si elle est appairée, sinon volume Windows de la sortie TV.</summary>
    private async Task ChangeConsoleVolumeAsync(ConsoleWindow window, int direction)
    {
        _volumeAskedAt = DateTime.UtcNow;
        if (TvRemote.IsPaired && await TvRemote.VolumeStepAsync(direction)) return;   // la TV renvoie son volume -> jauge
        var level = MasterVolume.ChangeAndGet(direction * 2);
        window.ShowVolume(level, 100, false);
    }

    /// <summary>Fermeture de SysDeck : quitte le mode sans attendre d'interface (écrans, icônes et audio sont remis).</summary>
    private void StopConsoleMode()
    {
        try
        {
            var window = _consoleWindow;
            _consoleWindow = null;
            try { window?.Close(); } catch (Exception ex) { Log.Warn("Mode console : fermeture de l'interface TV", ex); }
            if (_consoleService.IsActive) _consoleService.ExitAsync().Wait(TimeSpan.FromSeconds(20));
            RestoreConsoleMinimized();
            _comboWatcher?.Dispose();
            _tvRemote?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : sortie à la fermeture en échec", ex);
        }
        _desktopWidgetsHidden = false;
        _widgetsHiddenForConsole.Clear();
        TileRuntime.ConsoleActive = false;
    }

    // ---------- Widgets du bureau ----------

    /// <summary>
    /// Les widgets du bureau seraient affichés sur la TV : ils sont FERMÉS pendant le mode (sans toucher à
    /// Config.Layout), car le bureau de Windows (WorkerW qui les héberge) est reconstruit à chaque bascule d'écrans.
    /// </summary>
    private void HideDesktopWidgets()
    {
        _desktopWidgetsHidden = true;
        foreach (var id in _windows.Keys.ToList())
            CloseWidget(id);
        _widgetsHiddenForConsole.Clear();
        UpdateSensorDemand();
    }

    /// <summary>Recrée les widgets exactement comme au démarrage : positions lues dans Config.Layout, rattachés au bureau.</summary>
    private void RestoreDesktopWidgets()
    {
        _desktopWidgetsHidden = false;
        foreach (var id in _windows.Keys.ToList())
            CloseWidget(id); // créés pendant le mode (cachés) : recréés proprement
        _widgetsHiddenForConsole.Clear();
        try
        {
            CreateWidgets();
            RefreshAll();
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : recréation des modules impossible", ex);
        }
    }
}
