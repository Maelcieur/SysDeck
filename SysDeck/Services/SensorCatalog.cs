using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace SysDeck.Services;

/// <summary>Une mesure disponible : identifiant stable, groupe (composant), nom lisible, unité, valeur.</summary>
public sealed record SensorReading(string Id, string Group, string Name, string Unit, float Value)
{
    public string Label => $"{Group} · {Name}";
}

/// <summary>Une barre du module Barres de capteurs.</summary>
public sealed class SensorBarItem
{
    public string SensorId { get; set; } = "";
    /// <summary>Nom affiché au-dessus de la barre (vide : nom court du capteur).</summary>
    public string Label { get; set; } = "";
    /// <summary>Valeur qui remplit la barre (0 : selon l'unité, 100 pour % et °C).</summary>
    public double Max { get; set; }
    public string Color { get; set; } = "#FF00E0FF";
}

/// <summary>Réglages du module Barres de capteurs (globaux : bureau et Switch).</summary>
public sealed class SensorBarsSettings : ObservableSettings
{
    private string _style = "Segments", _orientation = "Horizontal";
    private double _barWidth = 16, _barHeight = 220, _spacing = 12, _cornerRadius = 4;
    private int _segments = 18;
    private bool _alert = true, _glow = true;
    private double _alertPercent = 85;
    private string _alertColor = "#FFFF3B30";

    public List<SensorBarItem> Items { get; set; } = new();

    /// <summary>« Solid » (plein), « Segments » (blocs empilés), « Gradient » (dégradé vers le haut).</summary>
    public string Style { get => _style; set => Set(ref _style, value); }
    /// <summary>« Horizontal » (par défaut) ou « Vertical ».</summary>
    public string Orientation { get => _orientation; set => Set(ref _orientation, value); }
    /// <summary>Épaisseur de la barre.</summary>
    public double BarWidth { get => _barWidth; set => Set(ref _barWidth, Math.Clamp(value, 6, 80)); }
    /// <summary>Longueur de la barre (sens du remplissage).</summary>
    public double BarHeight { get => _barHeight; set => Set(ref _barHeight, Math.Clamp(value, 60, 500)); }
    public double Spacing { get => _spacing; set => Set(ref _spacing, Math.Clamp(value, 4, 60)); }
    public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Math.Clamp(value, 0, 40)); }
    public int Segments { get => _segments; set => Set(ref _segments, Math.Clamp(value, 4, 40)); }
    /// <summary>Halo lumineux autour de la partie remplie.</summary>
    public bool Glow { get => _glow; set => Set(ref _glow, value); }
    /// <summary>Couleur d'alerte au-delà d'un pourcentage de la barre.</summary>
    public bool Alert { get => _alert; set => Set(ref _alert, value); }
    public double AlertPercent { get => _alertPercent; set => Set(ref _alertPercent, Math.Clamp(value, 10, 100)); }
    public string AlertColor { get => _alertColor; set => Set(ref _alertColor, value); }

    /// <summary>Liste des barres modifiée (ajout, retrait, capteur, couleur…).</summary>
    public void ItemsChanged() => Raise(nameof(Items));

    public static List<SensorBarItem> DefaultItems() => new()
    {
        new() { SensorId = SensorCatalog.CpuTemp, Label = "CPU", Color = "#FF00E0FF" },
        new() { SensorId = SensorCatalog.GpuTemp, Label = "GPU", Color = "#FF3DDC84" },
        new() { SensorId = SensorCatalog.RamLoad, Label = "RAM", Color = "#FFFFB238" },
        new() { SensorId = SensorCatalog.VramLoad, Label = "VRAM", Color = "#FFB06BFF" },
    };
}

/// <summary>
/// Toutes les mesures proposées : d'abord celles de SysDeck (température du processeur avec son repli, VRAM en %…),
/// puis chaque capteur trouvé par LibreHardwareMonitor (ventilateurs de la carte graphique, tensions, fréquences…).
/// </summary>
public static class SensorCatalog
{
    public const string CpuTemp = "sysdeck/cpu/temp", CpuLoad = "sysdeck/cpu/load", CpuClock = "sysdeck/cpu/clock", CpuPower = "sysdeck/cpu/power",
        GpuTemp = "sysdeck/gpu/temp", GpuLoad = "sysdeck/gpu/load", GpuPower = "sysdeck/gpu/power",
        VramLoad = "sysdeck/vram/load", VramUsed = "sysdeck/vram/used", RamLoad = "sysdeck/ram/load", RamUsed = "sysdeck/ram/used";

