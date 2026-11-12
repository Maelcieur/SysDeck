using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SysDeck.Deck;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

public sealed class WidgetPosition
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public bool Visible { get; set; } = true;
}

/// <summary>Nom court et icône choisis pour une sortie audio (clé : identifiant MMDevice, stable).</summary>
public sealed class AudioDeviceCustom
{
    /// <summary>Nom affiché à la place du nom complet ; vide = nom d'origine.</summary>
    public string? Name { get; set; }

    /// <summary>Symbole Segoe, émoji ou chemin d'une image, affiché à gauche du nom ; vide = aucune icône.</summary>
    public string? Icon { get; set; }

    /// <summary>Couleur du symbole (hexadécimal) ; null = couleur du texte du thème.</summary>
    public string? IconColor { get; set; }

    /// <summary>Dernier nom d'origine connu, pour afficher la ligne d'un périphérique débranché dans les Paramètres.</summary>
    public string? OriginalName { get; set; }

    /// <summary>Vrai si rien n'est personnalisé (l'entrée peut être supprimée).</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrEmpty(Icon);

    /// <summary>Nom à afficher : le nom court s'il y en a un, sinon le nom d'origine.</summary>
    public static string ResolveName(string originalName, AudioDeviceCustom? custom)
        => string.IsNullOrWhiteSpace(custom?.Name) ? originalName : custom!.Name!.Trim();
}

/// <summary>Icône choisie à la main pour une application du mixeur de volume (clé : nom d'exe en minuscules).</summary>
public sealed class AppIconCustom
{
    /// <summary>Symbole Segoe, émoji ou chemin d'une image, à la place du logo trouvé automatiquement.</summary>
    public string? Icon { get; set; }

    public string? IconColor { get; set; }

    /// <summary>Nom affiché de l'application, pour la liste des Paramètres.</summary>
    public string? DisplayName { get; set; }
}

/// <summary>
/// Réglages de l'intégration OBS Studio (obs-websocket v5). Le mot de passe est chiffré pour le compte Windows
/// courant (DPAPI) et jamais écrit en clair ; il ne fait pas partie des profils exportés.
/// </summary>
public sealed class ObsSettings
{
    public const int DefaultPort = 4455;

    /// <summary>Vrai : SysDeck se connecte à OBS (et se reconnecte tout seul).</summary>
    public bool Enabled { get; set; }

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = DefaultPort;

    /// <summary>Mot de passe du serveur WebSocket d'OBS ; vide = aucun.</summary>
    [JsonIgnore]
    public string? Password
    {
        get => Unprotect(PasswordProtected);
        set => PasswordProtected = Protect(value);
    }

    /// <summary>Mot de passe chiffré (DPAPI, portée utilisateur) puis encodé en base64.</summary>
    public string? PasswordProtected { get; set; }

    public static string? Protect(string? secret) => Secrets.Protect(secret);

    public static string? Unprotect(string? protectedValue)
        => Secrets.Unprotect(protectedValue, "obs-password", "Mot de passe OBS illisible (copié depuis un autre compte ou PC ?) : à ressaisir.");
}

/// <summary>Chiffrement DPAPI (portée utilisateur) des secrets de la configuration, encodés en base64.</summary>
public static class Secrets
{
    public static string? Protect(string? secret)
        => string.IsNullOrEmpty(secret)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    /// <summary>Déchiffre un secret ; null s'il est absent ou illisible (copié depuis un autre compte ou PC), signalé une fois dans le journal.</summary>
    public static string? Unprotect(string? protectedValue, string logKey, string unreadableMessage)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.WarnOnce(logKey, unreadableMessage, ex);
            return null;
        }
    }
}

/// <summary>Intégration Voicemod : la clé de la Control API est intégrée à l'application, rien d'autre à régler.</summary>
public sealed class VoicemodSettings
{
    public bool Enabled { get; set; }
}

