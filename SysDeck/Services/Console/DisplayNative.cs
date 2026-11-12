using System.Runtime.InteropServices;

namespace SysDeck.Services.ConsoleMode;

/// <summary>Structures et fonctions natives de la configuration d'affichage (user32, API CCD).</summary>
public static class DisplayNative
{
    public const uint QdcAllPaths = 0x1;
    public const uint QdcOnlyActivePaths = 0x2;

    public const uint SdcTopologySupplied = 0x10;
    public const uint SdcUseSuppliedDisplayConfig = 0x20;
    public const uint SdcValidate = 0x40;
    public const uint SdcApply = 0x80;
    public const uint SdcNoOptimization = 0x100;
    public const uint SdcSaveToDatabase = 0x200;
    public const uint SdcAllowChanges = 0x400;

    public const uint PathActive = 0x1;
    public const uint ModeSource = 1;
    public const uint ModeTarget = 2;

    public const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;

        public long ToLong() => ((long)HighPart << 32) | LowPart;
        public static Luid From(long v) => new() { LowPart = (uint)(v & 0xFFFFFFFF), HighPart = (int)(v >> 32) };
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO (72 octets), source et cible aplaties.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PathInfo
    {
        public Luid SourceAdapter;
        public uint SourceId;
        public uint SourceModeIdx;
        public uint SourceStatus;
        public Luid TargetAdapter;
        public uint TargetId;
        public uint TargetModeIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatus;
        public uint Flags;
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO (64 octets). Union aplatie en 12 mots : pour un mode source,
    /// D0 = largeur, D1 = hauteur, D2 = format de pixel, D3/D4 = position X/Y (signés).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct ModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public uint D0;
        [FieldOffset(20)] public uint D1;
        [FieldOffset(24)] public uint D2;
        [FieldOffset(28)] public uint D3;
        [FieldOffset(32)] public uint D4;
        [FieldOffset(36)] public uint D5;
        [FieldOffset(40)] public uint D6;
        [FieldOffset(44)] public uint D7;
        [FieldOffset(48)] public uint D8;
        [FieldOffset(52)] public uint D9;
        [FieldOffset(56)] public uint D10;
        [FieldOffset(60)] public uint D11;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths,
        ref uint numModes, [Out] ModeInfo[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(uint numPaths, [In] PathInfo[]? paths, uint numModes, [In] ModeInfo[]? modes, uint flags);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(IntPtr requestPacket);
}
