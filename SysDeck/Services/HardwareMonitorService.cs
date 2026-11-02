using System.IO;
using LibreHardwareMonitor.Hardware;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

public sealed class HardwareSnapshot
{
    public float? CpuTemperature { get; init; }
    public float? CpuLoad { get; init; }
    public float? GpuTemperature { get; init; }
    public float? GpuLoad { get; init; }
    public string CpuName { get; init; } = "CPU";
    public string GpuName { get; init; } = "GPU";

    public float? RamUsedGb { get; init; }
    public float? RamTotalGb { get; init; }
    public float? RamLoad { get; init; }

    /// <summary>Puissance du processeur (capteur « Package »), null si le pilote est bloqué.</summary>
    public float? CpuPower { get; init; }
    /// <summary>Puissance cumulée des cartes graphiques dédiées (l'iGPU est déjà compté dans le CPU).</summary>
    public float? GpuPower { get; init; }

    /// <summary>Fréquence moyenne des cœurs, en MHz.</summary>
    public float? CpuClockMhz { get; init; }

    /// <summary>Fréquences du processeur graphique et de sa mémoire, en MHz.</summary>
    public float? GpuCoreClockMhz { get; init; }
    public float? GpuMemoryClockMhz { get; init; }
    /// <summary>Mémoire vidéo utilisée et totale, en Mo.</summary>
    public float? GpuMemoryUsedMb { get; init; }
    public float? GpuMemoryTotalMb { get; init; }
}

public sealed class HardwareMonitorService : IDisposable
{
    // Écrit par CpuTempBridge.ps1 (tâche planifiée élevée utilisant le SDK officiel AMD Ryzen
    // Master) quand le pilote de LibreHardwareMonitor est bloqué par l'Intégrité de la mémoire.
    // ProgramData (pas AppData) car la tâche tourne sous un compte différent de l'utilisateur
    // courant. On ignore le fichier s'il est trop vieux : la tâche pont n'est peut-être pas
    // configurée ou plus active, mieux vaut afficher "indisponible" qu'une valeur figée.
    private const string BridgeFilePath = @"C:\ProgramData\SysDeck\cputemp.txt";
    private static readonly TimeSpan BridgeFileMaxAge = TimeSpan.FromSeconds(15);
    private const long BridgeFileMaxBytes = 64; // une température tient en quelques octets
    private const double BridgeMinCelsius = 0, BridgeMaxCelsius = 150;

    // LibreHardwareMonitor n'est pas prévu pour des accès concurrents. Read() tourne maintenant sur un
    // thread de fond (il gelait l'interface) : ce verrou le sérialise avec le changement de MemoryEnabled
    // (thread de l'interface) et avec Dispose.
    private readonly object _sync = new();
    private bool _disposed;

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly CpuFrequencyCounter _cpuFrequency = new();

    /// <summary>Mesures d'une carte graphique ; la carte dédiée est préférée à l'iGPU du processeur.</summary>
    private sealed record GpuReading(string Name, bool Integrated, float? Temperature, float? Load,
        float? CoreClock, float? MemoryClock, float? MemoryUsed, float? MemoryTotal);