    /// <summary>Mesures calculées par SysDeck à partir de la dernière lecture.</summary>
    public static IEnumerable<SensorReading> Derived(HardwareSnapshot s)
    {
        SensorReading? R(string id, string group, string name, string unit, float? v)
            => v is { } x && !float.IsNaN(x) ? new SensorReading(id, group, name, unit, x) : null;

        float? vramPercent = s.GpuMemoryUsedMb is { } u && s.GpuMemoryTotalMb is > 0 and var t ? u / t * 100 : null;
        var all = new[]
        {
            R(CpuTemp, "Processeur", "Température", "°C", s.CpuTemperature),
            R(CpuLoad, "Processeur", "Charge", "%", s.CpuLoad),
            R(CpuClock, "Processeur", "Fréquence", "MHz", s.CpuClockMhz),
            R(CpuPower, "Processeur", "Puissance", "W", s.CpuPower),
            R(GpuTemp, "Carte graphique", "Température", "°C", s.GpuTemperature),
            R(GpuLoad, "Carte graphique", "Charge", "%", s.GpuLoad),
            R(GpuPower, "Carte graphique", "Puissance", "W", s.GpuPower),
            R(VramLoad, "Carte graphique", "VRAM utilisée", "%", vramPercent),
            R(VramUsed, "Carte graphique", "VRAM utilisée", "Go", s.GpuMemoryUsedMb / 1024f),
            R(RamLoad, "Mémoire", "RAM utilisée", "%", s.RamLoad),
            R(RamUsed, "Mémoire", "RAM utilisée", "Go", s.RamUsedGb),
        };
        return all.OfType<SensorReading>();
    }

    /// <summary>Capteur LibreHardwareMonitor → mesure (null pour les types sans intérêt ici).</summary>
    public static SensorReading? FromSensor(IHardware hardware, ISensor sensor)
    {
        if (sensor.Value is not { } value || float.IsNaN(value)) return null;
        var (kind, unit) = sensor.SensorType switch
        {
            SensorType.Temperature => ("Température", "°C"),
            SensorType.Load => ("Charge", "%"),
            SensorType.Fan => ("Ventilateur", "tr/min"),
            SensorType.Control => ("Ventilateur (consigne)", "%"),
            SensorType.Clock => ("Fréquence", "MHz"),
            SensorType.Power => ("Puissance", "W"),
            SensorType.Voltage => ("Tension", "V"),
            SensorType.SmallData => ("Données", "Mo"),
            SensorType.Data => ("Données", "Go"),
            _ => ("", "")
        };
        if (unit.Length == 0) return null;
        // D3D… : compteurs internes de Windows, peu parlants et très nombreux.
        if (sensor.Name.StartsWith("D3D", StringComparison.OrdinalIgnoreCase)) return null;
        var name = sensor.SensorType is SensorType.Fan or SensorType.Control ? sensor.Name.Replace("GPU Fan", "Ventilateur GPU") : $"{kind} · {sensor.Name}";
        if (sensor.SensorType == SensorType.Control) name += " (%)";
        return new SensorReading(sensor.Identifier.ToString(), hardware.Name, name, unit, value);
    }

    /// <summary>Maximum par défaut d'une barre selon l'unité.</summary>
    public static double DefaultMax(string unit) => unit switch
    {
        "%" or "°C" => 100,
        "tr/min" => 3000,
        "MHz" => 6000,
        "W" => 400,
        "V" => 2,
        "Mo" => 16384,
        "Go" => 64,
        _ => 100
    };

    /// <summary>Valeur affichée sous la barre (« 54 °C », « 1 250 tr/min », « 12,4 Go »).</summary>
    public static string Format(float value, string unit)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var number = unit is "Go" or "V" ? value.ToString("0.0", fr) : Math.Round(value).ToString("#,0", fr);
        return unit is "%" or "°C" ? $"{number} {unit}".Replace(" °C", "°C") : $"{number} {unit}";
    }
}
