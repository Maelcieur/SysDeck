using System.Windows;
using System.Windows.Media;

namespace SysDeck.Controls;

/// <summary>
/// Petits outils de dessin partagés par les jauges, le sparkline, le volume et le visualiseur
/// (ils étaient recopiés dans chacun). Tout ce qui est renvoyé est figé : prêt à être réutilisé
/// sans surcoût de rendu.
/// </summary>
internal static class DrawingHelpers
{
    /// <summary>Fige un pinceau, une plume ou une géométrie et le renvoie (pratique en expression).</summary>
    public static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Interpolation linéaire entre deux couleurs (k borné à 0–1) ; le résultat est opaque.</summary>
    public static Color Lerp(Color a, Color b, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * k),
            (byte)(a.G + (b.G - a.G) * k),
            (byte)(a.B + (b.B - a.B) * k));
    }

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Point OnCircle(double cx, double cy, double r, double deg) =>
        new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));

    /// <summary>Arc de cercle (sens horaire, angles en degrés) ; un tour complet donne un cercle.</summary>
    public static Geometry Arc(double cx, double cy, double r, double fromDeg, double toDeg)
    {
        if (toDeg - fromDeg >= 359.9)
            return Frozen(new EllipseGeometry(new Point(cx, cy), r, r));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(cx, cy, r, fromDeg), false, false);
            ctx.ArcTo(OnCircle(cx, cy, r, toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Secteur plein (part de camembert) depuis le centre.</summary>
    public static Geometry Sector(double cx, double cy, double r, double fromDeg, double toDeg)
    {
        if (toDeg - fromDeg >= 359.9)
            return Frozen(new EllipseGeometry(new Point(cx, cy), r, r));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(cx, cy), true, true);
            ctx.LineTo(OnCircle(cx, cy, r, fromDeg), false, false);
            ctx.ArcTo(OnCircle(cx, cy, r, toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, false, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
