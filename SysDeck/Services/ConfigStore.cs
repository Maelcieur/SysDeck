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

    /// <summary>Écran de la Switch (payload SwitchDeck) : disposition des éléments, fond, luminosité.</summary>
    public DeckSettings Deck { get; set; } = new();

    /// <summary>
    /// Migre les anciens réglages (thème, clé en clair) et remplace les listes absentes (« null »
    /// dans un fichier modifié à la main) par des vides. Renvoie vrai si le fichier doit être réécrit.
    /// </summary>
    public bool MigrateLegacySettings()
    {
        var changed = false;
        Layout ??= new();
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
