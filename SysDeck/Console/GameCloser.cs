using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>Ferme un jeu lancé depuis le mode console (bouton « Quitter »).</summary>
public static class GameCloser
{
    /// <summary>
    /// <paramref name="force"/> (émulateurs) : fin de tâche immédiate du processus et de tout ce qu'il a lancé.
    /// Sinon : fermeture normale (WM_CLOSE, comme la croix), puis fin de tâche s'il tourne encore 10 s après.
    /// </summary>
    public static void Close(int pid, IntPtr hwnd, string? exePath, bool force)
    {
        try
        {
            var targets = new List<Process>();
            if (pid != 0) TryAdd(targets, pid);
            // Émulateur dont on ne connaît pas le processus : on le retrouve par son exécutable.
            if (targets.Count == 0 && !string.IsNullOrWhiteSpace(exePath))
            {
                var name = Path.GetFileNameWithoutExtension(exePath);
                foreach (var p in Process.GetProcessesByName(name))
                {
                    var path = GameLauncher.ImagePath(p.Id);
                    if (path != null && path.Equals(Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase)) targets.Add(p);
                    else p.Dispose();
                }
            }
            if (targets.Count == 0) { Log.Warn("Mode console : jeu à fermer introuvable."); return; }

            if (!force)
            {
                if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                foreach (var p in targets) { try { p.CloseMainWindow(); } catch { } }
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(10) && targets.Any(Alive)) Thread.Sleep(250);
                if (!targets.Any(Alive)) { Log.Info("Mode console : jeu fermé normalement."); return; }
                Log.Info("Mode console : le jeu ne s'est pas fermé en 10 s, fin de tâche.");
            }

            foreach (var p in targets.Where(Alive))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) { Log.Warn($"Mode console : fin de tâche impossible ({p.Id})", ex); }
            }
            Log.Info("Mode console : fin de tâche du jeu.");
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : fermeture du jeu", ex);
        }
    }

    private static void TryAdd(List<Process> list, int pid)
    {
        try { list.Add(Process.GetProcessById(pid)); } catch { }
    }

    private static bool Alive(Process p)
    {
        try { return !p.HasExited; } catch { return false; }
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
