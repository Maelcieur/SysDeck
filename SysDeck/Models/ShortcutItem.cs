using System.Text.Json.Serialization;
using SysDeck.Deck;

namespace SysDeck.Models;

public sealed class ShortcutItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Icône choisie manuellement (chemin d'image), prioritaire sur la détection automatique.</summary>
    public string? CustomIconPath { get; set; }

    /// <summary>
    /// Symbole (glyphe Segoe Fluent Icons) ou émoji choisi comme icône ; prioritaire sur
    /// <see cref="CustomIconPath"/>, lui-même prioritaire sur l'icône détectée. Null = aucun.
    /// </summary>
    public string? IconGlyph { get; set; }

    /// <summary>Couleur du symbole (ARGB hexadécimal) ; null = couleur du texte du thème. Sans effet sur un émoji ou une image.</summary>
    public string? IconColor { get; set; }

    /// <summary>Couleur de fond de la tuile sur la Switch (ARGB hexadécimal) ; null = fond du thème.</summary>
    public string? TileColor { get; set; }

    /// <summary>
    /// Couleur appliquée à une image choisie (<see cref="CustomIconPath"/>) : l'image devient d'une seule couleur, idéal
    /// pour les icônes monochromes des packs. Null = couleurs d'origine. Sans effet sur un symbole ou un émoji.
    /// </summary>
    public string? ImageTint { get; set; }

    /// <summary>Taille de l'icône dans la tuile de la Switch (part du côté, 0,25 à 1) ; null = réglage général de la Switch.</summary>
    public double? IconScale { get; set; }

    /// <summary>Masque le nom sous la tuile de la Switch (utile avec une image qui remplit la tuile).</summary>
    public bool HideName { get; set; }

    /// <summary>Nom de la tuile de la Switch affiché (vrai) ou masqué (faux) quel que soit le réglage de la page ; null = suit la page.</summary>
    public bool? ShowName { get; set; }

    // ---------- Tuile active (micro coupé, stream OBS en cours, mode Twitch, voix Voicemod, état 1 d'un plugin…) ----------

    /// <summary>Fond de la tuile quand elle est active ; null = rouge (coupé, en cours) ou accent du thème (scène, mode actif).</summary>
    public string? ActiveColor { get; set; }

    /// <summary>Symbole ou émoji affiché quand la tuile est active (ex. micro barré) ; null = même icône.</summary>
    public string? ActiveIconGlyph { get; set; }

    /// <summary>Image (copie gérée par SysDeck) affichée quand la tuile est active ; ignorée si <see cref="ActiveIconGlyph"/> est choisi.</summary>
    public string? ActiveIconPath { get; set; }

    /// <summary>Couleur du symbole actif ; null = celle du symbole normal.</summary>
    public string? ActiveIconColor { get; set; }

    /// <summary>Couleur appliquée à l'image active ; null = couleurs d'origine.</summary>
    public string? ActiveImageTint { get; set; }

    /// <summary>Bouton en verre qui reste enfoncé tant que la tuile est active.</summary>
    public bool ActivePressed { get; set; } = true;

    [JsonIgnore]
    public bool HasActiveIcon => !string.IsNullOrEmpty(ActiveIconGlyph) || !string.IsNullOrWhiteSpace(ActiveIconPath);

    /// <summary>Tuile servant à dessiner l'icône active.</summary>
    public ShortcutItem ActiveDisplay() => new()
    {
        Name = Name,
        IconGlyph = ActiveIconGlyph,
        CustomIconPath = string.IsNullOrEmpty(ActiveIconGlyph) ? ActiveIconPath : null,
        IconColor = ActiveIconColor ?? IconColor,
        ImageTint = ActiveImageTint,
        TileColor = TileColor
    };

    /// <summary>Recopie l'apparence « active » d'une autre tuile (modification d'une tuile, aperçu).</summary>
    public void CopyActiveFrom(ShortcutItem other)
    {
        ActiveColor = other.ActiveColor;
        ActiveIconGlyph = other.ActiveIconGlyph;
        ActiveIconPath = other.ActiveIconPath;
        ActiveIconColor = other.ActiveIconColor;
        ActiveImageTint = other.ActiveImageTint;
        ActivePressed = other.ActivePressed;
    }

    /// <summary>
    /// Combinaison de touches envoyée à la fenêtre au premier plan (forme de <c>HotkeyGesture.Serialize</c>) :
    /// la tuile est alors un raccourci clavier, sans programme (<see cref="Path"/> vide). Null = application normale.
    /// </summary>
    public string? Hotkey { get; set; }

    [JsonIgnore]
    public bool IsHotkey => !string.IsNullOrWhiteSpace(Hotkey);

    private TileKind? _kind;

    /// <summary>
    /// Type de tuile. Absent des anciens fichiers : déduit alors de <see cref="Hotkey"/>
    /// (raccourci clavier si renseigné, sinon application).
    /// </summary>
    public TileKind Kind
    {
        get => _kind ?? (IsHotkey ? TileKind.Hotkey : TileKind.App);
        set => _kind = value;
    }

    /// <summary>Ligne de commande d'une tuile <see cref="TileKind.Command"/> (exécutée via <c>cmd.exe /c</c>).</summary>
    public string? Command { get; set; }

    /// <summary>Action de démarrage de la Switch pour une tuile <see cref="TileKind.SwitchAction"/>.</summary>
    public SwitchActionKind? SwitchAction { get; set; }

    /// <summary>Commande exécutée avec élévation (demande UAC).</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>Demande un second appui dans les 3 s avant d'agir (évite les appuis accidentels).</summary>
    public bool ConfirmTap { get; set; }

    /// <summary>Identifiant du périphérique audio d'une tuile micro/sortie muet ; vide = défaut de Windows.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Colonne de la case dans la grille d'applications de la Switch (0 = première) ; null = pas encore placée.</summary>
    public int? GridCol { get; set; }

    /// <summary>Ligne de la case dans la grille d'applications de la Switch (0 = première) ; null = pas encore placée.</summary>
    public int? GridRow { get; set; }

    /// <summary>Index de la sous-page du widget des tuiles dans laquelle cette tuile apparaît.</summary>
    public int AppsSubpage { get; set; }

    /// <summary>Sens d'une action de navigation entre les sous-pages du widget des tuiles.</summary>
    public TilePageNavigation? PageNavigation { get; set; }

    /// <summary>Bouton Joy-Con qui déclenche cette navigation uniquement sur la page complète qui contient la tuile.</summary>
    public JoyConButton? MappedJoyConButton { get; set; }

    /// <summary>Action OBS d'une tuile <see cref="TileKind.Obs"/> ; null pour les autres types.</summary>
    public ObsAction? ObsAction { get; set; }

    /// <summary>Cible de l'action OBS : nom de la scène (Scene), de l'entrée audio (InputMute) ou de la source (SourceVisibility).</summary>
    public string? ObsTarget { get; set; }

    /// <summary>Scène qui contient la source d'une tuile SourceVisibility.</summary>
    public string? ObsScene { get; set; }

    /// <summary>Action Twitch d'une tuile <see cref="TileKind.Twitch"/> ; null pour les autres types.</summary>
    public TwitchAction? TwitchAction { get; set; }

    /// <summary>Paramètre de l'action Twitch : message, titre, description du marqueur, durée de la pub (secondes) ou nom de la catégorie.</summary>
    public string? TwitchText { get; set; }

    /// <summary>Identifiant Twitch de la catégorie d'une tuile SetCategory (<see cref="TwitchText"/> en garde le nom).</summary>
    public string? TwitchId { get; set; }

    /// <summary>Message, titre ou catégorie saisi au moment de l'appui (clavier de la Switch) au lieu du texte fixe.</summary>
    public bool TwitchAsk { get; set; }

    /// <summary>Vrai pour une tuile Twitch qui ouvre la saisie : seuls le message, le titre et la catégorie s'y prêtent.</summary>
    [JsonIgnore]
    public bool AsksTwitchText => Kind == TileKind.Twitch && TwitchAsk
        && TwitchAction is Models.TwitchAction.ChatMessage or Models.TwitchAction.SetTitle or Models.TwitchAction.SetCategory;

    /// <summary>Action d'une tuile <see cref="TileKind.Voicemod"/>.</summary>
    public VoicemodAction? VoicemodAction { get; set; }

    /// <summary>Identifiant de la voix (LoadVoice) ou nom de fichier du son (PlaySound) dans Voicemod.</summary>
    public string? VoicemodTarget { get; set; }

    /// <summary>Nom lisible de la voix ou du son choisi (affichage dans les Paramètres).</summary>
    public string? VoicemodTargetName { get; set; }

    /// <summary>Identifiant du plugin d'une tuile <see cref="TileKind.Plugin"/> (manifest « Id »).</summary>
    public string? PluginId { get; set; }

    /// <summary>Identifiant de l'action du plugin (manifest « Actions[].Id »).</summary>
    public string? PluginAction { get; set; }

    /// <summary>Réglages de la tuile, propres à l'action du plugin (formulaire généré, puis <c>setSettings</c> du plugin).</summary>
    public System.Text.Json.Nodes.JsonObject? PluginSettings { get; set; }

    /// <summary>Identifiant stable de la tuile pour son plugin (« context » du protocole) ; voir <see cref="EnsurePluginContext"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginContext { get; set; }

    /// <summary>Image à afficher à la place de l'icône (envoyée par un plugin) ; jamais enregistrée.</summary>
    [JsonIgnore]
    public System.Windows.Media.ImageSource? DisplayImage { get; set; }

    /// <summary>Contexte de la tuile, créé à la première demande.</summary>
    public string EnsurePluginContext() => PluginContext ??= Guid.NewGuid().ToString("N");

    [JsonIgnore]
    public bool IsMuteTile =>Kind is TileKind.MicMute or TileKind.OutputMute;

}

