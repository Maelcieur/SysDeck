namespace SysDeck.Services;

/// <summary>
/// Phase de la Lune calculée sans connexion : âge depuis une nouvelle lune moyenne de référence (janvier 2000) sur le
/// mois synodique, corrigé des principaux termes périodiques (Meeus), ce qui place les phases à quelques heures près.
/// </summary>
public static class MoonPhase
{
    public const double SynodicMonth = 29.530588853;
    // Nouvelle lune moyenne de référence (Meeus : JDE 2451550,09766 = 6 janvier 2000, 14 h 20).
    private static readonly DateTime Reference = new(2000, 1, 6, 14, 20, 40, DateTimeKind.Utc);

    public static readonly string[] Names =
    {
        "Nouvelle lune", "Premier croissant", "Premier quartier", "Gibbeuse croissante",
        "Pleine lune", "Gibbeuse décroissante", "Dernier quartier", "Dernier croissant"
    };

    /// <summary>Âge de la Lune en jours (0 = nouvelle lune, ~14,8 = pleine lune).</summary>
    public static double Age(DateTime utc)
    {
        var days = (utc.ToUniversalTime() - Reference).TotalDays;
        var e = 1 - 0.002516 * days / 36525.0;
        // Anomalies moyennes du Soleil (m) et de la Lune (mp), argument de latitude (f), en radians.
        var m = Rad(2.5534 + 0.98560028 * days);
        var mp = Rad(201.5643 + 13.06499295 * days);
        var f = Rad(160.7108 + 13.22935024 * days);
        // Écart (jours) entre la nouvelle lune vraie et la moyenne : termes principaux de Meeus.
        var correction = -0.40720 * Math.Sin(mp) + 0.17241 * e * Math.Sin(m) + 0.01608 * Math.Sin(2 * mp)
                         + 0.01039 * Math.Sin(2 * f) + 0.00739 * e * Math.Sin(mp - m) - 0.00514 * e * Math.Sin(mp + m);
        var age = (days - correction) % SynodicMonth;
        return age < 0 ? age + SynodicMonth : age;
    }

    /// <summary>Fraction du disque éclairée (0 à 1).</summary>
    public static double Illumination(double age) => (1 - Math.Cos(2 * Math.PI * age / SynodicMonth)) / 2;

    /// <summary>Croissante (de la nouvelle à la pleine lune).</summary>
    public static bool Waxing(double age) => age < SynodicMonth / 2;

    /// <summary>Indice 0-7 dans <see cref="Names"/> : les quatre phases principales durent environ un jour chacune.</summary>
    public static int Index(double age)
    {
        var quarter = SynodicMonth / 4;
        // Proche d'une phase principale (± ~1 jour) : nouvelle lune, premier quartier, pleine lune, dernier quartier.
        for (var i = 0; i < 4; i++)
        {
            var target = i * quarter;
            var distance = Math.Abs(age - target);
            distance = Math.Min(distance, SynodicMonth - distance);
            if (distance < 1.0) return i * 2;
        }
        return (int)(age / quarter) * 2 + 1;
    }

    public static string Name(double age) => Names[Index(age)];

    /// <summary>Prochaine date (heure locale) où la Lune atteint la fraction de cycle voulue (0 nouvelle, 0,5 pleine…).</summary>
    public static DateTime Next(DateTime now, double cycleFraction)
    {
        var target = cycleFraction * SynodicMonth;
        var age = Age(now);
        var wait = (target - age) % SynodicMonth;
        if (wait < 0.05) wait += SynodicMonth;
        // Affinage : l'âge corrigé ne varie pas exactement d'un jour par jour.
        var date = now.AddDays(wait);
        for (var i = 0; i < 3; i++)
        {
            var delta = (target - Age(date) + SynodicMonth * 1.5) % SynodicMonth - SynodicMonth / 2;
            date = date.AddDays(delta);
        }
        return date.ToLocalTime();
    }

    private static double Rad(double degrees) => degrees % 360 * Math.PI / 180;
}
