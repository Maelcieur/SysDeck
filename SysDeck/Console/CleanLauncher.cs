using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Lance un jeu comme si l'utilisateur l'ouvrait depuis le menu Démarrer : avec un environnement NEUF de la session
/// (CreateEnvironmentBlock), et non celui hérité de SysDeck. Quand SysDeck a été démarré par son installateur, cet
/// héritage (réglages de compatibilité...) fait planter certains émulateurs dès le lancement (Lichii : 0xc0000409).
/// Raccourcis, URI (steam://...) et programmes qui demandent l'administrateur passent par l'Explorateur.
/// </summary>
public static class CleanLauncher
{
    /// <summary>Démarre ; renvoie le processus, ou null s'il a été confié à l'Explorateur (pas de suivi direct).</summary>
    public static Process? Start(ProcessStartInfo psi)
    {
        var file = psi.FileName;
        var isProgram = File.Exists(file) && Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".com";
        if (!isProgram)
        {
            ViaExplorer(file, psi.Arguments, psi.WorkingDirectory);
            return null;
        }

        psi.UseShellExecute = false;
        var env = FreshEnvironment();
        if (env != null)
        {
            psi.Environment.Clear();
            foreach (var (k, v) in env) psi.Environment[k] = v;
        }
        try
        {
            return Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)   // ERROR_ELEVATION_REQUIRED
        {
            ViaExplorer(file, psi.Arguments, psi.WorkingDirectory);
            return null;
        }
    }

    /// <summary>Demande à l'Explorateur de lancer (comme un double-clic) : environnement et droits de l'Explorateur.</summary>
    private static void ViaExplorer(string file, string? args, string? dir)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application indisponible");
            dynamic shell = Activator.CreateInstance(type)!;
            try { shell.ShellExecute(file, args ?? "", dir ?? "", "open", 1); }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex)
        {
            Log.Warn("Lancement par l'Explorateur impossible, lancement direct", ex);
            Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true, WorkingDirectory = dir ?? "" })?.Dispose();
        }
    }

    /// <summary>Environnement par défaut de l'utilisateur (comme à l'ouverture de session), ou null si indisponible.</summary>
    public static Dictionary<string, string>? FreshEnvironment()
    {
        IntPtr token = IntPtr.Zero, block = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */ | 0x0002 /* TOKEN_DUPLICATE */, out token)) return null;
            if (!CreateEnvironmentBlock(out block, token, false)) return null;
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var p = block;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(p);
                if (string.IsNullOrEmpty(entry)) break;
                var eq = entry.IndexOf('=', 1);   // les variables cachées commencent par « = »
                if (eq > 0) result[entry[..eq]] = entry[(eq + 1)..];
                p += (entry.Length + 1) * 2;
            }
            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("fresh-env", "Environnement neuf indisponible, environnement de SysDeck utilisé", ex);
            return null;
        }
        finally
        {
            if (block != IntPtr.Zero) DestroyEnvironmentBlock(block);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr block);
}
