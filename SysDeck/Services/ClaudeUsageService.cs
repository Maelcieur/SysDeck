using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

public enum ClaudeUsageState
{
    /// <summary>Pas encore de lecture.</summary>
    Pending,
    Ok,
    /// <summary>Fichier d'identifiants absent ou illisible.</summary>
    NotConnected,
    /// <summary>Jeton expiré ou refusé (401) : Claude Code doit le renouveler.</summary>
    Expired,
    /// <summary>L'endpoint a disparu (404/410) ou répond plusieurs fois dans un format inconnu : il a sans doute changé.</summary>
    Unavailable
}

/// <summary>
/// Utilisation du quota de l'abonnement Claude (pourcentages 0-100, null si inconnu).
/// NetworkError : la dernière tentative a échoué côté réseau, les valeurs sont les précédentes.
/// UpdatedAt : heure de la dernière lecture réussie (null si aucune).
/// </summary>
public sealed record ClaudeUsageSnapshot(
    ClaudeUsageState State,
    double? SessionPercent,
    DateTimeOffset? SessionResetsAt,
    double? WeekPercent,
    DateTimeOffset? WeekResetsAt,
    string? Plan,
    bool NetworkError,
    DateTimeOffset? UpdatedAt = null)
{
    public static readonly ClaudeUsageSnapshot Empty = new(ClaudeUsageState.Pending, null, null, null, null, null, false);
}

/// <summary>
/// Lit le quota de l'abonnement Claude (session 5 h et semaine) auprès de l'API de Claude Code.
/// Un seul service pour le bureau et la Switch ; ne tourne que si <see cref="Enabled"/> est vrai.
/// Le jeton est relu à chaque rafraîchissement dans le fichier de Claude Code, qui reste seul à l'écrire :
/// on ne le modifie jamais, on ne le renouvelle pas et on ne le journalise pas.
/// </summary>
public sealed class ClaudeUsageService : IDisposable
{
    // ATTENTION : endpoint NON DOCUMENTÉ, utilisé en interne par Claude Code (jeton OAuth + en-tête beta).
    // Anthropic peut le modifier, le restreindre ou le supprimer sans préavis ; le format JSON (five_hour,
    // seven_day, utilization, resets_at) n'est garanti nulle part. Tout ce qui est lu ici doit donc être
    // validé et échouer proprement (journal une seule fois + valeur « indisponible »), jamais planter.
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(30);
    // Réponses au format inconnu tolérées d'affilée avant de déclarer l'endpoint indisponible (un raté isolé arrive).
    private const int FormatFailuresBeforeUnavailable = 3;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private bool _enabled;
    private bool _busy;
    private DateTime _lastAttempt = DateTime.MinValue;
    private int _failures;       // échecs d'affilée : espacent les tentatives (2, 4, 8… jusqu'à 30 min)
    private int _formatFailures; // réponses 200 illisibles d'affilée

