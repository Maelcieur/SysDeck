using System.Runtime.InteropServices;
using System.Text;

namespace SysDeck.Services;

/// <summary>
/// Accès natif aux informations d'un processus qui résistent aux restrictions de Process.MainModule :
/// chemin de l'exécutable (OpenProcess en lecture limitée, accepté pour la plupart des processus protégés
/// — anti-triche, applications du Store/Xbox) et dossier d'installation du paquet MSIX/Appx éventuel.
/// </summary>
internal static class ProcessNative
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Chemin complet de l'exécutable du processus, ou null si inaccessible.</summary>
    public static string? QueryImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) && size > 0 ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Dossier d'installation du paquet auquel appartient le processus (application empaquetée), sinon null.</summary>
    public static string? GetPackageInstallPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var nameBuffer = new StringBuilder(256);
            var nameLength = (uint)nameBuffer.Capacity;
            if (GetPackageFullName(handle, ref nameLength, nameBuffer) != 0) return null; // 15700 = pas de paquet

            uint pathLength = 0;
            if (GetPackagePathByFullName(nameBuffer.ToString(), ref pathLength, null) != ErrorInsufficientBuffer || pathLength == 0)
                return null;
            var pathBuffer = new StringBuilder((int)pathLength);
            return GetPackagePathByFullName(nameBuffer.ToString(), ref pathLength, pathBuffer) == 0 ? pathBuffer.ToString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr process, ref uint packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder? path);
}
