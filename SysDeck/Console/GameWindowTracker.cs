using System.IO;
using System.Runtime.InteropServices;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Outils de premier plan pour le Mode console : à qui appartient une fenêtre (shell Windows, lanceur),
/// et remise au premier plan d'une fenêtre de jeu (bouton « Reprendre »).
/// </summary>
public static class GameWindowTracker
{
    /// <summary>Lanceurs / plateformes : écran de lancement Xbox, Steam, Epic, EA, Battle.net, Ubisoft, GOG.</summary>
    private static readonly HashSet<string> LauncherExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "gamingservicesui.exe", "gamingservices.exe", "xboxpcapp.exe", "xboxapp.exe", "gamebar.exe",
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "eadesktop.exe", "origin.exe",
        "battle.net.exe", "upc.exe", "ubisoftconnect.exe", "galaxyclient.exe",
    };

    private static readonly HashSet<string> ShellExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
    };

    private static string ExeName(int pid) => Path.GetFileName(GameLauncher.ImagePath(pid) ?? "");

    public static bool IsLauncher(int pid) => LauncherExes.Contains(ExeName(pid));

    /// <summary>Vrai pour l'Explorateur et le shell Windows (bureau, barre des tâches, menu Démarrer, recherche).</summary>
    public static bool IsShell(int pid) => ShellExes.Contains(ExeName(pid));

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd);

    public static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);

    /// <summary>Vrai si le processus est terminé (faux si on ne peut pas le savoir, ex. anti-triche qui bloque l'accès).</summary>
    public static bool ProcessExited(int pid)
    {
        var h = OpenProcess(0x00100000 /* SYNCHRONIZE */, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 87;   // ERROR_INVALID_PARAMETER : le processus n'existe plus
        try { return WaitForSingleObject(h, 0) == 0; }                     // signalé = terminé
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Fenêtre de ce processus qui couvre tout son écran (le rendu plein écran d'un émulateur, à côté de sa fenêtre
    /// « liste des jeux »), ou zéro.
    /// </summary>
    public static IntPtr FullscreenWindowOf(int pid)
    {
        if (pid == 0) return IntPtr.Zero;
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out var p);
            if (p != pid || !GetWindowRect(h, out var r)) return true;
            var mon = MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return true;
            if (r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

    /// <summary>Le processus a-t-il encore une fenêtre principale visible (non réduite) ?</summary>
    public static bool HasVisibleWindow(int pid)
    {
        if (pid == 0) return false;
        var found = false;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(h, out var p);
            if (p != pid) return true;
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Processus propriétaire d'une fenêtre.</summary>
    public static int ProcessOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>Titre de la fenêtre (journal).</summary>
    public static string Title(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Autorise n'importe quel processus (le jeu qu'on va lancer) à prendre le premier plan.</summary>
    public static void AllowAnyForeground() => AllowSetForegroundWindow(-1);

    /// <summary>Met la fenêtre au premier plan, même si une autre application avait le focus (restaure si réduite).</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        try
        {
            var fg = GetForegroundWindow();
            var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            var me = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
                // Appui simulé sur Alt : lève le verrou de premier plan de Windows (sinon la fenêtre ne fait que clignoter).
                keybd_event(0x12, 0, 0x1, UIntPtr.Zero);
                keybd_event(0x12, 0, 0x1 | 0x2, UIntPtr.Zero);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
        }
        catch (Exception ex)
        {
            Log.WarnOnce("game-foreground", "Mode console : mise au premier plan du jeu impossible", ex);
        }
    }

    private const int SwRestore = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
}
