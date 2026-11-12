using System.IO;
using SysDeck.Infrastructure;

namespace SysDeck.Services.ConsoleMode;

/// <param name="TvTargetId">Identifiant stable de l'écran de la TV (<see cref="DisplayTarget.Id"/>).</param>
/// <param name="TvAudioDeviceId">Sortie audio de la TV ; vide = ne pas toucher à l'audio.</param>
/// <param name="DisableTvWhenIdle">Désactive la TV à la sortie du mode console si la configuration restaurée la laisse active.</param>
public sealed record ConsoleModeOptions(string TvTargetId, string? TvAudioDeviceId = null, bool DisableTvWhenIdle = false);

/// <summary>Résultat de l'entrée en mode console ; Tv / Bounds / GdiName servent à ouvrir l'interface sur la TV.</summary>
public sealed record ConsoleModeResult(bool Success, string? Error, DisplayTarget? Tv, string? GdiName, DisplayBounds? Bounds);

/// <summary>Fichier d'état écrit AVANT la bascule : permet de tout restaurer après un plantage.</summary>
public sealed class ConsoleState
{
    public int Version { get; set; } = 1;
    public DateTime Started { get; set; } = DateTime.Now;
    public string TvTargetId { get; set; } = "";
    public string? TvAudioDeviceId { get; set; }
    public string? PreviousAudioDeviceId { get; set; }
    public bool DisableTvWhenIdle { get; set; }
    public DisplaySnapshot? Display { get; set; }
    public DesktopIconModel? Icons { get; set; }
    /// <summary>Échecs de restauration consécutifs : après 3, le fichier est abandonné.</summary>
    public int FailedRestores { get; set; }
}

/// <summary>
/// Mode console : n'affiche que la TV (les autres écrans sont désactivés), bascule l'audio, puis remet tout
/// (écrans, icônes, audio) à la sortie ou au prochain démarrage après un plantage. Ne lève jamais d'exception.
/// </summary>
public sealed class ConsoleModeService
{
    public const string StateFileName = "console-state.json";

    private readonly DisplayTopology _display;
    private readonly DesktopIconLayout _icons;
    private readonly AudioService _audio;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConsoleState? _state;

    public ConsoleModeService(DisplayTopology? display = null, DesktopIconLayout? icons = null, AudioService? audio = null, string? statePath = null)
    {
        _display = display ?? new DisplayTopology();
        _icons = icons ?? new DesktopIconLayout();
        _audio = audio ?? new AudioService();
        _statePath = statePath ?? Path.Combine(AppPaths.DataDir, StateFileName);
    }

    public bool IsActive { get; private set; }

    public event EventHandler? StateChanged;

    public string StatePath => _statePath;

    public DisplayTopology Display => _display;

    // ---------- Entrée ----------

