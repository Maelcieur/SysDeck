using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck.Widgets;

public partial class MediaWidgetContent : UserControl
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

    private static readonly Dictionary<string, IconSet> IconSets = new()
    {
        // Jeu d'origine : précédent/suivant en traits, lecture/pause/stop pleins
        ["Classic"] = new("M9,2 L2,7 L9,12 M2,2 L2,12", "M2,2 L9,7 L2,12 M9,2 L9,12",
            "M2,1 L11,7 L2,13 Z", "M2,1 L5,1 L5,13 L2,13 Z M8,1 L11,1 L11,13 L8,13 Z", Square, NavFilled: false, PlayFilled: true),
        // Tout en formes pleines
        ["Solid"] = new("M1,1 L3.5,1 L3.5,13 L1,13 Z M13,1 L13,13 L4.5,7 Z", "M13,1 L10.5,1 L10.5,13 L13,13 Z M1,1 L1,13 L9.5,7 Z",
            "M2,1 L12,7 L2,13 Z", BarsPause, Square, NavFilled: true, PlayFilled: true),
        // Doubles flèches ◀◀ ▶▶
        ["Double"] = new("M7,1 L7,13 L0,7 Z M14,1 L14,13 L7,7 Z", "M0,1 L7,7 L0,13 Z M7,1 L14,7 L7,13 Z",
            "M2,1 L12,7 L2,13 Z", BarsPause, Square, NavFilled: true, PlayFilled: true),
        // Chevrons et contours fins
        ["Chevron"] = new("M9,1 L3,7 L9,13", "M4,1 L10,7 L4,13",
            "M3,1 L12,7 L3,13 Z", LinesPause, "M2,2 L12,2 L12,12 L2,12 Z", NavFilled: false, PlayFilled: false),
        // Contours au dégradé, bouton lecture en anneau
        ["Neon"] = new("M12,2 L5,7 L12,12 Z M2,2 L2,12", "M2,2 L9,7 L2,12 Z M12,2 L12,12",
            "M3,1 L12,7 L3,13 Z", LinesPause, "M2,2 L12,2 L12,12 L2,12 Z", NavFilled: false, PlayFilled: false, Neon: true),
    };

    private IconSet _icons = IconSets["Classic"];
    private bool _isPlaying;

    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? StopSpotifyRequested;

    public MediaWidgetContent()
    {
        InitializeComponent();
        ApplyIcons();
        // La couleur du bouton lecture/pause a changé (onglet Boutons) : l'icône est redessinée.
        AddHandler(ButtonPart.IconBrushChangedEvent, new RoutedEventHandler((_, _) => ApplyIcons()));

        // Démarré au Loaded plutôt qu'au constructeur : Storyboard.TargetName se résout dans
        // l'espace de noms XAML du contrôle, qui n'est fiable qu'une fois le contrôle chargé.
        Loaded += (_, _) => UpdateSpin();
        // Une animation Forever tient l'horloge de rendu éveillée : arrêtée dès que le contrôle quitte l'écran.
        Unloaded += (_, _) => StopSpin();
        // Suit la ressource du thème effectif du module (0 quand « Activer les animations » est décoché).
        SetResourceReference(AnimationScaleProperty, "AnimationScale");
    }

    /// <summary>Échelle d'animation du thème effectif (ressource AnimationScale) : 0 = animations désactivées.</summary>
    public static readonly DependencyProperty AnimationScaleProperty = DependencyProperty.Register(
        nameof(AnimationScale), typeof(double), typeof(MediaWidgetContent),
        new PropertyMetadata(1.0, (d, _) => ((MediaWidgetContent)d).UpdateSpin()));

    public double AnimationScale { get => (double)GetValue(AnimationScaleProperty); set => SetValue(AnimationScaleProperty, value); }

    private bool _spinStarted;

    /// <summary>
    /// Le disque ne tourne que pendant une lecture et si les animations sont activées : une animation
    /// sans fin sur un module au repos consomme du rendu pour rien. En pause (ou animations coupées), il
    /// garde son angle (Pause) et reprend là où il était.
    /// </summary>
    private void UpdateSpin()
    {
        if (!IsLoaded) return;
        var storyboard = (Storyboard)Resources["SpinStoryboard"];
        if (_isPlaying && AnimationScale > 0)
        {
            if (_spinStarted) storyboard.Resume(this);
            else
            {
                storyboard.Begin(this, true);
                _spinStarted = true;
            }
        }
        else if (_spinStarted)
        {
            storyboard.Pause(this);
        }
    }

    private void StopSpin()
    {
        if (!_spinStarted) return;
        ((Storyboard)Resources["SpinStoryboard"]).Stop(this);
        _spinStarted = false;
    }

    /// <summary>Jeu d'icônes : "Classic", "Solid", "Double", "Chevron" ou "Neon".</summary>
    public void SetIconSet(string? name)
    {
        _icons = IconSets.GetValueOrDefault(name ?? "", IconSets["Classic"]);
        ApplyIcons();
    }

    private void ApplyIcons()
    {
        var accentKey = _icons.Neon ? "GaugeGradientBrush" : "TextBrush";
        StyleIcon(PrevIcon, _icons.Prev, accentKey, _icons.NavFilled);
        StyleIcon(NextIcon, _icons.Next, accentKey, _icons.NavFilled);
        StyleIcon(StopIcon, _icons.Stop, accentKey, _icons.PlayFilled);

        // Sur le disque plein au dégradé, l'icône lecture/pause reste blanche ; sur l'anneau Néon, elle prend le dégradé.
        PlayPauseButton.Tag = _icons.Neon ? "Outline" : null;
        var playData = _isPlaying ? _icons.Pause : _icons.Play;
        // Couleur choisie dans l'onglet Boutons (null = couleurs d'origine ci-dessous).
        var ink = ButtonPart.GetIconBrush(PlayPauseButton);
        if (_icons.Neon)
        {
            StyleIcon(PlayPauseIcon, playData, "GaugeGradientBrush", _icons.PlayFilled);
            if (ink is not null)
            {
                if (_icons.PlayFilled) PlayPauseIcon.Fill = ink; else PlayPauseIcon.Stroke = ink;
            }
        }
        else
        {
            PlayPauseIcon.Data = IconGeometry(playData);
            PlayPauseIcon.Fill = _icons.PlayFilled ? ink ?? Brushes.White : null;
            PlayPauseIcon.Stroke = _icons.PlayFilled ? null : ink ?? Brushes.White;
        }
        // Un réglage par état : « pause » pendant la lecture, « play » sinon.
        ButtonPart.SetKey(PlayPauseButton, _isPlaying ? "pause" : "play");
    }

    // Les formes sont des constantes : analysées une seule fois puis figées, plutôt qu'à chaque mise à jour.
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

    public void Update(NowPlayingInfo info)
    {
        TitleText.Text = info.HasSession ? info.Title : "Aucune lecture en cours";
        SubtitleText.Text = info.HasSession ? info.Subtitle : "Appuyer sur lecture pour ouvrir Spotify";

        ThumbnailImage.Source = info.Thumbnail;
        var playingChanged = _isPlaying != info.IsPlaying;
        _isPlaying = info.IsPlaying;
        if (playingChanged)
        {
            ApplyIcons();
            UpdateSpin();
        }
        // Wanted plutôt que Visibility : le bouton peut aussi être masqué par l'utilisateur (onglet Boutons).
        ButtonPart.SetWanted(StopSpotifyButton, info.IsSpotify);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
    private void Prev_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke();
    private void StopSpotify_Click(object sender, RoutedEventArgs e) => StopSpotifyRequested?.Invoke();
}
