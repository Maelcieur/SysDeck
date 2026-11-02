namespace SysDeck.Services;

public enum AlertKind { CpuTemperature, GpuTemperature, MouseBattery, ClaudeSession, ClaudeWeek }

/// <summary>Alerte à afficher : titre, message, valeur mise en avant et sévérité (0 = attention, 1 = critique).</summary>
public sealed record Alert(AlertKind Kind, string Title, string Message, string Value, bool Critical);

/// <summary>
/// Décide quand prévenir, sans rien afficher (pure, testée). Une alerte ne se déclenche qu'une fois par
/// dépassement : elle se réarme quand la valeur revient nettement du bon côté (hystérésis), pour ne pas
/// clignoter autour du seuil. Les températures doivent rester au-dessus du seuil plusieurs lectures de suite :
/// un pic d'une seconde au lancement d'un programme ne mérite pas une alerte.
/// </summary>
public sealed class AlertMonitor
{
    public const int TemperatureReadings = 4;   // lectures de suite au-dessus du seuil (≈ 6 s à 1,5 s par lecture)
    public const double TemperatureMargin = 5;  // °C sous le seuil pour réarmer
    public const double PercentMargin = 5;      // points de % pour réarmer

    private readonly AlertSettings _settings;
    private readonly Dictionary<AlertKind, bool> _armed = new();
    private int _cpuHot, _gpuHot;

    public AlertMonitor(AlertSettings settings) => _settings = settings;

    private bool Armed(AlertKind kind) => _armed.GetValueOrDefault(kind, true);

    /// <summary>Déclenche si <paramref name="over"/> et armée ; réarme si <paramref name="back"/>.</summary>
    private Alert? Edge(AlertKind kind, bool enabled, bool over, bool back, Func<Alert> create)
    {
        if (!enabled || back)
        {
            _armed[kind] = true;
            return null;
        }
        if (!over || !Armed(kind)) return null;
        _armed[kind] = false;
        return create();
    }

    public List<Alert> CheckTemperatures(float? cpu, float? gpu)
    {
        var alerts = new List<Alert>();
        _cpuHot = cpu >= _settings.CpuTemperature ? _cpuHot + 1 : 0;
        _gpuHot = gpu >= _settings.GpuTemperature ? _gpuHot + 1 : 0;

        if (Edge(AlertKind.CpuTemperature, _settings.CpuEnabled, _cpuHot >= TemperatureReadings,
                cpu < _settings.CpuTemperature - TemperatureMargin,
                () => Temperature(AlertKind.CpuTemperature, "Processeur trop chaud", cpu!.Value, _settings.CpuTemperature)) is { } c)
            alerts.Add(c);
        if (Edge(AlertKind.GpuTemperature, _settings.GpuEnabled, _gpuHot >= TemperatureReadings,
                gpu < _settings.GpuTemperature - TemperatureMargin,
                () => Temperature(AlertKind.GpuTemperature, "Carte graphique trop chaude", gpu!.Value, _settings.GpuTemperature)) is { } g)
            alerts.Add(g);
        return alerts;
    }

    private static Alert Temperature(AlertKind kind, string title, float value, double threshold)
        => new(kind, title, $"Seuil réglé à {threshold:0} °C. Vérifie la ventilation ou ce qui tourne en fond.",
            $"{value:0} °C", value >= threshold + 10);

    public Alert? CheckMouse(int percent, bool charging)
        => Edge(AlertKind.MouseBattery, _settings.MouseEnabled,
            percent <= _settings.MousePercent,
            charging || percent > _settings.MousePercent + PercentMargin,
            () => new Alert(AlertKind.MouseBattery, "Batterie de la souris faible",
                percent <= 5 ? "Branche-la vite, elle va s'éteindre." : "Pense à la mettre en charge.",
                $"{percent} %", percent <= Math.Min(10, _settings.MousePercent / 2)));

    public List<Alert> CheckClaude(double? session, double? week)
    {
        var alerts = new List<Alert>();
        // Pas de valeur (non connecté, service indisponible) : on ne réarme pas, on attend la prochaine vraie lecture.
        if (session is { } s
            && Edge(AlertKind.ClaudeSession, _settings.ClaudeSessionEnabled, s >= _settings.ClaudeSessionPercent,
                s < _settings.ClaudeSessionPercent - PercentMargin,
                () => Quota(AlertKind.ClaudeSession, "Quota Claude de la session", s, "de ta session de 5 h")) is { } a)
            alerts.Add(a);
        if (week is { } w
            && Edge(AlertKind.ClaudeWeek, _settings.ClaudeWeekEnabled, w >= _settings.ClaudeWeekPercent,
                w < _settings.ClaudeWeekPercent - PercentMargin,
                () => Quota(AlertKind.ClaudeWeek, "Quota Claude de la semaine", w, "de ton quota de la semaine")) is { } b)
            alerts.Add(b);
        return alerts;
    }

    private static Alert Quota(AlertKind kind, string title, double percent, string what)
        => new(kind, title, $"Tu as utilisé {percent:0} % {what}.", $"{percent:0} %", percent >= 95);

    /// <summary>Exemple de chaque alerte, pour le bouton « Tester » des Paramètres.</summary>
    public static Alert Sample(AlertKind kind, AlertSettings s) => kind switch
    {
        AlertKind.CpuTemperature => Temperature(kind, "Processeur trop chaud", (float)s.CpuTemperature + 2, s.CpuTemperature),
        AlertKind.GpuTemperature => Temperature(kind, "Carte graphique trop chaude", (float)s.GpuTemperature + 2, s.GpuTemperature),
        AlertKind.MouseBattery => new Alert(kind, "Batterie de la souris faible", "Pense à la mettre en charge.", $"{s.MousePercent:0} %", false),
        AlertKind.ClaudeSession => Quota(kind, "Quota Claude de la session", s.ClaudeSessionPercent, "de ta session de 5 h"),
        _ => Quota(kind, "Quota Claude de la semaine", s.ClaudeWeekPercent, "de ton quota de la semaine")
    };
}
