using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SysDeck.Services;

/// <summary>Réglage sérialisable qui notifie ses changements, pour être lié directement à la fenêtre Paramètres.</summary>
public abstract class ObservableSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Apparence complète : thème global de l'application, ou style propre d'un module quand
/// celui-ci n'hérite pas du thème global. Les couleurs sont en hexadécimal ARGB.
/// </summary>
public sealed class ThemeSettings : ObservableSettings
{
    private string _gaugeStartColor = "#FF00E0FF";
    private string _gaugeEndColor = "#FF8B5CFF";
    private string _textColor = "#FFE8E9EE";
    private string _subTextColor = "#FF9A9DAC";
    private string _textFont = "Segoe UI";
    private string _numberFont = "Bahnschrift SemiBold";
    private double _panelOpacity = 0.88;
    private double _borderOpacity = 0.2;
    private double _cornerRadius = 18;
    private string _gaugeShape = "Arc";
    private double _gaugeThickness = 1;
    private bool _showTicks = true;
    private bool _glow = true;
    private bool _animationsEnabled = true;
    private double _animationSpeed = 1;
    private string _animationEasing = "Smooth";
    private bool _introAnimation = true;
    private string _entranceStyle = "Slide";
    private string _panelColor = "#FF232530";
    private string _buttonColor = "#22FFFFFF";

