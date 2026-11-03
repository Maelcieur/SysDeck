using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SysDeck.Infrastructure;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck.Deck;

/// <summary>
/// Écran de la Switch : héberge <see cref="DeckScreen"/> dans une fenêtre hors de l'écran (pour que
/// mise en page, animations et événements Loaded fonctionnent), le rend à intervalle régulier pour
/// <see cref="DeckLink"/> et transforme le tactile reçu en actions (raccourcis, boutons des modules).
/// </summary>
public sealed partial class DeckController : IDisposable
{
    /// <summary>Éléments propres à la Switch, en plus des modules.</summary>
    public static readonly WidgetDescriptor[] ExtraElements =
    {
        new("clock", "Horloge"),
        new("apps", "Tuiles de la page"),
    };

    /// <summary>Tous les éléments plaçables sur la Switch (modules du bureau sauf la barre, + éléments propres).</summary>
    public static IEnumerable<WidgetDescriptor> AllElements
        => ExtraElements.Concat(WidgetManager.AllWidgets.Where(w => w.Id != "shortcuts"));

    private readonly DeckSettings _settings;
    private readonly Action<ShortcutItem> _launch;
    private readonly Func<string, ModuleAppearance> _desktopAppearance;
    private readonly Window _host;
    private readonly DeckLink _link;
    private readonly DispatcherTimer _renderTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _nightTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly RenderTargetBitmap _bitmap = new(DeckProtocol.ScreenWidth, DeckProtocol.ScreenHeight, 96, 96, PixelFormats.Pbgra32);

    /// <param name="desktopAppearance">Apparence du module sur le bureau (point de départ de celle de la Switch).</param>
    public DeckController(AppConfig config, Action save, Func<string, UIElement?> buildModule, Func<string, ModuleAppearance> desktopAppearance,
        Action<UIElement, ModuleAppearance> applyModuleSettings, Action<ShortcutItem> launch)
    {
        _settings = config.Deck;
        _desktopAppearance = desktopAppearance;
        _launch = launch;
        if (_settings.IsNew) AddDefaultElements();

        Screen = new DeckScreen(config, save, buildModule, GetAppearance, applyModuleSettings);

        // Fenêtre visible mais loin de tout écran, absente de la barre des tâches et d'Alt+Tab.
        _host = new Window
        {
            Title = "SysDeck — Switch",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Left = -32000,
            Top = -32000,
            FontSize = 13,
            Content = Screen
        };
        _host.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "TextFont");
        _host.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(_host).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
        };
        Screen.Sync();
        _host.Show();

        _link = new DeckLink(Application.Current.Dispatcher)
        {
            Enabled = _settings.Enabled,
            Brightness = _settings.Brightness
        };
        _link.StatusChanged += status =>
        {
            Status = status;
            if (!status.Connected)
            {
                ResetTouch(); // un Up perdu avec la liaison ne doit pas laisser tuile ou curseur « tenus »
                CloseTextEntry();
            }
            StatusChanged?.Invoke(status);
        };
        _link.Touch += OnTouch;
        _link.VolumeStep += step => MasterVolume.Change(step * 2); // boutons VOL± de la Switch : pas de 2 %

        _nightTimer.Tick += (_, _) => NightTick();
        _nightTimer.Start();
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _renderTimer.Tick += (_, _) => RenderFrame();
        SetFps(_settings.Fps);
        _renderTimer.Start();
        _link.Start();
    }

    public DeckSettings Settings => _settings;

    /// <summary>Visuel de l'écran (aperçu en direct dans l'éditeur des Paramètres).</summary>
    public DeckScreen Screen { get; }

    public DeckStatus Status { get; private set; } = new(false, -1, false);

    public event Action<DeckStatus>? StatusChanged;

    /// <summary>Des modules ont été ajoutés ou retirés de la Switch (capteurs à rafraîchir).</summary>
    public event Action? ContentsChanged;

    public IEnumerable<UIElement> ModuleContents => Screen.ModuleContents;

    /// <summary>Tuile enfoncée sur tous les panneaux de tuiles ; null les relâche toutes.</summary>
    private void SetPressed(Border? tile)
    {
        foreach (var panel in Screen.TilePanels) panel.SetPressed(tile);
    }

    public bool IsConnected => _link.IsConnected;

    private DeckTextEntry? _textEntry;

    /// <summary>Ouvre la saisie de texte sur la Switch (remplace celle déjà ouverte).</summary>
    public void ShowTextEntry(TextEntryRequest request, TwitchService twitch)
    {
        CloseTextEntry();
        ResetTouch();
        _textEntry = new DeckTextEntry(request, twitch, WakeRender, CloseTextEntry);
        Screen.SetOverlay(_textEntry);
        WakeRender();
    }

    public void CloseTextEntry()
    {
        if (_textEntry is null) return;
        _textEntry.Dispose();
        _textEntry = null;
        Screen.SetOverlay(null);
        WakeRender();
    }

    public void SetBrightness(int percent)
    {
        _settings.Brightness = Math.Clamp(percent, 0, 100);
        _link.Brightness = _settings.Brightness;
    }

    public void SetFps(int fps)
    {
        _settings.Fps = Math.Clamp(fps, 2, 30);
        _renderTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _settings.Fps);
    }

    public void SetEnabled(bool enabled)
    {
        _settings.Enabled = enabled;
        _link.Enabled = enabled;
    }

    // ---------- Rendu ----------

    // Écran au repos (images identiques d'affilée) : une image sur IdleDivider seulement, pleine cadence au moindre changement.
    private const int IdleAfterFrames = 6;
    private const int IdleDivider = 6;
    private int _idleSkip;

    /// <summary>Rend l'image suivante tout de suite, même au repos (un toucher doit répondre sans attendre).</summary>
    private void WakeRender() => _idleSkip = IdleDivider;

    private void RenderFrame()
    {
        if (!_link.IsConnected) return;
        if (_link.UnchangedFrames >= IdleAfterFrames && ++_idleSkip < IdleDivider) return;
        _idleSkip = 0;
        try
        {
            _bitmap.Clear();
            _bitmap.Render(Screen);
            var frame = _link.RentFrame();
            _bitmap.CopyPixels(frame, DeckProtocol.ScreenWidth * 4, 0);
            _link.Submit(frame);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("deck:render", "Rendu de l'écran Switch en échec", ex);
        }
    }

    public void Dispose()
    {
        _renderTimer.Stop();
        _nightTimer.Stop();
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NightTick(force: true); // kit à jour avant l'extinction (l'heure et BYE partent à la fermeture de la liaison)
        _link.Dispose(); // envoie BYE : la Switch revient à son écran d'attente
        _host.Close();
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(nint hwnd, int index, nint value);
}
