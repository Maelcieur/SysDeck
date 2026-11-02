using System.Runtime.InteropServices;

namespace SysDeck.Interop;

/// <summary>
/// Rattache une fenêtre au niveau du bureau Windows (comme Rainmeter en mode "sur le bureau" ou un
/// moteur de fond d'écran animé) : la fenêtre reste visible en permanence sur le bureau, mais toute
/// fenêtre normale (jeu, logiciel) s'affiche AU-DESSUS d'elle, exactement comme les icônes du bureau.
///
/// Technique standard (non documentée mais stable depuis Windows 7, utilisée par la plupart des
/// utilitaires de fond d'écran/widgets) : demander à Progman (le processus qui gère le bureau) de
/// créer une fenêtre "WorkerW" derrière les icônes, puis reparenter notre fenêtre dans ce WorkerW.
/// </summary>
internal static class DesktopWidgetHost
{
    private const uint WM_SPAWN_WORKER = 0x052C;

    // Le handle trouvé est gardé : SendMessageTimeout vers Progman peut bloquer jusqu'à 1 s si Explorer est
    // occupé, et FindDesktopWorkerW est appelée pour chaque module. On ne cherche à nouveau que si la
    // fenêtre n'existe plus (Explorer redémarré).
    private static IntPtr _cachedWorkerW;

    public static IntPtr FindDesktopWorkerW()
    {
        if (_cachedWorkerW != IntPtr.Zero && NativeMethods.IsWindow(_cachedWorkerW))
            return _cachedWorkerW;
        _cachedWorkerW = IntPtr.Zero;

        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return IntPtr.Zero;

        // Demande à Progman de faire apparaître un WorkerW pour héberger un fond d'écran tiers.
        // Le résultat n'est pas utilisé : c'est l'effet de bord (création de la fenêtre) qui compte.
        NativeMethods.SendMessageTimeout(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_NORMAL, 1000, out _);

        var workerW = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var shellView = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                // Le WorkerW à utiliser est celui qui suit immédiatement (en Z-order) la fenêtre
                // qui héberge les icônes du bureau.
                workerW = NativeMethods.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);

        _cachedWorkerW = workerW;
        return workerW;
    }

    /// <summary>
    /// Reparente la fenêtre donnée dans le WorkerW du bureau. Doit être appelé après la création du
    /// handle natif de la fenêtre (SourceInitialized), et la fenêtre ne doit plus être Topmost —
    /// Topmost n'a plus de sens pour une fenêtre enfant, son ordre d'affichage suit désormais celui
    /// des autres éléments du bureau (toujours sous les fenêtres normales).
    /// </summary>
    public static bool AttachToDesktop(IntPtr windowHandle)
    {
        var workerW = FindDesktopWorkerW();
        if (workerW == IntPtr.Zero)
            return false;

        return NativeMethods.SetParent(windowHandle, workerW) != IntPtr.Zero;
    }

    private static class NativeMethods
    {
        public const uint SMTO_NORMAL = 0x0000;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    }
}
