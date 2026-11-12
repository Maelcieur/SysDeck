using System.Runtime.InteropServices;
using System.Text;
using SysDeck.Infrastructure;

namespace SysDeck.Services.ConsoleMode;

public sealed record DesktopIconPosition(string Name, int X, int Y);

/// <summary>Positions des icônes du bureau, clé = nom de l'icône.</summary>
public sealed class DesktopIconModel
{
    public DateTime Created { get; set; } = DateTime.Now;
    public List<DesktopIconPosition> Icons { get; set; } = new();
}

/// <summary>
/// Sauvegarde et restauration de la position des icônes du bureau en interrogeant la ListView
/// (SysListView32) de l'Explorateur : lecture/écriture dans la mémoire du processus explorer.exe.
/// Aucune méthode ne lève d'exception : toute erreur est journalisée.
/// </summary>
public sealed class DesktopIconLayout
{
    private const uint LvmGetItemCount = 0x1004;
    private const uint LvmSetItemPosition32 = 0x1031;
    private const uint LvmGetItemPosition = 0x1010;
    private const uint LvmGetItemTextW = 0x1073;
    private const uint LvifText = 0x1;

    private const uint ProcessVmOperation = 0x8, ProcessVmRead = 0x10, ProcessVmWrite = 0x20, ProcessQueryInformation = 0x400;
    private const uint MemCommitReserve = 0x3000, MemRelease = 0x8000, PageReadWrite = 0x4;
    private const uint SmtoAbortIfHung = 0x2;

    // Disposition du tampon alloué dans explorer.exe.
    private const int OffItem = 0, OffText = 128, OffPoint = 768, RemoteSize = 4096, TextChars = 260;

    /// <summary>Lit les positions actuelles ; un modèle vide est renvoyé en cas d'échec.</summary>
    public DesktopIconModel Save()
    {
        var model = new DesktopIconModel();
        try
        {
            using var session = Open();
            if (session is null) return model;
            int count = (int)session.Send(LvmGetItemCount, 0, IntPtr.Zero);
            if (count < 0 || count > 5000) { Log.Warn($"Icônes : nombre d'éléments invraisemblable ({count})."); return model; }

            for (int i = 0; i < count; i++)
            {
                var name = session.ReadText(i);
                if (string.IsNullOrEmpty(name)) continue;
                if (session.ReadPosition(i) is { } pos) model.Icons.Add(new DesktopIconPosition(name, pos.X, pos.Y));
            }
            Log.Info($"Icônes du bureau : {model.Icons.Count} position(s) sauvegardée(s).");
        }
        catch (Exception ex)
        {
            Log.Warn("Icônes : sauvegarde impossible", ex);
        }
        return model;
    }

