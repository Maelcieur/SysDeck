using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using SysDeck.Infrastructure;
using SysDeck.Models;

namespace SysDeck.Services;

/// <summary>Réponse de <c>/oauth2/device</c> : codes à présenter à l'utilisateur et rythme d'interrogation.</summary>
public sealed record TwitchDeviceCode(string DeviceCode, string UserCode, string VerificationUri, int Interval, int ExpiresIn);

public enum TwitchTokenOutcome { Success, Pending, SlowDown, Denied, Expired, Rejected, Failed }

/// <summary>Réponse de <c>/oauth2/token</c> lue : jetons en cas de succès, sinon la raison.</summary>
public sealed record TwitchTokenResult(TwitchTokenOutcome Outcome, string? AccessToken, string? RefreshToken, int ExpiresIn, string? Message);

/// <summary>Réponse de <c>/oauth2/validate</c>.</summary>
public sealed record TwitchIdentity(string Login, string UserId, int ExpiresIn);

/// <summary>État du direct (pour les tuiles et un futur module Stream).</summary>
public sealed record TwitchStreamInfo(bool Live, int Viewers, DateTimeOffset? StartedAt);

/// <summary>Modes du tchat.</summary>
public sealed record TwitchChatSettings(bool Slow, bool Emote, bool Followers, bool Subscribers);

public sealed record TwitchCategory(string Id, string Name, string? BoxArtUrl = null);

/// <summary>Émote utilisable par le compte ; <see cref="Type"/> = « globals », « subscriptions », « follower », « channelpoints »…</summary>
public sealed record TwitchEmote(string Id, string Name, string Type, string ImageUrl);

/// <summary>Page de <c>chat/emotes/user</c> : émotes, curseur de la page suivante (null = fin).</summary>
public sealed record TwitchEmotePage(List<TwitchEmote> Emotes, string? Cursor);

/// <summary>Résultat d'une action : erreur lisible, ou null ; notification et texte à copier facultatifs.</summary>
public sealed record TwitchActionResult(string? Error, string? Notice = null, string? Clipboard = null);

public sealed record HelixResponse(int Status, string Body);

/// <summary>Échec d'authentification ; <see cref="Fatal"/> : la session est perdue (jeton révoqué ou expiré), l'utilisateur doit se reconnecter.</summary>
public sealed class TwitchAuthException : Exception
{
    public bool Fatal { get; }

    public TwitchAuthException(string message, bool fatal) : base(message) => Fatal = fatal;
}

/// <summary>État de la chaîne tenu à jour par interrogation. Sans verrou : <see cref="TwitchService"/> l'utilise sous verrou, les tests directement.</summary>
public sealed class TwitchState
{
    public TwitchStreamInfo Stream { get; private set; } = new(false, 0, null);
    public TwitchChatSettings? Chat { get; private set; }
    public bool? Shield { get; private set; }

    public void Clear()
    {
        Stream = new(false, 0, null);
        Chat = null;
        Shield = null;
    }

    /// <summary>Renvoie vrai si la valeur a changé.</summary>
    public bool SetStream(TwitchStreamInfo value)
    {
        var changed = value != Stream;
        Stream = value;
        return changed;
    }

    public bool SetChat(TwitchChatSettings value)
    {
        var changed = value != Chat;
        Chat = value;
        return changed;
    }

    public bool SetShield(bool value)
    {
        var changed = Shield != value;
        Shield = value;
        return changed;
    }

    /// <summary>Vrai quand le mode est actif ; null si inconnu ou si l'action n'a pas d'état.</summary>
    public bool? IsOn(TwitchAction action) => action switch
    {
        TwitchAction.ShieldMode => Shield,
        TwitchAction.SlowMode or TwitchAction.EmoteOnly or TwitchAction.FollowersOnly or TwitchAction.SubscribersOnly
            => Chat is { } chat ? TwitchService.ChatMode(chat, action) : null,
        _ => null
    };
}

/// <summary>
/// Client de l'API Helix de Twitch : connexion par Device Code Flow (client public, sans secret), rafraîchissement des jetons,
/// actions des tuiles, état du direct et du tchat relu toutes les 30 s. <see cref="StateChanged"/> est levé sur le contexte
/// de synchronisation capturé à la création (thread interface).
/// </summary>
public sealed class TwitchService : IDisposable
{
    /// <summary>Identifiant de l'application Twitch (type « Public » : il n'est pas confidentiel).</summary>
    public const string ClientId = "nzx05mokz3lr5ffqjed785erqxeiyz";

    public const string Scopes = "clips:edit channel:manage:broadcast user:write:chat channel:edit:commercial "
        + "moderator:manage:chat_settings moderator:manage:shield_mode moderator:read:followers user:read:emotes";

    /// <summary>Droit de lecture des émotes, ajouté après la première version : un ancien compte doit se reconnecter.</summary>
    public const string EmotesScope = "user:read:emotes";