/// <summary>Type d'une tuile de la Switch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileKind
{
    /// <summary>Application, fichier, dossier, lien ou protocole.</summary>
    App,
    /// <summary>Combinaison de touches envoyée au PC.</summary>
    Hotkey,
    /// <summary>Ligne de commande exécutée sans fenêtre.</summary>
    Command,
    /// <summary>Bascule muet d'un micro.</summary>
    MicMute,
    /// <summary>Bascule muet d'une sortie audio.</summary>
    OutputMute,
    /// <summary>Action sur OBS Studio (scène, stream, enregistrement, muet d'une entrée…).</summary>
    Obs,
    /// <summary>Action sur la chaîne Twitch (clip, marqueur, message, pub, modes du tchat, titre, catégorie…).</summary>
    Twitch,
    /// <summary>Action sur Voicemod (changeur de voix, voix, son du soundboard, micro…).</summary>
    Voicemod,
    /// <summary>Action fournie par un plugin installé (programme séparé relié par WebSocket local).</summary>
    Plugin,
    /// <summary>Action d'alimentation ou de démarrage de la Nintendo Switch.</summary>
    SwitchAction,
    /// <summary>Navigation entre les sous-pages du widget des tuiles.</summary>
    TilePageNavigation,
    /// <summary>Bascule du Mode console (TV seule, interface manette) ; allumée tant que le mode est actif.</summary>
    ConsoleMode
}

