using System.Runtime.InteropServices;
using System.Text;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Réduit toutes les applications ouvertes à l'entrée du mode console, puis les rouvre à la sortie
/// (dans leur ordre d'empilement d'origine), pour retrouver le bureau exactement comme avant.
/// </summary>
public static class OpenAppWindows
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>Réduit les fenêtres d'application visibles (hors SysDeck et shell). Renvoie celles réduites, de la plus haute à la plus basse.</summary>
    public static List<IntPtr> MinimizeAll()
    {
        var self = (uint)Environment.ProcessId;
        var list = new List<IntPtr>();
        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, GwOwner) != IntPtr.Zero) return true;
                GetWindowThreadProcessId(h, out var pid);
                if (pid == self) return true;
                var ex = GetWindowLongPtr(h, GwlExStyle).ToInt64();
                if ((ex & WsExToolWindow) != 0) return true;
                if ((GetWindowLongPtr(h, GwlStyle).ToInt64() & WsMinimizeBox) == 0) return true;   // pas réductible (popups, overlays)
                if (DwmGetWindowAttribute(h, DwmwaCloaked, out var cloaked, 4) == 0 && cloaked != 0) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (ShellClasses.Contains(cls.ToString())) return true;
                list.Add(h);
                return true;
            }, IntPtr.Zero);
            // Comme un clic sur « Réduire » : certaines applications (Discord et autres Electron) ignorent ShowWindow.
            foreach (var h in list) PostMessage(h, WmSysCommand, (IntPtr)ScMinimize, IntPtr.Zero);
            var snapshot = list.ToArray();
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                var forced = 0;
                foreach (var h in snapshot)
                    if (IsWindow(h) && IsWindowVisible(h) && !IsIconic(h)) { ShowWindowAsync(h, SwMinimize); forced++; }
                if (forced > 0) Log.Info($"Mode console : {forced} application(s) réduite(s) de force.");
            });
            Log.Info($"Mode console : {list.Count} application(s) réduite(s).");
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : réduction des applications", ex);
        }
        return list;
    }

    /// <summary>
    /// Re-réduit les fenêtres de la liste qui se sont rouvertes : les applications Chromium/Electron (Discord, VS Code,
    /// navigateurs...) se rouvrent d'elles-mêmes quand leur écran disparaît (bascule vers la TV).
    /// </summary>
    public static void MinimizeAgain(IReadOnlyList<IntPtr> windows)
    {
        var again = 0;
        foreach (var h in windows)
        {
            if (!IsWindow(h) || !IsWindowVisible(h) || IsIconic(h)) continue;
            PostMessage(h, WmSysCommand, (IntPtr)ScMinimize, IntPtr.Zero);
            ShowWindowAsync(h, SwMinimize);
            again++;
        }
        if (again > 0) Log.Info($"Mode console : {again} application(s) rouverte(s) toute(s) seule(s), réduite(s) de nouveau.");
    }

    /// <summary>Rouvre les fenêtres réduites par MinimizeAll (encore réduites), la plus haute en dernier pour garder l'ordre.</summary>
    public static void RestoreAll(IReadOnlyList<IntPtr> windows)
    {
        try
        {
            var count = 0;
            for (var i = windows.Count - 1; i >= 0; i--)
            {
                var h = windows[i];
                if (!IsWindow(h) || !IsIconic(h)) continue;   // fermée ou déjà rouverte par l'utilisateur
                ShowWindowAsync(h, SwRestore);
                count++;
            }
            if (count > 0) Log.Info($"Mode console : {count} application(s) rouverte(s).");
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : réouverture des applications", ex);
        }
    }

    private const int GwOwner = 4, GwlStyle = -16, GwlExStyle = -20, SwMinimize = 6, SwRestore = 9, DwmwaCloaked = 14;
    private const uint WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    private const long WsExToolWindow = 0x80, WsMinimizeBox = 0x20000;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}