    public HardwareMonitorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true
        };
        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            // Pilote refusé ou matériel inattendu : SysDeck démarre quand même, sans ces mesures.
            Log.Error("Ouverture des capteurs matériels impossible (processeur, carte graphique, mémoire)", ex);
        }
    }

    /// <summary>
    /// Lecture de la RAM, coupée quand son module est masqué. Processeur et carte graphique restent
    /// toujours lus : le module Consommation a besoin de leur charge et de leur puissance.
    /// </summary>
    public bool MemoryEnabled
    {
        get { lock (_sync) return _computer.IsMemoryEnabled; }
        set
        {
            lock (_sync)
            {
                if (_disposed || _computer.IsMemoryEnabled == value) return;
                _computer.IsMemoryEnabled = value;
            }
        }
    }

    /// <summary>Peut être appelé depuis un thread de fond ; les lectures sont sérialisées.</summary>
    public HardwareSnapshot Read()
    {
        lock (_sync)
            return ReadLocked();
    }

    private HardwareSnapshot ReadLocked()
    {
        if (_disposed) return new HardwareSnapshot();

        float? cpuTemp = null, cpuLoad = null;
        float? ramUsed = null, ramAvailable = null, ramLoad = null;
        float? cpuPower = null, gpuPower = null, cpuClock = null;
        string cpuName = "CPU";
        var gpus = new List<GpuReading>();

        // LibreHardwareMonitor peut lever une exception en essayant d'accéder aux capteurs CPU si
        // son pilote (bloqué par l'Intégrité de la mémoire) est indisponible, selon la génération de
        // processeur. Sans ce try/catch dédié, une telle exception empêcherait GPU/RAM d'être lus et
        // sauterait complètement le repli via CpuTempBridge (voir TryReadBridgeTemperature).
        try
        {
            _computer.Accept(_visitor);

            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    cpuName = hardware.Name;
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Temperature && sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            cpuTemp = sensor.Value;
                        else if (sensor.SensorType == SensorType.Temperature && cpuTemp is null)
                            cpuTemp = sensor.Value;

                        if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                            cpuLoad = sensor.Value;

                        if (sensor.SensorType == SensorType.Power && sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            cpuPower = sensor.Value;

                        if (sensor.SensorType == SensorType.Clock && sensor.Name.Equals("Cores (Average)", StringComparison.OrdinalIgnoreCase))
                            cpuClock = sensor.Value;
                    }
                }
                else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    var integrated = IsIntegratedGpu(hardware.Name);
                    gpus.Add(ReadGpu(hardware, integrated));
                    if (!integrated && ReadGpuPower(hardware) is { } watts)
                        gpuPower = (gpuPower ?? 0) + watts;
                }
                else if (hardware.HardwareType == HardwareType.Memory)
                {
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Data && sensor.Name.Contains("Used", StringComparison.OrdinalIgnoreCase))
                            ramUsed = sensor.Value;
                        else if (sensor.SensorType == SensorType.Data && sensor.Name.Contains("Available", StringComparison.OrdinalIgnoreCase))
                            ramAvailable = sensor.Value;
                        else if (sensor.SensorType == SensorType.Load)
                            ramLoad = sensor.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Repli via CpuTempBridge ci-dessous malgré l'échec de LibreHardwareMonitor.
            Log.WarnOnce("hardware-read", "Lecture des capteurs matériels en échec", ex);
        }

        // pas juste "??=" : un capteur présent mais non lisible (pilote bloqué) renvoie souvent 0
        // plutôt que null, ce qui empêcherait le repli de se déclencher.
        if (cpuTemp is not > 0)
            cpuTemp = TryReadBridgeTemperature();
        if (cpuClock is not > 0)
            cpuClock = _cpuFrequency.ReadMhz();

        // Carte dédiée en priorité : avant, la dernière carte lue l'emportait (parfois l'iGPU du Ryzen).
        var gpu = gpus.FirstOrDefault(g => !g.Integrated) ?? gpus.FirstOrDefault();

        return new HardwareSnapshot
        {
            CpuTemperature = cpuTemp is > 0 ? cpuTemp : null,
            CpuLoad = cpuLoad,
            GpuTemperature = gpu?.Temperature is > 0 ? gpu.Temperature : null,
            GpuLoad = gpu?.Load,
            CpuName = cpuName,
            GpuName = gpu?.Name ?? "GPU",
            CpuClockMhz = cpuClock is > 0 ? cpuClock : null,
            GpuCoreClockMhz = gpu?.CoreClock is > 0 ? gpu.CoreClock : null,
            GpuMemoryClockMhz = gpu?.MemoryClock is > 0 ? gpu.MemoryClock : null,
            GpuMemoryUsedMb = gpu?.MemoryUsed,
            GpuMemoryTotalMb = gpu?.MemoryTotal,
            RamUsedGb = ramUsed,
            RamTotalGb = ramUsed is { } u && ramAvailable is { } a ? u + a : null,
            RamLoad = ramLoad,
            // Un capteur présent mais non lisible (pilote bloqué) renvoie 0 : on le traite comme absent.
            CpuPower = cpuPower is > 0 ? cpuPower : null,
            GpuPower = gpuPower
        };
    }

    /// <summary>
    /// iGPU (« AMD Radeon(TM) Graphics », « Intel(R) UHD Graphics 770 »…) : sa consommation fait
    /// déjà partie de celle du processeur. Les cartes dédiées portent un nom de modèle (RX, Arc, RTX…).
    /// </summary>
    private static bool IsIntegratedGpu(string name)
        => name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("RX", StringComparison.Ordinal)
           && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("Pro", StringComparison.OrdinalIgnoreCase);

    private static GpuReading ReadGpu(IHardware gpu, bool integrated)
    {
        float? Find(SensorType type, string name)
            => gpu.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        return new GpuReading(
            gpu.Name,
            integrated,
            gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value,
            gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))?.Value,
            Find(SensorType.Clock, "GPU Core"),
            Find(SensorType.Clock, "GPU Memory"),
            Find(SensorType.SmallData, "GPU Memory Used"),
            Find(SensorType.SmallData, "GPU Memory Total"));
    }

    /// <summary>Puissance totale de la carte : « GPU Package » (NVIDIA), « Total »/« PPT »/« Board » (AMD, Intel).</summary>
    private static float? ReadGpuPower(IHardware gpu)
    {
        var sensors = gpu.Sensors.Where(s => s.SensorType == SensorType.Power && s.Value is > 0).ToList();
        var total = sensors.FirstOrDefault(s =>
            s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains("PPT", StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains("Board", StringComparison.OrdinalIgnoreCase));
        return total?.Value ?? sensors.Select(s => s.Value).Max();
    }

    private static float? TryReadBridgeTemperature()
    {
        try
        {
            var info = new FileInfo(BridgeFilePath);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > BridgeFileMaxAge)
                return null;

            // Le dossier est dans ProgramData, donc potentiellement modifiable par un autre compte :
            // on n'en fait pas confiance. Un fichier démesuré n'est même pas lu.
            if (info.Length > BridgeFileMaxBytes)
            {
                Log.WarnOnce("hardware-bridge-invalid", $"Pont de température ignoré : fichier trop volumineux ({info.Length} octets)");
                return null;
            }

            var text = File.ReadAllText(BridgeFilePath).Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                && value is >= BridgeMinCelsius and <= BridgeMaxCelsius)
                return (float)value;

            Log.WarnOnce("hardware-bridge-invalid", "Pont de température ignoré : contenu invalide ou hors plage (0 à 150 °C attendus)");
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cpuFrequency.Dispose();
            try { _computer.Close(); }
            catch (Exception ex) { Log.Warn("Fermeture des capteurs matériels", ex); }
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
