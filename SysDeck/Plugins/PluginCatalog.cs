using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using SysDeck.Infrastructure;

namespace SysDeck.Plugins;

/// <summary>Sorte de plugin d'une fiche du catalogue.</summary>
public enum CatalogKind { SysDeck, StreamDeck }

/// <summary>Plugin trouvé sur GitHub (dépôt) : fiche du catalogue.</summary>
public sealed record CatalogEntry(
    string Repo, string Name, string? Description, string Owner, int Stars, DateTime? Updated, string? License,
    string Url, CatalogKind Kind, bool Verified);

/// <summary>Dernière version publiée d'un dépôt et le fichier à installer.</summary>
public sealed record CatalogRelease(string Tag, string AssetName, string DownloadUrl, long Size);

/// <summary>
/// Catalogue de plugins sur GitHub, sans compte : la liste « vérifiés » (fichier <c>plugins/catalogue.json</c> du dépôt
/// SysDeck) et une recherche des dépôts marqués <c>sysdeck-plugin</c> ou <c>streamdeck-plugin</c>. L'installation
/// télécharge le fichier de la dernière version publiée (.sysdeckplugin, .streamDeckPlugin ou .zip).
/// </summary>
public static class PluginCatalog
{
    public const string SysDeckTopic = "sysdeck-plugin";
    public const string StreamDeckTopic = "streamdeck-plugin";

    /// <summary>Liste des plugins vérifiés, tenue dans le dépôt de SysDeck.</summary>
    public const string VerifiedUrl = "https://raw.githubusercontent.com/fenixo76/SysDeck/main/plugins/catalogue.json";

