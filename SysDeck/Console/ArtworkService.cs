using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

public enum ArtworkSource { Steam, SteamGridDb }

/// <summary>Résultat de recherche proposé à l'utilisateur.</summary>
public sealed record ArtworkCandidate(string Title, ArtworkSource Source, string AppId);

/// <summary>Visuels téléchargés (chemins locaux, vides si introuvables) et description nettoyée.</summary>
public sealed class ArtworkResult
{
    public string CoverPath { get; set; } = "";
    public string HeroPath { get; set; } = "";
    public string LogoPath { get; set; } = "";
    public string Description { get; set; } = "";
    public string? SteamAppId { get; set; }
}

/// <summary>
/// Recherche de jaquette, fond et description : Steam (sans clé) puis SteamGridDB (clé API facultative).
/// Aucune méthode ne lève d'exception : en cas d'échec réseau, résultat vide.
/// </summary>
public sealed class ArtworkService
{
    private static readonly HttpClient SharedHttp = CreateHttp();
    private const string SteamCdn = "https://cdn.cloudflare.steamstatic.com/steam/apps/";

    private readonly HttpClient _http;
    private readonly string? _gridKey;
    private readonly string _artDir;

    public ArtworkService(string? steamGridDbKey = null, HttpClient? http = null, string? artDir = null)
    {
        _gridKey = string.IsNullOrWhiteSpace(steamGridDbKey) ? null : steamGridDbKey.Trim();
        _http = http ?? SharedHttp;
        _artDir = artDir ?? ConsoleLibrary.ArtDir;
    }

    /// <summary>Vrai si une clé SteamGridDB a été fournie.</summary>
    public bool HasSteamGridDb => _gridKey != null;

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SysDeck/1.0");
        return c;
    }

    // ---------- Recherche ----------

    /// <summary>
    /// Steam d'abord ; SteamGridDB si Steam ne trouve rien ou si <paramref name="includeSteamGridDb"/>.
    /// <paramref name="gridOnly"/> (jeux d'émulateur) saute Steam.
    /// </summary>
    public async Task<List<ArtworkCandidate>> SearchAsync(string title, bool includeSteamGridDb = false,
        bool gridOnly = false, CancellationToken ct = default)
    {
        var list = new List<ArtworkCandidate>();
        if (string.IsNullOrWhiteSpace(title)) return list;
        try
        {
            if (!gridOnly)
            {
                var json = await GetStringAsync(
                    $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(title)}&l=french&cc=FR", null, ct);
                list.AddRange(ParseSteamSearch(json));
            }
            if (_gridKey != null && (gridOnly || includeSteamGridDb || list.Count == 0))
            {
                var grid = await GetStringAsync(
                    $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(title)}", _gridKey, ct);
                list.AddRange(ParseGridSearch(grid));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.WarnOnce("console-art-search", "Recherche de visuels impossible", ex);
        }
        return list;
    }

    // ---------- Téléchargement ----------

    public async Task<ArtworkResult> DownloadAsync(ArtworkCandidate c, string gameId, CancellationToken ct = default)
    {
        var res = new ArtworkResult();
        try
        {
            Directory.CreateDirectory(_artDir);
            if (c.Source == ArtworkSource.Steam)
            {
                res.SteamAppId = c.AppId;
                var id = c.AppId;
                res.CoverPath = await DownloadFirstAsync(
                    new[] { $"{SteamCdn}{id}/library_600x900_2x.jpg", $"{SteamCdn}{id}/library_600x900.jpg", $"{SteamCdn}{id}/header.jpg" },
                    gameId + "-cover", ct);
                res.HeroPath = await DownloadFirstAsync(new[] { $"{SteamCdn}{id}/library_hero.jpg" }, gameId + "-hero", ct);
                res.LogoPath = await DownloadFirstAsync(new[] { $"{SteamCdn}{id}/logo.png" }, gameId + "-logo", ct);
                try
                {
                    var json = await GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={id}&l=french", null, ct);
                    res.Description = ParseSteamDescription(json, id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.WarnOnce("console-art-desc", "Description Steam indisponible", ex);
                }
            }
            else if (_gridKey != null)
            {
                var id = c.AppId;
                res.CoverPath = await GridImageAsync($"grids/game/{id}?dimensions=600x900", gameId + "-cover", ct);
                res.HeroPath = await GridImageAsync($"heroes/game/{id}", gameId + "-hero", ct);
                res.LogoPath = await GridImageAsync($"logos/game/{id}", gameId + "-logo", ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.WarnOnce("console-art-dl", "Téléchargement des visuels impossible", ex);
        }
        return res;
    }

    private async Task<string> GridImageAsync(string path, string name, CancellationToken ct)
    {
        try
        {
            var json = await GetStringAsync("https://www.steamgriddb.com/api/v2/" + path, _gridKey, ct);
            return await DownloadFirstAsync(ParseGridImageUrls(json).Take(3), name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }

    private async Task<string> DownloadFirstAsync(IEnumerable<string> urls, string baseName, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 200) continue;
                var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
                if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) ext = ".jpg";
                var file = Path.Combine(_artDir, baseName + ext);
                foreach (var old in Directory.GetFiles(_artDir, baseName + ".*")) { try { File.Delete(old); } catch { } }
                await File.WriteAllBytesAsync(file, bytes, ct);
                return file;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // on essaie l'adresse suivante
            }
        }
        return "";
    }

    private async Task<string> GetStringAsync(string url, string? bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ---------- Analyse des réponses (pures, testées) ----------

    public static List<ArtworkCandidate> ParseSteamSearch(string json)
    {
        var list = new List<ArtworkCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return list;
            foreach (var it in items.EnumerateArray())
            {
                if (!it.TryGetProperty("id", out var id) || !it.TryGetProperty("name", out var name)) continue;
                list.Add(new ArtworkCandidate(name.GetString() ?? "", ArtworkSource.Steam, id.ToString()));
                if (list.Count >= 10) break;
            }
        }
        catch (Exception) { }
        return list;
    }

    public static string ParseSteamDescription(string json, string appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(appId, out var app)
                && app.TryGetProperty("success", out var ok) && ok.GetBoolean()
                && app.TryGetProperty("data", out var data)
                && data.TryGetProperty("short_description", out var d))
                return CleanHtml(d.GetString());
        }
        catch (Exception) { }
        return "";
    }

    public static List<ArtworkCandidate> ParseGridSearch(string json)
    {
        var list = new List<ArtworkCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
            foreach (var it in data.EnumerateArray())
            {
                if (!it.TryGetProperty("id", out var id) || !it.TryGetProperty("name", out var name)) continue;
                list.Add(new ArtworkCandidate(name.GetString() ?? "", ArtworkSource.SteamGridDb, id.ToString()));
                if (list.Count >= 10) break;
            }
        }
        catch (Exception) { }
        return list;
    }

    public static List<string> ParseGridImageUrls(string json)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
            foreach (var it in data.EnumerateArray())
                if (it.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } s && s.StartsWith("http")) list.Add(s);
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>Retire les balises HTML, décode les entités et compacte les espaces.</summary>
    public static string CleanHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = Regex.Replace(html, @"<\s*(br|/p|/li|/h\d)\s*/?>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
