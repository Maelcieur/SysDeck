using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SysDeck.Controls;
using SysDeck.Services;
using MediaTimeline = SysDeck.Services.MediaTimeline;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Widgets;

/// <summary>
/// Lecture en cours, en quatre dispositions : « Compact » (petit disque, titre, boutons), « Banner » (grande pochette
/// carrée et grand titre), « Vinyl » (vinyle qui tourne avec la pochette en étiquette) et « Sleeve » (pochette avec le
/// disque qui en sort). Barre de temps au choix, boutons avec ou sans fond, aléatoire et répétition.
/// Les mêmes éléments (textes, boutons) sont replacés d'une disposition à l'autre : leurs réglages restent.
/// </summary>
public sealed class MediaWidgetContent : UserControl, ITickingWidget
{
    /// <summary>
    /// Jeu d'icônes des boutons. NavFilled : précédent/suivant en formes pleines (sinon traits) ;
    /// PlayFilled : lecture/pause/stop en formes pleines (sinon contours).
    /// </summary>
    private sealed record IconSet(string Prev, string Next, string Play, string Pause, string Stop,
        bool NavFilled, bool PlayFilled, bool Neon = false);

    private const string BarsPause = "M2,1 L5.5,1 L5.5,13 L2,13 Z M8.5,1 L12,1 L12,13 L8.5,13 Z";
    private const string LinesPause = "M4,1 L4,13 M10,1 L10,13";
    private const string Square = "M1,1 L12,1 L12,12 L1,12 Z";
    private const string ShuffleIcon = "M1,3 L4,3 L10,11 L13,11 M11,9 L13,11 L11,13 M1,11 L4,11 L6,8.3 M8,5.7 L10,3 L13,3 M11,1 L13,3 L11,5";
    private const string RepeatIcon = "M2,8 L2,5.5 Q2,3 4.5,3 L12,3 M10,1 L12,3 L10,5 M12,6 L12,8.5 Q12,11 9.5,11 L2,11 M4,9 L2,11 L4,13";

    private static readonly Dictionary<string, IconSet> IconSets = new()
    {
        ["Classic"] = new("M9,2 L2,7 L9,12 M2,2 L2,12", "M2,2 L9,7 L2,12 M9,2 L9,12",
            "M2,1 L11,7 L2,13 Z", "M2,1 L5,1 L5,13 L2,13 Z M8,1 L11,1 L11,13 L8,13 Z", Square, NavFilled: false, PlayFilled: true),
        ["Solid"] = new("M1,1 L3.5,1 L3.5,13 L1,13 Z M13,1 L13,13 L4.5,7 Z", "M13,1 L10.5,1 L10.5,13 L13,13 Z M1,1 L1,13 L9.5,7 Z",
            "M2,1 L12,7 L2,13 Z", BarsPause, Square, NavFilled: true, PlayFilled: true),
        ["Double"] = new("M7,1 L7,13 L0,7 Z M14,1 L14,13 L7,7 Z", "M0,1 L7,7 L0,13 Z M7,1 L14,7 L7,13 Z",
            "M2,1 L12,7 L2,13 Z", BarsPause, Square, NavFilled: true, PlayFilled: true),
        ["Chevron"] = new("M9,1 L3,7 L9,13", "M4,1 L10,7 L4,13",
            "M3,1 L12,7 L3,13 Z", LinesPause, "M2,2 L12,2 L12,12 L2,12 Z", NavFilled: false, PlayFilled: false),
        ["Neon"] = new("M12,2 L5,7 L12,12 Z M2,2 L2,12", "M2,2 L9,7 L2,12 Z M12,2 L12,12",
            "M3,1 L12,7 L3,13 Z", LinesPause, "M2,2 L12,2 L12,12 L2,12 Z", NavFilled: false, PlayFilled: false, Neon: true),
    };

