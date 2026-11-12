using System.Runtime.InteropServices;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Batterie des appareils Bluetooth telle que Windows l'affiche dans Paramètres > Bluetooth
/// (propriété DEVPKEY_Bluetooth_Battery). Utile pour les manettes Xbox en Bluetooth, pour lesquelles
/// XInputGetBatteryInformation ne renvoie rien.
/// </summary>
public static class BluetoothBattery
{
    public sealed record Entry(string Name, int Percent);

    /// <summary>Appareils Bluetooth présents qui exposent un niveau de batterie (0-100).</summary>
    public static List<Entry> Read()
    {
        var list = new List<Entry>();
        foreach (var enumerator in new[] { "BTHLE", "BTHENUM" })
        {
            var set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == INVALID_HANDLE) continue;
            try
            {
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    var battery = BatteryKey;
                    var buf = new byte[4];
                    if (!SetupDiGetDevicePropertyW(set, ref info, ref battery, out var type, buf, (uint)buf.Length, out _, 0)) continue;
                    if (type != DEVPROP_TYPE_BYTE) continue;
                    var percent = buf[0];
                    if (percent > 100) continue;
                    var name = ReadString(set, ref info, FriendlyNameKey) ?? ReadString(set, ref info, DeviceDescKey) ?? "";
                    list.Add(new Entry(name, percent));
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("bt-battery", "Batterie Bluetooth illisible", ex);
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }
        return list;
    }

    /// <summary>Vrai si le nom ressemble à une manette Xbox (« Xbox Wireless Controller », « Manette Xbox »...).</summary>
    public static bool IsXboxName(string name) => name.Contains("Xbox", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
    {
        var buf = new byte[512];
        if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, buf, (uint)buf.Length, out var size, 0)) return null;
        if (type != DEVPROP_TYPE_STRING || size < 2) return null;
        var s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // ---------- SetupAPI ----------

    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    private const uint DEVPROP_TYPE_BYTE = 0x3, DEVPROP_TYPE_STRING = 0x12;
    private static readonly IntPtr INVALID_HANDLE = new(-1);

    private static readonly DEVPROPKEY BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DEVPROPKEY FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly DEVPROPKEY DeviceDescKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(Guid g, uint p) { fmtid = g; pid = p; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key,
        out uint type, byte[] buffer, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