    public Task<ConsoleModeResult> EnterAsync(ConsoleModeOptions opts) => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return EnterCore(opts); }
        catch (Exception ex)
        {
            Log.Error("Mode console : entrée impossible", ex);
            return new ConsoleModeResult(false, ex.Message, null, null, null);
        }
        finally { _gate.Release(); }
    });

    private ConsoleModeResult EnterCore(ConsoleModeOptions opts)
    {
        Log.Info("Mode console : entrée demandée.");
        if (IsActive || File.Exists(_statePath))
        {
            // Déjà actif (ou état résiduel) : on renvoie l'écran TV actuel sans refaire de capture.
            var cur = FindTv(opts.TvTargetId);
            if (IsActive && cur is { IsActive: true })
                return new ConsoleModeResult(true, null, cur, cur.GdiName, cur.Bounds);
            if (!IsActive && RecoverCore()) { /* état résiduel nettoyé, on continue */ }
        }

        var tv = FindTv(opts.TvTargetId);
        if (tv is null) return Fail("La TV n'est pas connectée.");

        var snap = _display.Snapshot();
        if (snap is null || snap.Paths.Count == 0) return Fail("Impossible de lire la configuration des écrans.");

        var icons = _icons.Save();
        string? prevAudio = null;
        try { prevAudio = _audio.GetPlaybackDevices().FirstOrDefault(d => d.IsDefault)?.Id; }
        catch (Exception ex) { Log.Warn("Mode console : sortie audio actuelle illisible", ex); }

        var state = new ConsoleState
        {
            TvTargetId = opts.TvTargetId,
            TvAudioDeviceId = opts.TvAudioDeviceId,
            PreviousAudioDeviceId = prevAudio,
            DisableTvWhenIdle = opts.DisableTvWhenIdle,
            Display = snap,
            Icons = icons,
        };
        // Écrit AVANT de toucher aux écrans ; sans cette garantie de reprise, on refuse de basculer.
        if (!JsonFile.Save(_statePath, state)) return Fail("Impossible d'écrire le fichier d'état.");
        _state = state;

        var res = _display.ActivateOnly(opts.TvTargetId);
        if (!res.Success)
        {
            Log.Warn($"Mode console : activation refusée ({res.Message}).");
            if (!_display.Matches(snap)) _display.Restore(snap);
            DeleteState();
            return Fail($"Windows n'a pas pu basculer sur la TV : {res.Message}");
        }

        if (!string.IsNullOrWhiteSpace(opts.TvAudioDeviceId))
        {
            try { _audio.SetDefaultDevice(opts.TvAudioDeviceId); Log.Info("Mode console : audio basculé sur la TV."); }
            catch (Exception ex) { Log.Warn("Mode console : bascule audio impossible", ex); }
        }

        // Attente (8 s max) que l'écran de la TV soit actif avec ses bornes.
        DisplayTarget? ready = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var t = FindTv(opts.TvTargetId);
            if (t is { IsActive: true, Bounds: not null, GdiName: not null }) { ready = t; break; }
            Thread.Sleep(250);
        }
        if (ready is null)
        {
            Log.Warn("Mode console : l'écran de la TV n'est pas devenu actif à temps, retour au bureau normal.");
            ExitCore();
            return Fail("L'écran de la TV ne s'est pas activé.");
        }

        IsActive = true;
        if (!string.IsNullOrWhiteSpace(opts.TvAudioDeviceId)) StartAudioGuard(opts.TvAudioDeviceId);
        LogTvMode(ready.GdiName, "à l'entrée");
        WatchDisplayChanges(ready.GdiName);
        var gdi = ready.GdiName;
        _ = Task.Run(async () => { await Task.Delay(15000); if (IsActive) LogTvMode(gdi, "15 s après"); });
        Log.Info($"Mode console actif sur {ready.Name} ({ready.GdiName}, {ready.Bounds}).");
        RaiseChanged();
        return new ConsoleModeResult(true, null, ready, ready.GdiName, ready.Bounds);

        ConsoleModeResult Fail(string msg)
        {
            Log.Warn("Mode console : " + msg);
            return new ConsoleModeResult(false, msg, null, null, null);
        }
    }

    // ---------- Sortie ----------

    // ---------- Garde audio ----------

    private CancellationTokenSource? _audioGuard;

    /// <summary>
    /// Après la bascule d'écrans, la sortie HDMI de la TV disparaît et réapparaît parfois (resynchronisation) :
    /// Windows repasse alors le son sur le PC. Pendant tout le mode console, on remet la TV par défaut dès qu'elle est là.
    /// </summary>
    private void StartAudioGuard(string tvAudioId)
    {
        StopAudioGuard();
        _keepAlive.Start(tvAudioId);
        var cts = new CancellationTokenSource();
        _audioGuard = cts;
        _ = Task.Run(async () =>
        {
            var fixes = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1500, cts.Token);
                    if (!IsActive) continue;
                    var devices = _audio.GetPlaybackDevices();
                    var tv = devices.FirstOrDefault(d => d.Id == tvAudioId);
                    if (tv is null || tv.IsDefault) continue;
                    _audio.SetDefaultDevice(tvAudioId);
                    if (++fixes <= 5) Log.Info("Mode console : sortie audio remise sur la TV (Windows l'avait changée).");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.WarnOnce("console-audio-guard", "Mode console : garde audio", ex); }
            }
        });
    }

    private readonly HdmiAudioKeepAlive _keepAlive = new();

    private void StopAudioGuard()
    {
        _keepAlive.Stop();
        _audioGuard?.Cancel();
        _audioGuard = null;
    }

    public Task<bool> ExitAsync() => Task.Run(async () =>
    {
        StopAudioGuard();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return ExitCore(); }
        catch (Exception ex) { Log.Error("Mode console : sortie impossible", ex); return false; }
        finally { _gate.Release(); }
    });

    /// <summary>Au démarrage de l'application : restaure tout si un plantage a interrompu le mode console. Renvoie true si une reprise a eu lieu.</summary>
    public bool RecoverIfNeeded()
    {
        try
        {
            _gate.Wait();
            try { return RecoverCore(); }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            Log.Error("Mode console : reprise impossible", ex);
            return false;
        }
    }

    private bool RecoverCore()
    {
        if (!File.Exists(_statePath)) return false;
        var state = JsonFile.Load<ConsoleState?>(_statePath, () => null);
        if (state is null)
        {
            Log.Warn("Mode console : fichier d'état illisible, supprimé.");
            DeleteState();
            return false;
        }
        Log.Warn("Mode console : fichier d'état trouvé (arrêt anormal ?), restauration.");
        _state = state;
        ExitCore();
        return true;
    }

    private bool ExitCore()
    {
        SysDeck.ConsoleMode.DesktopShell.ShowAll(); // icônes et barre des tâches (aussi après un plantage)
        var state =_state ?? (File.Exists(_statePath) ? JsonFile.Load<ConsoleState?>(_statePath, () => null) : null);
        if (state is null)
        {
            SetInactive();
            return true;
        }
        Log.Info("Mode console : sortie.");

        bool displayOk = true;
        if (state.Display is not null)
        {
            var r = _display.Restore(state.Display);
            displayOk = r.Success;
            if (!r.Success) Log.Warn($"Mode console : écrans non restaurés ({r.Message}).");
        }

        // La TV ne sert qu'au mode console : elle est toujours désactivée en sortant (même si elle était
        // étendue avant l'entrée), à condition qu'un autre écran reste allumé.
        if (!string.IsNullOrEmpty(state.TvTargetId)) DisableTvAfterExit(state.TvTargetId); // vérifie qu'un autre écran reste allumé
        Thread.Sleep(1500); // stabilisation : l'Explorateur réorganise les icônes après le changement d'écran

        if (state.Icons is not null)
        {
            _icons.Restore(state.Icons);
            // L'Explorateur réorganise en différé : surveillance en arrière-plan pendant 15 s (ne bloque pas la sortie).
            var icons = state.Icons;
            _ = Task.Run(() => _icons.RestoreAndHold(icons, TimeSpan.FromSeconds(15)));
        }

        if (!string.IsNullOrWhiteSpace(state.PreviousAudioDeviceId))
        {
            try
            {
                if (_audio.GetPlaybackDevices().Any(d => d.Id == state.PreviousAudioDeviceId))
                    _audio.SetDefaultDevice(state.PreviousAudioDeviceId);
            }
            catch (Exception ex) { Log.Warn("Mode console : audio non restauré", ex); }
        }

        if (displayOk || ++state.FailedRestores >= 3)
        {
            if (!displayOk) Log.Error("Mode console : restauration des écrans abandonnée après 3 essais.");
            DeleteState();
        }
        else
        {
            JsonFile.Save(_statePath, state); // sera retenté au prochain démarrage
            _state = state;
        }
        SetInactive();
        return displayOk;
    }

    // ---------- Divers ----------

    /// <summary>Désactive la TV hors mode console (Windows ne la voit plus). Sans effet si le mode console est actif.</summary>
    public DisplayResult EnsureTvDisabled(string tvId)
    {
        try
        {
            if (IsActive) return DisplayResult.Ok("mode console actif");
            var tv = FindTv(tvId);
            if (tv is null) return DisplayResult.Ok("TV non connectée");
            if (!tv.IsActive) return DisplayResult.Ok("déjà inactive");
            return _display.Deactivate(tvId);
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : désactivation de la TV impossible", ex);
            return DisplayResult.Fail(0, ex.Message);
        }
    }

    /// <summary>Désactive la TV après la restauration (appelé pendant la sortie, quand IsActive est encore vrai).</summary>
    private void DisableTvAfterExit(string tvId)
    {
        try
        {
            var targets = _display.ListTargets();
            var tv = targets.FirstOrDefault(t => DisplayTargetId.Matches(tvId, t.AdapterLuid, t.TargetId, t.DevicePath));
            if (tv is null || !tv.IsActive) return;
            if (!targets.Any(t => t.IsActive && !ReferenceEquals(t, tv)))
            {
                Log.Warn("Mode console : TV laissée allumée (aucun autre écran actif).");
                return;
            }
            var r = _display.Deactivate(tvId);
            if (r.Success) Log.Info("Mode console : TV désactivée.");
            else Log.Warn($"Mode console : TV non désactivée ({r.Message}).");
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : désactivation de la TV impossible", ex);
        }
    }

    private EventHandler? _displayChanged;

    /// <summary>Journal : chaque changement d'affichage signalé par Windows pendant le mode console (diagnostic écran noir).</summary>
    private void WatchDisplayChanges(string? gdiName)
    {
        if (_displayChanged != null) Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _displayChanged;
        _displayChanged = (_, _) => { if (IsActive) LogTvMode(gdiName, "Windows signale un changement d'affichage"); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _displayChanged;
    }

    /// <summary>Journal : mode d'affichage actuel de la TV (pour repérer un changement fait par Windows ou le pilote).</summary>
    private static void LogTvMode(string? gdiName, string when)
    {
        if (string.IsNullOrEmpty(gdiName)) return;
        try
        {
            var dm = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(gdiName, -1, ref dm))
                Log.Info($"Mode console : TV {dm.dmPelsWidth}x{dm.dmPelsHeight} à {dm.dmDisplayFrequency} Hz ({when}).");
        }
        catch (Exception ex) { Log.WarnOnce("tv-mode", "Mode console : mode de la TV illisible", ex); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    private DisplayTarget? FindTv(string tvId)
        => _display.ListTargets().FirstOrDefault(t => DisplayTargetId.Matches(tvId, t.AdapterLuid, t.TargetId, t.DevicePath));

    private void DeleteState()
    {
        _state = null;
        try { if (File.Exists(_statePath)) File.Delete(_statePath); }
        catch (Exception ex) { Log.Warn("Mode console : suppression du fichier d'état impossible", ex); }
    }

    private void SetInactive()
    {
        if (!IsActive) return;
        IsActive = false;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warn("Mode console : un abonné à StateChanged a échoué", ex); }
    }
}
