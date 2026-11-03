using System.ComponentModel;
using System.Text.Json.Serialization;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck.Deck;

/// <summary>Réglages de l'écran de la Switch (payload SwitchDeck) : disposition, fond, luminosité.</summary>
public sealed class DeckSettings
{
    /// <summary>Faux : SysDeck ne cherche plus la Switch (la disposition reste modifiable).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rétroéclairage de la Switch pendant la session, 0-100 %.</summary>
    public int Brightness { get; set; } = 60;

    /// <summary>Images envoyées par seconde au maximum (seules les zones modifiées partent).</summary>
    public int Fps { get; set; } = 10;

    // ---------- Apparence propre à chaque page ----------
    // Lues et écrites sur la page affichée (DeckPage). Les valeurs « Default… » gardent les noms des anciens fichiers,
    // où ces réglages étaient communs à toutes les pages : elles sont recopiées dans chaque page à la migration.

    [JsonIgnore]
    public string BackgroundColor { get => PageOrNull?.BackgroundColor ?? DefaultLook.BackgroundColor; set => LookToEdit.BackgroundColor = value; }

    /// <summary>Image de fond de la page (copie gérée par SysDeck), null = couleur seule.</summary>
    [JsonIgnore]
    public string? BackgroundImage { get => PageOrNull is { } p ? p.BackgroundImage : DefaultLook.BackgroundImage; set => LookToEdit.BackgroundImage = value; }

    [JsonIgnore]
    public double BackgroundDim { get => PageOrNull?.BackgroundDim ?? DefaultLook.BackgroundDim; set => LookToEdit.BackgroundDim = value; }

    /// <summary>Côté des tuiles de la page, en pixels de l'écran Switch.</summary>
    [JsonIgnore]
    public double TileSize { get => PageOrNull?.TileSize ?? DefaultLook.TileSize; set => LookToEdit.TileSize = value; }

    /// <summary>
    /// Mode nuit : la Switch garde le fond et l'heure (sans la date) à luminosité minimale quand le PC est éteint ;
    /// SysDeck lui envoie pour cela un « kit nuit » à la connexion, puis à chaque changement.
    /// </summary>
    public bool NightClock { get; set; } = true;

    /// <summary>Rétroéclairage de la Switch en mode nuit, 1-100 % (la Switch ne descend jamais sous un minimum visible).</summary>
    public int NightBrightness { get; set; } = 2;

    /// <summary>Opacité du fond (cadre) de l'horloge de la page, 0 = transparent, 1 = opaque.</summary>
    [JsonIgnore]
    public double ClockOpacity { get => PageOrNull?.ClockOpacity ?? DefaultLook.ClockOpacity; set => LookToEdit.ClockOpacity = value; }

    /// <summary>Opacité du fond des tuiles de la page (pas de l'icône ni du nom), 0 = transparent, 1 = opaque.</summary>
    [JsonIgnore]
    public double AppsOpacity { get => PageOrNull?.AppsOpacity ?? DefaultLook.AppsOpacity; set => LookToEdit.AppsOpacity = value; }

    /// <summary>Apparence des tuiles de la page : à plat (thème) ou bouton en verre 3D façon Stream Deck.</summary>
    [JsonIgnore]
    public DeckTileStyle TileStyle { get => PageOrNull?.TileStyle ?? DefaultLook.TileStyle; set => LookToEdit.TileStyle = value; }

    /// <summary>Taille de l'icône dans les tuiles de la page, en part du côté (0,25 à 1). Une tuile peut la remplacer.</summary>
    [JsonIgnore]
    public double TileIconScale { get => PageOrNull?.TileIconScale ?? DefaultLook.TileIconScale; set => LookToEdit.TileIconScale = value; }

    /// <summary>Nom affiché sous l'icône dans les tuiles de la page (une tuile peut le masquer seule).</summary>
    [JsonIgnore]
    public bool ShowTileNames { get => PageOrNull?.ShowTileNames ?? DefaultLook.ShowTileNames; set => LookToEdit.ShowTileNames = value; }