    private const long MaxDownload = 500L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SysDeck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// Plugins vérifiés puis dépôts GitHub correspondant à la recherche (plugins SysDeck d'abord, puis Stream Deck).
    /// Lève <see cref="CatalogException"/> avec un message lisible (pas de réseau, limite de GitHub atteinte…).
    /// </summary>
    public static async Task<IReadOnlyList<CatalogEntry>> SearchAsync(string query, CancellationToken token)
    {
        var results = new List<CatalogEntry>();
        try
        {
            var verified = await GetVerifiedAsync(token);
            results.AddRange(verified.Where(v => Matches(v, query)));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        {
            Log.Info($"Liste des plugins vérifiés indisponible : {ex.Message}");
        }

        foreach (var (topic, kind) in new[] { (SysDeckTopic, CatalogKind.SysDeck), (StreamDeckTopic, CatalogKind.StreamDeck), ("stream-deck-plugin", CatalogKind.StreamDeck) })
        {
            var q = Uri.EscapeDataString($"{query.Trim()} topic:{topic} archived:false".Trim());
            var json = await GetJsonAsync($"https://api.github.com/search/repositories?q={q}&sort=stars&order=desc&per_page=30", token);
            foreach (var entry in ParseSearch(json, kind))
                if (!results.Any(r => string.Equals(r.Repo, entry.Repo, StringComparison.OrdinalIgnoreCase)))
                    results.Add(entry);
        }
        return results;
    }

    private static bool Matches(CatalogEntry entry, string query)
        => string.IsNullOrWhiteSpace(query)
           || entry.Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)
           || (entry.Description?.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) ?? false)
           || entry.Repo.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<CatalogEntry>> GetVerifiedAsync(CancellationToken token)
    {
        using var response = await Http.GetAsync(VerifiedUrl, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return Array.Empty<CatalogEntry>(); // liste pas encore publiée
        response.EnsureSuccessStatusCode();
        return ParseVerified(await response.Content.ReadAsStringAsync(token));
    }

    /// <summary>Dernière version publiée et son fichier de plugin ; null si le dépôt n'en publie aucun.</summary>
    public static async Task<CatalogRelease?> GetLatestReleaseAsync(string repo, CancellationToken token)
    {
        try
        {
            return ParseRelease(await GetJsonAsync($"https://api.github.com/repos/{repo}/releases/latest", token));
        }
        catch (CatalogException ex) when (ex.NotFound)
        {
            return null;
        }
    }

    /// <summary>Télécharge le fichier d'une version dans un dossier temporaire ; renvoie son chemin.</summary>
    public static async Task<string> DownloadAsync(CatalogRelease release, CancellationToken token)
    {
        if (release.Size > MaxDownload) throw new CatalogException("Ce plugin est trop volumineux pour être installé.");
        var folder = Path.Combine(Path.GetTempPath(), "SysDeck-plugins");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}-{Path.GetFileName(release.AssetName)}");
        try
        {
            using var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                if (total > MaxDownload) throw new CatalogException("Ce plugin est trop volumineux pour être installé.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            return path;
        }
        catch (HttpRequestException ex)
        {
            TryDelete(path);
            throw new CatalogException($"Téléchargement impossible : {ex.Message}");
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* fichier temporaire */ }
    }

    private static async Task<string> GetJsonAsync(string url, CancellationToken token)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url, token);
        }
        catch (HttpRequestException ex)
        {
            throw new CatalogException($"GitHub est injoignable : {ex.Message}");
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CatalogException("GitHub ne répond pas : réessaie dans un instant.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) throw new CatalogException("Introuvable sur GitHub.", notFound: true);
            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
                throw new CatalogException("GitHub limite les recherches sans compte (environ 10 par minute) : réessaie dans une minute.");
            if (!response.IsSuccessStatusCode) throw new CatalogException($"GitHub a répondu {(int)response.StatusCode}.");
            return await response.Content.ReadAsStringAsync(token);
        }
    }

    // ---------- Lecture des réponses (partie testée) ----------

    internal static IReadOnlyList<CatalogEntry> ParseSearch(string json, CatalogKind kind)
    {
        var list = new List<CatalogEntry>();
        if (JsonNode.Parse(json)?["items"] is not JsonArray items) return list;
        foreach (var item in items.OfType<JsonObject>())
        {
            if (Text(item, "full_name") is not { } repo || Text(item, "html_url") is not { } url) continue;
            list.Add(new CatalogEntry(
                repo,
                Pretty(Text(item, "name") ?? repo),
                Text(item, "description"),
                item["owner"] is JsonObject owner ? Text(owner, "login") ?? "" : "",
                item["stargazers_count"] is JsonValue s && s.TryGetValue(out int stars) ? stars : 0,
                Text(item, "pushed_at") is { } pushed && DateTime.TryParse(pushed, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : null,
                item["license"] is JsonObject license ? Text(license, "spdx_id") : null,
                url,
                kind,
                Verified: false));
        }
        return list;
    }

    /// <summary>catalogue.json : [{ "repo": "auteur/depot", "name", "description", "kind": "sysdeck"|"streamdeck" }].</summary>
    internal static IReadOnlyList<CatalogEntry> ParseVerified(string json)
    {
        var list = new List<CatalogEntry>();
        if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonArray items)
            return list;
        foreach (var item in items.OfType<JsonObject>())
        {
            if (Text(item, "repo") is not { } repo || repo.Count(c => c == '/') != 1) continue;
            list.Add(new CatalogEntry(repo, Text(item, "name") ?? Pretty(repo.Split('/')[1]), Text(item, "description"), repo.Split('/')[0],
                0, null, null, $"https://github.com/{repo}",
                string.Equals(Text(item, "kind"), "streamdeck", StringComparison.OrdinalIgnoreCase) ? CatalogKind.StreamDeck : CatalogKind.SysDeck,
                Verified: true));
        }
        return list;
    }

    /// <summary>
    /// Fichier à installer d'une version : .sysdeckplugin, sinon .streamDeckPlugin (la variante Windows plutôt que macOS),
    /// sinon un .zip dont le nom parle de plugin (pas une extension de navigateur) ; null s'il n'y en a pas.
    /// </summary>
    internal static CatalogRelease? ParseRelease(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject release || release["assets"] is not JsonArray assets) return null;
        var tag = Text(release, "tag_name") ?? "";
        var files = assets.OfType<JsonObject>()
            .Select(a => (Name: Text(a, "name") ?? "", Url: Text(a, "browser_download_url") ?? "", Size: a["size"] is JsonValue v && v.TryGetValue(out long size) ? size : 0))
            .Where(a => a.Name.Length > 0 && a.Url.StartsWith("https://", StringComparison.Ordinal))
            .ToList();

        static bool IsMac(string name) => name.Contains("mac", StringComparison.OrdinalIgnoreCase) || name.Contains("darwin", StringComparison.OrdinalIgnoreCase);
        var pick = files.FirstOrDefault(f => f.Name.EndsWith(".sysdeckplugin", StringComparison.OrdinalIgnoreCase));
        if (pick.Name is null)
        {
            var streamDeck = files.Where(f => f.Name.EndsWith(".streamDeckPlugin", StringComparison.OrdinalIgnoreCase)).ToList();
            pick = streamDeck.FirstOrDefault(f => !IsMac(f.Name));
            if (pick.Name is null) pick = streamDeck.FirstOrDefault();
        }
        if (pick.Name is null)
            pick = files.FirstOrDefault(f => f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !IsMac(f.Name)
                                             && (f.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("sdPlugin", StringComparison.OrdinalIgnoreCase)));
        return pick.Name is null ? null : new CatalogRelease(tag, pick.Name, pick.Url, pick.Size);
    }

    /// <summary>« streamdeck-spotify-plugin » → « Streamdeck spotify plugin ».</summary>
    internal static string Pretty(string name)
    {
        var text = name.Replace('-', ' ').Replace('_', ' ').Trim();
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string? Text(JsonObject obj, string name)
        => obj[name] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

/// <summary>Erreur du catalogue, avec un message à montrer tel quel.</summary>
public sealed class CatalogException : Exception
{
    public CatalogException(string message, bool notFound = false) : base(message) => NotFound = notFound;

    public bool NotFound { get; }
}