/// <summary>Mode console : écran de la TV, sortie audio, clé SteamGridDB (chiffrée) et raccourci de secours.</summary>
public sealed class ConsoleSettings
{
    public const string DefaultEmergencyHotkey = "Ctrl+Alt+End";

    /// <summary>Identifiant stable de l'écran de la TV (vide = non configuré).</summary>
    public string TvTargetId { get; set; } = "";

    /// <summary>Sortie audio de la TV (vide = ne pas toucher à l'audio).</summary>
    public string TvAudioDeviceId { get; set; } = "";

    /// <summary>Désactive la TV dans Windows quand le mode console n'est pas actif.</summary>
    public bool DisableTvWhenIdle { get; set; }

    /// <summary>Clé API SteamGridDB (facultative), chiffrée DPAPI.</summary>
    public string? SteamGridDbKeyProtected { get; set; }

    [JsonIgnore]
    public string SteamGridDbKey
    {
        get => Secrets.Unprotect(SteamGridDbKeyProtected, "steamgriddb-key", "Clé SteamGridDB illisible (copiée depuis un autre compte ou PC ?) : à ressaisir.") ?? "";
        set => SteamGridDbKeyProtected = Secrets.Protect(value?.Trim());
    }

    /// <summary>Raccourci global qui quitte le mode console (secours).</summary>
    public string EmergencyHotkey { get; set; } = DefaultEmergencyHotkey;

    /// <summary>Adresse IP de la TV Google TV appairée (vide = pas de commande réseau).</summary>
    public string TvRemoteHost { get; set; } = "";

    /// <summary>Adresse MAC de la TV (réveil Wake-on-LAN / WoWLAN quand son réseau dort).</summary>
    public string TvRemoteMac { get; set; } = "";

    /// <summary>Certificat d'appairage avec la TV (PFX en base64), chiffré DPAPI.</summary>
    public string? TvRemoteCertificateProtected { get; set; }

    [JsonIgnore]
    public string TvRemoteCertificate
    {
        get => Secrets.Unprotect(TvRemoteCertificateProtected, "tv-remote-cert", "Appairage de la TV illisible (copié depuis un autre compte ou PC ?) : à refaire.") ?? "";
        set => TvRemoteCertificateProtected = Secrets.Protect(value);
    }

    /// <summary>Entrée HDMI de la TV où est branché le PC (1 à 4) ; 0 = ne pas changer d'entrée.</summary>
    public int TvHdmiInput { get; set; }

    /// <summary>Méthode qui passe la TV sur l'entrée du PC, trouvée par l'assistant (« key:243 » ou « link:content://... ») ; vide = méthode par défaut.</summary>
    public string TvInputCommand { get; set; } = "";

    /// <summary>Allumer la TV en entrant en mode console (TV appairée).</summary>
    public bool TvAutoPower { get; set; } = true;

    /// <summary>Mettre la TV en veille en revenant au PC (TV appairée).</summary>
    public bool TvAutoOff { get; set; } = true;

    /// <summary>Combinaison de la manette qui lance le mode console depuis le PC : ViewMenu, LbRbMenu, LbRbView, ou Off.</summary>
    public string StartCombo { get; set; } = "ViewMenu";

    /// <summary>Thème de l'interface TV (identifiant de ConsoleThemes).</summary>
    public string ThemeId { get; set; } = "nuit";

    /// <summary>Style de l'intro et des bruitages : « cyber » (par défaut), « elegant », ou « auto » (cyberpunk avec le thème Néon glitch).</summary>
    public string IntroStyle { get; set; } = "cyber";

    /// <summary>Bruitages du menu de la console.</summary>
    public bool UiSounds { get; set; } = true;

    /// <summary>Vibrations de la manette dans le menu de la console.</summary>
    public bool Vibration { get; set; } = true;

    /// <summary>Animation de lancement (10 s) à l'entrée en mode console.</summary>
    public bool IntroEnabled { get; set; } = true;