    /// <summary>Anciens réglages communs à toutes les pages (mêmes noms dans le fichier) : modèle des pages qui n'ont pas encore les leurs.</summary>
    [JsonPropertyName("BackgroundColor")] public string DefaultBackgroundColor { get => DefaultLook.BackgroundColor; set => DefaultLook.BackgroundColor = value; }
    [JsonPropertyName("BackgroundImage")] public string? DefaultBackgroundImage { get => DefaultLook.BackgroundImage; set => DefaultLook.BackgroundImage = value; }
    [JsonPropertyName("BackgroundDim")] public double DefaultBackgroundDim { get => DefaultLook.BackgroundDim; set => DefaultLook.BackgroundDim = value; }
    [JsonPropertyName("TileSize")] public double DefaultTileSize { get => DefaultLook.TileSize; set => DefaultLook.TileSize = value; }
    [JsonPropertyName("ClockOpacity")] public double DefaultClockOpacity { get => DefaultLook.ClockOpacity; set => DefaultLook.ClockOpacity = value; }
    [JsonPropertyName("AppsOpacity")] public double DefaultAppsOpacity { get => DefaultLook.AppsOpacity; set => DefaultLook.AppsOpacity = value; }
    [JsonPropertyName("TileStyle")] public DeckTileStyle DefaultTileStyle { get => DefaultLook.TileStyle; set => DefaultLook.TileStyle = value; }
    [JsonPropertyName("TileIconScale")] public double DefaultTileIconScale { get => DefaultLook.TileIconScale; set => DefaultLook.TileIconScale = value; }
    [JsonPropertyName("ShowTileNames")] public bool DefaultShowTileNames { get => DefaultLook.ShowTileNames; set => DefaultLook.ShowTileNames = value; }

    private readonly DeckPage DefaultLook = new();

    private DeckPage? PageOrNull => Pages is { Count: > 0 } ? Page : null;

    /// <summary>Page dont on modifie l'apparence : celle affichée (le modèle tant qu'il n'y a aucune page, à la lecture du fichier).</summary>
    private DeckPage LookToEdit => PageOrNull ?? DefaultLook;

    /// <summary>Pages de l'écran, chacune avec ses éléments ; on passe de l'une à l'autre en glissant sur la Switch.</summary>
    public List<DeckPage> Pages { get; set; } = new();

    /// <summary>Page affichée (et modifiée dans les Paramètres).</summary>
    public int CurrentPage { get; set; }

    [JsonIgnore]
    public DeckPage Page => Pages[Math.Clamp(CurrentPage, 0, Pages.Count - 1)];

    /// <summary>Éléments de la page affichée (1280x720), dans l'ordre d'affichage.</summary>
    [JsonIgnore]
    public List<DeckElement> Elements => Page.Elements;

    /// <summary>Vrai si aucune disposition n'existait encore (premier lancement) : la page reçoit les éléments par défaut.</summary>
    [JsonIgnore]
    public bool IsNew { get; private set; }