    /// <summary>
    /// Surveille les icônes pendant <paramref name="duration"/> après un changement d'écrans : l'Explorateur les
    /// réorganise en différé (parfois plusieurs secondes après), on les remet donc en place à chaque passage.
    /// </summary>
    public int RestoreAndHold(DesktopIconModel? model, TimeSpan duration)
    {
        if (model is null || model.Icons.Count == 0) return 0;
        var total = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            total += Restore(model, quiet: true);
            Thread.Sleep(700);
        }
        total += Restore(model, quiet: true);
        Log.Info($"Icônes du bureau : {total} replacement(s) en {duration.TotalSeconds:0} s.");
        return total;
    }

    /// <summary>Replace les icônes connues ; renvoie le nombre d'icônes déplacées.</summary>
    public int Restore(DesktopIconModel? model) => Restore(model, quiet: false);

    private int Restore(DesktopIconModel? model, bool quiet)
    {
        int moved = 0;
        try
        {
            if (model is null || model.Icons.Count == 0) return 0;
            using var session = Open();
            if (session is null) return 0;

            var wanted = new Dictionary<string, DesktopIconPosition>(StringComparer.OrdinalIgnoreCase);
            foreach (var icon in model.Icons) wanted.TryAdd(icon.Name, icon);

            int count = (int)session.Send(LvmGetItemCount, 0, IntPtr.Zero);
            for (int i = 0; i < count && i < 5000; i++)
            {
                var name = session.ReadText(i);
                if (name is null || !wanted.TryGetValue(name, out var target)) continue;
                if (session.ReadPosition(i) is { } cur && cur.X == target.X && cur.Y == target.Y) continue;
                if (session.SetPosition(i, target.X, target.Y)) moved++;
            }
            if (!quiet) Log.Info($"Icônes du bureau : {moved} icône(s) replacée(s).");
        }
        catch (Exception ex)
        {
            Log.Warn("Icônes : restauration impossible", ex);
        }
        return moved;
    }

    // ---------- Session dans explorer.exe ----------

    private static Session? Open()
    {
        if (!Environment.Is64BitProcess) { Log.Warn("Icônes : SysDeck n'est pas en 64 bits, lecture impossible."); return null; }
        var list = FindDesktopListView();
        if (list == IntPtr.Zero) { Log.Warn("Icônes : liste des icônes du bureau introuvable."); return null; }

        GetWindowThreadProcessId(list, out var pid);
        var proc = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, pid);
        if (proc == IntPtr.Zero) { Log.Warn($"Icônes : OpenProcess a échoué (code {Marshal.GetLastWin32Error()})."); return null; }

        if (!IsWow64Process(proc, out var wow) || wow)
        {
            CloseHandle(proc);
            Log.Warn("Icônes : explorer.exe n'est pas un processus 64 bits.");
            return null;
        }
        var remote = VirtualAllocEx(proc, IntPtr.Zero, RemoteSize, MemCommitReserve, PageReadWrite);
        if (remote == IntPtr.Zero)
        {
            Log.Warn($"Icônes : VirtualAllocEx a échoué (code {Marshal.GetLastWin32Error()}).");
            CloseHandle(proc);
            return null;
        }
        return new Session(list, proc, remote);
    }

    private static IntPtr FindDesktopListView()
    {
        var defView = IntPtr.Zero;
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero) defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            EnumWindows((hwnd, _) =>
            {
                var sb = new StringBuilder(32);
                GetClassName(hwnd, sb, sb.Capacity);
                if (sb.ToString() != "WorkerW") return true;
                var dv = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv == IntPtr.Zero) return true;
                defView = dv;
                return false;
            }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    private sealed class Session : IDisposable
    {
        private readonly IntPtr _list, _proc, _remote;

        public Session(IntPtr list, IntPtr proc, IntPtr remote) { _list = list; _proc = proc; _remote = remote; }

        public long Send(uint msg, long wParam, IntPtr lParam)
        {
            var r = SendMessageTimeout(_list, msg, (IntPtr)wParam, lParam, SmtoAbortIfHung, 3000, out var result);
            return r == IntPtr.Zero ? -1 : result.ToInt64();
        }

        public string? ReadText(int index)
        {
            // LVITEMW 64 bits : mask@0, iItem@4, iSubItem@8, pszText@24, cchTextMax@32.
            var item = new byte[88];
            BitConverter.GetBytes(LvifText).CopyTo(item, 0);
            BitConverter.GetBytes(index).CopyTo(item, 4);
            BitConverter.GetBytes((long)(_remote + OffText)).CopyTo(item, 24);
            BitConverter.GetBytes(TextChars).CopyTo(item, 32);
            if (!WriteProcessMemory(_proc, _remote + OffItem, item, item.Length, out _)) return null;
            if (!WriteProcessMemory(_proc, _remote + OffText, new byte[TextChars * 2], TextChars * 2, out _)) return null;
            if (Send(LvmGetItemTextW, index, _remote + OffItem) < 0) return null;

            var text = new byte[TextChars * 2];
            if (!ReadProcessMemory(_proc, _remote + OffText, text, text.Length, out _)) return null;
            var s = Encoding.Unicode.GetString(text);
            int z = s.IndexOf('\0');
            return z >= 0 ? s[..z] : s;
        }

        public (int X, int Y)? ReadPosition(int index)
        {
            if (Send(LvmGetItemPosition, index, _remote + OffPoint) <= 0) return null;
            var pt = new byte[8];
            if (!ReadProcessMemory(_proc, _remote + OffPoint, pt, 8, out _)) return null;
            return (BitConverter.ToInt32(pt, 0), BitConverter.ToInt32(pt, 4));
        }

        /// <summary>LVM_SETITEMPOSITION32 : POINT complet dans la mémoire d'explorer.exe (pas de troncature 16 bits).</summary>
        public bool SetPosition(int index, int x, int y)
        {
            var pt = new byte[8];
            BitConverter.GetBytes(x).CopyTo(pt, 0);
            BitConverter.GetBytes(y).CopyTo(pt, 4);
            if (!WriteProcessMemory(_proc, _remote + OffPoint, pt, 8, out _)) return false;
            return Send(LvmSetItemPosition32, index, _remote + OffPoint) >= 0;
        }

        public void Dispose()
        {
            VirtualFreeEx(_proc, _remote, 0, MemRelease);
            CloseHandle(_proc);
        }
    }

    // ---------- P/Invoke ----------

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, uint size, uint type, uint protect);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, uint size, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);
}