    private const string PlayTemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
            <Grid Background="Transparent">
                <Ellipse x:Name="Bg" Fill="{DynamicResource GaugeGradientBrush}"/>
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Grid>
            <ControlTemplate.Triggers>
                <Trigger Property="Tag" Value="Outline">
                    <Setter TargetName="Bg" Property="Fill" Value="Transparent"/>
                    <Setter TargetName="Bg" Property="Stroke" Value="{DynamicResource GaugeGradientBrush}"/>
                    <Setter TargetName="Bg" Property="StrokeThickness" Value="2"/>
                </Trigger>
                <Trigger Property="Tag" Value="None">
                    <Setter TargetName="Bg" Property="Fill" Value="Transparent"/>
                </Trigger>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bg" Property="Opacity" Value="0.8"/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """;

    private static ControlTemplate? _playTemplate;

    // Éléments réutilisés d'une disposition à l'autre.
    private readonly TextBlock _title, _artist, _album;
    private readonly Button _prev, _play, _next, _stop, _shuffle, _repeat;
    private readonly Path _prevIcon = NewIcon(), _playIcon = NewIcon(), _nextIcon = NewIcon(), _stopIcon = NewIcon(),
        _shuffleIcon = NewIcon(), _repeatIcon = NewIcon();
    private readonly Ellipse _repeatOneDot = new() { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly MediaProgressBar _progress = new();
    private readonly List<ImageBrush> _covers = new();

    private IconSet _icons = IconSets["Classic"];
    private string _layout = "", _progressStyle = "Line";
    private bool _buttonBackground = true, _isPlaying, _isSpotify;
    private MediaTimeline _timeline = MediaTimeline.Empty;
    private ImageSource? _thumbnail;
    private string _source = "", _albumTitle = "";

    // Disque qui tourne pendant la lecture (null : pochette fixe).
    private RotateTransform? _spin;
    private double _spinSeconds = 6;
    private bool _spinning;

    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? StopSpotifyRequested;
    public event Action? ShuffleRequested;
    public event Action? RepeatRequested;
    public event Action<TimeSpan>? SeekRequested;

    public MediaWidgetContent()
    {
        _title = WidgetUi.Text("track", 13, weight: FontWeights.SemiBold);
        _title.Text = "Aucune lecture en cours";
        _artist = WidgetUi.Text("artist", 11.5, sub: true);
        _artist.Margin = new Thickness(0, 2, 0, 0);
        _album = WidgetUi.Text("album", 11, sub: true);
        _album.Margin = new Thickness(0, 3, 0, 0);

        _prev = IconButton(_prevIcon, "previous", () => PreviousRequested?.Invoke());
        _next = IconButton(_nextIcon, "next", () => NextRequested?.Invoke());
        _stop = IconButton(_stopIcon, "stopSpotify", () => StopSpotifyRequested?.Invoke());
        _stop.ToolTip = "Fermer Spotify";
        ButtonPart.SetWanted(_stop, false);
        _shuffle = IconButton(_shuffleIcon, "shuffle", () => ShuffleRequested?.Invoke());
        _shuffle.ToolTip = "Aléatoire";
        _repeat = IconButton(new Grid { Children = { _repeatIcon, _repeatOneDot } }, "repeat", () => RepeatRequested?.Invoke());
        _repeat.ToolTip = "Répétition";
        _repeatOneDot.SetResourceReference(Shape.FillProperty, "GaugeGradientBrush");
        ButtonPart.SetWanted(_shuffle, false);
        ButtonPart.SetWanted(_repeat, false);

        _play = new Button { Cursor = System.Windows.Input.Cursors.Hand, Content = _playIcon, Template = PlayTemplate() };
        _play.Click += (_, _) => PlayPauseRequested?.Invoke();
        ButtonPart.SetKey(_play, "play");

        _progress.SeekRequested += ratio =>
        {
            if (_timeline.CanSeek && _timeline.Duration > TimeSpan.Zero) SeekRequested?.Invoke(_timeline.Duration * ratio);
        };

        AddHandler(ButtonPart.IconBrushChangedEvent, new RoutedEventHandler((_, _) => ApplyIcons()));
        Loaded += (_, _) => UpdateSpin();
        Unloaded += (_, _) => StopSpin();
        SetResourceReference(AnimationScaleProperty, "AnimationScale");

        ApplySettings("Classic", "Compact", "Line", true);
    }

    /// <summary>Échelle d'animation du thème effectif (ressource AnimationScale) : 0 = animations désactivées.</summary>
    public static readonly DependencyProperty AnimationScaleProperty = DependencyProperty.Register(
        nameof(AnimationScale), typeof(double), typeof(MediaWidgetContent),
        new PropertyMetadata(1.0, (d, _) => ((MediaWidgetContent)d).UpdateSpin()));

    public double AnimationScale { get => (double)GetValue(AnimationScaleProperty); set => SetValue(AnimationScaleProperty, value); }

    // ---------- Réglages ----------

    /// <summary>Jeu d'icônes, disposition, barre de temps et fond des boutons (apparence du module, bureau ou Switch).</summary>
    public void ApplySettings(string? iconSet, string? layout, string? progress, bool buttonBackground)
    {
        _icons = IconSets.GetValueOrDefault(iconSet ?? "", IconSets["Classic"]);
        _buttonBackground = buttonBackground;
        var newLayout = layout is "Banner" or "Vinyl" or "Sleeve" ? layout : "Compact";
        var newProgress = progress is "None" or "Thick" or "Segments" ? progress : "Line";
        if (newLayout != _layout || newProgress != _progressStyle)
        {
            _layout = newLayout;
            _progressStyle = newProgress;
            _progress.SetStyle(newProgress);
            BuildLayout();
        }
        ApplyButtonBackgrounds();
        ApplyIcons();
        ApplyExtraButtons();
    }

    /// <summary>Ancien nom (jeu d'icônes seul).</summary>
    public void SetIconSet(string? name) => ApplySettings(name, _layout, _progressStyle, _buttonBackground);

    // ---------- Données ----------

    public void Update(NowPlayingInfo info)
    {
        _title.Text = info.HasSession ? info.Title : "Aucune lecture en cours";
        _artist.Text = info.HasSession ? info.Subtitle : "Appuyer sur lecture pour ouvrir Spotify";
        _albumTitle = info.Album;
        _source = info.Source;
        UpdateAlbumLine();

        _thumbnail = info.Thumbnail;
        foreach (var brush in _covers) brush.ImageSource = _thumbnail;
        var playingChanged = _isPlaying != info.IsPlaying;
        _isPlaying = info.IsPlaying;
        _isSpotify = info.IsSpotify;
        if (playingChanged)
        {
            ApplyIcons();
            UpdateSpin();
        }
        // Wanted plutôt que Visibility : le bouton peut aussi être masqué par l'utilisateur (onglet Boutons).
        ButtonPart.SetWanted(_stop, info.IsSpotify);
    }

    public void SetTimeline(MediaTimeline timeline)
    {
        _timeline = timeline;
        ApplyExtraButtons();
        Tick(DateTime.Now);
    }

    /// <summary>Chaque seconde : la position avance entre deux mesures de la source.</summary>
    public void Tick(DateTime now)
    {
        if (_progressStyle == "None") return;
        _progress.SetProgress(_timeline.PositionAt(now), _timeline.Duration);
    }

    private void UpdateAlbumLine()
    {
        var parts = new[] { _albumTitle, _source }.Where(p => !string.IsNullOrWhiteSpace(p));
        _album.Text = string.Join(" · ", parts);
        _album.Visibility = _album.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Dispositions ----------

    private void BuildLayout()
    {
        StopSpin();
        _spin = null;
        _covers.Clear();
        foreach (var element in new FrameworkElement[] { _title, _artist, _album, _prev, _play, _next, _stop, _shuffle, _repeat, _progress })
            Detach(element);

        var showProgress = _progressStyle != "None";
        _progress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        Movable.SetKey(_progress, "progress");

        switch (_layout)
        {
            case "Banner": BuildBanner(); break;
            case "Vinyl": BuildVinyl(); break;
            case "Sleeve": BuildSleeve(); break;
            default: BuildCompact(); break;
        }
        foreach (var brush in _covers) brush.ImageSource = _thumbnail;
        UpdateAlbumLine();
        Tick(DateTime.Now);
        UpdateSpin();
        // Les éléments déplaçables ont changé de place : la carte les recherche à nouveau.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => RaiseEvent(new RoutedEventArgs(Movable.ElementsChangedEvent)));
    }

    private void BuildCompact()
    {
        Width = 230;
        SetTextSizes(13, 11.5, 11);
        _album.Visibility = Visibility.Collapsed;
        var disc = RoundDisc(56);
        Movable.SetKey(disc, "disc");
        disc.Margin = new Thickness(0, 0, 12, 0);
        var track = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 150, Children = { _title, _artist } };
        Movable.SetKey(track, "track");
        _progress.Margin = new Thickness(0, 12, 0, 0);
        var controls = Controls(14, 20, 34);
        controls.Margin = new Thickness(0, 12, 0, 0);
        Content = new StackPanel
        {
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { disc, track } }, _progress, controls }
        };
        _spinSeconds = 6;
    }

    private void BuildBanner()
    {
        Width = 540;
        SetTextSizes(28, 15, 12);
        var cover = SquareCover(140, 12);
        Movable.SetKey(cover, "disc");
        var track = new StackPanel { Children = { _title, _artist, _album } };
        Movable.SetKey(track, "track");
        _title.FontWeight = FontWeights.Bold;
        _progress.Margin = new Thickness(0, 14, 0, 0);
        var controls = Controls(18, 28, 42);
        controls.Margin = new Thickness(0, 10, 0, 0);
        var right = new StackPanel { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { track, _progress, controls } };
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        Grid.SetColumn(right, 1);
        grid.Children.Add(cover);
        grid.Children.Add(right);
        Content = grid;
    }

    private void BuildVinyl()
    {
        Width = 520;
        SetTextSizes(30, 17, 12);
        var vinyl = Vinyl(176);
        Movable.SetKey(vinyl, "disc");
        var track = new StackPanel { Children = { _title, _artist, _album } };
        Movable.SetKey(track, "track");
        _title.FontWeight = FontWeights.Bold;
        _artist.FontWeight = FontWeights.SemiBold;
        _progress.Margin = new Thickness(0, 14, 0, 0);
        var controls = Controls(18, 28, 42);
        controls.Margin = new Thickness(0, 10, 0, 0);
        var right = new StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { track, _progress, controls } };
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        Grid.SetColumn(right, 1);
        grid.Children.Add(vinyl);
        grid.Children.Add(right);
        Content = grid;
        _spinSeconds = 8;
    }

    private void BuildSleeve()
    {
        Width = 280;
        SetTextSizes(19, 13, 11);
        // Le disque sort de la pochette vers la droite.
        var vinyl = Vinyl(140);
        vinyl.Margin = new Thickness(92, 0, 0, 0);
        vinyl.VerticalAlignment = VerticalAlignment.Center;
        var cover = SquareCover(150, 4);
        cover.HorizontalAlignment = HorizontalAlignment.Left;
        cover.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 300, Opacity = 0.55 };
        var art = new Grid { Height = 150, Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Children = { vinyl, cover } };
        Movable.SetKey(art, "disc");
        var track = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { _title, _artist } };
        Movable.SetKey(track, "track");
        _title.FontWeight = FontWeights.Bold;
        _progress.Margin = new Thickness(0, 10, 0, 0);
        var controls = Controls(14, 24, 38);
        controls.Margin = new Thickness(0, 8, 0, 0);
        Content = new StackPanel { Children = { art, track, _progress, controls } };
        _spinSeconds = 8;
    }

    private void SetTextSizes(double title, double artist, double album)
    {
        _title.FontSize = title;
        _title.FontWeight = FontWeights.SemiBold;
        _artist.FontSize = artist;
        _artist.FontWeight = FontWeights.Normal;
        _album.FontSize = album;
    }

    /// <summary>Rangée de boutons : [aléatoire] précédent, lecture, suivant [répétition] [fermer Spotify].</summary>
    private StackPanel Controls(double gap, double size, double playSize)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Movable.SetKey(row, "controls");
        foreach (var b in new[] { _shuffle, _prev, _play, _next, _repeat, _stop })
        {
            var isPlay = b == _play;
            b.Width = b.Height = isPlay ? playSize : size;
            b.Margin = new Thickness(row.Children.Count == 0 ? 0 : gap, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(b);
        }
        var iconSize = Math.Round(size * 0.55);
        foreach (var icon in new[] { _prevIcon, _nextIcon, _shuffleIcon, _repeatIcon }) icon.Width = icon.Height = iconSize;
        _stopIcon.Width = _stopIcon.Height = Math.Round(size * 0.5);
        _playIcon.Width = _playIcon.Height = Math.Round(playSize * 0.38);
        return row;
    }

    // ---------- Pochettes ----------

    private ImageBrush CoverBrush()
    {
        var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
        _covers.Add(brush);
        return brush;
    }

    /// <summary>Petit disque : pochette découpée en rond, anneau au dégradé, trou central (disposition Compact).</summary>
    private FrameworkElement RoundDisc(double size)
    {
        var spin = new Grid { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 0.5) };
        spin.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x27)) });
        spin.Children.Add(new Ellipse { Fill = CoverBrush() });
        var ring = new Ellipse { StrokeThickness = 2 };
        ring.SetResourceReference(Shape.StrokeProperty, "GaugeGradientBrush");
        spin.Children.Add(ring);
        var hole = new Ellipse { Width = size / 4, Height = size / 4, StrokeThickness = 1 };
        hole.SetResourceReference(Shape.FillProperty, "BgBrush");
        hole.SetResourceReference(Shape.StrokeProperty, "AccentBrush2");
        spin.Children.Add(hole);
        var dot = new Ellipse { Width = 4, Height = 4 };
        dot.SetResourceReference(Shape.FillProperty, "AccentBrush2");
        spin.Children.Add(dot);
        spin.RenderTransform = _spin = new RotateTransform();
        return new Grid { Children = { spin } };
    }

    /// <summary>Pochette carrée aux coins arrondis.</summary>
    private Border SquareCover(double size, double radius) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius),
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x27)),
        Child = new Border { CornerRadius = new CornerRadius(radius), Background = CoverBrush() }
    };

    /// <summary>Vinyle noir à sillons, pochette en étiquette au centre ; un reflet fixe passe dessus pendant qu'il tourne.</summary>
    private FrameworkElement Vinyl(double size)
    {
        var disc = new Grid { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 0.5) };
        disc.Children.Add(new Ellipse
        {
            Fill = new RadialGradientBrush(Color.FromRgb(0x1C, 0x1C, 0x1E), Color.FromRgb(0x08, 0x08, 0x09)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1
        });
        var groove = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        foreach (var f in new[] { 0.92, 0.84, 0.76, 0.68, 0.6, 0.52 })
            disc.Children.Add(new Ellipse { Width = size * f, Height = size * f, Stroke = groove, StrokeThickness = 1 });
        disc.Children.Add(new Ellipse { Width = size * 0.4, Height = size * 0.4, Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDA)) });
        disc.Children.Add(new Ellipse { Width = size * 0.4, Height = size * 0.4, Fill = CoverBrush() });
        disc.Children.Add(new Ellipse { Width = size * 0.05, Height = size * 0.05, Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)) });
        disc.RenderTransform = _spin = new RotateTransform();

        var sheen = new Ellipse
        {
            Width = size, Height = size, IsHitTestVisible = false,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.3),
                new(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0.5),
                new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.7)
            }, new Point(0, 0), new Point(1, 1))
        };
        return new Grid { Width = size, Height = size, Children = { disc, sheen } };
    }

    // ---------- Rotation ----------

    private void UpdateSpin()
    {
        if (_spin is null) return;
        var on = _isPlaying && AnimationScale > 0 && IsLoaded;
        if (on && !_spinning)
        {
            var from = _spin.Angle % 360;
            _spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(_spinSeconds)) { RepeatBehavior = RepeatBehavior.Forever });
            _spinning = true;
        }
        else if (!on && _spinning)
        {
            // Pause : le disque garde son angle et repart de là.
            var angle = _spin.Angle;
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            _spin.Angle = angle;
            _spinning = false;
        }
    }

    private void StopSpin()
    {
        if (_spin is null || !_spinning) return;
        var angle = _spin.Angle;
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _spin.Angle = angle;
        _spinning = false;
    }

    // ---------- Boutons ----------

    private static Path NewIcon() => new()
    {
        StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, Width = 12, Height = 12
    };

    private static Button IconButton(UIElement content, string key, Action click)
    {
        var button = new Button { Content = content, Padding = new Thickness(0) };
        button.SetResourceReference(StyleProperty, "IconButtonStyle");
        button.Click += (_, _) => click();
        ButtonPart.SetKey(button, key);
        return button;
    }

    private static ControlTemplate PlayTemplate() => _playTemplate ??= (ControlTemplate)XamlReader.Parse(PlayTemplateXaml);

    private void ApplyButtonBackgrounds()
    {
        foreach (var b in new[] { _prev, _next, _stop, _shuffle, _repeat })
        {
            if (_buttonBackground)
            {
                b.ClearValue(BackgroundProperty);
                b.ClearValue(BorderThicknessProperty);
            }
            else
            {
                b.Background = Brushes.Transparent;
                b.BorderThickness = new Thickness(0);
            }
        }
    }

    /// <summary>Aléatoire et répétition : affichés hors disposition Compact, si l'application les permet ; allumés quand actifs.</summary>
    private void ApplyExtraButtons()
    {
        var big = _layout != "Compact";
        ButtonPart.SetWanted(_shuffle, big && _timeline.CanShuffle);
        ButtonPart.SetWanted(_repeat, big && _timeline.CanRepeat);
        StyleIcon(_shuffleIcon, ShuffleIcon, _timeline.Shuffle ? "GaugeGradientBrush" : "SubTextBrush", filled: false);
        StyleIcon(_repeatIcon, RepeatIcon, _timeline.Repeat > 0 ? "GaugeGradientBrush" : "SubTextBrush", filled: false);
        _repeatOneDot.Visibility = _timeline.Repeat == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyIcons()
    {
        var accentKey = _icons.Neon ? "GaugeGradientBrush" : "TextBrush";
        StyleIcon(_prevIcon, _icons.Prev, accentKey, _icons.NavFilled);
        StyleIcon(_nextIcon, _icons.Next, accentKey, _icons.NavFilled);
        StyleIcon(_stopIcon, _icons.Stop, accentKey, _icons.PlayFilled);

        // Disque plein au dégradé (icône blanche), anneau (Néon) ou sans fond (icône de la couleur du texte).
        _play.Tag = _icons.Neon ? "Outline" : _buttonBackground ? null : "None";
        var playData = _isPlaying ? _icons.Pause : _icons.Play;
        var ink = ButtonPart.GetIconBrush(_play);
        if (_icons.Neon || !_buttonBackground)
        {
            StyleIcon(_playIcon, playData, accentKey, _icons.PlayFilled);
            if (ink is not null)
            {
                if (_icons.PlayFilled) _playIcon.Fill = ink; else _playIcon.Stroke = ink;
            }
        }
        else
        {
            _playIcon.Data = IconGeometry(playData);
            _playIcon.Fill = _icons.PlayFilled ? ink ?? Brushes.White : null;
            _playIcon.Stroke = _icons.PlayFilled ? null : ink ?? Brushes.White;
        }
        ButtonPart.SetKey(_play, _isPlaying ? "pause" : "play");
    }

    private static readonly Dictionary<string, Geometry> GeometryCache = new();

    private static Geometry IconGeometry(string data)
    {
        if (!GeometryCache.TryGetValue(data, out var geometry))
        {
            geometry = Geometry.Parse(data);
            geometry.Freeze();
            GeometryCache[data] = geometry;
        }
        return geometry;
    }

    private void StyleIcon(Path path, string data, string brushKey, bool filled)
    {
        path.Data = IconGeometry(data);
        path.StrokeThickness = _icons.Neon ? 1.9 : 1.6;
        if (filled)
        {
            path.SetResourceReference(Shape.FillProperty, brushKey);
            path.Stroke = null;
        }
        else
        {
            path.SetResourceReference(Shape.StrokeProperty, brushKey);
            path.Fill = null;
        }
    }

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case ContentControl control: control.Content = null; break;
            case Decorator decorator: decorator.Child = null; break;
        }
    }
}