    // Ancienne disposition à une seule page : lue une fois pour devenir la première page, jamais réécrite.
    [JsonPropertyName("Elements"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DeckElement>? LegacyElements { get; set; }

    /// <summary>
    /// Apparence des modules sur la Switch, indépendante de celle du bureau (même réglages : style,
    /// textes, fond, position des éléments). Créée à la demande en copiant celle du bureau.
    /// </summary>
    public Dictionary<string, ModuleAppearance> Modules { get; set; } = new();

    public DeckElement? Find(string id) => Elements.FirstOrDefault(e => e.Id == id);

    /// <summary>Remplace les listes absentes et convertit l'ancienne disposition à une page.</summary>
    public void Migrate()
    {
        Pages ??= new();
        Modules ??= new();
        if (Pages.Count == 0)
        {
            IsNew = LegacyElements is null;
            Pages.Add(new DeckPage { Name = "Page 1", Elements = LegacyElements ?? new() });
        }
        LegacyElements = null;
        foreach (var page in Pages)
        {
            page.Elements ??= new();
            page.Apps ??= new();
            // La barre de raccourcis du PC n'est plus affichée sur la Switch : seules restent les tuiles de chaque page.
            page.Elements.RemoveAll(e => e.Id == "shortcuts");
            // Ancien fichier : l'apparence commune devient celle de chaque page.
            if (!page.OwnLook)
            {
                page.CopyLookFrom(DefaultLook);
                page.OwnLook = true;
            }
            page.ClampLook();
        }
        CurrentPage = Math.Clamp(CurrentPage, 0, Pages.Count - 1);
        // Fichier édité à la main : une valeur hors plage ne doit pas arriver telle quelle à la Switch.
        Brightness = Math.Clamp(Brightness, 0, 100);
        NightBrightness = Math.Clamp(NightBrightness, 1, 100);
    }

    /// <summary>Donne à toutes les pages l'apparence de la page affichée.</summary>
    public void ApplyLookToAllPages()
    {
        foreach (var page in Pages.Where(p => p != Page)) page.CopyLookFrom(Page);
    }

    public const double MinIconScale = 0.25, MaxIconScale = 1;

    /// <summary>Taille d'icône ramenée dans la plage permise ; null si absente ou invalide.</summary>
    public static double? ClampIconScale(double? scale)
        => scale is { } s && !double.IsNaN(s) ? Math.Clamp(s, MinIconScale, MaxIconScale) : null;
}

/// <summary>Apparence des tuiles de la Switch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeckTileStyle { Flat, Glass }

/// <summary>Une page de l'écran Switch.</summary>
public sealed class DeckPage : INotifyPropertyChanged
{
    private string _name = "Page";

    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
    }

    public List<DeckElement> Elements { get; set; } = new();

    /// <summary>Applications de la page (élément « apps »), indépendantes de la barre de raccourcis.</summary>
    public List<ShortcutItem> Apps { get; set; } = new();

    // ---------- Apparence de la page ----------

    /// <summary>Faux dans les anciens fichiers : la page reçoit alors l'apparence commune d'avant (voir <see cref="DeckSettings.Migrate"/>).</summary>
    public bool OwnLook { get; set; }

    public double TileSize { get; set; } = 120;
    public DeckTileStyle TileStyle { get; set; } = DeckTileStyle.Glass;
    public double TileIconScale { get; set; } = 0.5;
    public bool ShowTileNames { get; set; } = true;
    public double AppsOpacity { get; set; } = 1;
    public string BackgroundColor { get; set; } = "#0B0C10";
    public string? BackgroundImage { get; set; }
    public double BackgroundDim { get; set; } = 0.35;
    public double ClockOpacity { get; set; } = 1;

    /// <summary>Reprend l'apparence (tuiles, fond, horloge) d'une autre page ; ni le nom, ni les éléments, ni les tuiles.</summary>
    public void CopyLookFrom(DeckPage other)
    {
        TileSize = other.TileSize;
        TileStyle = other.TileStyle;
        TileIconScale = other.TileIconScale;
        ShowTileNames = other.ShowTileNames;
        AppsOpacity = other.AppsOpacity;
        BackgroundColor = other.BackgroundColor;
        BackgroundImage = other.BackgroundImage;
        BackgroundDim = other.BackgroundDim;
        ClockOpacity = other.ClockOpacity;
    }

    /// <summary>Valeurs hors plage (fichier modifié à la main) ramenées dans les bornes.</summary>
    public void ClampLook()
    {
        TileSize = double.IsNaN(TileSize) ? 120 : Math.Clamp(TileSize, 60, 300);
        if (!Enum.IsDefined(TileStyle)) TileStyle = DeckTileStyle.Glass;
        TileIconScale = DeckSettings.ClampIconScale(TileIconScale) ?? 0.5;
        AppsOpacity = double.IsNaN(AppsOpacity) ? 1 : Math.Clamp(AppsOpacity, 0, 1);
        BackgroundDim = double.IsNaN(BackgroundDim) ? 0.35 : Math.Clamp(BackgroundDim, 0, 0.9);
        ClockOpacity = double.IsNaN(ClockOpacity) ? 1 : Math.Clamp(ClockOpacity, 0, 1);
        if (string.IsNullOrWhiteSpace(BackgroundColor)) BackgroundColor = "#0B0C10";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Un élément de l'écran Switch : module, horloge ou grille de raccourcis, avec son cadre.</summary>
public sealed class DeckElement
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 180;
    public bool Visible { get; set; } = true;
    /// <summary>
    /// Échelle du contenu d'un module en taille automatique : le cadre suit ses éléments à cette échelle.
    /// null = à recalculer depuis le cadre actuel (première fois, ou cadre redimensionné dans l'éditeur).
    /// </summary>
    public double? Scale { get; set; }
}
