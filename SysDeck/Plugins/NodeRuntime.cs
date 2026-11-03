using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using SysDeck.Infrastructure;
using SysDeck.Services;

namespace SysDeck.Plugins;

/// <summary>
/// Node.js pour les plugins Stream Deck écrits en JavaScript (le logiciel d'Elgato embarque le sien) : celui du PC s'il
/// est assez récent, sinon une version officielle téléchargée une fois sur nodejs.org (empreinte SHA-256 vérifiée) et
/// rangée dans <c>Runtime\</c>.
/// </summary>
public static class NodeRuntime
{
    private static readonly Dictionary<int, Task<string>> Downloads = new();

    public static string Dir => AppPaths.Folder("Runtime");

    /// <summary>node.exe utilisable tout de suite pour la version majeure demandée (au moins), sinon null.</summary>
    public static string? Find(int major)
    {
        // Version déjà téléchargée par SysDeck : la plus récente de cette majeure.
        if (Directory.Exists(Dir))
        {
            var managed = Directory.GetDirectories(Dir, $"node-v{major}.*-win-x64")
                .Select(d => Path.Combine(d, "node.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (managed is not null) return managed;
        }
        // Node du PC : même majeure ou plus récente (les plugins visent une version minimale).
        return SystemNode() is { } system && SystemMajor(system) >= major ? system : null;
    }

    /// <summary>Télécharge (une seule fois, même si plusieurs plugins le demandent) la dernière version de la majeure.</summary>
    public static Task<string> EnsureAsync(int major)
    {
        lock (Downloads)
        {
            // Échec ou délai dépassé : on retente ; sinon le téléchargement en cours (ou fini) est partagé.
            if (Downloads.TryGetValue(major, out var running) && !running.IsFaulted && !running.IsCanceled) return running;
            // Hors du thread de l'interface : empreinte et décompression de milliers de fichiers ne la figent pas.
            return Downloads[major] = Task.Run(() => DownloadAsync(major));
        }
    }

    private static async Task<string> DownloadAsync(int major)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SysDeck");
        var folder = $"https://nodejs.org/dist/latest-v{major}.x/";

        // Nom exact et empreinte du zip Windows x64, lus dans la liste officielle.
        var sums = await http.GetStringAsync(folder + "SHASUMS256.txt");
        var (hash, file) = ParseShasums(sums) ?? throw new InvalidDataException($"Node.js {major} pour Windows introuvable sur nodejs.org.");

        Directory.CreateDirectory(Dir);
        var zip = Path.Combine(Dir, file + ".part");
        try
        {
            await using (var stream = await http.GetStreamAsync(folder + file))
            await using (var output = File.Create(zip))
                await stream.CopyToAsync(output);

            string actual;
            await using (var check = File.OpenRead(zip))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Téléchargement de Node.js corrompu (empreinte SHA-256 différente).");

            var staging = Path.Combine(Dir, $".node-{Guid.NewGuid():N}");
            SafeZip.Extract(zip, staging, 400L * 1024 * 1024, 60_000);
            var extracted = Directory.GetDirectories(staging).Single();
            var dest = Path.Combine(Dir, Path.GetFileName(extracted));
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(extracted, dest);
            Directory.Delete(staging, recursive: true);
            Log.Info($"Node.js installé pour les plugins : {Path.GetFileName(dest)}");
            return Path.Combine(dest, "node.exe");
        }
        finally
        {
            try { File.Delete(zip); } catch { /* reliquat sans conséquence */ }
        }
    }

    /// <summary>Ligne « empreinte  node-vX.Y.Z-win-x64.zip » de SHASUMS256.txt ; null si absente.</summary>
    internal static (string Hash, string File)? ParseShasums(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].StartsWith("node-v", StringComparison.Ordinal) && parts[1].EndsWith("-win-x64.zip", StringComparison.Ordinal)
                && parts[0].Length == 64)
                return (parts[0], parts[1]);
        }
        return null;
    }

    private static string? _systemNode;
    private static bool _systemSearched;

    /// <summary>node.exe trouvé dans le PATH (cherché une fois).</summary>
    private static string? SystemNode()
    {
        if (_systemSearched) return _systemNode;
        _systemSearched = true;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(path)) return _systemNode = path;
            }
            catch (ArgumentException) { /* entrée du PATH invalide */ }
        }
        return null;
    }

    private static int? _systemMajor;

    /// <summary>Version majeure du Node du PC, lue une seule fois.</summary>
    private static int SystemMajor(string node) => _systemMajor ??= MajorOf(node);

    /// <summary>Version majeure d'un node.exe (« node -v », 3 s au plus), 0 si illisible.</summary>
    private static int MajorOf(string node)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(node, "-v") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (process is null) return 0;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!output.Wait(3000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return 0;
            }
            return ParseMajor(output.Result.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>« v24.18.0 » → 24 ; 0 si illisible.</summary>
    internal static int ParseMajor(string version)
        => int.TryParse(version.TrimStart('v', 'V').Split('.')[0], out var major) ? major : 0;
}