    /// <summary>Couleurs du dégradé ; la couleur d'accent de toute l'interface en découle.</summary>
    public string GaugeStartColor { get => _gaugeStartColor; set => Set(ref _gaugeStartColor, value); }
    public string GaugeEndColor { get => _gaugeEndColor; set => Set(ref _gaugeEndColor, value); }
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }
    public string SubTextColor { get => _subTextColor; set => Set(ref _subTextColor, value); }
    /// <summary>Fond des boutons des modules (lecture, mixeur, sorties audio…).</summary>
    public string ButtonColor { get => _buttonColor; set => Set(ref _buttonColor, value); }
    private bool _showTiles = true;
    private string _tileColor = "#FFFFFFFF";
    private double _tileOpacity = 0.08;

    /// <summary>Bulles plus claires à l'intérieur des modules (tuiles, lignes des listes) ; faux = transparentes.</summary>
    public bool ShowTiles { get => _showTiles; set => Set(ref _showTiles, value); }
    public string TileColor { get => _tileColor; set => Set(ref _tileColor, value); }
    /// <summary>Intensité des bulles (opacité de leur couleur).</summary>
    public double TileOpacity { get => _tileOpacity; set => Set(ref _tileOpacity, value); }

    private string _borderColor = "#FFFFFFFF";
    /// <summary>Couleur du contour de la carte (son intensité reste réglée par l'opacité du contour).</summary>
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value); }
    /// <summary>Couleur de fond de la carte ; un léger dégradé (plus clair en haut) en est dérivé.</summary>
    public string PanelColor { get => _panelColor; set => Set(ref _panelColor, value); }

    /// <summary>Nom d'une police installée, ou "@fichier#Famille" pour une police importée.</summary>
    public string TextFont { get => _textFont; set => Set(ref _textFont, value); }
    public string NumberFont { get => _numberFont; set => Set(ref _numberFont, value); }

    private double _fontScale = 1;
    /// <summary>Taille des textes (multiplicateur : 1 = taille d'origine).</summary>
    public double FontScale { get => _fontScale; set => Set(ref _fontScale, value); }

    public double PanelOpacity { get => _panelOpacity; set => Set(ref _panelOpacity, value); }
    public double BorderOpacity { get => _borderOpacity; set => Set(ref _borderOpacity, value); }
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, value); }

    /// <summary>"Arc" (270°), "Full" (cercle complet) ou "Half" (demi-cercle).</summary>
    public string GaugeShape { get => _gaugeShape; set => Set(ref _gaugeShape, value); }
    private string _tickStyle = "Lines";
    /// <summary>Graduations : "Lines", "Dense", "Majors", "Dots", "Inner" ou "Numbers".</summary>
    public string TickStyle { get => _tickStyle; set => Set(ref _tickStyle, value); }
    private string _gaugeStyle = "Smooth";
    /// <summary>
    /// Dessin des jauges : "Smooth" (lisse), "Segments", "Thin" (fine), "Dots" (pointillés), "Needle" (compteur),
    /// "Pie" (secteur), "Neon", "Rays" (rayons), "Double" (arc + écho) ou "Capsule".
    /// </summary>
    public string GaugeStyle { get => _gaugeStyle; set => Set(ref _gaugeStyle, value); }
    /// <summary>Multiplicateur de l'épaisseur des anneaux.</summary>
    public double GaugeThickness { get => _gaugeThickness; set => Set(ref _gaugeThickness, value); }
    public bool ShowTicks { get => _showTicks; set => Set(ref _showTicks, value); }
    public bool Glow { get => _glow; set => Set(ref _glow, value); }

    public bool AnimationsEnabled { get => _animationsEnabled; set => Set(ref _animationsEnabled, value); }
    /// <summary>Multiplicateur de durée des animations (plus grand = plus lent).</summary>
    public double AnimationSpeed { get => _animationSpeed; set => Set(ref _animationSpeed, value); }
    /// <summary>"Smooth", "Elastic", "Bounce", "Back", "Sine", "Snap" ou "Linear".</summary>
    public string AnimationEasing { get => _animationEasing; set => Set(ref _animationEasing, value); }
    /// <summary>Remplissage des jauges depuis zéro à l'apparition.</summary>
    public bool IntroAnimation { get => _introAnimation; set => Set(ref _introAnimation, value); }
    /// <summary>
    /// Apparition d'un module : "Slide" (fondu glissé, comme avant), "Fade" (fondu seul), "Zoom" (léger
    /// agrandissement), "Drop" (tombe d'en haut), "Pop" (surgit en grossissant), "Flip" (se déplie) ou "None".
    /// Une valeur inconnue est traitée comme "Slide".
    /// </summary>
    public string EntranceStyle
    {
        get => _entranceStyle;
        set
        {
            if (_entranceStyle == value) return;
            _entranceStyle = value;
            Raise(nameof(EntranceStyle));
            Raise(nameof(FadeIn));
        }
    }

    /// <summary>
    /// Ancien réglage « apparition en fondu », dérivé de <see cref="EntranceStyle"/> et conservé pour deux raisons :
    /// les anciennes configs (FadeIn=false) migrent vers "None", et une version précédente de SysDeck relit encore ce champ.
    /// </summary>
    public bool FadeIn
    {
        get => _entranceStyle != "None";
        set
        {
            if (FadeIn == value) return;
            EntranceStyle = value ? "Slide" : "None";
        }
    }

    private string _hoverEffect = "None";
    /// <summary>
    /// Survol à la souris (bureau seulement) : "None", "Lift" (léger agrandissement), "Glow" (contour plus lumineux)
    /// ou "LiftGlow" (les deux).
    /// </summary>
    public string HoverEffect { get => _hoverEffect; set => Set(ref _hoverEffect, value); }

    private string _idleEffect = "None";
    /// <summary>
    /// Animation continue au repos (bureau seulement) : "None", "Breathe" (respire doucement), "Float" (flotte de
    /// haut en bas) ou "Pulse" (contour qui s'allume et s'éteint). À faible cadence pour ménager le processeur.
    /// </summary>
    public string IdleEffect { get => _idleEffect; set => Set(ref _idleEffect, value); }

    private bool _panelGradient;
    private string _panelColor2 = "#FF101218";
    private double _panelGradientAngle = 90;
    private double _borderThickness = 1;
    private string _borderStyle = "Solid";
    private bool _shadowEnabled;
    private string _shadowColor = "#FF000000";
    private double _shadowSize = 16;
    private double _shadowOpacity = 0.5;

    /// <summary>Vrai : fond de carte en dégradé de <see cref="PanelColor"/> vers <see cref="PanelColor2"/> ; faux : léger dégradé dérivé de PanelColor (rendu d'origine).</summary>
    public bool PanelGradient { get => _panelGradient; set => Set(ref _panelGradient, value); }
    /// <summary>Seconde couleur du dégradé de fond (ARGB) ; l'opacité du fond s'applique aux deux couleurs.</summary>
    public string PanelColor2 { get => _panelColor2; set => Set(ref _panelColor2, value); }
    /// <summary>Direction du dégradé de fond en degrés : 0 = gauche vers droite, 90 = haut vers bas.</summary>
    public double PanelGradientAngle { get => _panelGradientAngle; set => Set(ref _panelGradientAngle, value); }
    /// <summary>Épaisseur du contour de la carte, en pixels.</summary>
    public double BorderThickness { get => _borderThickness; set => Set(ref _borderThickness, value); }
    /// <summary>"Solid" (couleur du contour, comme avant) ou "Accent" (dégradé d'accent GaugeStartColor vers GaugeEndColor).</summary>
    public string BorderStyle { get => _borderStyle; set => Set(ref _borderStyle, value); }
    /// <summary>Ombre portée autour de la carte (bureau seulement) ; calque statique, sans effet recalculé.</summary>
    public bool ShadowEnabled { get => _shadowEnabled; set => Set(ref _shadowEnabled, value); }
    public string ShadowColor { get => _shadowColor; set => Set(ref _shadowColor, value); }
    /// <summary>Étendue de l'ombre autour de la carte, en pixels.</summary>
    public double ShadowSize { get => _shadowSize; set => Set(ref _shadowSize, value); }
    /// <summary>Intensité de l'ombre contre la carte (0 à 1).</summary>
    public double ShadowOpacity { get => _shadowOpacity; set => Set(ref _shadowOpacity, value); }

    // Aller-retour JSON plutôt que MemberwiseClone, qui copierait aussi les abonnés de PropertyChanged.
    public ThemeSettings Clone()
        => System.Text.Json.JsonSerializer.Deserialize<ThemeSettings>(System.Text.Json.JsonSerializer.Serialize(this))!;

    // Groupes de réglages qu'un module peut reprendre à son compte indépendamment les uns des autres.

    public void CopyColorsAndFontsFrom(ThemeSettings s)
    {
        GaugeStartColor = s.GaugeStartColor; GaugeEndColor = s.GaugeEndColor;
        TextColor = s.TextColor; SubTextColor = s.SubTextColor; ButtonColor = s.ButtonColor;
        TextFont = s.TextFont; NumberFont = s.NumberFont; FontScale = s.FontScale;
    }

    public void CopyCardFrom(ThemeSettings s)
    {
        ShowTiles = s.ShowTiles; TileColor = s.TileColor; TileOpacity = s.TileOpacity;
        PanelColor = s.PanelColor; BorderColor = s.BorderColor; PanelOpacity = s.PanelOpacity; BorderOpacity = s.BorderOpacity; CornerRadius = s.CornerRadius;
        PanelGradient = s.PanelGradient; PanelColor2 = s.PanelColor2; PanelGradientAngle = s.PanelGradientAngle;
        BorderThickness = s.BorderThickness; BorderStyle = s.BorderStyle;
        ShadowEnabled = s.ShadowEnabled; ShadowColor = s.ShadowColor; ShadowSize = s.ShadowSize; ShadowOpacity = s.ShadowOpacity;
    }

    public void CopyGaugesFrom(ThemeSettings s)
    {
        GaugeShape = s.GaugeShape; GaugeStyle = s.GaugeStyle; GaugeThickness = s.GaugeThickness; ShowTicks = s.ShowTicks; TickStyle = s.TickStyle; Glow = s.Glow;
    }

    public void CopyAnimationsFrom(ThemeSettings s)
    {
        AnimationsEnabled = s.AnimationsEnabled; AnimationSpeed = s.AnimationSpeed; AnimationEasing = s.AnimationEasing;
        IntroAnimation = s.IntroAnimation; EntranceStyle = s.EntranceStyle; HoverEffect = s.HoverEffect; IdleEffect = s.IdleEffect;
    }
}