/// <summary>Navigation relative dans le widget des tuiles.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TilePageNavigation
{
    Previous,
    Next
}

/// <summary>Action matérielle exécutée par le payload SwitchDeck.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SwitchActionKind
{
    EnterRcm = 1,
    LaunchHekate = 2
}

/// <summary>Action d'une tuile Voicemod.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoicemodAction
{
    /// <summary>Active ou coupe le changeur de voix.</summary>
    VoiceChanger,
    /// <summary>Charge une voix précise.</summary>
    LoadVoice,
    /// <summary>Charge une voix au hasard.</summary>
    RandomVoice,
    /// <summary>Joue un son du soundboard.</summary>
    PlaySound,
    /// <summary>Arrête tous les sons du soundboard.</summary>
    StopSounds,
    /// <summary>Coupe ou rétablit le micro dans Voicemod.</summary>
    MuteMic,
    /// <summary>Active ou coupe « M'entendre ».</summary>
    HearMyself,
    /// <summary>Active ou coupe l'effet d'ambiance.</summary>
    Background,
    /// <summary>Coupe ou rétablit les sons du soundboard pour soi.</summary>
    MuteSoundsForMe
}

/// <summary>Action d'une tuile Twitch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TwitchAction
{
    /// <summary>Crée un clip du direct.</summary>
    Clip,
    /// <summary>Pose un marqueur dans le direct.</summary>
    Marker,
    /// <summary>Envoie un message dans le tchat.</summary>
    ChatMessage,
    /// <summary>Lance une pub.</summary>
    Commercial,
    /// <summary>Active ou coupe le mode lent.</summary>
    SlowMode,
    /// <summary>Active ou coupe le mode émotes seulement.</summary>
    EmoteOnly,
    /// <summary>Active ou coupe le mode réservé aux followers.</summary>
    FollowersOnly,
    /// <summary>Active ou coupe le mode réservé aux abonnés.</summary>
    SubscribersOnly,
    /// <summary>Active ou coupe le mode bouclier.</summary>
    ShieldMode,
    /// <summary>Change le titre du direct.</summary>
    SetTitle,
    /// <summary>Change la catégorie du direct.</summary>
    SetCategory
}

/// <summary>Action d'une tuile OBS Studio.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObsAction
{
    /// <summary>Passe à une scène.</summary>
    Scene,
    /// <summary>Démarre ou arrête le stream.</summary>
    Stream,
    /// <summary>Démarre ou arrête l'enregistrement.</summary>
    Record,
    /// <summary>Met en pause ou reprend l'enregistrement.</summary>
    RecordPause,
    /// <summary>Active ou coupe la caméra virtuelle.</summary>
    VirtualCam,
    /// <summary>Active ou coupe le tampon de relecture.</summary>
    ReplayBuffer,
    /// <summary>Enregistre le tampon de relecture.</summary>
    SaveReplay,
    /// <summary>Bascule le muet d'une entrée audio.</summary>
    InputMute,
    /// <summary>Affiche ou masque une source dans une scène.</summary>
    SourceVisibility,
    /// <summary>Lance la transition du mode Studio.</summary>
    StudioTransition
}
