using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SysDeck.Infrastructure;
using SysDeck.Models;

namespace SysDeck.Deck;

/// <summary>
/// Accès USB à la Switch (payload SwitchDeck) via WinUSB, installé automatiquement par Windows
/// grâce aux descripteurs Microsoft du payload. Aucune dépendance externe.
/// </summary>
public sealed class DeckUsb : IDisposable
{
    /// <summary>Doit rester identique à DeviceInterfaceGUIDs dans payload/src/usb_descriptors.c.</summary>
    public static readonly Guid InterfaceGuid = new("7D3E5A1C-4B2F-4E8A-9C61-5D0B8F2A3E17");

    private const byte PipeOut = 0x01;
    private const byte PipeIn = 0x81;
    private const uint TimeoutMs = 3000;

    /// <summary>
    /// Taille maximale d'un WinUsb_WritePipe (multiple de 512). Le transfert logique reste continu côté Switch
    /// (aucun paquet court intermédiaire) ; seul l'appel physique est découpé pour que le délai de la pipe
    /// s'applique à chaque morceau et non à 3,6 Mo d'un coup (liaison Full Speed ≈ 1 Mo/s).
    /// </summary>
    private const int WriteChunk = 256 * 1024;

    /// <summary>Délai de la pipe de sortie : 3 s + temps d'un morceau à 100 Ko/s au pire (marge pour une liaison lente).</summary>
    private const uint WriteTimeoutMs = TimeoutMs + WriteChunk / 100;

    private readonly SafeFileHandle _file;
    private readonly IntPtr _winusb;
    private readonly byte[] _block = new byte[DeckProtocol.BlockSize];
    private uint _seq;

    private DeckUsb(SafeFileHandle file, IntPtr winusb)
    {
        _file = file;
        _winusb = winusb;
    }

    /// <summary>Ouvre la première Switch SwitchDeck branchée, ou null s'il n'y en a pas.</summary>
    public static DeckUsb? TryOpen()
    {
        string? path = FindDevicePath();
        if (path == null)
            return null;

        var file = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileAttributeNormal | FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid)
            return null;

        if (!WinUsb_Initialize(file, out IntPtr winusb))
        {
            file.Dispose();
            return null;
        }

        // Pas de WinUsb_ResetPipe : la requête de contrôle bloquerait tant que la Switch attend une commande.
        foreach (byte pipe in new[] { PipeOut, PipeIn })
        {
            uint timeout = pipe == PipeOut ? WriteTimeoutMs : TimeoutMs;
            if (!WinUsb_SetPipePolicy(winusb, pipe, PipeTransferTimeout, sizeof(uint), ref timeout))
            {
                // Sans délai, une Switch figée bloquerait le thread USB indéfiniment : on n'ouvre pas la liaison.
                Log.WarnOnce("deck:pipe-policy", "Délai USB non appliqué, liaison Switch abandonnée",
                    new Win32Exception(Marshal.GetLastWin32Error()));
                WinUsb_Free(winusb);
                file.Dispose();
                return null;
            }
        }