    public ClaudeUsageService()
    {
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Dernier état connu (valeurs conservées en cas d'erreur réseau).</summary>
    public ClaudeUsageSnapshot Latest { get; private set; } = ClaudeUsageSnapshot.Empty;

    /// <summary>Déclenché sur le thread de l'interface à chaque nouvelle lecture.</summary>
    public event Action<ClaudeUsageSnapshot>? Updated;

    /// <summary>Vrai tant qu'au moins un module Claude est affiché. Le premier affichage lit tout de suite (sauf lecture récente).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                _timer.Start();
                if (DateTime.UtcNow - _lastAttempt >= Interval) _ = RefreshAsync();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    private static string CredentialsPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private async Task RefreshAsync()
    {
        if (_busy || !_enabled) return;
        _busy = true;
        _lastAttempt = DateTime.UtcNow;
        try
        {
            var previous = Latest;
            var (snapshot, outcome) = await Task.Run(() => FetchAsync(previous));

            _formatFailures = outcome == FetchOutcome.BadFormat ? _formatFailures + 1 : 0;
            if (_formatFailures >= FormatFailuresBeforeUnavailable)
                snapshot = Empty(ClaudeUsageState.Unavailable, snapshot.Plan);

            // Inutile d'insister toutes les 2 min sur un service coupé ou modifié : pauses de plus en plus longues.
            _failures = outcome == FetchOutcome.Ok ? 0 : _failures + 1;
            var factor = 1L << Math.Min(_failures, 4);
            _timer.Interval = TimeSpan.FromTicks(Math.Min(Interval.Ticks * factor, MaxInterval.Ticks));

            Latest = snapshot;
            Updated?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            // Seul le type de l'erreur est journalisé : jamais de détail pouvant contenir le jeton.
            Log.WarnOnce("refresh:claude", "Lecture du quota Claude en échec : " + ex.GetType().Name);
        }
        finally
        {
            _busy = false;
        }
    }

    private enum FetchOutcome { Ok, Failed, BadFormat }

    /// <summary>Hors thread de l'interface : lit le jeton puis interroge l'API.</summary>
    private static async Task<(ClaudeUsageSnapshot Snapshot, FetchOutcome Outcome)> FetchAsync(ClaudeUsageSnapshot previous)
    {
        string? token;
        long expiresAt = 0;
        string? plan = null;
        try
        {
            if (!File.Exists(CredentialsPath))
                return (Empty(ClaudeUsageState.NotConnected), FetchOutcome.Failed);

            using var stream = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return (Empty(ClaudeUsageState.NotConnected), FetchOutcome.Failed);

            token = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            if (oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number) expiresAt = e.GetInt64();
            if (oauth.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String) plan = s.GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (Empty(ClaudeUsageState.NotConnected), FetchOutcome.Failed);
        }

        if (string.IsNullOrWhiteSpace(token))
            return (Empty(ClaudeUsageState.NotConnected), FetchOutcome.Failed);
        if (expiresAt > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) <= DateTimeOffset.UtcNow)
            return (Empty(ClaudeUsageState.Expired, plan), FetchOutcome.Failed);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.0");
            using var response = await Http.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (Empty(ClaudeUsageState.Expired, plan), FetchOutcome.Failed);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // Endpoint supprimé ou déplacé : inutile de garder de vieilles valeurs affichées comme si de rien n'était.
                Log.WarnOnce("claude-gone", $"Quota Claude indisponible : l'endpoint répond {(int)response.StatusCode} (non documenté, a pu être retiré)");
                return (Empty(ClaudeUsageState.Unavailable, plan), FetchOutcome.Failed);
            }
            if (!response.IsSuccessStatusCode)
                return (previous with { NetworkError = true }, FetchOutcome.Failed);

            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (Parse(body.RootElement, plan, DateTimeOffset.Now) is { } snapshot)
                return (snapshot, FetchOutcome.Ok);

            // Réponse 200 mais aucune des deux fenêtres lisible : le format de l'endpoint a sans doute
            // changé. On n'affiche pas de faux 0 % : valeurs précédentes conservées, indicateur d'erreur.
            Log.WarnOnce("claude-format", "Quota Claude indisponible : format de réponse inattendu (endpoint non documenté, peut avoir changé)");
            return (previous with { NetworkError = true }, FetchOutcome.BadFormat);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException
                                       or InvalidOperationException or FormatException)
        {
            // Réseau coupé ou réponse illisible / de forme inattendue : on garde les dernières valeurs.
            var outcome = ex is JsonException or InvalidOperationException or FormatException ? FetchOutcome.BadFormat : FetchOutcome.Failed;
            return (previous with { NetworkError = true }, outcome);
        }
    }

    private static ClaudeUsageSnapshot Empty(ClaudeUsageState state, string? plan = null)
        => new(state, null, null, null, null, plan, false);

    /// <summary>
    /// Décode la réponse de l'endpoint (fenêtres « five_hour » et « seven_day ») ; null si aucune n'est lisible,
    /// signe que le format a changé. Pure, testée.
    /// </summary>
    internal static ClaudeUsageSnapshot? Parse(JsonElement root, string? plan, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var (sessionPct, sessionReset) = ReadWindow(root, "five_hour");
        var (weekPct, weekReset) = ReadWindow(root, "seven_day");
        if (sessionPct is null && weekPct is null) return null;
        return new ClaudeUsageSnapshot(ClaudeUsageState.Ok, sessionPct, sessionReset, weekPct, weekReset, plan, false, now);
    }

    /// <summary>Lit « utilization » et « resets_at » d'une fenêtre ; (null, null) si elle est absente ou nulle.</summary>
    private static (double? Percent, DateTimeOffset? ResetsAt) ReadWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return (null, null);

        double? percent = window.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
        // Hors 0-100 : format différent de celui attendu (ex. fraction ou autre unité) -> considéré inconnu.
        if (percent is < 0 or > 100) percent = null;
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            reset = parsed;
        return (percent, reset);
    }

    public void Dispose() => _timer.Stop();
}
