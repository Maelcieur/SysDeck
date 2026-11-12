using System.Runtime.InteropServices;
using System.Text;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Masque / réaffiche les icônes du bureau et la barre des tâches pendant le Mode console
/// (rien n'est enregistré : un redémarrage de l'Explorateur remet tout ; ShowAll() est aussi appelé à la sortie et à la reprise).
/// </summary>
public static class DesktopShell
{
    public static void HideAll()
    {
        Set(false);
        Log.Info("Mode console : icônes du bureau et barre des tâches masquées.");
    }

    public static void ShowAll() => Set(true);

    private static void Set(bool visible)
    {
        try
        {
            var cmd = visible ? SwShow : SwHide;
            var defView = FindDefView();
            if (defView != IntPtr.Zero) ShowWindow(defView, cmd);
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero) ShowWindow(tray, cmd);
            // Barres des tâches des autres écrans.
            var h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                ShowWindow(h, cmd);
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : bureau / barre des tâches", ex);
        }
    }

    /// <summary>Le conteneur des icônes (SHELLDLL_DefView), dans Progman ou dans un WorkerW.</summary>
    private static IntPtr FindDefView()
    {
        var progman = FindWindow("Progman", null);
        var defView = progman == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return defView;
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
        return defView;
    }

    private const int SwHide = 0, SwShow = 5;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
