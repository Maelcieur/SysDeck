using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SysDeck.Infrastructure;
using WpfImage = System.Windows.Controls.Image;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

using Rectangle = System.Windows.Shapes.Rectangle;
using Path = System.Windows.Shapes.Path;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Interface TV plein écran du Mode console (jaquettes, détail, manette + clavier + souris).
/// Elle ne fait PAS la bascule des écrans : l'appelant fournit les bornes de l'écran (ShowOn) et
/// réagit à ExitRequested. Pendant un jeu, elle reste derrière lui comme fond (jamais de bureau visible).
/// </summary>
/// <summary>Réglages d'affichage de l'interface TV (thème, intro).</summary>
public sealed record ConsoleUiOptions(string ThemeId = "nuit", bool IntroEnabled = true, string? IntroFolder = null, string? AudioDeviceId = null, bool Preview = false,
    bool Sounds = true, bool Vibration = true, string IntroStyle = "auto");

public partial class ConsoleWindow : Window
{
    private ConsoleTheme _theme = ConsoleThemes.Default;
    private Color Accent => _theme.Accent;
    private Brush TextBrush => _theme.Brush(_theme.Text);
    private Brush SubBrush => _theme.Brush(_theme.SubText);
    private Color Soft(byte alpha) => _theme.WithAlpha(_theme.Text, alpha);
    private const double SelW = 260, SelH = 390, NormW = 190, NormH = 285, ShelfH = 440, Step = 215, ShelfLeft = 100;
    private static readonly TimeSpan GameWindowTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Zone qui a le focus de la manette : onglets (haut), jaquettes, boutons Jouer / Infos (bas).</summary>
    private enum Zone { Tabs, Shelf, Actions }

    private readonly ConsoleLibrary _library;
    private readonly GamepadService _pad;
    private readonly DispatcherTimer _clock;
    private readonly List<Border> _tiles = new();
    private readonly List<string> _tabs = new();
    private List<ConsoleGame> _all = new();
    private List<ConsoleGame> _view = new();
    private int _sel, _tab, _action;
    private Zone _zone = Zone.Shelf;
    private Rect _bounds;
    private bool _playing, _heroFlip, _layoutDone, _introDone, _infoOpen;
    private string? _heroPath;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _gameWatch;
    private readonly ConsoleUiOptions _options;
    private CancellationTokenSource? _introCts;
    private bool _themeOpen, _closed;
    private ConsoleBackdrop? _backdrop;
    private int _themeIndex;
    private string _themeBefore = "nuit";

    /// <summary>Demandé par la tuile « Retour au PC » : à l'appelant de rebasculer les écrans et de fermer la fenêtre.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Gâchettes LT (-1) / RT (+1) : l'appelant règle le volume (TV par le réseau, sinon Windows) puis appelle ShowVolume.</summary>
    public event Action<int>? VolumeRequested;

    /// <summary>Thème gardé depuis le menu Thèmes de la console (identifiant) : à enregistrer par l'appelant.</summary>
    public event Action<string>? ThemeChosen;

    public ConsoleWindow() : this(new ConsoleLibrary()) { }

    public ConsoleWindow(ConsoleLibrary library, ConsoleUiOptions? options = null)
    {
        InitializeComponent();
        _library = library;
        _options = options ?? new ConsoleUiOptions();
        if (_options.Sounds) _sounds = new UiSounds(_options.AudioDeviceId, ConsoleIntro.IsCyber(_options.IntroStyle, _theme));
        ApplyTheme(ConsoleThemes.Find(_options.ThemeId), rebuild: false);
        _pad = new GamepadService(Dispatcher);
        _pad.ButtonPressed += OnPad;
        _pad.GuideLongPressed += OnGuideLong;
        _pad.ControllersChanged += RefreshControllers;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm");
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        PreviewKeyDown += OnKey;
        MouseWheel += (_, e) => { if (!_playing && !_infoOpen && !_themeOpen) Move(e.Delta < 0 ? 1 : -1); };
        // Curseur caché : pendant l'intro et le chargement, et dans le menu après 3 s sans bouger la souris.
        Cursor = System.Windows.Input.Cursors.None;
        var cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        cursorTimer.Tick += (_, _) => { cursorTimer.Stop(); Cursor = System.Windows.Input.Cursors.None; };
        var lastMouse = new Point(double.NaN, double.NaN);
        MouseMove += (_, e) =>
        {
            var p = e.GetPosition(this);
            if (p == lastMouse) return;          // évènement sans vrai mouvement (fenêtre qui s'affiche)
            lastMouse = p;
            if (!_introDone || LoadingLayer.Visibility == Visibility.Visible) return;
            Cursor = null;
            cursorTimer.Stop();
            cursorTimer.Start();
        };
        Closed += (_, _) => { _closed = true; _clock.Stop(); _gameWatch?.Stop(); _pad.Dispose(); _cts?.Cancel(); _introCts?.Cancel(); _backdrop?.Close(); _sounds?.Dispose(); _bgGlitchTimer?.Stop(); if (!_options.Preview) DesktopShell.ShowAll(); };
    }

    // ---------- Thème ----------

    /// <summary>Applique un thème à toute l'interface (couleurs, coins, police, décor animé).</summary>
    private void ApplyTheme(ConsoleTheme t, bool rebuild = true)
    {
        _theme = t;
        var bg = t.Bg;
        Background = t.Brush(bg);
        FontFamily = new FontFamily(t.Font);
        BaseBg.Fill = new RadialGradientBrush(t.Bg2, t.Bg) { Center = new Point(0.75, 0.3), GradientOrigin = new Point(0.75, 0.3), RadiusX = 0.9, RadiusY = 0.9 };
        var h = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        h.GradientStops.Add(new GradientStop(t.WithAlpha(bg, 0xF0), 0));
        h.GradientStops.Add(new GradientStop(t.WithAlpha(bg, 0xB0), 0.45));
        h.GradientStops.Add(new GradientStop(t.WithAlpha(bg, 0x30), 1));
        ShadeH.Fill = h;
        var v = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        v.GradientStops.Add(new GradientStop(t.WithAlpha(bg, 0x90), 0));
        v.GradientStops.Add(new GradientStop(t.WithAlpha(bg, 0x00), 0.25));
        v.GradientStops.Add(new GradientStop(t.WithAlpha(bg, 0x00), 0.5));
        v.GradientStops.Add(new GradientStop(t.WithAlpha(bg, 0xE6), 1));
        ShadeV.Fill = v;
        ConsoleAmbientLayer.Build(AmbientCanvas, t);
        UpdateBackgroundGlitch();
        if (_sounds != null) _sounds.Cyber = ConsoleIntro.IsCyber(_options.IntroStyle, t);   // bruitages assortis au style

        ClockText.Foreground = TextBrush;
        TitleText.Foreground = TextBrush;
        DescriptionText.Foreground = t.Brush(t.WithAlpha(t.Text, 0xD8));
        InfoText.Foreground = SubBrush;
        EmptyText.Foreground = TextBrush;
        PlatformBadge.Background = t.Brush(t.WithAlpha(t.Accent, 0x33));
        PlatformBadge.BorderBrush = t.Brush(t.Accent);
        PlatformText.Foreground = TextBrush;
        PlayButton.CornerRadius = InfoButton.CornerRadius = QuitButton.CornerRadius = new CornerRadius(Math.Max(6, t.Corner));
        QuitGlyph.Foreground = QuitText.Foreground = TextBrush;
        PlayText.Foreground = PlayGlyph.Foreground = t.Brush(t.OnAccent);
        InfoGlyph.Foreground = InfoLabel.Foreground = TextBrush;
        InfoCard.Background = t.Brush(t.Panel);
        InfoCard.BorderBrush = t.Brush(t.WithAlpha(t.Text, 0x30));
        InfoCard.CornerRadius = new CornerRadius(t.Corner + 4);
        InfoPanel.Background = t.Brush(t.WithAlpha(bg, 0xE6));
        InfoTitle.Foreground = TextBrush;
        InfoMeta.Foreground = SubBrush;
        InfoDescription.Foreground = TextBrush;
        Toast.Background = t.Brush(t.WithAlpha(t.Panel, 0xF0));
        VolumeOsd.Background = t.Brush(t.WithAlpha(t.Panel, 0xF0));
        VolumeGlyph.Foreground = VolumeText.Foreground = TextBrush;
        VolumeTrack.Background = t.Brush(t.WithAlpha(t.Text, 0x30));
        VolumeFill.Background = t.Brush(t.Accent);
        Toast.BorderBrush = t.Brush(t.Accent);
        ToastText.Foreground = TextBrush;
        LoadingLayer.Background = t.Brush(bg);
        LoadingVignette.Fill = new RadialGradientBrush(t.WithAlpha(bg, 0x60), bg) { RadiusX = 0.75, RadiusY = 0.75 };
        LoadingTitle.Foreground = TextBrush;
        LoadingText.Foreground = SubBrush;
        LoadingSpinner.Stroke = t.Brush(t.Accent);
        ThemeShade.Fill = new LinearGradientBrush(t.WithAlpha(bg, 0x00), t.WithAlpha(bg, 0xF5), 90);
        ThemeHeader.Foreground = TextBrush;
        ThemeHint.Foreground = SubBrush;

        if (!rebuild) return;
        var keep = _sel < _view.Count ? _view[_sel].Id : null;
        BuildView(keep);
        RefreshControllers();
        if (_themeOpen) RenderThemeStrip();
    }