    /// <summary>Dossier d'intro personnalisée (images + sons du même nom, ou vidéos, joués dans l'ordre alphabétique). Vide = intro intégrée.</summary>
    public string IntroFolder { get; set; } = "";
}

/// <summary>Réglages des plugins : ceux que l'utilisateur a désactivés, et les réglages globaux que chacun enregistre (<c>setGlobalSettings</c>).</summary>
public sealed class PluginsConfig
{
    public List<string> Disabled { get; set; } = new();
    public Dictionary<string, System.Text.Json.Nodes.JsonObject> Global { get; set; } = new();

    /// <summary>Plugins installés depuis le catalogue : dépôt GitHub et version, pour proposer les mises à jour.</summary>
    public Dictionary<string, PluginSource> Sources { get; set; } = new();
}

/// <summary>Origine d'un plugin installé depuis GitHub.</summary>
public sealed class PluginSource
{
    public string Repo { get; set; } = "";
    public string Tag { get; set; } = "";
}

/// <summary>
/// Compte Twitch connecté (Device Code Flow, client public). Les jetons sont chiffrés pour le compte Windows
/// courant (DPAPI), jamais écrits en clair ni dans le journal, et ne font pas partie des profils exportés.
/// </summary>
public sealed class TwitchSettings
{
    /// <summary>Vrai tant qu'un compte est connecté (mis à faux par la déconnexion ou un jeton révoqué).</summary>
    public bool Enabled { get; set; }

    public string? Login { get; set; }

    public string? UserId { get; set; }

    [JsonIgnore]
    public string? AccessToken
    {
        get => Secrets.Unprotect(AccessTokenProtected, "twitch-access", "Jeton Twitch illisible (copié depuis un autre compte ou PC ?) : à reconnecter.");
        set => AccessTokenProtected = Secrets.Protect(value);
    }

    [JsonIgnore]
    public string? RefreshToken
    {
        get => Secrets.Unprotect(RefreshTokenProtected, "twitch-refresh", "Jeton Twitch illisible (copié depuis un autre compte ou PC ?) : à reconnecter.");
        set => RefreshTokenProtected = Secrets.Protect(value);
    }

    public string? AccessTokenProtected { get; set; }

    public string? RefreshTokenProtected { get; set; }

    /// <summary>Fin de validité du jeton d'accès.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Efface le compte et les jetons.</summary>
    public void Clear()
    {
        Enabled = false;
        Login = UserId = AccessTokenProtected = RefreshTokenProtected = null;
        ExpiresAt = null;
    }
}

public sealed class AppConfig
{
    /// <summary>Clé API Gemini, chiffrée pour le compte Windows courant (DPAPI) : illisible copiée ailleurs.</summary>
    [JsonIgnore]
    public string? GeminiApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(GeminiApiKeyProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(GeminiApiKeyProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                Log.WarnOnce("gemini-key", "Clé Gemini illisible (copiée depuis un autre compte ou PC ?) : à ressaisir.", ex);
                return null;
            }
        }
        set => GeminiApiKeyProtected = string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public string? GeminiApiKeyProtected { get; set; }

    public List<WidgetPosition> Layout { get; set; } = new();
    public List<string> HiddenAudioDeviceIds { get; set; } = new();

    /// <summary>Applications masquées dans le mixeur de volume par application (par nom de processus).</summary>
    public List<string> HiddenVolumeMixerApps { get; set; } = new();

    /// <summary>Nom court et icône de chaque sortie audio, par identifiant de périphérique (global : bureau et Switch).</summary>
    public Dictionary<string, AudioDeviceCustom> AudioDevices { get; set; } = new();

    /// <summary>Icône choisie pour chaque application du mixeur de volume, par nom d'exe en minuscules (global).</summary>
    public Dictionary<string, AppIconCustom> AppIcons { get; set; } = new();

    /// <summary>Thème global : couleurs (dont l'accent), polices, fond, jauges, animations.</summary>
    public ThemeSettings? Theme { get; set; }