/// <summary>Apparence propre à un module (widget).</summary>
public sealed class ModuleAppearance : ObservableSettings
{
    private bool _autoSize = true;
    private double _width = 260;
    private double _height = 120;
    private string _stretchMode = "Uniform";
    private string? _backgroundImage;
    private double _backgroundImageOpacity = 0.7;
    private double _backgroundDim = 0.35;
    private bool _useCustomTheme;
    private ThemeSettings? _theme;

    /// <summary>Taille naturelle du contenu ; sinon le contenu est mis à l'échelle dans Width × Height.</summary>
    public bool AutoSize { get => _autoSize; set => Set(ref _autoSize, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    /// <summary>"Uniform" (garde les proportions), "Fill" (étire le contenu) ou "None" (taille réelle, espace libre autour).</summary>
    public string StretchMode { get => _stretchMode; set => Set(ref _stretchMode, value); }

    /// <summary>Copie de l'image gérée par SysDeck (dossier Backgrounds), ou null.</summary>
    public string? BackgroundImage { get => _backgroundImage; set => Set(ref _backgroundImage, value); }
    public double BackgroundImageOpacity { get => _backgroundImageOpacity; set => Set(ref _backgroundImageOpacity, value); }
    /// <summary>Voile sombre posé sur l'image pour garder les textes lisibles (0 à 1).</summary>
    public double BackgroundDim { get => _backgroundDim; set => Set(ref _backgroundDim, value); }

    private string _mediaBackground = "None";
    private double _mediaBlur = 0.6;
    private string _mediaAnimStyle = "KenBurns";
    private double _mediaAnimDuration = 16;
    private double _mediaAnimZoom = 0.25;
    private double _mediaAnimPan = 0.06;

    /// <summary>Lecture en cours : "None", "Cover" (pochette fixe en fond) ou "Animated" (pochette en mouvement lent).</summary>
    public string MediaBackground { get => _mediaBackground; set => Set(ref _mediaBackground, value); }
    /// <summary>Intensité du flou de la pochette de fond, de 0 (nette) à 1 (très floue).</summary>
    public double MediaBlur { get => _mediaBlur; set => Set(ref _mediaBlur, value); }
    /// <summary>Animation de la pochette : "KenBurns" (zoom + glissement), "Zoom", "Pan" (glissement) ou "Rotate".</summary>
    public string MediaAnimStyle { get => _mediaAnimStyle; set => Set(ref _mediaAnimStyle, value); }
    /// <summary>Durée d'un aller de l'animation, en secondes (plus petit = plus rapide).</summary>
    public double MediaAnimDuration { get => _mediaAnimDuration; set => Set(ref _mediaAnimDuration, value); }
    /// <summary>Amplitude du zoom (0,25 = jusqu'à +25 %).</summary>
    public double MediaAnimZoom { get => _mediaAnimZoom; set => Set(ref _mediaAnimZoom, value); }
    /// <summary>Amplitude du glissement, en fraction de la taille du module.</summary>
    public double MediaAnimPan { get => _mediaAnimPan; set => Set(ref _mediaAnimPan, value); }

    private string _volumeStyle = "Vertical";
    private string _mediaIconSet = "Classic";
    private string _diskStyle = "Ring";

    /// <summary>Disques : "Ring" (anneaux) ou "Bar" (lignes horizontales).</summary>
    public string DiskStyle { get => _diskStyle; set => Set(ref _diskStyle, value); }

    private string _visualizerStyle = "Bars";
    private int _visualizerBars = 32;
    private double _visualizerSensitivity = 1;

    /// <summary>Visualiseur audio : "Bars" (barres verticales), "Mirror" (onde miroir) ou "Circle" (anneau).</summary>
    public string VisualizerStyle { get => _visualizerStyle; set => Set(ref _visualizerStyle, value); }
    /// <summary>Nombre de bandes de fréquences affichées.</summary>
    public int VisualizerBars { get => _visualizerBars; set => Set(ref _visualizerBars, value); }
    /// <summary>Amplification des niveaux (1 = normale).</summary>
    public double VisualizerSensitivity { get => _visualizerSensitivity; set => Set(ref _visualizerSensitivity, value); }

    /// <summary>Volume par application : "Vertical", "Horizontal", "Circle", "Half" ou "Segments".</summary>
    public string VolumeStyle { get => _volumeStyle; set => Set(ref _volumeStyle, value); }
    /// <summary>Lecture en cours, icônes des boutons : "Classic", "Solid", "Double", "Chevron" ou "Neon".</summary>
    public string MediaIconSet { get => _mediaIconSet; set => Set(ref _mediaIconSet, value); }

    private double _fontScale = 1;
    /// <summary>Taille des textes de ce module, multipliée par celle du thème (1 = inchangée).</summary>
    public double FontScale { get => _fontScale; set => Set(ref _fontScale, value); }

    /// <summary>Réglages de chaque texte du module (afficher, police, taille), par clé (TextPart.Key).</summary>
    public Dictionary<string, TextPartStyle> Texts { get; set; } = new();

    public TextPartStyle GetText(string key)
    {
        if (!Texts.TryGetValue(key, out var style))
            Texts[key] = style = new TextPartStyle();
        return style;
    }

    public void ResetTexts()
    {
        Texts.Clear();
        Raise(nameof(Texts));
    }

    /// <summary>
    /// Style de chaque bouton du module (fond, texte, survol, icône, masquage), par clé (ButtonPart.Key).
    /// Absent d'une ancienne configuration : vide, les boutons gardent alors leur rendu d'origine.
    /// </summary>
    public Dictionary<string, ButtonPartStyle> Buttons { get; set; } = new();

    public ButtonPartStyle GetButton(string key)
    {
        Buttons ??= new();
        if (!Buttons.TryGetValue(key, out var style))
            Buttons[key] = style = new ButtonPartStyle();
        return style;
    }

    public void ResetButtons()
    {
        Buttons ??= new();
        Buttons.Clear();
        Raise(nameof(Buttons));
    }

    private bool _customCard;
    private bool _customGauges;
    private bool _customAnimations;

    /// <summary>Vrai : couleurs et polices du module prises dans <see cref="Theme"/> au lieu du thème global.</summary>
    public bool UseCustomTheme { get => _useCustomTheme; set => Set(ref _useCustomTheme, value); }
    /// <summary>Carte (fond, contour, arrondi) propre au module.</summary>
    public bool CustomCard { get => _customCard; set => Set(ref _customCard, value); }
    /// <summary>Forme, épaisseur, graduations et halo des jauges propres au module.</summary>
    public bool CustomGauges { get => _customGauges; set => Set(ref _customGauges, value); }
    /// <summary>Animations propres au module.</summary>
    public bool CustomAnimations { get => _customAnimations; set => Set(ref _customAnimations, value); }

    [JsonIgnore]
    public bool HasAnyCustomStyle => Theme is not null && (UseCustomTheme || CustomCard || CustomGauges || CustomAnimations);

    /// <summary>Thème réellement appliqué : le thème global, dont chaque groupe personnalisé est remplacé par celui du module.</summary>
    public ThemeSettings EffectiveTheme(ThemeSettings global)
    {
        if (!HasAnyCustomStyle) return global;
        var effective = global.Clone();
        if (UseCustomTheme) effective.CopyColorsAndFontsFrom(Theme!);
        if (CustomCard) effective.CopyCardFrom(Theme!);
        if (CustomGauges) effective.CopyGaugesFrom(Theme!);
        if (CustomAnimations) effective.CopyAnimationsFrom(Theme!);
        return effective;
    }

    public ThemeSettings? Theme
    {
        get => _theme;
        set
        {
            if (ReferenceEquals(_theme, value)) return;
            if (_theme is not null) _theme.PropertyChanged -= OnThemeChanged;
            _theme = value;
            if (_theme is not null) _theme.PropertyChanged += OnThemeChanged;
            Raise(nameof(Theme));
        }
    }

    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e) => Raise(nameof(Theme));

    /// <summary>Copie indépendante (aller-retour JSON : les abonnés de PropertyChanged ne sont pas copiés).</summary>
    public ModuleAppearance Clone()
        => System.Text.Json.JsonSerializer.Deserialize<ModuleAppearance>(System.Text.Json.JsonSerializer.Serialize(this))!;

    /// <summary>Position, taille et visibilité personnalisées des éléments du module, par clé (Movable.Key).</summary>
    public Dictionary<string, ElementLayout> Elements { get; set; } = new();

    public ElementLayout GetElement(string key)
    {
        if (!Elements.TryGetValue(key, out var layout))
            Elements[key] = layout = new ElementLayout();
        return layout;
    }

    public void ResetLayout()
    {
        Elements.Clear();
        Raise(nameof(Elements));
    }

    /// <summary>Signale un changement fait directement sur un <see cref="ElementLayout"/> (liste des éléments des Paramètres).</summary>
    public void NotifyElementsChanged() => Raise(nameof(Elements));

    public void ShowAllElements()
    {
        foreach (var layout in Elements.Values)
            layout.Hidden = false;
        Raise(nameof(Elements));
    }
}

/// <summary>
/// Apparence du sélecteur d'émojis : transparent, avec un léger fond noir par défaut.
/// </summary>
public sealed class EmojiPickerSettings : ObservableSettings
{
    private string _backgroundColor = "#FF000000";
    private double _backgroundOpacity = 0.5;
    private string _borderColor = "#FFFFFFFF";
    private double _borderOpacity = 0.18;
    private double _cornerRadius = 18;

