namespace SysDeck.Services;

/// <summary>
/// Ce que chaque module affiche réellement (jauge circulaire, barre, visualiseur…). Source unique pour
/// n'afficher dans Personnaliser que les options qui ont un effet sur le module choisi.
/// </summary>
public static class ModuleCapabilities
{
    private static readonly HashSet<string> Rings = new() { "cpu", "gpu", "ram", "power", "mouse", "disk" };
    private static readonly HashSet<string> Bars = new() { "claude", "disk" };
    private static readonly HashSet<string> Spectrums = new() { "visualizer" };

    private static readonly HashSet<string> Ticked = new() { "cpu", "gpu", "ram", "power" };
    private static readonly HashSet<string> Tiled = new() { "cpu", "gpu", "ram", "power", "volumemixer", "audio" };
    private static readonly HashSet<string> Buttoned = new() { "audio", "volumemixer", "media" };

    /// <summary>Styles de jauge gérés par la jauge linéaire (les autres retombent sur « Lisse »).</summary>
    private static readonly string[] LinearStyles = { "Smooth", "Segments", "Thin", "Dots", "Neon", "Rays", "Capsule" };

    /// <summary>Disques en mode « lignes » (jauge linéaire) : les réglages d'anneau n'y ont aucun effet.</summary>
    public static bool IsLinearDisk(string? id, string? diskStyle) => id == "disk" && diskStyle == "Bar";

    /// <summary>Jauge circulaire (forme, épaisseur, remplissage animé). Disques : seulement en mode anneaux.</summary>
    public static bool HasRingGauge(string? id, string? diskStyle = null)
        => id is null || (Rings.Contains(id) && !IsLinearDisk(id, diskStyle));

    /// <summary>Style de jauge : jauge circulaire ou barre linéaire.</summary>
    public static bool HasGaugeStyle(string? id) => id is null || Rings.Contains(id) || Bars.Contains(id);

    /// <summary>Graduations : seulement CPU, GPU, mémoire et consommation.</summary>
    public static bool HasTicks(string? id, string? diskStyle = null)
        => id is null || (Ticked.Contains(id) && !IsLinearDisk(id, diskStyle));

    /// <summary>Bulles (tuiles) : modules qui en dessinent.</summary>
    public static bool HasTiles(string? id) => id is null || Tiled.Contains(id);

    /// <summary>Couleur des boutons : modules qui ont des boutons.</summary>
    public static bool HasButtons(string? id) => id is null || Buttoned.Contains(id);

    /// <summary>Styles de jauge proposés ; null = tous (jauge circulaire ou thème global).</summary>
    public static IReadOnlyCollection<string>? SupportedGaugeStyles(string? id, string? diskStyle)
        => id == "claude" || IsLinearDisk(id, diskStyle) ? LinearStyles : null;

    /// <summary>Courbe d'animation : jauges circulaires, ou apparition « Zoom » (tous les modules).</summary>
    public static bool HasEasing(string? id, string? diskStyle, string? entranceStyle)
        => HasRingGauge(id, diskStyle) || entranceStyle == "Zoom";

    /// <summary>Remplissage à l'apparition : jauges circulaires seulement (ni Claude, ni disques en lignes).</summary>
    public static bool HasIntro(string? id, string? diskStyle) => HasRingGauge(id, diskStyle);

    /// <summary>Halo lumineux : jauges et visualiseur.</summary>
    public static bool HasGlow(string? id) => HasGaugeStyle(id) || Spectrums.Contains(id!);

    /// <summary>Au moins un réglage de l'onglet « Jauges » s'applique au module.</summary>
    public static bool HasAnyGaugeOption(string? id) => HasGlow(id);
}