    /// <summary>Prix de l'électricité et estimation de la consommation (module Consommation).</summary>
    public PowerSettings Power { get; set; } = new();

    /// <summary>Ville du module Météo.</summary>
    public WeatherSettings Weather { get; set; } = new();

    /// <summary>Couleurs de l'horloge à cases et des courbes de températures.</summary>
    public ExtraWidgetSettings WidgetExtras { get; set; } = new();

    /// <summary>Module Barres de capteurs : données choisies et style des barres.</summary>
    public SensorBarsSettings SensorBars { get; set; } = new() { Items = SensorBarsSettings.DefaultItems() };

    /// <summary>Apparence propre à chaque module, par identifiant de widget.</summary>
    public Dictionary<string, ModuleAppearance> Modules { get; set; } = new();

    // Anciens réglages : lus une seule fois pour migrer, jamais réécrits.
    [JsonPropertyName("GeminiApiKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyGeminiApiKey { get; set; }

    [JsonPropertyName("PanelOpacity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyPanelOpacity { get; set; }

    [JsonPropertyName("BorderOpacity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyBorderOpacity { get; set; }

    [JsonPropertyName("GaugeStartColorHex"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyGaugeStartColorHex { get; set; }

    [JsonPropertyName("GaugeEndColorHex"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyGaugeEndColorHex { get; set; }

    /// <summary>Opacité du fond de la barre de raccourcis (indépendante des autres widgets).</summary>
    public double DockPanelOpacity { get; set; } = 0.88;

    /// <summary>Opacité du contour de la barre de raccourcis (indépendante des autres widgets).</summary>
    public double DockBorderOpacity { get; set; } = 0.25;

    /// <summary>"Horizontal" ou "Vertical".</summary>
    public string DockOrientation { get; set; } = "Horizontal";

    /// <summary>Direction dans laquelle les icônes s'animent au survol : "Up", "Down", "Left" ou "Right".</summary>
    public string DockAnimationDirection { get; set; } = "Up";

    /// <summary>Taille au repos des icônes du dock, en pixels.</summary>
    public double DockIconSize { get; set; } = 50;

    /// <summary>Espacement entre les icônes du dock, en pixels.</summary>
    public double DockIconSpacing { get; set; } = 6;

    /// <summary>Raccourci global qui ouvre le sélecteur d'émojis (« Ctrl+Maj+E »…) ; vide = désactivé.</summary>
    public string? EmojiHotkey { get; set; }

    /// <summary>Apparence du sélecteur d'émojis (verre, flou, zoom, couleurs).</summary>
    public EmojiPickerSettings EmojiPicker { get; set; } = new();

    /// <summary>Derniers émojis insérés, le plus récent en premier.</summary>
    public List<string> RecentEmojis { get; set; } = new();

    /// <summary>Alertes (températures, batterie de la souris, quota Claude) : activation et seuils.</summary>
    public AlertSettings Alerts { get; set; } = new();

    /// <summary>Empêche le déplacement des widgets (et de la barre de raccourcis) à la souris.</summary>
    public bool WidgetsLocked { get; set; }

    /// <summary>Menu de gauche des Paramètres masqué (bouton ☰ ou Ctrl+B).</summary>
    public bool SettingsNavCollapsed { get; set; }

    /// <summary>Écran de la Switch (payload SwitchDeck) : disposition des éléments, fond, luminosité.</summary>
    public DeckSettings Deck { get; set; } = new();

    /// <summary>Intégration OBS Studio (désactivée par défaut).</summary>
    public ObsSettings Obs { get; set; } = new();

    /// <summary>Compte Twitch connecté (aucun par défaut).</summary>
    public TwitchSettings Twitch { get; set; } = new();

    /// <summary>Intégration Voicemod (désactivée par défaut).</summary>
    public VoicemodSettings Voicemod { get; set; } = new();

    /// <summary>Mode console (TV, audio, SteamGridDB, raccourci de secours).</summary>
    public ConsoleSettings Console { get; set; } = new();

    /// <summary>Plugins désactivés et réglages globaux de chaque plugin.</summary>
    public PluginsConfig Plugins { get; set; } = new();

    /// <summary>
    /// Applications affichées dans la page « Applications connectées », dans l'ordre d'ajout (voir <see cref="ConnectedApps"/>).
    /// Absente des anciens fichiers : déduite des réglages OBS et Twitch par <see cref="MigrateLegacySettings"/>.
    /// </summary>
    public List<string>? ConnectedApps { get; set; }

    /// <summary>
    /// Migre les anciens réglages (thème, clé en clair) et remplace les listes absentes (« null »
    /// dans un fichier modifié à la main) par des vides. Renvoie vrai si le fichier doit être réécrit.
    /// </summary>
    public bool MigrateLegacySettings()
    {
        var changed = false;
        Layout ??= new();
        Weather ??= new();
        WidgetExtras ??= new();
        SensorBars ??= new() { Items = SensorBarsSettings.DefaultItems() };
        SensorBars.Items ??= new();
        HiddenAudioDeviceIds ??= new();
        HiddenVolumeMixerApps ??= new();
        RecentEmojis ??= new();
        EmojiPicker ??= new();
        Modules ??= new();
        AudioDevices ??= new();
        AppIcons ??= new();
        foreach (var module in Modules.Values.Where(m => m is not null)) module.Buttons ??= new();
        Power ??= new();
        Alerts ??= new();
        Deck ??= new();
        Obs ??= new();
        Twitch ??= new();
        Voicemod ??= new();
        Console ??= new();
        Console.TvTargetId ??= "";
        Console.TvAudioDeviceId ??= "";
        if (string.IsNullOrWhiteSpace(Console.EmergencyHotkey)) Console.EmergencyHotkey = ConsoleSettings.DefaultEmergencyHotkey;
        Plugins ??= new();
        Plugins.Disabled ??= new();
        Plugins.Global ??= new();
        Plugins.Sources ??= new();
        if (string.IsNullOrWhiteSpace(Obs.Host)) Obs.Host = "127.0.0.1";
        if (Obs.Port is < 1 or > 65535) Obs.Port = ObsSettings.DefaultPort;
        if (ConnectedApps is null)
        {
            ConnectedApps = Services.ConnectedApps.FromSettings(Obs, Twitch, Voicemod);
            changed = true;
        }
        Deck.Migrate();

        if (!string.IsNullOrWhiteSpace(LegacyGeminiApiKey))
        {
            GeminiApiKey = LegacyGeminiApiKey;
            LegacyGeminiApiKey = null;
            changed = true; // la clé en clair doit disparaître du disque tout de suite
        }

        if (Theme is null)
        {
            Theme = new ThemeSettings();
            if (LegacyPanelOpacity is { } panel) Theme.PanelOpacity = panel;
            if (LegacyBorderOpacity is { } border) Theme.BorderOpacity = border;
            if (!string.IsNullOrWhiteSpace(LegacyGaugeStartColorHex)) Theme.GaugeStartColor = LegacyGaugeStartColorHex;
            if (!string.IsNullOrWhiteSpace(LegacyGaugeEndColorHex)) Theme.GaugeEndColor = LegacyGaugeEndColorHex;
        }

        LegacyPanelOpacity = LegacyBorderOpacity = null;
        LegacyGaugeStartColorHex = LegacyGaugeEndColorHex = null;
        return changed;
    }
}

public sealed class ConfigStore
{
    public AppConfig Load()
    {
        var config = JsonFile.Load(AppPaths.ConfigFile, () => new AppConfig());
        if (config.MigrateLegacySettings())
            Save(config);
        return config;
    }

    public void Save(AppConfig config) => JsonFile.Save(AppPaths.ConfigFile, config);
}