        return new DeckUsb(file, winusb);
    }

    public DeckProtocol.Reply Ping() => Send(DeckProtocol.Cmd.Ping);

    public DeckProtocol.Reply Present() => Send(DeckProtocol.Cmd.Present);

    public DeckProtocol.Reply SetBrightness(int percent) =>
        Send(DeckProtocol.Cmd.Brightness, arg: (uint)Math.Clamp(percent, 0, 100));

    public DeckProtocol.Reply Bye() => Send(DeckProtocol.Cmd.Bye);

    public DeckProtocol.Reply RunSwitchAction(SwitchActionKind action) =>
        Send(DeckProtocol.Cmd.SwitchAction, arg: (uint)action);

    /// <summary>Efface le kit nuit de la Switch (commande Night sans données).</summary>
    public DeckProtocol.Reply ClearNight() => Send(DeckProtocol.Cmd.Night);

    /// <summary>
    /// Envoie le kit nuit (en-tête de 512 octets + image + glyphes, voir <see cref="DeckProtocol.WriteNightHeader"/>).
    /// <paramref name="kit"/> doit avoir une taille multiple de 512 octets. L'affichage ne change pas.
    /// </summary>
    public DeckProtocol.Reply SendNight(byte[] kit)
    {
        if (kit.Length % DeckProtocol.BlockSize != 0 || kit.Length > DeckProtocol.MaxDataBytes)
            throw new ArgumentException("Kit nuit : taille invalide.", nameof(kit));

        return Send(DeckProtocol.Cmd.Night, data: kit, dataLen: kit.Length);
    }

    /// <summary>Recale l'horloge de nuit de la Switch (heure locale en secondes epoch « locale »).</summary>
    /// <summary>Change la luminosité de veille du kit déjà stocké (un seul bloc de 512 octets, firmware v5+).</summary>
    public DeckProtocol.Reply SendNightBrightness(int percent) =>
        Send(DeckProtocol.Cmd.NightBrightness, arg: (uint)Math.Clamp(percent, 0, 100));

    public DeckProtocol.Reply SendNightTime(uint localTime) => Send(DeckProtocol.Cmd.NightTime, arg: localTime);

    /// <summary>
    /// Envoie un rectangle de pixels XRGB8888. <paramref name="pixels"/> contient w*h pixels
    /// et doit avoir une taille multiple de 512 octets (voir <see cref="DeckProtocol.PaddedLength"/>).
    /// </summary>
    public DeckProtocol.Reply SendRect(int x, int y, int w, int h, byte[] pixels, bool present)
    {
        int dataLen = DeckProtocol.PaddedLength(w * h * 4);
        if (pixels.Length < dataLen)
            throw new ArgumentException("Tampon de pixels trop petit (non complété à 512 octets).", nameof(pixels));

        return Send(DeckProtocol.Cmd.Rect, present ? DeckProtocol.FlagPresent : (ushort)0,
            x, y, w, h, pixels, dataLen);
    }

    private DeckProtocol.Reply Send(DeckProtocol.Cmd cmd, ushort flags = 0, int x = 0, int y = 0,
        int w = 0, int h = 0, byte[]? data = null, int dataLen = 0, uint arg = 0)
    {
        uint seq = ++_seq;
        DeckProtocol.WriteCmd(_block, cmd, seq, flags, x, y, w, h, dataLen, arg);
        Write(_block, _block.Length);
        if (data != null && dataLen > 0)
            Write(data, dataLen);

        Read(_block);
        var reply = DeckProtocol.ParseReply(_block);
        if (reply.Seq != seq)
            throw new InvalidDataException($"Réponse désynchronisée (attendu {seq}, reçu {reply.Seq}).");

        return reply;
    }

    private void Write(byte[] buffer, int length)
    {
        if (length <= WriteChunk)
        {
            WriteOnce(buffer, 0, length);
            return;
        }
        for (int offset = 0; offset < length; offset += WriteChunk)
            WriteOnce(buffer, offset, Math.Min(WriteChunk, length - offset));
    }

    private void WriteOnce(byte[] buffer, int offset, int count)
    {
        bool ok;
        uint done;
        // WinUsb_WritePipe n'a pas de décalage : on épingle le tableau et on passe l'adresse du morceau.
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            ok = WinUsb_WritePipe(_winusb, PipeOut, handle.AddrOfPinnedObject() + offset, (uint)count, out done, IntPtr.Zero);
        }
        finally
        {
            handle.Free();
        }
        if (!ok || done != count)
            throw new IOException("Écriture USB échouée.", new Win32Exception());
    }

    private void Read(byte[] buffer)
    {
        if (!WinUsb_ReadPipe(_winusb, PipeIn, buffer, (uint)buffer.Length, out uint done, IntPtr.Zero)
            || done != buffer.Length)
            throw new IOException("Lecture USB échouée.", new Win32Exception());
    }

    public void Dispose()
    {
        if (_winusb != IntPtr.Zero)
            WinUsb_Free(_winusb);
        _file.Dispose();
    }

    private static string? FindDevicePath()
    {
        Guid guid = InterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1))
            return null;

        try
        {
            var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data))
                return null;

            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
            IntPtr detail = Marshal.AllocHGlobal((int)size);
            try
            {
                // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W : 8 en 64 bits, 6 en 32 bits.
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
                    return null;

                return Marshal.PtrToStringUni(detail + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    // --- Win32 / WinUSB ---

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80, FileFlagOverlapped = 0x40000000;
    private const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    private const uint PipeTransferTimeout = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index,
        ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data,
        IntPtr detail, uint detailSize, out uint requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, IntPtr buffer, uint length,
        out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, byte[] buffer, uint length,
        out uint transferred, IntPtr overlapped);
}