    public string BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    /// <summary>Opacité du fond (0 = entièrement transparent).</summary>
    public double BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, value); }
    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, value); }
    public double BorderOpacity { get => _borderOpacity; set => Set(ref _borderOpacity, value); }
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, value); }

    public void Reset()
    {
        var d = new EmojiPickerSettings();
        BackgroundColor = d.BackgroundColor; BackgroundOpacity = d.BackgroundOpacity;
        BorderColor = d.BorderColor; BorderOpacity = d.BorderOpacity; CornerRadius = d.CornerRadius;
    }
}

/// <summary>Affichage, police et taille d'un texte de module.</summary>
public sealed class TextPartStyle : ObservableSettings
{
    private bool _hidden;
    private string? _font;
    private double _scale = 1;

    public bool Hidden { get => _hidden; set { Set(ref _hidden, value); Raise(nameof(Visible)); } }

    /// <summary>Pour l'interrupteur « afficher » des Paramètres.</summary>
    [JsonIgnore]
    public bool Visible { get => !Hidden; set => Hidden = !value; }

    /// <summary>Police propre à ce texte ; vide ou null = police du thème.</summary>
    public string? Font { get => _font; set => Set(ref _font, value); }

    /// <summary>Taille propre à ce texte (multiplicateur, cumulé avec la taille du module et du thème).</summary>
    public double Scale { get => _scale; set => Set(ref _scale, value); }