    public static readonly int[] CommercialLengths = { 30, 60, 90, 120, 150, 180 };

    private const string DeviceUrl = "https://id.twitch.tv/oauth2/device";
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string ValidateUrl = "https://id.twitch.tv/oauth2/validate";
    private const string RevokeUrl = "https://id.twitch.tv/oauth2/revoke";
    private const string HelixUrl = "https://api.twitch.tv/helix/";

    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ValidateEvery = TimeSpan.FromMinutes(55);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly HttpClient _http;
    private readonly TwitchState _state = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _configLock = new();

    private TwitchSettings? _settings;
    private Action? _save;
    private CancellationTokenSource? _cts;
    private string? _access, _refresh;
    private DateTimeOffset? _expires;
    private DateTime _lastValidate;
    private bool _disposed, _connected, _watchTiles, _watchShield;

    public TwitchService(HttpMessageHandler? handler = null)
        => _http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) } : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Levé quand la connexion ou une valeur de l'état change (sur le thread interface).</summary>
    public event Action? StateChanged;

    public bool IsConnected => Volatile.Read(ref _connected);
    public string? Login => _settings?.Login;
    public string? UserId => _settings?.UserId;

    /// <summary>Dernière raison d'échec ou de déconnexion (vide tant que tout va bien).</summary>
    public string? LastError { get; private set; }

    /// <summary>Dernier état du direct lu (en live, spectateurs, heure de début).</summary>
    public TwitchStreamInfo Stream { get { lock (_state) return _state.Stream; } }

    /// <summary>État « actif » d'un mode (voir <see cref="TwitchState.IsOn"/>) ; null si Twitch est déconnecté ou si l'état est inconnu.</summary>
    public bool? IsOn(TwitchAction action)
    {
        if (!IsConnected) return null;
        lock (_state) return _state.IsOn(action);
    }

    // ---------- Pur : formulaires, corps JSON, lecture des réponses ----------

    public static Dictionary<string, string> DeviceForm() => new() { ["client_id"] = ClientId, ["scopes"] = Scopes };

    public static Dictionary<string, string> PollForm(string deviceCode) => new()
    {
        ["client_id"] = ClientId,
        ["scopes"] = Scopes,
        ["device_code"] = deviceCode,
        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
    };

    public static Dictionary<string, string> RefreshForm(string refreshToken) => new()
    {
        ["client_id"] = ClientId,
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken
    };

    public static Dictionary<string, string> RevokeForm(string token) => new() { ["client_id"] = ClientId, ["token"] = token };

    /// <summary>Vrai pour une adresse https de twitch.tv : seule celle-ci est ouverte dans le navigateur.</summary>
    public static bool IsSafeVerificationUri(string? uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            && (parsed.Host == "twitch.tv" || parsed.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase));

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int fallback = 0)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    private static bool Flag(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Premier élément du tableau <c>data</c> d'une réponse Helix ; null si absent ou vide.</summary>
    private static JsonElement? FirstData(string? json)
    {
        if (Parse(json) is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return null;
        return data[0];
    }

    public static TwitchDeviceCode? ParseDeviceCode(string? json)
    {
        if (Parse(json) is not { } root) return null;
        var device = Str(root, "device_code");
        var user = Str(root, "user_code");
        var uri = Str(root, "verification_uri");
        if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(uri)) return null;
        return new TwitchDeviceCode(device, user, uri, Int(root, "interval", 5), Int(root, "expires_in", 1800));
    }

    /// <summary>Lit une réponse de <c>/oauth2/token</c> (device flow ou rafraîchissement).</summary>
    public static TwitchTokenResult ParseToken(int status, string? json)
    {
        var root = Parse(json);
        if (status == 200 && root is { } ok && Str(ok, "access_token") is { Length: > 0 } access)
            return new TwitchTokenResult(TwitchTokenOutcome.Success, access, Str(ok, "refresh_token"), Int(ok, "expires_in", 3600), null);

        var message = root is { } error ? Str(error, "message") ?? Str(error, "error") : null;
        var lower = message?.ToLowerInvariant() ?? "";
        var outcome = lower switch
        {
            "authorization_pending" => TwitchTokenOutcome.Pending,
            "slow_down" => TwitchTokenOutcome.SlowDown,
            "access_denied" => TwitchTokenOutcome.Denied,
            "expired_token" or "invalid device code" => TwitchTokenOutcome.Expired,
            _ => status is 400 or 401 or 403 ? TwitchTokenOutcome.Rejected : TwitchTokenOutcome.Failed
        };
        return new TwitchTokenResult(outcome, null, null, 0, message);
    }

    public static TwitchIdentity? ParseIdentity(string? json)
    {
        if (Parse(json) is not { } root) return null;
        var login = Str(root, "login");
        var id = Str(root, "user_id");
        return string.IsNullOrEmpty(login) || string.IsNullOrEmpty(id) ? null : new TwitchIdentity(login, id, Int(root, "expires_in"));
    }

    /// <summary>Lit <c>/streams</c> : tableau vide = hors ligne ; null si la réponse est illisible.</summary>
    public static TwitchStreamInfo? ParseStream(string? json)
    {
        if (Parse(json) is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        if (data.GetArrayLength() == 0) return new TwitchStreamInfo(false, 0, null);
        var stream = data[0];
        DateTimeOffset? started = Str(stream, "started_at") is { } text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
        return new TwitchStreamInfo(Str(stream, "type") is null or "live", Int(stream, "viewer_count"), started);
    }

    public static TwitchChatSettings? ParseChatSettings(string? json)
        => FirstData(json) is { } d
            ? new TwitchChatSettings(Flag(d, "slow_mode"), Flag(d, "emote_mode"), Flag(d, "follower_mode"), Flag(d, "subscriber_mode"))
            : null;

    public static bool? ParseShield(string? json) => FirstData(json) is { } d ? Flag(d, "is_active") : null;

    /// <summary>Lien d'édition du clip créé ; null si absent.</summary>
    public static string? ParseClipEditUrl(string? json) => FirstData(json) is { } d ? Str(d, "edit_url") : null;

    public static List<TwitchCategory> ParseCategories(string? json)
    {
        var list = new List<TwitchCategory>();
        if (Parse(json) is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
                if (Str(item, "id") is { Length: > 0 } id && Str(item, "name") is { Length: > 0 } name)
                    list.Add(new TwitchCategory(id, name, LargeBoxArt(Str(item, "box_art_url"))));
        return list;
    }

    /// <summary>Jaquette en 144x192 : la recherche renvoie une miniature 52x72, ou un modèle {width}x{height}.</summary>
    public static string? LargeBoxArt(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.Contains("{width}")) return url.Replace("{width}", "144").Replace("{height}", "192");
        return System.Text.RegularExpressions.Regex.Replace(url, @"-\d+x\d+(\.\w+)$", "-144x192$1");
    }

    /// <summary>Droits accordés, lus dans la réponse de <c>/oauth2/validate</c>.</summary>
    public static List<string> ParseScopes(string? json)
    {
        var scopes = new List<string>();
        if (Parse(json) is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("scopes", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var scope in list.EnumerateArray())
                if (scope.ValueKind == JsonValueKind.String && scope.GetString() is { Length: > 0 } value) scopes.Add(value);
        return scopes;
    }

    /// <summary>Titre actuel de la chaîne (<c>/channels</c>) ; null si illisible.</summary>
    public static string? ParseChannelTitle(string? json) => FirstData(json) is { } d ? Str(d, "title") ?? "" : null;

    /// <summary>Lit une page de <c>chat/emotes/user</c> ; images statiques, thème sombre, échelle 2.0.</summary>
    public static TwitchEmotePage ParseUserEmotes(string? json)
    {
        var emotes = new List<TwitchEmote>();
        string? cursor = null;
        if (Parse(json) is { ValueKind: JsonValueKind.Object } root)
        {
            var template = Str(root, "template");
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray())
                    if (Str(item, "id") is { Length: > 0 } id && Str(item, "name") is { Length: > 0 } name)
                        emotes.Add(new TwitchEmote(id, name, Str(item, "emote_type") ?? "", EmoteImageUrl(template, id)));
            if (root.TryGetProperty("pagination", out var page) && Str(page, "cursor") is { Length: > 0 } next) cursor = next;
        }
        return new TwitchEmotePage(emotes, cursor);
    }

    public static string EmoteImageUrl(string? template, string id)
        => string.IsNullOrEmpty(template)
            ? $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/static/dark/2.0"
            : template.Replace("{{id}}", id).Replace("{{format}}", "static").Replace("{{theme_mode}}", "dark").Replace("{{scale}}", "2.0");

    /// <summary>Motif de refus d'un message du tchat (réponse 200 avec <c>is_sent</c> faux) ; null s'il est parti.</summary>
    public static string? ParseChatSendError(string? json)
    {
        if (FirstData(json) is not { } d || Flag(d, "is_sent")) return null;
        if (d.TryGetProperty("drop_reason", out var reason) && Str(reason, "message") is { Length: > 0 } message) return $"Twitch n'a pas envoyé le message : {message}";
        return "Twitch n'a pas envoyé le message.";
    }

    /// <summary>Motif de refus d'une pub (réponse 200 avec un <c>message</c> non vide) ; null si elle est lancée.</summary>
    public static string? ParseCommercialError(string? json)
        => FirstData(json) is { } d && Str(d, "message") is { Length: > 0 } message ? $"Twitch n'a pas lancé la pub : {message}" : null;

    /// <summary>Durée de pub valide la plus proche de la valeur saisie (30 s par défaut).</summary>
    public static int CommercialLength(string? text)
        => int.TryParse(text?.Trim(), out var value) && CommercialLengths.Contains(value) ? value : CommercialLengths[0];

    public static bool ChatMode(TwitchChatSettings chat, TwitchAction action) => action switch
    {
        TwitchAction.SlowMode => chat.Slow,
        TwitchAction.EmoteOnly => chat.Emote,
        TwitchAction.FollowersOnly => chat.Followers,
        TwitchAction.SubscribersOnly => chat.Subscribers,
        _ => false
    };

    public static string BuildMarkerBody(string userId, string? description)
    {
        var body = new JsonObject { ["user_id"] = userId };
        var text = description?.Trim();
        if (!string.IsNullOrEmpty(text)) body["description"] = text.Length > 140 ? text[..140] : text;
        return body.ToJsonString();
    }

    public static string BuildChatMessageBody(string broadcasterId, string senderId, string message)
        => new JsonObject { ["broadcaster_id"] = broadcasterId, ["sender_id"] = senderId, ["message"] = message }.ToJsonString();

    public static string BuildCommercialBody(string broadcasterId, int length)
        => new JsonObject { ["broadcaster_id"] = broadcasterId, ["length"] = length }.ToJsonString();

    /// <summary>Corps de <c>PATCH /chat/settings</c> qui active ou coupe un mode (mode lent : 30 s ; followers : tous).</summary>
    public static string BuildChatSettingsBody(TwitchAction action, bool enable)
    {
        var body = new JsonObject();
        switch (action)
        {
            case TwitchAction.SlowMode:
                body["slow_mode"] = enable;
                if (enable) body["slow_mode_wait_time"] = 30;
                break;
            case TwitchAction.EmoteOnly:
                body["emote_mode"] = enable;
                break;
            case TwitchAction.FollowersOnly:
                body["follower_mode"] = enable;
                if (enable) body["follower_mode_duration"] = 0;
                break;
            case TwitchAction.SubscribersOnly:
                body["subscriber_mode"] = enable;
                break;
        }
        return body.ToJsonString();
    }

    public static string BuildShieldBody(bool active) => new JsonObject { ["is_active"] = active }.ToJsonString();

    public static string BuildTitleBody(string title) => new JsonObject { ["title"] = title }.ToJsonString();

    public static string BuildCategoryBody(string gameId) => new JsonObject { ["game_id"] = gameId }.ToJsonString();

    /// <summary>Message du JSON d'erreur de Twitch (<c>message</c>, sinon <c>error</c>) ; null si absent.</summary>
    public static string? ReadErrorMessage(string? json)
        => Parse(json) is { } root ? Str(root, "message") ?? Str(root, "error") : null;

    /// <summary>Message lisible pour une réponse en erreur de Twitch.</summary>
    public static string TranslateError(int status, string? json)
    {
        var message = ReadErrorMessage(json);
        var lower = message?.ToLowerInvariant() ?? "";
        if (lower.Contains("offline") || lower.Contains("not live") || lower.Contains("not streaming") || lower.Contains("must be streaming"))
            return "La chaîne n'est pas en direct.";
        return status switch
        {
            401 => "Twitch a refusé l'autorisation : reconnecte-toi dans Paramètres › Applications connectées.",
            403 => "Twitch refuse cette action : le compte n'a pas le droit nécessaire. Reconnecte-toi (Paramètres › Applications connectées) pour accorder les autorisations.",
            429 => "Twitch limite le nombre de requêtes : réessaie dans quelques secondes.",
            >= 500 => $"Twitch rencontre un problème (HTTP {status}) : réessaie plus tard.",
            _ => string.IsNullOrWhiteSpace(message) ? $"Twitch a refusé la requête (HTTP {status})." : $"Twitch : {message}"
        };
    }

    // ---------- Configuration et boucle d'arrière-plan ----------

    /// <summary>Charge le compte enregistré et démarre la connexion d'arrière-plan s'il est connecté ; <paramref name="save"/> écrit la configuration.</summary>
    public void Configure(TwitchSettings settings, Action? save)
    {
        lock (_configLock)
        {
            if (_disposed) return;
            StopLoopLocked();
            _settings = settings;
            _save = save;
            _access = settings.AccessToken;
            _refresh = settings.RefreshToken;
            _expires = settings.ExpiresAt;
            LastError = null;
            if (settings.Enabled && (_access is not null || _refresh is not null)) StartLoopLocked();
            else if (settings.Enabled) LastError = "Les jetons Twitch sont illisibles : reconnecte-toi.";
        }
        SetConnected(false, CancellationToken.None);
    }

    /// <summary>
    /// Indique si des tuiles Twitch existent (l'état du direct et du tchat est alors relu toutes les 30 s) et si l'une d'elles
    /// utilise le mode bouclier (état relu aussi).
    /// </summary>
    public void Watch(bool tiles, bool shield)
    {
        var changed = tiles != _watchTiles || shield != _watchShield;
        _watchTiles = tiles;
        _watchShield = shield;
        if (changed && IsConnected) Wake();
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private void StartLoopLocked()
    {
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(cts.Token));
    }

    private void StopLoopLocked()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsConnected) await ValidateAsync(ct);
                else if (DateTime.UtcNow - _lastValidate > ValidateEvery) await ValidateAsync(ct);
                if (IsConnected && _watchTiles) await PollAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (TwitchAuthException ex) when (ex.Fatal)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex is TwitchAuthException auth ? auth.Message : "Twitch est injoignable.";
                SetConnected(false, ct);
                Log.WarnOnce("twitch-loop", "Connexion à Twitch interrompue (nouvelle tentative automatique).", ex);
            }
            try { await _wake.WaitAsync(IsConnected ? PollEvery : RetryEvery, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetConnected(bool connected, CancellationToken ct)
    {
        bool changed;
        lock (_state)
        {
            if (ct.IsCancellationRequested) return;
            changed = _connected != connected;
            _connected = connected;
            if (!connected) _state.Clear();
        }
        if (!changed) return;
        Log.Info(connected ? $"Twitch connecté ({Login})." : "Twitch déconnecté.");
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        if (_context is null) StateChanged?.Invoke();
        else _context.Post(_ => StateChanged?.Invoke(), null);
    }

    /// <summary>Écrit la configuration sur le thread interface (les jetons y sont modifiés depuis des tâches d'arrière-plan).</summary>
    private void Persist()
    {
        var save = _save;
        if (save is null) return;
        try
        {
            if (_context is null || SynchronizationContext.Current == _context) save();
            else _context.Send(_ => save(), null);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("twitch-save", "Enregistrement de la configuration Twitch impossible", ex);
        }
    }

    // ---------- Jetons ----------

    private async Task<HelixResponse> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        return new HelixResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private void StoreTokens(TwitchTokenResult token)
    {
        _access = token.AccessToken;
        if (token.RefreshToken is not null) _refresh = token.RefreshToken;
        _expires = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        if (_settings is { } settings)
        {
            settings.AccessToken = _access;
            settings.RefreshToken = _refresh;
            settings.ExpiresAt = _expires;
        }
        Persist(); // le refresh token ne sert qu'une fois : le nouveau est écrit tout de suite
    }

    /// <summary>
    /// Jeton d'accès valide ; rafraîchi s'il expire bientôt, ou s'il vient d'être refusé (<paramref name="stale"/>) et qu'aucun autre
    /// appel ne l'a déjà remplacé.
    /// </summary>
    private async Task<string> AccessTokenAsync(string? stale, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var access = _access;
            if (access is not null)
            {
                if (stale is null && (_expires is null || _expires.Value - DateTimeOffset.UtcNow > RefreshMargin)) return access;
                if (stale is not null && access != stale) return access;
            }
            return await RefreshLockedAsync(ct);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> RefreshLockedAsync(CancellationToken ct)
    {
        const string lost = "La session Twitch a expiré ou a été révoquée : reconnecte-toi (Paramètres › Applications connectées).";
        if (_refresh is not { } refresh)
        {
            Disconnect(lost);
            throw new TwitchAuthException(lost, fatal: true);
        }

        HelixResponse response;
        try { response = await PostFormAsync(TokenUrl, RefreshForm(refresh), ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new TwitchAuthException("Twitch est injoignable.", fatal: false);
        }

        var token = ParseToken(response.Status, response.Body);
        if (token.Outcome == TwitchTokenOutcome.Success && token.AccessToken is { } access)
        {
            StoreTokens(token);
            return access;
        }
        if (token.Outcome is TwitchTokenOutcome.Rejected or TwitchTokenOutcome.Expired)
        {
            Disconnect(lost);
            throw new TwitchAuthException(lost, fatal: true);
        }
        throw new TwitchAuthException(TranslateError(response.Status, response.Body), fatal: false);
    }

    /// <summary>Repasse à l'état déconnecté : arrête la boucle, efface le compte et les jetons.</summary>
    private void Disconnect(string reason)
    {
        lock (_configLock)
        {
            StopLoopLocked();
            _access = _refresh = null;
            _expires = null;
            _settings?.Clear();
        }
        LastError = reason;
        SetConnected(false, CancellationToken.None);
        Persist();
    }

    /// <summary>Envoie une requête authentifiée ; sur une réponse 401, rafraîchit le jeton et recommence une seule fois.</summary>
    private async Task<HelixResponse> SendAuthedAsync(Func<string, HttpRequestMessage> build, CancellationToken ct)
    {
        string? stale = null;
        for (var attempt = 0; ; attempt++)
        {
            var token = await AccessTokenAsync(stale, ct);
            using var request = build(token);
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode == 401 && attempt == 0)
            {
                stale = token;
                continue;
            }
            return new HelixResponse((int)response.StatusCode, body);
        }
    }

    private Task<HelixResponse> HelixAsync(HttpMethod method, string path, string? body = null, CancellationToken ct = default)
        => SendAuthedAsync(token =>
        {
            var request = new HttpRequestMessage(method, HelixUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Client-Id", ClientId);
            if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return request;
        }, ct);

    /// <summary>Lit l'identité du compte (login, identifiant) ; passe à l'état connecté.</summary>
    private async Task ValidateAsync(CancellationToken ct)
    {
        var response = await SendAuthedAsync(token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ValidateUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
            return request;
        }, ct);

        if (response.Status == 401)
        {
            const string invalid = "La session Twitch n'est plus valable : reconnecte-toi (Paramètres › Applications connectées).";
            Disconnect(invalid);
            throw new TwitchAuthException(invalid, fatal: true);
        }
        if (response.Status != 200 || ParseIdentity(response.Body) is not { } identity)
            throw new TwitchAuthException(TranslateError(response.Status, response.Body), fatal: false);

        if (_settings is { } settings && (settings.Login != identity.Login || settings.UserId != identity.UserId))
        {
            settings.Login = identity.Login;
            settings.UserId = identity.UserId;
            Persist();
        }
        lock (_state) _scopes = ParseScopes(response.Body);
        _lastValidate = DateTime.UtcNow;
        LastError = null;
        SetConnected(true, ct);
    }

    private List<string> _scopes = new();

    /// <summary>Vrai si le jeton actuel porte ce droit (faux tant que Twitch n'est pas connecté).</summary>
    public bool HasScope(string scope)
    {
        if (!IsConnected) return false;
        lock (_state) return _scopes.Contains(scope);
    }

    // ---------- Connexion et déconnexion ----------

    /// <summary>
    /// Connexion par Device Code Flow : <paramref name="onCode"/> reçoit le code à afficher et l'adresse à ouvrir (sur le contexte
    /// de l'appelant), puis Twitch est interrogé jusqu'à l'autorisation. Renvoie un message d'erreur lisible, ou null si le
    /// compte est connecté.
    /// </summary>
    public async Task<string?> LoginAsync(Action<TwitchDeviceCode> onCode, CancellationToken ct)
    {
        if (_settings is null) return "Twitch n'est pas initialisé.";
        try
        {
            var first = await PostFormAsync(DeviceUrl, DeviceForm(), ct);
            if (first.Status != 200 || ParseDeviceCode(first.Body) is not { } code) return TranslateError(first.Status, first.Body);
            onCode(code);

            var interval = Math.Max(1, code.Interval);
            var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                HelixResponse poll;
                try { poll = await PostFormAsync(TokenUrl, PollForm(code.DeviceCode), ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { continue; }

                var token = ParseToken(poll.Status, poll.Body);
                switch (token.Outcome)
                {
                    case TwitchTokenOutcome.Pending:
                        continue;
                    case TwitchTokenOutcome.SlowDown:
                        interval += 5;
                        continue;
                    case TwitchTokenOutcome.Denied:
                        return "Autorisation refusée sur Twitch.";
                    case TwitchTokenOutcome.Expired:
                        return "Le code a expiré : recommence la connexion.";
                    case TwitchTokenOutcome.Success:
                        return await FinishLoginAsync(token, ct);
                    default:
                        return TranslateError(poll.Status, poll.Body);
                }
            }
            return "Le code a expiré : recommence la connexion.";
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested ? "Connexion annulée." : "Twitch ne répond pas.";
        }
        catch (HttpRequestException)
        {
            return "Twitch est injoignable.";
        }
    }

    private async Task<string?> FinishLoginAsync(TwitchTokenResult token, CancellationToken ct)
    {
        var settings = _settings!;
        lock (_configLock)
        {
            if (_disposed) return "Connexion annulée.";
            StopLoopLocked();
        }
        _emotes = null; // émotes de l'éventuel compte précédent
        StoreTokens(token);
        try
        {
            await ValidateAsync(ct);
        }
        catch (Exception ex)
        {
            lock (_configLock)
            {
                _access = _refresh = null;
                _expires = null;
                settings.Clear();
            }
            Persist();
            SetConnected(false, CancellationToken.None);
            return ex is TwitchAuthException auth ? auth.Message : ex is OperationCanceledException ? "Connexion annulée." : "Twitch est injoignable.";
        }

        lock (_configLock)
        {
            if (_disposed) return "Connexion annulée.";
            settings.Enabled = true;
            StartLoopLocked();
        }
        Persist();
        return null;
    }

    /// <summary>Révoque le jeton auprès de Twitch (si possible), puis efface le compte et les jetons.</summary>
    public async Task LogoutAsync()
    {
        string?[] tokens;
        lock (_configLock)
        {
            StopLoopLocked();
            tokens = new[] { _access, _refresh };
        }
        foreach (var token in tokens.Where(t => !string.IsNullOrEmpty(t)))
        {
            try { await PostFormAsync(RevokeUrl, RevokeForm(token!), CancellationToken.None); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.WarnOnce("twitch-revoke", "Révocation du jeton Twitch impossible (réseau) : le compte est tout de même déconnecté.", ex);
            }
        }
        lock (_configLock)
        {
            _access = _refresh = null;
            _expires = null;
            _settings?.Clear();
        }
        _emotes = null;
        LastError = null;
        SetConnected(false, CancellationToken.None);
        Persist();
    }

    // ---------- État en direct ----------

    /// <summary>Relit l'état du direct, des modes du tchat et (si une tuile l'utilise) du mode bouclier.</summary>
    private async Task PollAsync(CancellationToken ct)
    {
        if (UserId is not { } me) return;
        var changed = false;

        var stream = await HelixAsync(HttpMethod.Get, $"streams?user_id={me}", null, ct);
        if (stream.Status == 200 && ParseStream(stream.Body) is { } info)
            lock (_state) changed |= _state.SetStream(info);
        else LogPollFailure("streams", stream.Status);

        var chat = await HelixAsync(HttpMethod.Get, $"chat/settings?broadcaster_id={me}&moderator_id={me}", null, ct);
        if (chat.Status == 200 && ParseChatSettings(chat.Body) is { } modes)
            lock (_state) changed |= _state.SetChat(modes);
        else LogPollFailure("chat", chat.Status);

        if (_watchShield)
        {
            var shield = await HelixAsync(HttpMethod.Get, $"moderation/shield_mode?broadcaster_id={me}&moderator_id={me}", null, ct);
            if (shield.Status == 200 && ParseShield(shield.Body) is { } active)
                lock (_state) changed |= _state.SetShield(active);
            else LogPollFailure("shield", shield.Status);
        }
        if (changed && !ct.IsCancellationRequested) RaiseStateChanged();
    }

    private static void LogPollFailure(string what, int status)
        => Log.WarnOnce($"twitch-poll-{what}", $"Lecture de l'état Twitch ({what}) refusée (HTTP {status}).");

    private async Task RefreshQuietlyAsync()
    {
        try { await PollAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TwitchAuthException)
        {
            Log.WarnOnce("twitch-refresh-state", "Relecture de l'état Twitch après une action impossible.", ex);
        }
    }

    // ---------- Éditeur ----------

    /// <summary>Catégories dont le nom correspond à la recherche ; liste vide si Twitch ne répond pas.</summary>
    public async Task<List<TwitchCategory>> SearchCategoriesAsync(string query)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(query)) return new List<TwitchCategory>();
        try
        {
            var response = await HelixAsync(HttpMethod.Get, $"search/categories?first=20&query={Uri.EscapeDataString(query.Trim())}");
            return response.Status == 200 ? ParseCategories(response.Body) : new List<TwitchCategory>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TwitchAuthException)
        {
            return new List<TwitchCategory>();
        }
    }

    /// <summary>Titre actuel de la chaîne ; null si Twitch ne répond pas.</summary>
    public async Task<string?> GetTitleAsync()
    {
        if (!IsConnected || UserId is not { } me) return null;
        try
        {
            var response = await HelixAsync(HttpMethod.Get, $"channels?broadcaster_id={me}");
            return response.Status == 200 ? ParseChannelTitle(response.Body) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TwitchAuthException)
        {
            return null;
        }
    }

    private static readonly TimeSpan EmotesCacheFor = TimeSpan.FromMinutes(10);
    private List<TwitchEmote>? _emotes;
    private DateTime _emotesAt;

    /// <summary>Émotes utilisables par le compte (toutes les pages), gardées 10 min ; null sans le droit ou si Twitch ne répond pas.</summary>
    public async Task<List<TwitchEmote>?> GetEmotesAsync()
    {
        if (!HasScope(EmotesScope) || UserId is not { } me) return null;
        if (_emotes is { } cached && DateTime.UtcNow - _emotesAt < EmotesCacheFor) return cached;
        try
        {
            var all = new List<TwitchEmote>();
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var response = await HelixAsync(HttpMethod.Get,
                    $"chat/emotes/user?user_id={me}" + (cursor is null ? "" : $"&after={Uri.EscapeDataString(cursor)}"));
                if (response.Status != 200) return _emotes;
                var parsed = ParseUserEmotes(response.Body);
                all.AddRange(parsed.Emotes);
                if ((cursor = parsed.Cursor) is null) break;
            }
            _emotes = all.DistinctBy(e => e.Id).ToList();
            _emotesAt = DateTime.UtcNow;
            return _emotes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TwitchAuthException)
        {
            return _emotes;
        }
    }

    /// <summary>Télécharge une image publique de Twitch (émote, jaquette) ; null en cas d'échec.</summary>
    public async Task<byte[]?> DownloadAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            return await _http.GetByteArrayAsync(uri);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    // ---------- Actions ----------

    private static TwitchActionResult Fail(string error) => new(error);

    private static readonly TwitchActionResult Done = new(null);

    /// <summary>Exécute l'action d'une tuile ; renvoie une erreur lisible, ou un résultat sans erreur (avec notification et texte à copier pour un clip).</summary>
    public async Task<TwitchActionResult> ExecuteAsync(TwitchAction action, string? text, string? categoryId)
    {
        if (!IsConnected || UserId is not { } me) return Fail("Twitch n'est pas connecté.");
        try
        {
            switch (action)
            {
                case TwitchAction.Clip:
                {
                    var response = await HelixAsync(HttpMethod.Post, $"clips?broadcaster_id={me}");
                    if (response.Status is < 200 or >= 300) return Fail(TranslateError(response.Status, response.Body));
                    return new TwitchActionResult(null, "Clip créé", ParseClipEditUrl(response.Body));
                }
                case TwitchAction.Marker:
                    return await SendAsync(HttpMethod.Post, "streams/markers", BuildMarkerBody(me, text));
                case TwitchAction.ChatMessage:
                {
                    var message = text?.Trim();
                    if (string.IsNullOrEmpty(message)) return Fail("Aucun message n'est défini pour cette tuile.");
                    var response = await HelixAsync(HttpMethod.Post, "chat/messages", BuildChatMessageBody(me, me, message));
                    if (response.Status is < 200 or >= 300) return Fail(TranslateError(response.Status, response.Body));
                    return ParseChatSendError(response.Body) is { } dropped ? Fail(dropped) : Done;
                }
                case TwitchAction.Commercial:
                {
                    var length = CommercialLength(text);
                    var response = await HelixAsync(HttpMethod.Post, "channels/commercial", BuildCommercialBody(me, length));
                    if (response.Status is < 200 or >= 300) return Fail(TranslateError(response.Status, response.Body));
                    return ParseCommercialError(response.Body) is { } refused ? Fail(refused) : new TwitchActionResult(null, $"Pub de {length} s lancée");
                }
                case TwitchAction.SlowMode or TwitchAction.EmoteOnly or TwitchAction.FollowersOnly or TwitchAction.SubscribersOnly:
                    return await ToggleChatModeAsync(action, me);
                case TwitchAction.ShieldMode:
                    return await ToggleShieldAsync(me);
                case TwitchAction.SetTitle:
                {
                    var title = text?.Trim();
                    if (string.IsNullOrEmpty(title)) return Fail("Aucun titre n'est défini pour cette tuile.");
                    if (title.Length > 140) return Fail("Le titre dépasse 140 caractères.");
                    return await SendAsync(HttpMethod.Patch, $"channels?broadcaster_id={me}", BuildTitleBody(title));
                }
                case TwitchAction.SetCategory:
                    return string.IsNullOrEmpty(categoryId) ? Fail("Aucune catégorie n'est choisie pour cette tuile.")
                        : await SendAsync(HttpMethod.Patch, $"channels?broadcaster_id={me}", BuildCategoryBody(categoryId));
                default:
                    return Fail("Action Twitch inconnue.");
            }
        }
        catch (TwitchAuthException ex)
        {
            return Fail(ex.Message);
        }
        catch (HttpRequestException)
        {
            return Fail("Twitch est injoignable.");
        }
        catch (OperationCanceledException)
        {
            return Fail("Twitch ne répond pas.");
        }
    }

    private async Task<TwitchActionResult> SendAsync(HttpMethod method, string path, string? body = null)
    {
        var response = await HelixAsync(method, path, body);
        return response.Status is >= 200 and < 300 ? Done : Fail(TranslateError(response.Status, response.Body));
    }

    /// <summary>Lit l'état courant du mode puis envoie l'inverse.</summary>
    private async Task<TwitchActionResult> ToggleChatModeAsync(TwitchAction action, string me)
    {
        var path = $"chat/settings?broadcaster_id={me}&moderator_id={me}";
        var current = await HelixAsync(HttpMethod.Get, path);
        if (current.Status != 200) return Fail(TranslateError(current.Status, current.Body));
        if (ParseChatSettings(current.Body) is not { } chat) return Fail("Réponse de Twitch illisible.");

        var result = await SendAsync(HttpMethod.Patch, path, BuildChatSettingsBody(action, !ChatMode(chat, action)));
        if (result.Error is null) await RefreshQuietlyAsync();
        return result;
    }

    private async Task<TwitchActionResult> ToggleShieldAsync(string me)
    {
        var path = $"moderation/shield_mode?broadcaster_id={me}&moderator_id={me}";
        var current = await HelixAsync(HttpMethod.Get, path);
        if (current.Status != 200) return Fail(TranslateError(current.Status, current.Body));
        if (ParseShield(current.Body) is not { } active) return Fail("Réponse de Twitch illisible.");

        var result = await SendAsync(HttpMethod.Put, path, BuildShieldBody(!active));
        if (result.Error is null) await RefreshQuietlyAsync();
        return result;
    }

    public void Dispose()
    {
        lock (_configLock)
        {
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        _http.Dispose();
    }
}
