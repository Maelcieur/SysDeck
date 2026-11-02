using System.Diagnostics.Eventing.Reader;
using Microsoft.Win32;

namespace SysDeck.Services;

/// <summary>Temps passé allumé (hors veille) d'après le journal Système de Windows, sur la période qu'il couvre encore.</summary>
/// <remarks>
/// <see cref="Periods"/> garde chaque période d'allumage pour pouvoir répartir les heures entre jour
/// et soir quel que soit le réglage ; absent dans les fichiers d'avant cette répartition (relu alors).
/// </remarks>
public sealed record PowerOnLog(DateTime FromUtc, DateTime UntilUtc, double OnSeconds, List<OnPeriod>? Periods = null);

public sealed record OnPeriod(DateTime FromUtc, DateTime UntilUtc);

/// <summary>
/// Reconstitue les périodes d'allumage à partir du journal Système (lisible sans droits admin) :
/// démarrage (Kernel-General 12), arrêt (Kernel-General 13), mise en veille (Kernel-Power 42),
/// réveil (Power-Troubleshooter 1). Le journal est circulaire (20 Mo par défaut) :
/// il ne couvre que les derniers mois.
/// </summary>
public static class PowerOnHistory
{
    private enum Kind { Other, On, Off }

    public static PowerOnLog? ReadLog(DateTime untilUtc)
    {
        try
        {
            // Début de la période couverte : tout premier événement du journal (lecture d'un seul enregistrement).
            DateTime? fromUtc = null;
            using (var first = new EventLogReader(new EventLogQuery("System", PathType.LogName, "*")))
            using (var firstRecord = first.ReadEvent())
                fromUtc = firstRecord?.TimeCreated?.ToUniversalTime();
            if (fromUtc is null || fromUtc > untilUtc) return null;

            // Seuls les quatre événements utiles, jusqu'à la date voulue : le journal compte des dizaines de milliers d'entrées.
            var until = untilUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var query = "*[System[((Provider[@Name='Microsoft-Windows-Kernel-General'] and (EventID=12 or EventID=13))"
                        + " or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=42)"
                        + " or (Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1))"
                        + $" and TimeCreated[@SystemTime<='{until}']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));

            // Le journal commence en pleine session : on considère le PC allumé dès le premier événement.
            DateTime? startUtc = fromUtc, lastSeenUtc = null;
            var periods = new List<OnPeriod>();

            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.TimeCreated?.ToUniversalTime() is not { } t) continue;
                    if (t > untilUtc) break;

                    switch (Classify(record.ProviderName, record.Id))
                    {
                        case Kind.On:
                            // Allumage sans arrêt enregistré avant (coupure de courant, plantage) :
                            // la session précédente a pris fin au dernier événement écrit.
                            if (startUtc is { } s && lastSeenUtc is { } seen) periods.Add(new OnPeriod(s, seen));
                            startUtc = t;
                            break;
                        case Kind.Off:
                            if (startUtc is { } begin) periods.Add(new OnPeriod(begin, t));
                            startUtc = null;
                            break;
                    }
                    lastSeenUtc = t;
                }
            }

            if (startUtc is { } open) periods.Add(new OnPeriod(open, untilUtc));
            periods.RemoveAll(p => p.UntilUtc <= p.FromUtc);
            return new PowerOnLog(fromUtc.Value, untilUtc, periods.Sum(p => (p.UntilUtc - p.FromUtc).TotalSeconds), periods);
        }
        catch
        {
            return null; // journal inaccessible (stratégie de groupe…) : seule l'estimation reste possible
        }
    }

    /// <summary>
    /// Répartit une période (heures locales) entre « jour » [dayStart ; dayEnd[ et « soir » (le reste),
    /// en la découpant à chaque changement de tranche.
    /// </summary>
    public static (double DaySeconds, double NightSeconds) SplitDayNight(DateTime fromLocal, DateTime untilLocal, double dayStartHour, double dayEndHour)
    {
        double day = 0, night = 0;
        var t = fromLocal;
        while (t < untilLocal)
        {
            var hour = t.TimeOfDay.TotalHours;
            var isDay = IsDayHour(hour, dayStartHour, dayEndHour);
            // Prochaine frontière : début ou fin du jour, aujourd'hui ou demain.
            var next = new[] { dayStartHour, dayEndHour, dayStartHour + 24, dayEndHour + 24 }
                .Select(h => t.Date.AddHours(h))
                .Where(b => b > t)
                .Min();
            var end = next < untilLocal ? next : untilLocal;
            if (isDay) day += (end - t).TotalSeconds; else night += (end - t).TotalSeconds;
            t = end;
        }
        return (day, night);
    }

    public static bool IsDayHour(double hour, double dayStartHour, double dayEndHour)
    {
        if (dayStartHour == dayEndHour) return true;
        return dayStartHour < dayEndHour
            ? hour >= dayStartHour && hour < dayEndHour
            : hour >= dayStartHour || hour < dayEndHour;
    }

    private static Kind Classify(string? provider, int id) => (provider, id) switch
    {
        // Pas Kernel-Power 107 : il porte l'heure de la mise en veille (2 s après le 42), pas celle du réveil.
        ("Microsoft-Windows-Kernel-General", 12) => Kind.On,
        ("Microsoft-Windows-Power-Troubleshooter", 1) => Kind.On,
        ("Microsoft-Windows-Kernel-General", 13) => Kind.Off,
        ("Microsoft-Windows-Kernel-Power", 42) => Kind.Off,
        _ => Kind.Other
    };

    /// <summary>
    /// Plus ancienne date d'installation de Windows connue : les mises à jour majeures réécrivent
    /// InstallDate, mais gardent l'ancienne dans HKLM\SYSTEM\Setup\Source OS (…).
    /// </summary>
    public static DateTime? WindowsInstallDate()
    {
        try
        {
            var dates = new List<long>();
            if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")?.GetValue("InstallDate") is int current)
                dates.Add((uint)current);

            using var setup = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup");
            foreach (var name in setup?.GetSubKeyNames().Where(n => n.StartsWith("Source OS", StringComparison.OrdinalIgnoreCase)) ?? [])
                if (setup!.OpenSubKey(name)?.GetValue("InstallDate") is int previous)
                    dates.Add((uint)previous);

            return dates.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds(dates.Min()).LocalDateTime : null;
        }
        catch
        {
            return null;
        }
    }
}