    private string? _customText;
    private string? _color;

    /// <summary>Texte de remplacement (textes fixes uniquement : titres, libellés) ; vide = texte d'origine.</summary>
    public string? CustomText { get => _customText; set => Set(ref _customText, string.IsNullOrWhiteSpace(value) ? null : value); }

    /// <summary>Couleur propre à ce texte (hexadécimal) ; null = couleur du thème.</summary>
    public string? Color { get => _color; set { Set(ref _color, value); Raise(nameof(HasColor)); } }

    [JsonIgnore]
    public bool HasColor => !string.IsNullOrEmpty(Color);

    /// <summary>Retour au texte d'origine : affiché, police et couleur du thème, taille et libellé d'origine.</summary>
    public void Reset()
    {
        Hidden = false;
        Font = null;
        Scale = 1;
        CustomText = null;
        Color = null;
    }
}

/// <summary>
/// Style d'un bouton de module : masquage, icône de remplacement et couleurs (fond, texte, survol).
/// Tout est vide par défaut : le bouton garde alors exactement son rendu d'origine.
/// </summary>
public sealed class ButtonPartStyle : ObservableSettings
{
    private bool _hidden;
    private string? _glyph;
    private string? _background;
    private string? _foreground;
    private string? _hoverBackground;

    public bool Hidden { get => _hidden; set { Set(ref _hidden, value); Raise(nameof(Visible)); } }

