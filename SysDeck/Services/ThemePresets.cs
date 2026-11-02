namespace SysDeck.Services;

/// <summary>
/// Thème prêt à l'emploi : un nom, une description courte et une fabrique de ThemeSettings.
/// La fabrique renvoie une instance neuve à chaque appel (les valeurs non précisées sont celles par défaut de
/// ThemeSettings, y compris pour les propriétés ajoutées plus tard) : le thème appliqué n'est jamais partagé.
/// </summary>
public sealed record ThemePreset(string Name, string Description, Func<ThemeSettings> CreateTheme)
{
    /// <summary>Couleurs du dégradé (début, fin) et couleur de texte, pour dessiner un aperçu dans l'interface.</summary>
    public (string Start, string End, string Panel, string Text) Swatch
    {
        get
        {
            var t = CreateTheme();
            return (t.GaugeStartColor, t.GaugeEndColor, t.PanelColor, t.TextColor);
        }
    }
}

/// <summary>
/// Les 8 thèmes prêts à l'emploi. Polices : uniquement celles livrées avec Windows (Segoe UI, Bahnschrift,
/// Consolas). Chaque thème garde un contraste texte / fond d'au moins 7:1 pour le texte principal et 4,5:1
/// pour le texte secondaire, sauf « Minimal clair » (fond clair, textes sombres).
/// </summary>
public static class ThemePresets
{
    public static IReadOnlyList<ThemePreset> All { get; } = new[]
    {
        new ThemePreset("Néon cyan", "Le thème d'origine : cyan et violet électriques sur fond ardoise.", () => new ThemeSettings
        {
            GaugeStartColor = "#FF00E0FF", GaugeEndColor = "#FF8B5CFF",
            TextColor = "#FFE8E9EE", SubTextColor = "#FF9A9DAC",
            PanelColor = "#FF232530", PanelOpacity = 0.88,
            BorderColor = "#FFFFFFFF", BorderOpacity = 0.2, CornerRadius = 18,
            ShowTiles = true, TileColor = "#FFFFFFFF", TileOpacity = 0.08, ButtonColor = "#22FFFFFF",
            TextFont = "Segoe UI", NumberFont = "Bahnschrift SemiBold",
            GaugeShape = "Arc", GaugeStyle = "Smooth", GaugeThickness = 1, ShowTicks = true, TickStyle = "Lines", Glow = true
        }),

        new ThemePreset("Synthwave", "Rose néon et violet profond, façon années 80.", () => new ThemeSettings
        {
            GaugeStartColor = "#FFFF2BD6", GaugeEndColor = "#FF7B5CFF",
            TextColor = "#FFF7EDFF", SubTextColor = "#FFC4B0E0",
            PanelColor = "#FF1C1033", PanelOpacity = 0.9,
            BorderColor = "#FFFF2BD6", BorderOpacity = 0.5, CornerRadius = 14,
            ShowTiles = true, TileColor = "#FFB44CFF", TileOpacity = 0.14, ButtonColor = "#33FF2BD6",
            TextFont = "Segoe UI Semibold", NumberFont = "Bahnschrift SemiBold",
            GaugeShape = "Arc", GaugeStyle = "Segments", GaugeThickness = 1.1, ShowTicks = true, TickStyle = "Dense", Glow = true
        }),

        new ThemePreset("Rouge gaming", "Rouge et orange agressifs sur fond presque noir, angles francs.", () => new ThemeSettings
        {
            GaugeStartColor = "#FFFF2D4D", GaugeEndColor = "#FFFF8A1F",
            TextColor = "#FFF4EEEE", SubTextColor = "#FFBBA6A6",
            PanelColor = "#FF1A1011", PanelOpacity = 0.92,
            BorderColor = "#FFFF2D4D", BorderOpacity = 0.45, CornerRadius = 6,
            ShowTiles = true, TileColor = "#FFFF2D4D", TileOpacity = 0.09, ButtonColor = "#33FF2D4D",
            TextFont = "Bahnschrift", NumberFont = "Bahnschrift SemiBold",
            GaugeShape = "Arc", GaugeStyle = "Needle", GaugeThickness = 1, ShowTicks = true, TickStyle = "Majors", Glow = true
        }),

        new ThemePreset("Vert matrice", "Vert terminal sur noir, police à chasse fixe.", () => new ThemeSettings
        {
            GaugeStartColor = "#FF00FF70", GaugeEndColor = "#FF00B84A",
            TextColor = "#FFD8FFE6", SubTextColor = "#FF86C79B",
            PanelColor = "#FF07130B", PanelOpacity = 0.92,
            BorderColor = "#FF00FF70", BorderOpacity = 0.4, CornerRadius = 4,
            ShowTiles = true, TileColor = "#FF00FF70", TileOpacity = 0.07, ButtonColor = "#3300FF70",
            TextFont = "Consolas", NumberFont = "Consolas",
            GaugeShape = "Arc", GaugeStyle = "Dots", GaugeThickness = 1, ShowTicks = true, TickStyle = "Dots", Glow = true
        }),

        new ThemePreset("Or et noir", "Noir mat et or champagne, sobre et luxueux.", () => new ThemeSettings
        {
            GaugeStartColor = "#FFFFD966", GaugeEndColor = "#FFD49A00",
            TextColor = "#FFF7EFD9", SubTextColor = "#FFBBAA80",
            PanelColor = "#FF121212", PanelOpacity = 0.94,
            BorderColor = "#FFFFD966", BorderOpacity = 0.4, CornerRadius = 12,
            ShowTiles = true, TileColor = "#FFFFD966", TileOpacity = 0.07, ButtonColor = "#33FFD966",
            TextFont = "Segoe UI Semibold", NumberFont = "Bahnschrift SemiBold",
            GaugeShape = "Arc", GaugeStyle = "Thin", GaugeThickness = 0.9, ShowTicks = true, TickStyle = "Majors", Glow = false
        }),

        new ThemePreset("Glace", "Bleus glacés et blanc, ambiance froide et douce.", () => new ThemeSettings
        {
            GaugeStartColor = "#FFA6EEFF", GaugeEndColor = "#FF4DA3FF",
            TextColor = "#FFEBF7FF", SubTextColor = "#FFA6BFD3",
            PanelColor = "#FF14212F", PanelOpacity = 0.86,
            BorderColor = "#FFC8EEFF", BorderOpacity = 0.3, CornerRadius = 22,
            ShowTiles = true, TileColor = "#FFFFFFFF", TileOpacity = 0.09, ButtonColor = "#26C8EEFF",
            TextFont = "Segoe UI", NumberFont = "Bahnschrift SemiLight",
            GaugeShape = "Arc", GaugeStyle = "Smooth", GaugeThickness = 1, ShowTicks = false, TickStyle = "Lines", Glow = true
        }),

        new ThemePreset("Minimal clair", "Fond clair, textes sombres, jauges fines : lisible en plein jour.", () => new ThemeSettings
        {
            GaugeStartColor = "#FF2563EB", GaugeEndColor = "#FF0891B2",
            TextColor = "#FF1B1F2A", SubTextColor = "#FF566074",
            PanelColor = "#FFF4F6FA", PanelOpacity = 0.95,
            BorderColor = "#FF1B1F2A", BorderOpacity = 0.14, CornerRadius = 16,
            ShowTiles = true, TileColor = "#FF1B1F2A", TileOpacity = 0.05, ButtonColor = "#1A000000",
            TextFont = "Segoe UI", NumberFont = "Segoe UI Semibold",
            GaugeShape = "Arc", GaugeStyle = "Thin", GaugeThickness = 0.9, ShowTicks = false, TickStyle = "Lines", Glow = false
        }),

        new ThemePreset("Coucher de soleil", "Dégradé orange et rose sur fond prune.", () => new ThemeSettings
        {
            GaugeStartColor = "#FFFFA03C", GaugeEndColor = "#FFFF4D8D",
            TextColor = "#FFFFF1E8", SubTextColor = "#FFDDB09A",
            PanelColor = "#FF2A1420", PanelOpacity = 0.9,
            BorderColor = "#FFFFA03C", BorderOpacity = 0.35, CornerRadius = 20,
            ShowTiles = true, TileColor = "#FFFFA03C", TileOpacity = 0.09, ButtonColor = "#33FFA03C",
            TextFont = "Segoe UI", NumberFont = "Bahnschrift SemiBold",
            GaugeShape = "Arc", GaugeStyle = "Smooth", GaugeThickness = 1.1, ShowTicks = true, TickStyle = "Lines", Glow = true
        }),
    };

    /// <summary>Retrouve un thème par son nom (sans tenir compte de la casse), ou null.</summary>
    public static ThemePreset? Find(string name)
        => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
}