    private void OpenThemes()
    {
        if (_playing || _infoOpen) return;
        _themeOpen = true;
        Feedback(UiSound.Open);
        _themeBefore = _theme.Id;
        _themeIndex = ConsoleThemes.IndexOf(_theme.Id);
        ThemePanel.Visibility = Visibility.Visible;
        ThemePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        RenderThemeStrip();
        RenderHints();
    }

    private void CloseThemes(bool keep)
    {
        _themeOpen = false;
        Feedback(keep ? UiSound.Select : UiSound.Back);
        ThemePanel.Visibility = Visibility.Collapsed;
        if (keep) ThemeChosen?.Invoke(_theme.Id);
        else if (_theme.Id != _themeBefore) ApplyTheme(ConsoleThemes.Find(_themeBefore));
        RenderHints();
    }

    private void PreviewTheme(int delta)
    {
        var n = ConsoleThemes.All.Count;
        _themeIndex = ((_themeIndex + delta) % n + n) % n;
        Feedback(UiSound.Move);
        ApplyTheme(ConsoleThemes.All[_themeIndex]);
    }

    /// <summary>Vignettes des thèmes : fond, couleur d'accent et décor du thème, nom et époque.</summary>
    private void RenderThemeStrip()
    {
        ThemeStrip.Children.Clear();
        for (var i = 0; i < ConsoleThemes.All.Count; i++)
        {
            var t = ConsoleThemes.All[i];
            var on = i == _themeIndex;
            var idx = i;
            var card = ThemeCard(t, 150, 96);
            var host = new StackPanel { Width = 168, Margin = new Thickness(0, 0, 14, 0), Cursor = System.Windows.Input.Cursors.Hand };
            var frame = new Border
            {
                Child = card, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(4), Padding = new Thickness(2),
                BorderBrush = on ? _theme.Brush(_theme.Accent) : Brushes.Transparent,
                Effect = on ? new DropShadowEffect { Color = _theme.Accent, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.8 } : null,
            };
            host.Children.Add(frame);
            host.Children.Add(new TextBlock { Text = t.Name, Foreground = on ? TextBrush : SubBrush, FontSize = 21, FontWeight = on ? FontWeights.Bold : FontWeights.Normal, Margin = new Thickness(4, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            host.Children.Add(new TextBlock { Text = t.Era, Foreground = SubBrush, FontSize = 15, Margin = new Thickness(4, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Opacity = on ? 1 : 0.7 });
            host.MouseLeftButtonUp += (_, _) => { if (idx == _themeIndex) CloseThemes(true); else PreviewTheme(idx - _themeIndex); };
            ThemeStrip.Children.Add(host);
        }
        // 20 thèmes ne tiennent pas sur l'écran : la bande glisse pour garder le thème choisi vers le milieu.
        const double cardStep = 182;
        var maxShift = Math.Max(0, ConsoleThemes.All.Count * cardStep - 1720);
        var shift = Math.Clamp(_themeIndex * cardStep - 760, 0, maxShift);
        ThemeStripShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-shift, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Petite vignette d'un thème (réutilisable : Paramètres).</summary>
    public static FrameworkElement ThemeCard(ConsoleTheme t, double w, double h)
    {
        var g = new Grid { Width = w, Height = h, ClipToBounds = true };
        g.Children.Add(new Rectangle { Fill = new RadialGradientBrush(t.Bg2, t.Bg) { Center = new Point(0.75, 0.3), GradientOrigin = new Point(0.75, 0.3), RadiusX = 0.9, RadiusY = 0.9 }, RadiusX = 8, RadiusY = 8 });
        var amb = new Canvas { Width = 1920, Height = 1080, ClipToBounds = true };
        ConsoleAmbientLayer.Build(amb, t);
        g.Children.Add(new Viewbox { Stretch = Stretch.UniformToFill, Child = amb });
        var tiles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Top, Margin = new Thickness(w * 0.08, h * 0.16, 0, 0) };
        for (var i = 0; i < 4; i++)
            tiles.Children.Add(new Border
            {
                Width = i == 0 ? w * 0.2 : w * 0.15, Height = i == 0 ? h * 0.46 : h * 0.36, Margin = new Thickness(0, 0, w * 0.035, 0),
                CornerRadius = new CornerRadius(Math.Min(6, t.Corner / 3)), VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Background = new SolidColorBrush(i == 0 ? t.Accent : t.Panel),
                BorderBrush = new SolidColorBrush(t.WithAlpha(t.Text, 0x40)), BorderThickness = new Thickness(i == 0 ? 0 : 1),
            });
        g.Children.Add(tiles);
        g.Children.Add(new Border
        {
            Width = w * 0.3, Height = h * 0.13, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(t.Accent),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = System.Windows.VerticalAlignment.Bottom, Margin = new Thickness(w * 0.08, 0, 0, h * 0.12),
        });
        g.Children.Add(new Border
        {
            Width = w * 0.45, Height = h * 0.06, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(t.WithAlpha(t.Text, 0xB0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = System.Windows.VerticalAlignment.Bottom, Margin = new Thickness(w * 0.08, 0, 0, h * 0.32),
        });
        return new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = g };
    }

    // ---------- Intro ----------

    private async void PlayIntro()
    {
        _introCts = new CancellationTokenSource();
        try
        {
            // Petite attente : la TV met souvent une seconde à afficher l'image après la bascule.
            IntroLayer.Background = Brushes.Black;
            IntroLayer.Visibility = Visibility.Visible;
            await Task.Delay(900, _introCts.Token);
        }
        catch (OperationCanceledException) { }
        // Fond transparent : à la fin, l'intro se dissout directement dans le menu déjà prêt derrière.
        IntroLayer.Background = Brushes.Transparent;
        Log.Info("Mode console : début de l'intro.");
        await ConsoleIntro.PlayAsync(IntroLayer, _theme, _options.IntroFolder, _introCts.Token, _options.AudioDeviceId, _options.IntroStyle);
        Log.Info("Mode console : fin de l'intro, menu affiché.");
        _introCts = null;
        _introDone = true;
        LoadingLayer.Visibility = Visibility.Collapsed;
        RenderHints();
    }

    private void SkipIntro() => _introCts?.Cancel();

    // ---------- Affichage ----------

    /// <summary>Affiche la fenêtre plein écran sur l'écran délimité par <paramref name="boundsPx"/> (pixels physiques).</summary>
    public void ShowOn(Rect boundsPx)
    {
        if (_closed) return;
        _bounds = boundsPx;
        if (!_options.Preview && _backdrop == null)
        {
            // Mode console réel : fond de console à la place du bureau, sans icônes ni barre des tâches.
            _backdrop = new ConsoleBackdrop(_theme);
            _backdrop.SetGame("SysDeck", null, "Mode console");
            _backdrop.Show(boundsPx);
            DesktopShell.HideAll();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Topmost = true;
        if (!_introDone) ShowLoading("SysDeck", "Mode console", null, null);
        Reload();
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Place(hwnd);
        // 2e passe : la TV n'a pas le même DPI que les écrans du PC, WPF peut redimensionner après le 1er placement.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Place(hwnd));
        Activate();
        SetForegroundWindow(hwnd);
        Focus();
        _clock.Start();
        _pad.Start();
        RefreshControllers();
        if (!_introDone && _introCts == null)
        {
            if (_options.IntroEnabled)
            {
                LoadingLayer.Visibility = Visibility.Collapsed;
                PlayIntro();
            }
            else
            {
                _introDone = true;
                HideLoading(900);
            }
        }
    }

    /// <summary>
    /// Plein écran MOINS 1 pixel en bas : une fenêtre qui couvre exactement l'écran est traitée par Windows comme un
    /// jeu plein écran (FreeSync / VRR activé), et beaucoup de TV se resynchronisent alors (écran noir + « PC »).
    /// </summary>
    private void Place(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, (int)_bounds.X, (int)_bounds.Y, (int)_bounds.Width, Math.Max(1, (int)_bounds.Height - 1), SwpShowWindow | SwpNoActivate);

    /// <summary>Affiche le calque de chargement (sortie du mode, etc.).</summary>
    public void ShowBusy(string message) => ShowLoading("SysDeck", message, null, null);

    /// <summary>Relit la bibliothèque et reconstruit l'interface (conserve le jeu sélectionné).</summary>
    public void Reload()
    {
        var keep = _sel < _view.Count ? _view[_sel].Id : null;
        _library.Load();
        _all = _library.Sorted();
        BuildTabs();
        BuildView(keep);
    }

    private string PlatformLabel(ConsoleGame g)
    {
        var p = _library.PlatformOf(g);
        return string.IsNullOrWhiteSpace(p) ? "PC" : p;
    }

    private void BuildTabs()
    {
        var platforms = _all.Select(PlatformLabel).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(p => p == "PC" ? "" : p, StringComparer.CurrentCultureIgnoreCase).ToList();
        var old = _tab < _tabs.Count ? _tabs[_tab] : null;
        _tabs.Clear();
        if (platforms.Count <= 1) _tabs.Add("Jeux");
        else { _tabs.Add("Tous"); _tabs.AddRange(platforms); }
        _tab = Math.Max(0, _tabs.IndexOf(old ?? ""));
    }

    private void RenderTabs()
    {
        TabsPanel.Children.Clear();
        var focused = _zone == Zone.Tabs;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var idx = i;
            var on = i == _tab;
            var text = new TextBlock
            {
                Text = _tabs[i], FontSize = 36, FontWeight = FontWeights.SemiBold,
                Foreground = on ? TextBrush : SubBrush,
            };
            var line = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 0), Background = on ? new SolidColorBrush(Accent) : Brushes.Transparent };
            var inner = new StackPanel();
            inner.Children.Add(text);
            inner.Children.Add(line);
            var host = new Border
            {
                Child = inner, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 18, 0), CornerRadius = new CornerRadius(10),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = focused && on ? new SolidColorBrush(_theme.WithAlpha(Accent, 0x40)) : Brushes.Transparent,
                BorderBrush = focused && on ? new SolidColorBrush(Accent) : Brushes.Transparent, BorderThickness = new Thickness(2),
            };
            host.MouseLeftButtonUp += (_, _) => SetTab(idx);
            TabsPanel.Children.Add(host);
        }
    }

    private void SetTab(int index)
    {
        if (_tabs.Count == 0) return;
        _tab = ((index % _tabs.Count) + _tabs.Count) % _tabs.Count;
        Feedback(UiSound.Tab);
        BuildView(null);
    }

    private void BuildView(string? keepId)
    {
        var label = _tabs.Count > _tab ? _tabs[_tab] : "Jeux";
        _view = _tabs.Count <= 1 || label == "Tous" ? _all.ToList() : _all.Where(g => PlatformLabel(g) == label).ToList();
        _sel = keepId == null ? 0 : Math.Max(0, _view.FindIndex(g => g.Id == keepId));
        RenderTabs();
        BuildTiles();
        EmptyText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Layout(false);
        UpdateSelection(false);
        UpdateFocus();
    }

    private void BuildTiles()
    {
        Shelf.Children.Clear();
        _tiles.Clear();
        _layoutDone = false;
        for (var i = 0; i <= _view.Count; i++)
        {
            var idx = i;
            var tile = new Border
            {
                Width = NormW, Height = NormH, CornerRadius = new CornerRadius(_theme.Corner), ClipToBounds = true,
                Background = _theme.Brush(_theme.Panel),
                BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
            };
            var grid = new Grid();
            if (i < _view.Count)
            {
                var g = _view[i];
                var bmp = LoadBitmap(g.CoverPath, 400);
                if (bmp != null) grid.Children.Add(new WpfImage { Source = bmp, Stretch = Stretch.UniformToFill });
                else grid.Children.Add(new TextBlock
                {
                    Text = g.Title, Foreground = TextBrush, FontSize = 26, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16), VerticalAlignment = System.Windows.VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
                });
                var plat = _library.PlatformOf(g);
                if (!string.IsNullOrWhiteSpace(plat))
                    grid.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x07, 0x09, 0x0E)), CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(10), HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                        Child = new TextBlock { Text = plat, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold },
                    });
            }
            else
            {
                var sp = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 64, Foreground = new SolidColorBrush(Accent), HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = "Retour au PC", Foreground = TextBrush, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
                grid.Children.Add(sp);
            }
            tile.Child = grid;
            tile.MouseLeftButtonUp += (_, _) =>
            {
                if (_playing) return;
                SetZone(Zone.Shelf);
                if (idx == _sel) Activate_(); else Select(idx);
            };
            Canvas.SetLeft(tile, 2000);
            Shelf.Children.Add(tile);
            _tiles.Add(tile);
        }
    }

    private static double TileLeft(int i, int sel) =>
        i < sel ? ShelfLeft - (sel - i) * Step : i == sel ? ShelfLeft : ShelfLeft + SelW + 30 + (i - sel - 1) * Step;

    private void Layout(bool animate)
    {
        var anim = animate && _layoutDone;
        for (var i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            var on = i == _sel;
            double w = on ? SelW : NormW, h = on ? SelH : NormH;
            Set(t, Canvas.LeftProperty, TileLeft(i, _sel), anim);
            Set(t, Canvas.TopProperty, (ShelfH - h) / 2, anim);
            Set(t, WidthProperty, w, anim);
            Set(t, HeightProperty, h, anim);
            Set(t, OpacityProperty, on ? 1 : 0.72, anim);
            Panel.SetZIndex(t, on ? 10 : 0);
        }
        _layoutDone = true;
        UpdateTileFocus();
    }

    /// <summary>Contour lumineux de la jaquette sélectionnée : fort si la zone des jaquettes a le focus, discret sinon.</summary>
    private void UpdateTileFocus()
    {
        for (var i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            var on = i == _sel;
            var strong = on && _zone == Zone.Shelf;
            t.BorderBrush = strong ? new SolidColorBrush(Accent) : on ? new SolidColorBrush(Soft(0x80)) : Brushes.Transparent;
            t.Effect = strong ? new DropShadowEffect { Color = Accent, BlurRadius = 45, ShadowDepth = 0, Opacity = 0.85 } : null;
        }
    }

    private static void Set(UIElement el, DependencyProperty p, double to, bool animate)
    {
        if (animate)
            el.BeginAnimation(p, new DoubleAnimation(to, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            el.BeginAnimation(p, null);
            el.SetValue(p, to);
        }
    }

    // ---------- Sélection et détail ----------

    private bool IsReturnSelected => _sel >= _view.Count;

    private void Select(int index)
    {
        index = Math.Clamp(index, 0, _view.Count);
        if (index == _sel) return;
        _sel = index;
        Feedback(_theme.Glitch ? UiSound.Glitch : UiSound.Move);
        Layout(true);
        UpdateSelection(true);
        UpdateFocus();
    }

    private void Move(int delta) => Select(_sel + delta);

    private void UpdateSelection(bool animate)
    {
        var empty = _all.Count == 0;
        Detail.Visibility = empty && IsReturnSelected ? Visibility.Collapsed : Visibility.Visible;
        if (IsReturnSelected)
        {
            SetHero(null);
            LogoImage.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            TitleText.Text = "Retour au PC";
            PlatformBadge.Visibility = Visibility.Collapsed;
            DescriptionText.Text = "Quitte le Mode console : la TV s'éteint et les écrans du PC reviennent exactement comme avant.";
            InfoText.Text = "";
            PlayText.Text = "Retour au PC";
            PlayGlyph.Text = "";
            InfoButton.Visibility = Visibility.Collapsed;
            QuitButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var g = _view[_sel];
            SetHero(g.HeroPath);
            var logo = LoadBitmap(g.LogoPath, 800);
            LogoImage.Source = logo;
            LogoImage.Visibility = logo != null ? Visibility.Visible : Visibility.Collapsed;
            TitleText.Visibility = logo != null ? Visibility.Collapsed : Visibility.Visible;
            TitleText.Text = g.Title;
            var plat = _library.PlatformOf(g);
            PlatformBadge.Visibility = string.IsNullOrWhiteSpace(plat) ? Visibility.Collapsed : Visibility.Visible;
            PlatformText.Text = plat;
            DescriptionText.Text = g.Description;
            InfoText.Text = $"Temps de jeu : {ConsoleFormat.PlayTime(g.PlayTime)}      Dernière session : {ConsoleFormat.LastPlayed(g.LastPlayed, DateTime.Now)}";
            PlayText.Text = IsRunning(g) ? "Reprendre" : "Jouer";
            PlayGlyph.Text = "";
            InfoButton.Visibility = Visibility.Visible;
            QuitButton.Visibility = IsRunning(g) ? Visibility.Visible : Visibility.Collapsed;
            if (!_quitArmed) QuitText.Text = "Quitter";
        }
        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Detail.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            DetailShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            if (_theme.Glitch) GlitchBurst();
        }
    }

    private readonly Random _glitchRnd = new();
    private DispatcherTimer? _bgGlitchTimer;

    /// <summary>Thème « Néon glitch » : le fond saute de temps en temps (toutes les 2 à 6 s, au hasard).</summary>
    private void UpdateBackgroundGlitch()
    {
        if (!_theme.Glitch)
        {
            _bgGlitchTimer?.Stop();
            BgGlitchLayer.Children.Clear();
            return;
        }
        if (_bgGlitchTimer == null)
        {
            _bgGlitchTimer = new DispatcherTimer();
            _bgGlitchTimer.Tick += (_, _) =>
            {
                _bgGlitchTimer!.Interval = TimeSpan.FromMilliseconds(2000 + _glitchRnd.Next(4000));
                if (!_closed && IsVisible && !_playing && _theme.Glitch) BackgroundGlitch();
            };
        }
        _bgGlitchTimer.Interval = TimeSpan.FromMilliseconds(1500 + _glitchRnd.Next(2000));
        _bgGlitchTimer.Start();
    }

    /// <summary>
    /// Saut de signal : quelques tranches horizontales de l'image de fond, décalées à gauche ou à droite pendant
    /// 60 à 180 ms, avec un voile magenta / cyan, puis le fond tremble légèrement.
    /// </summary>
    private void BackgroundGlitch()
    {
        var w = BgGlitchLayer.ActualWidth;
        var h = BgGlitchLayer.ActualHeight;
        if (w < 10 || h < 10) return;
        var hero = HeroA.Opacity >= HeroB.Opacity ? HeroA : HeroB;
        var cyan = Color.FromRgb(0x05, 0xD9, 0xE8);
        BgGlitchLayer.Children.Clear();
        var slices = 3 + _glitchRnd.Next(5);
        for (var i = 0; i < slices; i++)
        {
            var top = _glitchRnd.NextDouble() * h;
            var height = h * (0.01 + _glitchRnd.NextDouble() * 0.09);
            var shift = (_glitchRnd.NextDouble() - 0.5) * w * 0.12;
            var band = new Grid { Clip = new RectangleGeometry(new Rect(0, top, w, height)) };
            if (hero.Source != null && hero.Opacity > 0.05)
                band.Children.Add(new System.Windows.Controls.Image { Source = hero.Source, Stretch = Stretch.UniformToFill, Opacity = hero.Opacity });
            else
                band.Children.Add(new System.Windows.Shapes.Rectangle { Fill = BaseBg.Fill });
            var tint = i % 2 == 0 ? _theme.Accent : cyan;
            band.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x38, tint.R, tint.G, tint.B)) });
            band.RenderTransform = new TranslateTransform(shift, 0);
            var life = 60 + _glitchRnd.Next(120);
            var f = new DoubleAnimationUsingKeyFrames();
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(life / 2))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(life * 3 / 4))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(life))));
            band.BeginAnimation(OpacityProperty, f);
            BgGlitchLayer.Children.Add(band);
        }
        // Petit tremblement de l'image entière.
        var jitter = new DoubleAnimationUsingKeyFrames();
        foreach (var (ms, x) in new[] { (0, -8.0), (40, 6.0), (80, -3.0), (120, 0.0) })
            jitter.KeyFrames.Add(new DiscreteDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        if (hero.RenderTransform is not TranslateTransform ht) hero.RenderTransform = ht = new TranslateTransform();
        ht.BeginAnimation(TranslateTransform.XProperty, jitter);
    }

    /// <summary>
    /// Thème « Néon glitch » : au changement de jeu, le détail et le fond tremblent, des barres colorées décalées
    /// traversent l'écran et le titre se dédouble en cyan / magenta pendant ~250 ms.
    /// </summary>
    private void GlitchBurst()
    {
        var cyan = Color.FromRgb(0x05, 0xD9, 0xE8);
        // Tremblement horizontal saccadé (images clés discrètes).
        DoubleAnimationUsingKeyFrames Jitter(double amp)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            var offsets = new[] { -amp, amp * 0.7, -amp * 0.4, amp * 0.25, 0 };
            for (var i = 0; i < offsets.Length; i++)
                a.KeyFrames.Add(new DiscreteDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 45))));
            return a;
        }
        DetailShift.BeginAnimation(TranslateTransform.XProperty, Jitter(14));
        foreach (var hero in new[] { HeroA, HeroB })
        {
            if (hero.RenderTransform is not TranslateTransform ht) hero.RenderTransform = ht = new TranslateTransform();
            ht.BeginAnimation(TranslateTransform.XProperty, Jitter(24));
        }
        // Titre dédoublé (aberration chromatique).
        TitleText.Effect = new DropShadowEffect { Color = cyan, ShadowDepth = 5, Direction = 0, BlurRadius = 0, Opacity = 0.9 };
        LogoImage.Effect = new DropShadowEffect { Color = _theme.Accent, ShadowDepth = 6, Direction = 180, BlurRadius = 0, Opacity = 0.9 };
        var clear = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        clear.Tick += (_, _) => { clear.Stop(); TitleText.Effect = null; LogoImage.Effect = null; };
        clear.Start();
        // Barres décalées.
        GlitchLayer.Children.Clear();
        for (var i = 0; i < 9; i++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = 120 + _glitchRnd.NextDouble() * 900, Height = 3 + _glitchRnd.NextDouble() * 26,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(0x60 + _glitchRnd.Next(0x80)), i % 3 == 0 ? cyan.R : _theme.Accent.R,
                    i % 3 == 0 ? cyan.G : _theme.Accent.G, i % 3 == 0 ? cyan.B : _theme.Accent.B)),
            };
            Canvas.SetLeft(bar, _glitchRnd.NextDouble() * 1700);
            Canvas.SetTop(bar, _glitchRnd.NextDouble() * 1060);
            var start = _glitchRnd.Next(0, 120);
            var f = new DoubleAnimationUsingKeyFrames();
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(start))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(start + 50 + _glitchRnd.Next(90)))));
            bar.BeginAnimation(OpacityProperty, f);
            GlitchLayer.Children.Add(bar);
        }
    }

    private void SetHero(string? path)
    {
        if (path == _heroPath) return;
        _heroPath = path;
        var bmp = LoadBitmap(path, 1920);
        var incoming = _heroFlip ? HeroB : HeroA;
        var outgoing = _heroFlip ? HeroA : HeroB;
        var d = TimeSpan.FromMilliseconds(650);
        if (bmp != null)
        {
            incoming.Source = bmp;
            incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
            _heroFlip = !_heroFlip;
        }
        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
    }

    private static BitmapImage? LoadBitmap(string? path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.DecodePixelWidth = decodeWidth;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("console-img-" + path, "Image illisible", ex);
            return null;
        }
    }

    // ---------- Focus (zones) ----------

    private void SetZone(Zone zone)
    {
        if (zone == Zone.Tabs && _tabs.Count <= 1 && _all.Count == 0) zone = Zone.Shelf;
        if (zone == Zone.Actions && Detail.Visibility != Visibility.Visible) zone = Zone.Shelf;
        if (_zone != zone) Feedback(UiSound.Move);
        _zone = zone;
        if (zone == Zone.Actions && !VisibleActions().Contains(_action)) _action = 0;
        UpdateFocus();
    }

    private void UpdateFocus()
    {
        RenderTabs();
        UpdateTileFocus();
        StyleButton(PlayButton, PlayScale, _zone == Zone.Actions && _action == 0, primary: true);
        StyleButton(QuitButton, QuitScale, _zone == Zone.Actions && _action == 1, primary: false);
        StyleButton(InfoButton, InfoScale, _zone == Zone.Actions && _action == 2, primary: false);
        RenderHints();
    }

    private void StyleButton(Border b, ScaleTransform scale, bool focused, bool primary)
    {
        b.Background = primary
            ? new SolidColorBrush(focused ? Accent : _theme.WithAlpha(Accent, 0xB0))
            : new SolidColorBrush(focused ? Soft(0x60) : Soft(0x30));
        b.BorderBrush = focused ? TextBrush : Brushes.Transparent;
        b.Effect = focused ? new DropShadowEffect { Color = primary ? Accent : Colors.White, BlurRadius = 40, ShadowDepth = 0, Opacity = 0.6 } : null;
        var to = focused ? 1.06 : 1.0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    // ---------- Aide (boutons de la manette) ----------

    private void RenderHints()
    {
        HintsPanel.Children.Clear();
        if (_themeOpen)
        {
            AddHint("↔", "Aperçu", dpad: true);
            AddHint("A", "Garder");
            AddHint("B", "Annuler");
            return;
        }
        if (_infoOpen)
        {
            AddHint("A", "Jouer");
            AddHint("B", "Fermer");
            return;
        }
        switch (_zone)
        {
            case Zone.Tabs:
                AddHint("↔", "Plateforme", dpad: true);
                AddHint("A", "Valider");
                AddHint("B", "Retour");
                break;
            case Zone.Shelf:
                AddHint("A", IsReturnSelected ? "Retour au PC" : SelectedRunning ? "Reprendre" : "Jouer");
                if (SelectedRunning) AddHint("X", _quitArmed ? "Confirmer" : "Quitter le jeu");
                if (!IsReturnSelected) AddHint("Y", "Infos");
                AddHint("B", "Retour au PC");
                if (_tabs.Count > 1) AddHint("LB RB", "Plateformes", shoulder: true);
                AddHint("⧉+LT RT", "Volume", shoulder: true);
                AddHint("☰", "Thèmes", dpad: true);
                break;
            case Zone.Actions:
                AddHint("A", _action switch { 1 => _quitArmed ? "Confirmer" : "Quitter le jeu", 2 => "Infos", _ => IsReturnSelected ? "Retour au PC" : SelectedRunning ? "Reprendre" : "Jouer" });
                AddHint("B", "Retour");
                break;
        }
    }

    private void AddHint(string button, string label, bool shoulder = false, bool dpad = false)
    {
        var color = button switch
        {
            "A" => Color.FromRgb(0x5C, 0xB8, 0x4A),
            "B" => Color.FromRgb(0xE0, 0x4E, 0x45),
            "X" => Color.FromRgb(0x3A, 0x8E, 0xE6),
            "Y" => Color.FromRgb(0xE8, 0xB9, 0x2E),
            _ => Color.FromRgb(0x5A, 0x63, 0x76),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(36, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center };
        if (shoulder)
        {
            foreach (var part in button.Split(' '))
                row.Children.Add(new Border
                {
                    Width = 56, Height = 34, CornerRadius = new CornerRadius(9, 9, 6, 6), Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(Soft(0x30)),
                    BorderBrush = new SolidColorBrush(Soft(0x90)), BorderThickness = new Thickness(2),
                    Child = new TextBlock { Text = part, Foreground = TextBrush, FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center },
                });
        }
        else
        {
            var g = new Grid { Width = 42, Height = 42 };
            g.Children.Add(new Ellipse
            {
                Fill = dpad ? new SolidColorBrush(Soft(0x30)) : new SolidColorBrush(color),
                Stroke = dpad ? new SolidColorBrush(Soft(0x90)) : null, StrokeThickness = 2,
            });
            g.Children.Add(new TextBlock
            {
                Text = button, Foreground = dpad ? TextBrush : Brushes.White, FontSize = 21, FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
            row.Children.Add(g);
        }
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = _theme.Brush(_theme.WithAlpha(_theme.Text, 0xD0)), FontSize = 24,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center,
        });
        HintsPanel.Children.Add(row);
    }

    // ---------- Manettes ----------

    /// <summary>En haut à droite : la manette utilisée (dernière qui a servi) avec sa batterie.</summary>
    private void RefreshControllers()
    {
        ControllersPanel.Children.Clear();
        var list = _pad.Controllers;
        var c = list.FirstOrDefault(x => x.Active) ?? list.FirstOrDefault();
        if (c == null) return;

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 36, Foreground = TextBrush, VerticalAlignment = System.Windows.VerticalAlignment.Center });
        if (list.Count > 1)
            row.Children.Add(new TextBlock { Text = $"×{list.Count}", Foreground = SubBrush, FontSize = 20, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Bottom });

        if (c.Wired)
        {
            row.Children.Add(new TextBlock { Text = "Filaire", Foreground = TextBrush, FontSize = 24, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
        }
        else if (c.BatteryPercent >= 0)
        {
            row.Children.Add(BatteryIcon(c.BatteryPercent, c.Charging));
            row.Children.Add(new TextBlock { Text = $"{c.BatteryPercent} %", Foreground = TextBrush, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
        }
        ControllersPanel.Children.Add(row);
    }

    /// <summary>Pile dessinée : remplissage proportionnel, rouge sous 20 %, éclair en charge.</summary>
    private FrameworkElement BatteryIcon(int percent, bool charging)
    {
        var low = percent <= 20;
        var fill = new SolidColorBrush(low ? Color.FromRgb(0xE5, 0x57, 0x4B) : _theme.Text);
        var body = new Border
        {
            Width = 50, Height = 26, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(2.5),
            BorderBrush = TextBrush, Padding = new Thickness(3),
            Child = new Border
            {
                CornerRadius = new CornerRadius(2), Background = fill, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Width = Math.Max(3, 38 * Math.Clamp(percent, 0, 100) / 100.0),
            },
        };
        var tip = new Border { Width = 4, Height = 11, CornerRadius = new CornerRadius(0, 2, 2, 0), Background = TextBrush, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center };
        if (charging) sp.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20, Foreground = TextBrush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center });
        sp.Children.Add(body);
        sp.Children.Add(tip);
        return sp;
    }

    private void OnPad(PadButtons b)
    {
        if (_playing || !IsVisible) return;
        if (!_introDone)
        {
            if (b is PadButtons.A or PadButtons.B or PadButtons.Menu) SkipIntro();
            return;
        }
        switch (b)
        {
            case PadButtons.Menu: Nav(Key.T); break;
            case PadButtons.Left: Nav(Key.Left); break;
            case PadButtons.Right: Nav(Key.Right); break;
            case PadButtons.Up: Nav(Key.Up); break;
            case PadButtons.Down: Nav(Key.Down); break;
            case PadButtons.A: Nav(Key.Enter); break;
            case PadButtons.B: Nav(Key.Escape); break;
            case PadButtons.Y: Nav(Key.I); break;
            case PadButtons.X: if (!_infoOpen && !_themeOpen && SelectedRunning) QuitGame(); break;
            case PadButtons.PrevTab: if (!_infoOpen && !_themeOpen) SetTab(_tab - 1); break;
            case PadButtons.NextTab: if (!_infoOpen && !_themeOpen) SetTab(_tab + 1); break;
        }
    }

    /// <summary>Appui long sur Guide pendant un jeu : on reprend la main sans fermer le jeu.</summary>
    private void OnGuideLong()
    {
        if (_playing) ReturnToMenu();
    }

    private void OnKey(object sender, WpfKeyEventArgs e)
    {
        if (_playing) return;
        var key = e.Key switch { Key.Space => Key.Enter, _ => e.Key };
        if (!_introDone)
        {
            if (key is Key.Enter or Key.Escape) { SkipIntro(); e.Handled = true; }
            return;
        }
        var free = !_infoOpen && !_themeOpen;
        switch (key)
        {
            case Key.Home: if (free) { SetZone(Zone.Shelf); Select(0); } break;
            case Key.End: if (free) { SetZone(Zone.Shelf); Select(_view.Count); } break;
            case Key.PageUp: case Key.Q: if (free) SetTab(_tab - 1); break;
            case Key.PageDown: case Key.E: if (free) SetTab(_tab + 1); break;
            case Key.X: case Key.Delete: if (free && SelectedRunning) QuitGame(); break;
            case Key.OemPlus: case Key.Add: VolumeRequested?.Invoke(1); break;
            case Key.OemMinus: case Key.Subtract: VolumeRequested?.Invoke(-1); break;
            case Key.Left: case Key.Right: case Key.Up: case Key.Down: case Key.Enter: case Key.Escape: case Key.I: case Key.T:
                Nav(key); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Navigation commune manette / clavier selon la zone qui a le focus.</summary>
    private void Nav(Key key)
    {
        if (_themeOpen)
        {
            switch (key)
            {
                case Key.Left: PreviewTheme(-1); break;
                case Key.Right: PreviewTheme(1); break;
                case Key.Enter: CloseThemes(true); break;
                case Key.Escape: case Key.T: CloseThemes(false); break;
            }
            return;
        }
        if (key == Key.T) { if (!_infoOpen) OpenThemes(); return; }

        if (_infoOpen)
        {
            switch (key)
            {
                case Key.Escape: case Key.I: CloseInfo(); break;
                case Key.Enter: CloseInfo(); Activate_(); break;
                case Key.Down: InfoScroll.ScrollToVerticalOffset(InfoScroll.VerticalOffset + 60); break;
                case Key.Up: InfoScroll.ScrollToVerticalOffset(InfoScroll.VerticalOffset - 60); break;
            }
            return;
        }

        switch (_zone)
        {
            case Zone.Tabs:
                switch (key)
                {
                    case Key.Left: SetTab(_tab - 1); break;
                    case Key.Right: SetTab(_tab + 1); break;
                    case Key.Down: case Key.Enter: case Key.Escape: SetZone(Zone.Shelf); break;
                }
                break;

            case Zone.Shelf:
                switch (key)
                {
                    case Key.Left: Move(-1); break;
                    case Key.Right: Move(1); break;
                    case Key.Up: SetZone(Zone.Tabs); break;
                    case Key.Down: SetZone(Zone.Actions); break;
                    case Key.Enter: Activate_(); break;
                    case Key.I: OpenInfo(); break;
                    case Key.Escape: Select(_view.Count); break;
                }
                break;

            case Zone.Actions:
                switch (key)
                {
                    case Key.Left: MoveAction(-1); break;
                    case Key.Right: MoveAction(1); break;
                    case Key.Up: case Key.Escape: SetZone(Zone.Shelf); break;
                    case Key.Enter: if (_action == 2) OpenInfo(); else if (_action == 1) QuitGame(); else Activate_(); break;
                    case Key.I: OpenInfo(); break;
                }
                break;
        }
    }

    private void PlayButton_Click(object sender, MouseButtonEventArgs e) => Activate_();

    private void InfoButton_Click(object sender, MouseButtonEventArgs e) => OpenInfo();

    private void QuitButton_Click(object sender, MouseButtonEventArgs e) => QuitGame();

    /// <summary>Le jeu sélectionné est-il celui qui tourne (boutons « Reprendre » / « Quitter ») ?</summary>
    private bool SelectedRunning => !IsReturnSelected && _view.Count > _sel && IsRunning(_view[_sel]);

    /// <summary>Boutons visibles sous le jeu : 0 Jouer/Reprendre, 1 Quitter (jeu en cours), 2 Infos.</summary>
    private int[] VisibleActions() => IsReturnSelected ? new[] { 0 } : SelectedRunning ? new[] { 0, 1, 2 } : new[] { 0, 2 };

    private void MoveAction(int delta)
    {
        var list = VisibleActions();
        var i = Math.Max(0, Array.IndexOf(list, _action));
        var before = _action;
        _action = list[Math.Clamp(i + delta, 0, list.Length - 1)];
        if (_action != before) Feedback(UiSound.Move);
        UpdateFocus();
    }

    // ---------- Quitter le jeu ----------

    private bool _quitArmed;
    private DispatcherTimer? _quitTimer;

    /// <summary>
    /// 1er appui : « Confirmer ? » pendant 3 s ; 2e appui : ferme le jeu en cours.
    /// Émulateur : fin de tâche immédiate (lui et tout ce qu'il a lancé). Jeu PC : fermeture normale
    /// (comme la croix, pour ne pas perdre la sauvegarde), puis fin de tâche s'il est encore là 10 s après.
    /// </summary>
    private void QuitGame()
    {
        var game = _runningGame;
        if (game == null) return;
        if (!_quitArmed)
        {
            _quitArmed = true;
            QuitText.Text = "Confirmer ?";
            Feedback(UiSound.Back);
            RenderHints();
            _quitTimer?.Stop();
            _quitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _quitTimer.Tick += (_, _) => { _quitTimer?.Stop(); _quitArmed = false; QuitText.Text = "Quitter"; RenderHints(); };
            _quitTimer.Start();
            return;
        }
        _quitTimer?.Stop();
        _quitArmed = false;
        var pid = _gamePid;
        var hwnd = _lastGameHwnd;
        var emulator = game.UsesEmulator ? _library.FindEmulator(game.EmulatorId) : null;
        EndRun();
        Log.Info($"Mode console : fermeture de « {game.Title} » demandée depuis le menu.");
        Feedback(UiSound.Select);
        _ = Task.Run(() => GameCloser.Close(pid, hwnd, emulator?.ExePath, force: game.UsesEmulator));
        ShowToast($"« {game.Title} » fermé");
        if (_action == 1) _action = 0;
        UpdateSelection(false);
        UpdateFocus();
    }

    // ---------- Fiche complète ----------

    private void OpenInfo()
    {
        if (IsReturnSelected || _playing) return;
        var g = _view[_sel];
        InfoCover.Source = LoadBitmap(g.CoverPath, 600);
        InfoTitle.Text = g.Title;
        var plat = PlatformLabel(g);
        InfoMeta.Text = $"{plat}      Temps de jeu : {ConsoleFormat.PlayTime(g.PlayTime)}      Dernière session : {ConsoleFormat.LastPlayed(g.LastPlayed, DateTime.Now)}";
        InfoDescription.Text = string.IsNullOrWhiteSpace(g.Description) ? "Aucune description." : g.Description;
        InfoScroll.ScrollToTop();
        _infoOpen = true;
        Feedback(UiSound.Open);
        InfoPanel.Visibility = Visibility.Visible;
        InfoPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        RenderHints();
    }

    private void CloseInfo()
    {
        if (_infoOpen) Feedback(UiSound.Back);
        _infoOpen = false;
        InfoPanel.Visibility = Visibility.Collapsed;
        RenderHints();
    }

    // ---------- Calque de chargement ----------

    private void ShowLoading(string title, string message, string? coverPath, string? heroPath)
    {
        LoadingTitle.Text = title;
        LoadingText.Text = message;
        var cover = LoadBitmap(coverPath, 500);
        LoadingCover.Source = cover;
        LoadingCoverHost.Visibility = cover != null ? Visibility.Visible : Visibility.Collapsed;
        LoadingHero.Source = LoadBitmap(heroPath, 1920);
        LoadingSpinnerHost.Visibility = Visibility.Visible;
        LoadingLayer.BeginAnimation(OpacityProperty, null);
        LoadingLayer.Opacity = 1;
        LoadingLayer.Visibility = Visibility.Visible;
    }

    private void HideLoading(int delayMs = 0)
    {
        var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450)) { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        a.Completed += (_, _) => { if (LoadingLayer.Opacity < 0.01) LoadingLayer.Visibility = Visibility.Collapsed; };
        LoadingLayer.BeginAnimation(OpacityProperty, a);
    }

    // ---------- Lancement ----------

    private void Activate_()
    {
        if (_playing) return;
        if (IsReturnSelected)
        {
            Feedback(UiSound.Select);
            ShowBusy("Retour au PC…");
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        Launch(_view[_sel]);
    }

    // Jeu lancé (même quand le menu est réaffiché par-dessus) ; dernière fenêtre du jeu vue au premier plan.
    private ConsoleGame? _runningGame;
    private IntPtr _lastGameHwnd;
    private int _gamePid;
    private Stopwatch? _shownAt;
    private bool _gameIsLauncher;
    private bool _gameShown;
    private int _launchGen;

    private bool IsRunning(ConsoleGame g) => _runningGame?.Id == g.Id;

    private async void Launch(ConsoleGame game)
    {
        if (_runningGame != null)
        {
            if (IsRunning(game)) { ResumeGame(); return; }
            _cts?.Cancel();     // on arrête de suivre l'ancien jeu (il n'est pas fermé)
        }
        var gen = ++_launchGen;
        _runningGame = game;
        _lastGameHwnd = IntPtr.Zero;
        _gamePid = 0;
        _gameIsLauncher = false;
        _gameShown = false;
        _playing = true;
        var cts = _cts = new CancellationTokenSource();
        Feedback(UiSound.Launch);
        ShowLoading(game.Title, "Lancement…", game.CoverPath, game.HeroPath);
        _backdrop?.SetGame(game.Title, game.HeroPath, "En jeu – maintiens le bouton central de la manette pour revenir au menu");
        GameWindowTracker.AllowAnyForeground();
        Log.Info($"Mode console : lancement de « {game.Title} ».");
        WatchGame(gen);

        var start = DateTime.Now;
        var sw = Stopwatch.StartNew();
        LaunchOutcome outcome;
        try
        {
            outcome = await GameLauncher.RunAsync(game, _library, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("Lancement du jeu", ex);
            outcome = new LaunchOutcome(false, "Lancement impossible");
        }
        if (outcome.Started && sw.Elapsed > TimeSpan.FromSeconds(5)) _library.MarkPlayed(game, start, sw.Elapsed);
        if (gen != _launchGen || _closed) return;   // remplacé par un autre lancement, ou mode console quitté
        Log.Info($"Mode console : le processus lancé pour « {game.Title} » est terminé.");
        // Comme Playnite / Big Picture : la fin du processus lancé ne ramène PAS le menu (raccourcis, Xbox, Steam
        // passent par des lanceurs qui se ferment tout de suite). Le menu revient quand le bureau reprend la main.
        if (outcome.Error != null)
        {
            _gameWatch?.Stop();
            _runningGame = null;
            if (_playing) ReturnToMenu();
            ShowToast(outcome.Error);
            return;
        }
        // Un émulateur ne passe pas par un lanceur : s'il se ferme avant d'avoir affiché le jeu, il a planté
        // (ou refusé le jeu) : retour au menu au lieu de laisser tourner la roue.
        if (game.UsesEmulator && !_gameShown && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            var name = _library.FindEmulator(game.EmulatorId)?.Name ?? "L'émulateur";
            Log.Warn($"Mode console : {name} s'est fermé après {sw.Elapsed.TotalSeconds:0.0} s sans afficher le jeu.");
            EndRun();
            if (_playing) ReturnToMenu();
            ShowToast($"{name} s'est fermé tout de suite (plantage ?)");
        }
    }

    /// <summary>
    /// Suivi par le PREMIER PLAN (comme Playnite / Big Picture), sans deviner la fenêtre du jeu :
    /// - Pendant le chargement, la roue reste devant tout. Elle s'efface quand une application qui n'est ni un
    ///   lanceur (écran Xbox, Steam...) ni le bureau garde le premier plan 1 s : c'est le jeu. Un lanceur seul
    ///   pendant 20 s est laissé visible (il attend peut-être une action).
    /// - En jeu : dès que la fenêtre du jeu se ferme ou se réduit (1 s de grâce pour un jeu qui change de
    ///   fenêtre), ou que le bureau reprend la main, retour au menu.
    /// </summary>
    private void WatchGame(int gen)
    {
        var since = Stopwatch.StartNew();
        var self = Environment.ProcessId;
        IntPtr candidate = IntPtr.Zero;
        Stopwatch? candidateFor = null, launcherFor = null, goneFor = null;
        _gameWatch?.Stop();
        _gameWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _gameWatch.Tick += (_, _) =>
        {
            if (_closed || gen != _launchGen || _runningGame == null) { _gameWatch?.Stop(); return; }
            var fg = GetForegroundWindow();
            var pid = fg == IntPtr.Zero ? 0 : GameWindowTracker.ProcessOf(fg);
            var ours = pid == self;
            var shell = fg == IntPtr.Zero || GameWindowTracker.IsShell(pid);
            var launcher = !ours && !shell && GameWindowTracker.IsLauncher(pid);
            var foreign = !ours && !shell && !launcher;

            if (_playing && !_gameShown)
            {
                // Chargement : on attend le vrai jeu au premier plan.
                if (foreign)
                {
                    if (fg != candidate) { candidate = fg; candidateFor = Stopwatch.StartNew(); }
                    else if (candidateFor!.Elapsed >= TimeSpan.FromSeconds(1))
                    {
                        // Émulateurs : la fenêtre au premier plan peut être la « liste des jeux » ; s'il existe une
                        // fenêtre du même programme qui couvre tout l'écran (le jeu), c'est elle qu'on met devant.
                        var full = GameWindowTracker.FullscreenWindowOf(pid);
                        var target = full != IntPtr.Zero ? full : fg;
                        _lastGameHwnd = target;
                        _gamePid = pid;
                        _gameIsLauncher = false;
                        _shownAt = Stopwatch.StartNew();
                        Log.Info($"Mode console : « {GameWindowTracker.Title(target)} » au premier plan après {since.Elapsed.TotalSeconds:0.0} s, le jeu s'affiche.");
                        EnterGame();
                        GameWindowTracker.ForceForeground(target);
                    }
                    launcherFor = null;
                }
                else
                {
                    candidate = IntPtr.Zero;
                    if (launcher) launcherFor ??= Stopwatch.StartNew(); else launcherFor = null;
                    if (launcherFor?.Elapsed > TimeSpan.FromSeconds(20) || since.Elapsed > GameWindowTimeout)
                    {
                        Log.Warn($"Mode console : pas de jeu au premier plan (devant : « {GameWindowTracker.Title(fg)} »), la console s'efface.");
                        if (launcher) { _lastGameHwnd = fg; _gamePid = pid; _gameIsLauncher = true; }
                        EnterGame();
                    }
                }
                return;
            }

            if (!_playing || !_gameShown) return;

            // Premières secondes : si le programme du jeu remet une autre de ses fenêtres devant (liste des jeux de
            // l'émulateur...), on repasse sa fenêtre plein écran au premier plan.
            if (_shownAt?.Elapsed < TimeSpan.FromSeconds(8) && pid == _gamePid)
            {
                var full = GameWindowTracker.FullscreenWindowOf(_gamePid);
                if (full != IntPtr.Zero && fg != full)
                {
                    _lastGameHwnd = full;
                    GameWindowTracker.ForceForeground(full);
                    return;
                }
            }

            // En jeu (comme Playnite) : on suit le PROCESSUS du jeu, pas « la fenêtre au premier plan ».
            if (_gamePid == 0 || _gameIsLauncher)
            {
                // Le lanceur (écran Xbox...) a laissé la place au vrai jeu : on suit désormais le jeu.
                if (foreign) { _lastGameHwnd = fg; _gamePid = pid; _gameIsLauncher = false; }
            }
            else if (foreign && pid == _gamePid && fg != _lastGameHwnd) _lastGameHwnd = fg;   // le jeu a changé de fenêtre

            if (_gamePid != 0 && !_gameIsLauncher && GameWindowTracker.ProcessExited(_gamePid))
            {
                Log.Info("Mode console : processus du jeu terminé, retour au menu.");
                EndRun();
                ReturnToMenu();
                return;
            }
            var windowGone = !GameWindowTracker.IsAlive(_lastGameHwnd) && !GameWindowTracker.HasVisibleWindow(_gamePid);
            var gone = windowGone || GameWindowTracker.IsMinimized(_lastGameHwnd) || shell;
            if (!gone) { goneFor = null; return; }
            goneFor ??= Stopwatch.StartNew();
            if (goneFor.Elapsed < TimeSpan.FromSeconds(0.5)) return;
            Log.Info(windowGone ? "Mode console : fenêtre du jeu fermée, retour au menu." : "Mode console : jeu réduit ou bureau au premier plan, retour au menu.");
            if (windowGone) EndRun();
            ReturnToMenu();
        };
        _gameWatch.Start();
    }

    /// <summary>Le jeu est fermé : on arrête de le suivre (le bouton redevient « Jouer »).</summary>
    private void EndRun()
    {
        _gameWatch?.Stop();
        _cts?.Cancel();
        _launchGen++;
        _runningGame = null;
    }

    /// <summary>L'interface se retire : seul le fond de console (accroché au bureau) reste derrière le jeu.</summary>
    private void EnterGame()
    {
        _gameShown = true;
        _playing = true;
        Topmost = false;
        Hide();
    }

    /// <summary>Menu au premier plan ; le jeu (s'il tourne encore) peut être repris avec « Reprendre ».</summary>
    private void ReturnToMenu()
    {
        if (_closed) return;
        _playing = false;
        _gameShown = false;
        LoadingText.Text = "Retour au menu…";
        ShowOn(_bounds);
        HideLoading(200);
        UpdateSelection(false);
        UpdateFocus();
    }

    /// <summary>Remet le jeu en cours au premier plan (sans le relancer). Fenêtre disparue : le jeu est relancé.</summary>
    private void ResumeGame()
    {
        var game = _runningGame;
        if (game == null) return;
        if (!GameWindowTracker.IsAlive(_lastGameHwnd))
        {
            Log.Info($"Mode console : « {game.Title} » n'a plus de fenêtre, nouveau lancement.");
            _cts?.Cancel();
            _runningGame = null;
            Launch(game);
            return;
        }
        Feedback(UiSound.Launch);
        GameWindowTracker.AllowAnyForeground();
        EnterGame();
        GameWindowTracker.ForceForeground(_lastGameHwnd);
    }

    /// <summary>Affiche la jauge de volume 2 s (niveau sur <paramref name="max"/>).</summary>
    public void ShowVolume(int level, int max, bool muted)
    {
        if (_closed || level < 0) return;
        max = Math.Max(1, max);
        var ratio = Math.Clamp(level / (double)max, 0, 1);
        VolumeFill.Width = 380 * (muted ? 0 : ratio);
        VolumeText.Text = muted ? "Muet" : max == 100 ? $"{level}" : $"{level}";
        VolumeGlyph.Text = muted || level == 0 ? "\uE74F" : ratio < 0.34 ? "\uE993" : ratio < 0.67 ? "\uE994" : "\uE767";
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
        VolumeOsd.BeginAnimation(OpacityProperty, a);
    }

    // ---------- Bruitages et vibrations ----------

    private UiSounds? _sounds;
    private long _lastFeedback;

    /// <summary>Bruitage + vibration de la manette pour une action du menu (réglages Bruitages / Vibrations).</summary>
    private void Feedback(UiSound kind)
    {
        var now = Environment.TickCount64;
        // Un déplacement déclenché par une autre action (retour, validation...) ne rejoue pas de « tic » par-dessus.
        if (kind == UiSound.Move && now - _lastFeedback < 80) return;
        if (kind != UiSound.Move) _lastFeedback = now;
        _sounds?.Play(kind);
        if (!_options.Vibration) return;
        var (low, high, ms) = kind switch
        {
            UiSound.Move => (0.0, 0.22, 30),
            UiSound.Tab => (0.0, 0.32, 45),
            UiSound.Select => (0.30, 0.45, 70),
            UiSound.Back => (0.18, 0.25, 45),
            UiSound.Open => (0.22, 0.35, 60),
            UiSound.Launch => (0.65, 0.85, 220),
            _ => (0.50, 0.0, 160),   // erreur
        };
        _pad.Rumble(low, high, ms);
    }

    private void ShowToast(string message)
    {
        Feedback(UiSound.Error);
        ToastText.Text = message;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3700))));
        Toast.BeginAnimation(OpacityProperty, a);
    }

    // ---------- Win32 ----------

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
