using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>Résultat d'un lancement : démarré ou non, message d'erreur éventuel.</summary>
public sealed record LaunchOutcome(bool Started, string? Error);

/// <summary>Lance un jeu (exe, URI ou émulateur) et attend sa fin.</summary>
public static class GameLauncher
{
    private static readonly TimeSpan UntrackedLimit = TimeSpan.FromHours(4);

    /// <summary>
    /// Lance <paramref name="game"/> et attend. Retourne quand le jeu est terminé, ou quand <paramref name="ct"/> est annulé.
    /// </summary>
    public static async Task<LaunchOutcome> RunAsync(ConsoleGame game, ConsoleLibrary library, CancellationToken ct)
    {
        try
        {
            ProcessStartInfo psi;
            string? watchDir = null;
            var emulator = game.UsesEmulator ? library.FindEmulator(game.EmulatorId) : null;
            if (game.UsesEmulator)
            {
                if (emulator == null) return new LaunchOutcome(false, "Émulateur introuvable");
                if (!File.Exists(emulator.ExePath)) return new LaunchOutcome(false, "Exécutable de l'émulateur introuvable");
                // Famille Eden / yuzu (Lichii...) : en fenêtre sur la TV seule, l'émulateur plante au démarrage.
                // En mode console on force donc le plein écran, même si la case est décochée.
                var effective = emulator;
                if (!emulator.Fullscreen && EmulatorPresets.IsYuzuFamily(emulator.ExePath))
                {
                    EmulatorPresets.EnsureFullscreenFlag(emulator);
                    effective = new ConsoleEmulator
                    {
                        ExePath = emulator.ExePath, ArgumentsTemplate = emulator.ArgumentsTemplate, WorkingDir = emulator.WorkingDir,
                        Core = emulator.Core, FullscreenFlag = string.IsNullOrWhiteSpace(emulator.FullscreenFlag) ? "-f" : emulator.FullscreenFlag,
                        Fullscreen = true,
                    };
                }
                var cmd = EmulatorCommandBuilder.BuildEmulatorCommand(effective, game);
                Log.Info($"Mode console : {emulator.Name} {cmd.Arguments}");
                psi = new ProcessStartInfo(cmd.FileName, cmd.Arguments) { WorkingDirectory = cmd.WorkingDir, UseShellExecute = true };
            }
            else if (game.UsesUri)
            {
                psi = new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true };
                var id = game.EffectiveSteamAppId;
                if (id != null) watchDir = SteamLocator.FindInstallDir(id);
            }
            else
            {
                if (!File.Exists(game.ExePath)) return new LaunchOutcome(false, "Exécutable introuvable");
                var exe = game.ExePath;
                var args = game.Arguments ?? "";
                var dir = game.WorkingDir;
                var ext = Path.GetExtension(exe).ToLowerInvariant();
                string? uri = null;
                // Raccourcis : on lance la vraie cible (et on surveille SON dossier, pas celui du raccourci).
                if (ext == ".lnk" && ShortcutResolver.ResolveLnk(exe) is { } lnk && File.Exists(lnk.Target))
                {
                    exe = lnk.Target;
                    if (string.IsNullOrWhiteSpace(args)) args = lnk.Arguments;
                    if (string.IsNullOrWhiteSpace(dir)) dir = lnk.WorkingDir;
                }
                else if (ext == ".url") uri = ShortcutResolver.ResolveUrl(exe);

                if (uri != null)
                {
                    psi = new ProcessStartInfo(uri) { UseShellExecute = true };
                    var id = ConsoleGame.ParseSteamAppId(uri);
                    if (id != null) watchDir = SteamLocator.FindInstallDir(id);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(dir)) dir = Path.GetDirectoryName(exe) ?? "";
                    psi = new ProcessStartInfo(exe, args) { WorkingDirectory = dir, UseShellExecute = true };
                    watchDir = ext is ".lnk" or ".url" && exe == game.ExePath ? null : GameRoot(exe);
                }
            }

            var started = Stopwatch.StartNew();
            using var proc = CleanLauncher.Start(psi);   // environnement neuf : pas celui hérité de SysDeck
            if (game.UsesEmulator) watchDir = Path.GetDirectoryName(emulator!.ExePath);

            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
                // Lanceur qui rend la main vite : le vrai jeu tourne alors dans le dossier d'installation.
                if (started.Elapsed < TimeSpan.FromSeconds(30) && watchDir != null && !game.UsesEmulator)
                    await WaitForDirAsync(watchDir, TimeSpan.FromSeconds(20), ct);
            }
            else if (watchDir != null)
                await WaitForDirAsync(watchDir, TimeSpan.FromSeconds(90), ct);
            else
                await Task.Delay(UntrackedLimit, ct);
            return new LaunchOutcome(true, null);
        }
        catch (OperationCanceledException)
        {
            return new LaunchOutcome(true, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Lancement de « {game.Title} » impossible", ex);
            return new LaunchOutcome(false, "Lancement impossible : " + ex.Message);
        }
    }

    /// <summary>
    /// Dossier à surveiller pour un exe : pour un jeu Steam, tout le dossier steamapps\common\&lt;jeu&gt;
    /// (les lanceurs démarrent souvent le vrai jeu dans un sous-dossier), sinon le dossier de l'exe.
    /// </summary>
    public static string? GameRoot(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        const string marker = "\\steamapps\\common\\";
        var i = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var rest = full[(i + marker.Length)..];
            var slash = rest.IndexOf('\\');
            if (slash > 0) return full[..(i + marker.Length + slash)];
        }
        return Path.GetDirectoryName(full);
    }

    /// <summary>Attend l'apparition d'un processus du dossier (au plus <paramref name="appear"/>), puis sa disparition.</summary>
    private static async Task WaitForDirAsync(string dir, TimeSpan appear, CancellationToken ct)
    {
        var prefix = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        var sw = Stopwatch.StartNew();
        while (!AnyProcessIn(prefix))
        {
            if (sw.Elapsed > appear) return;
            await Task.Delay(1500, ct);
        }
        var misses = 0;
        while (misses < 2)
        {
            await Task.Delay(3000, ct);
            misses = AnyProcessIn(prefix) ? 0 : misses + 1;
        }
    }

    private static bool AnyProcessIn(string prefix)
    {
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self) continue;
                var path = ImagePath(p.Id);
                if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    internal static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}

/// <summary>Retrouve le dossier d'installation d'un jeu Steam à partir de son identifiant.</summary>
public static class SteamLocator
{
    public static string? FindInstallDir(string appId)
    {
        try
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrWhiteSpace(steam)) return null;
            steam = steam.Replace('/', '\\');
            var libs = new List<string> { steam };
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf)) libs.AddRange(ParseLibraryPaths(File.ReadAllText(vdf)));
            foreach (var lib in libs)
            {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;
                var name = ParseInstallDir(File.ReadAllText(acf));
                if (name == null) continue;
                var dir = Path.Combine(lib, "steamapps", "common", name);
                if (Directory.Exists(dir)) return dir;
            }
        }
        catch (Exception ex)
        {
            Log.WarnOnce("steam-locate", "Dossier Steam introuvable", ex);
        }
        return null;
    }

    public static List<string> ParseLibraryPaths(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace("\\\\", "\\")).ToList();

    public static string? ParseInstallDir(string acf)
    {
        var m = Regex.Match(acf, "\"installdir\"\\s+\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }
}