    /// <summary>Pour l'interrupteur « Afficher » des Paramètres.</summary>
    [JsonIgnore]
    public bool Visible { get => !Hidden; set => Hidden = !value; }

    /// <summary>Icône à la place du contenu d'origine : symbole Segoe, émoji ou chemin d'une image ; null = contenu d'origine.</summary>
    public string? Glyph { get => _glyph; set { Set(ref _glyph, string.IsNullOrWhiteSpace(value) ? null : value); Raise(nameof(HasGlyph)); } }

    /// <summary>Fond du bouton (hexadécimal) ; null = couleur du thème.</summary>
    public string? Background { get => _background; set => Set(ref _background, string.IsNullOrWhiteSpace(value) ? null : value); }

    /// <summary>Couleur du texte ou de l'icône (hexadécimal) ; null = couleur du thème.</summary>
    public string? Foreground { get => _foreground; set => Set(ref _foreground, string.IsNullOrWhiteSpace(value) ? null : value); }

    /// <summary>Fond au survol (hexadécimal) ; null = couleur d'accent du thème.</summary>
    public string? HoverBackground { get => _hoverBackground; set => Set(ref _hoverBackground, string.IsNullOrWhiteSpace(value) ? null : value); }

    [JsonIgnore]
    public bool HasGlyph => !string.IsNullOrEmpty(Glyph);

    /// <summary>Vrai si rien n'est personnalisé (rendu d'origine).</summary>
    [JsonIgnore]
    public bool IsDefault => !Hidden && Glyph is null && Background is null && Foreground is null && HoverBackground is null;

    public void Reset()
    {
        Hidden = false;
        Glyph = null;
        Background = null;
        Foreground = null;
        HoverBackground = null;
    }
}

/// <summary>Décalage (en pixels du contenu), échelle et visibilité d'un élément de module.</summary>
public sealed class ElementLayout
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1;
    public bool Hidden { get; set; }

    /// <summary>Dégradé propre à l'élément (jauges, graphes, boutons qu'il contient) ; null = celui du thème.</summary>
    public string? AccentStart { get; set; }
    public string? AccentEnd { get; set; }

    [JsonIgnore]
    public bool IsDefault => X == 0 && Y == 0 && Math.Abs(Scale - 1) < 0.001;

    [JsonIgnore]
    public bool HasAccent => !string.IsNullOrEmpty(AccentStart) || !string.IsNullOrEmpty(AccentEnd);
}
