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

    public string BackgroundColor { get; set; } = "#0B0C10";

    /// <summary>Image de fond (copie gérée par SysDeck), null = couleur seule.</summary>
    public string? BackgroundImage { get; set; }

    public double BackgroundDim { get; set; } = 0.35;

    /// <summary>Côté des tuiles de raccourcis, en pixels de l'écran Switch.</summary>
    public double TileSize { get; set; } = 120;

    /// <summary>
    /// Mode nuit : la Switch garde le fond et l'heure (sans la date) à luminosité minimale quand le PC est éteint ;
    /// SysDeck lui envoie pour cela un « kit nuit » à la connexion, puis à chaque changement.
    /// </summary>
    public bool NightClock { get; set; } = true;

    /// <summary>Rétroéclairage de la Switch en mode nuit, 1-100 % (la Switch ne descend jamais sous un minimum visible).</summary>
    public int NightBrightness { get; set; } = 2;

    /// <summary>Opacité du fond (cadre) de l'horloge, 0 = transparent, 1 = opaque.</summary>
    public double ClockOpacity { get; set; } = 1;

    /// <summary>Opacité du fond des tuiles d'applications (pas de l'icône ni du nom), 0 = transparent, 1 = opaque.</summary>
    public double AppsOpacity { get; set; } = 1;

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
        }
        CurrentPage = Math.Clamp(CurrentPage, 0, Pages.Count - 1);
        // Fichier édité à la main : une valeur hors plage ne doit pas arriver telle quelle à la Switch.
        Brightness = Math.Clamp(Brightness, 0, 100);
        NightBrightness = Math.Clamp(NightBrightness, 1, 100);
        AppsOpacity = double.IsNaN(AppsOpacity) ? 1 : Math.Clamp(AppsOpacity, 0, 1);
    }
}

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
