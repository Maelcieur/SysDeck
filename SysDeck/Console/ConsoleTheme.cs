using System.Windows.Media;

namespace SysDeck.ConsoleMode;

/// <summary>Décor animé derrière l'interface TV.</summary>
public enum ConsoleAmbient { None, Stars, Bubbles, Waves, Cubes, Ribbons, Particles, Grid, Ripples, Aurora, Snow, Fireflies, Petals, Rain, Hexagons, Scanlines }

/// <summary>
/// Thème de l'interface TV. Visuels ORIGINAUX inspirés d'ambiances d'époques (couleurs, formes) :
/// aucun logo, nom ni élément graphique de marque.
/// </summary>
public sealed record ConsoleTheme(
    string Id, string Name, string Era,
    Color Bg, Color Bg2, Color Accent, Color OnAccent, Color Text, Color SubText, Color Panel,
    ConsoleAmbient Ambient, double Corner, string Font = "Segoe UI", bool Light = false, bool Glitch = false)
{
    public SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    public Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

public static class ConsoleThemes
{
    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static readonly IReadOnlyList<ConsoleTheme> All = new[]
    {
        new ConsoleTheme("nuit", "Nuit bleue", "Par défaut",
            C(0xFF07090E), C(0xFF111A2E), C(0xFF5B8DEF), C(0xFFFFFFFF), C(0xFFFFFFFF), C(0xFF9AA3B5), C(0xFF1A1F2B),
            ConsoleAmbient.Stars, 14),
        new ConsoleTheme("gris-32", "Gris 32 bits", "Années 90, consoles à CD",
            C(0xFF16161A), C(0xFF3A3A42), C(0xFFD8D8DE), C(0xFF16161A), C(0xFFF2F2F5), C(0xFFA0A0AA), C(0xFF2A2A30),
            ConsoleAmbient.Cubes, 4, "Arial"),
        new ConsoleTheme("bleu-profond", "Bleu profond", "Années 2000, tours de lumière",
            C(0xFF02040C), C(0xFF0A1C4A), C(0xFF3D7BFF), C(0xFFFFFFFF), C(0xFFE8EEFF), C(0xFF8EA2CC), C(0xFF0E1A36),
            ConsoleAmbient.Waves, 8),
        new ConsoleTheme("cube-violet", "Cube violet", "Années 2000, petit cube",
            C(0xFF120C2E), C(0xFF35267A), C(0xFF9C8CFF), C(0xFF120C2E), C(0xFFFFFFFF), C(0xFFB4AEDD), C(0xFF241A55),
            ConsoleAmbient.Cubes, 16),
        new ConsoleTheme("neon-vert", "Vert néon", "Années 2000, noir et vert",
            C(0xFF050805), C(0xFF0F2410), C(0xFF3DDC5A), C(0xFF041004), C(0xFFFFFFFF), C(0xFF9DB89F), C(0xFF132315),
            ConsoleAmbient.Bubbles, 6),
        new ConsoleTheme("orbe", "Orbe argent", "Fin des années 2000, rubans",
            C(0xFFE6EBEF), C(0xFFFFFFFF), C(0xFF5FB53A), C(0xFFFFFFFF), C(0xFF1F2A22), C(0xFF5D6B61), C(0xFFD3DAD5),
            ConsoleAmbient.Ribbons, 10, "Segoe UI", Light: true),
        new ConsoleTheme("nuage", "Nuage", "Fin des années 2000, salon familial",
            C(0xFFF1F4F7), C(0xFFDCE6EE), C(0xFF2FA8E0), C(0xFFFFFFFF), C(0xFF2B3440), C(0xFF6B7785), C(0xFFFFFFFF),
            ConsoleAmbient.Ripples, 26, "Segoe UI", Light: true),
        new ConsoleTheme("rouge-neon", "Rouge néon", "Années 2010, hybride",
            C(0xFF111214), C(0xFF2A0E12), C(0xFFFF3B45), C(0xFFFFFFFF), C(0xFFFFFFFF), C(0xFFA5A8AF), C(0xFF1F2024),
            ConsoleAmbient.Particles, 18),
        new ConsoleTheme("minimal", "Blanc minimal", "Années 2020, épuré",
            C(0xFF0A0B10), C(0xFF1B1F2E), C(0xFFFFFFFF), C(0xFF0A0B10), C(0xFFFFFFFF), C(0xFF9EA4B4), C(0xFF181B25),
            ConsoleAmbient.Particles, 10),
        new ConsoleTheme("retro", "Rétro 16 bits", "Années 80-90, cartouches",
            C(0xFF1A1428), C(0xFF3B2B5E), C(0xFFFFC93C), C(0xFF1A1428), C(0xFFFFFFFF), C(0xFFB9AED6), C(0xFF2A2140),
            ConsoleAmbient.Grid, 2, "Consolas"),
        new ConsoleTheme("aurore", "Aurore boréale", "Nuit polaire",
            C(0xFF040A12), C(0xFF0A2B36), C(0xFF3CF0C0), C(0xFF04120E), C(0xFFFFFFFF), C(0xFF93B9B4), C(0xFF0D1F26),
            ConsoleAmbient.Aurora, 16),
        new ConsoleTheme("ocean", "Océan", "Grand bleu",
            C(0xFF021018), C(0xFF06344A), C(0xFF2EC4F1), C(0xFF021018), C(0xFFFFFFFF), C(0xFF8DB3C4), C(0xFF08263A),
            ConsoleAmbient.Bubbles, 18),
        new ConsoleTheme("foret", "Forêt", "Sous-bois la nuit",
            C(0xFF07120A), C(0xFF173A1E), C(0xFF9BD45A), C(0xFF07120A), C(0xFFF2F7EC), C(0xFF9DB497), C(0xFF12261A),
            ConsoleAmbient.Fireflies, 12),
        new ConsoleTheme("neige", "Neige", "Matin d'hiver",
            C(0xFFE8F0F6), C(0xFFFFFFFF), C(0xFF3B7DD8), C(0xFFFFFFFF), C(0xFF1E2A38), C(0xFF5E6E80), C(0xFFFFFFFF),
            ConsoleAmbient.Snow, 20, "Segoe UI", Light: true),
        new ConsoleTheme("sakura", "Sakura", "Printemps",
            C(0xFFFFF0F4), C(0xFFFFD6E3), C(0xFFE2557F), C(0xFFFFFFFF), C(0xFF3A2430), C(0xFF7E6070), C(0xFFFFFFFF),
            ConsoleAmbient.Petals, 22, "Segoe UI", Light: true),
        new ConsoleTheme("coucher", "Coucher de soleil", "Fin de journée",
            C(0xFF1A0B1E), C(0xFF5A1F3A), C(0xFFFF8A4C), C(0xFF1A0B1E), C(0xFFFFF4EC), C(0xFFC9A6A6), C(0xFF2A1430),
            ConsoleAmbient.Ribbons, 16),
        new ConsoleTheme("lave", "Lave", "Volcan",
            C(0xFF120504), C(0xFF4A1206), C(0xFFFF5A1F), C(0xFF120504), C(0xFFFFF0E8), C(0xFFC79C8C), C(0xFF2A0E08),
            ConsoleAmbient.Particles, 8),
        new ConsoleTheme("cyber", "Cyber", "Ville néon",
            C(0xFF07051A), C(0xFF241046), C(0xFF00E5FF), C(0xFF07051A), C(0xFFFFFFFF), C(0xFFA79CC9), C(0xFF150C2E),
            ConsoleAmbient.Rain, 4, "Consolas"),
        new ConsoleTheme("or", "Noir et or", "Édition luxe",
            C(0xFF0A0907), C(0xFF2A2312), C(0xFFE8C26A), C(0xFF0A0907), C(0xFFF7F0DE), C(0xFFB3A585), C(0xFF1A160D),
            ConsoleAmbient.Hexagons, 6),
        new ConsoleTheme("galaxie", "Galaxie", "Espace profond",
            C(0xFF03030A), C(0xFF1B1440), C(0xFFA98BFF), C(0xFF03030A), C(0xFFFFFFFF), C(0xFF9F98C2), C(0xFF120F2A),
            ConsoleAmbient.Stars, 20),
        new ConsoleTheme("glitch", "Néon glitch", "Futur dystopique",
            C(0xFF0A0514), C(0xFF2A0838), C(0xFFFF2A6D), C(0xFFFFFFFF), C(0xFFE8F6FF), C(0xFF8FC9D9), C(0xFF160A24),
            ConsoleAmbient.Scanlines, 2, "Consolas", Glitch: true),
    };

    public static ConsoleTheme Default => All[0];

    public static ConsoleTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;

    public static int IndexOf(string? id) => Math.Max(0, All.ToList().FindIndex(t => t.Id == id));
}
