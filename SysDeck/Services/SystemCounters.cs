using System.Runtime.InteropServices;
using Microsoft.Win32;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>
/// Fréquence réelle du processeur telle que l'affiche le Gestionnaire des tâches : fréquence de base ×
/// « % de performances du processeur » (compteur Windows). Sert quand le pilote de LibreHardwareMonitor
/// est bloqué (Intégrité de la mémoire) et que ses capteurs de fréquence restent à zéro.
/// </summary>
public sealed class CpuFrequencyCounter : IDisposable
{
    private const int PDH_FMT_DOUBLE = 0x200;
    private readonly IntPtr _query;
    private readonly IntPtr _counter;
    private readonly int _baseMhz;
    private readonly bool _ready;

    public CpuFrequencyCounter()
    {
        try
        {
            _baseMhz = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 0) is int mhz ? mhz : 0;
            // Nom anglais : fonctionne quelle que soit la langue de Windows (les noms affichés sont traduits).
            _ready = _baseMhz > 0
                     && PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                     && PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _counter) == 0
                     && PdhCollectQueryData(_query) == 0;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("cpu-frequency", "Compteur de fréquence du processeur indisponible", ex);
        }
    }

    /// <summary>Fréquence moyenne depuis le dernier appel, en MHz ; null si indisponible.</summary>
    public float? ReadMhz()
    {
        if (!_ready || PdhCollectQueryData(_query) != 0) return null;
        if (PdhGetFormattedCounterValue(_counter, PDH_FMT_DOUBLE, out _, out var value) != 0 || value.DoubleValue <= 0) return null;
        return (float)(_baseMhz * value.DoubleValue / 100);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public int Status;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, int format, out int type, out PdhFmtCounterValue value);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
}
