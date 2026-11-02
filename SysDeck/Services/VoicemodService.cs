using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SysDeck.Infrastructure;
using SysDeck.Models;

namespace SysDeck.Services;

/// <summary>Message reçu de Voicemod : réponse à une requête (<see cref="Id"/>) ou évènement de l'application.</summary>
public sealed record VoicemodMessage(string? Action, string? Id, string? ActionType, JsonElement Object, JsonElement Payload, string? Msg);

public sealed record VoicemodVoice(string Id, string Name, bool Enabled, bool Favorite, bool IsNew = false, bool IsCustom = false, string? ImageUrl = null);

/// <summary>Son du soundboard : <see cref="FileName"/> = identifiant attendu par <c>playMeme</c> ; <see cref="Profile"/> = nom du soundboard ; <see cref="Custom"/> = soundboard créé par l'utilisateur.</summary>
public sealed record VoicemodSound(string FileName, string Name, string Profile, bool Custom = false, string? ImageUrl = null);

/// <summary>État de Voicemod tenu à jour par les réponses et les évènements.</summary>
public sealed class VoicemodState
{
    public bool? VoiceChanger { get; private set; }
    public bool? HearMyself { get; private set; }
    public bool? Background { get; private set; }
    public bool? MuteMic { get; private set; }
    public bool? MuteSoundsForMe { get; private set; }
    public string? CurrentVoice { get; private set; }

    public void Clear()
    {
        VoiceChanger = HearMyself = Background = MuteMic = MuteSoundsForMe = null;
        CurrentVoice = null;
    }

    /// <summary>Applique un évènement (<c>…EnabledEvent</c>, <c>voiceLoadedEvent</c>) ou une réponse d'état ; vrai si une valeur a changé.</summary>
    public bool Apply(VoicemodMessage message)
    {
        if (message.Action is { } action && action.EndsWith("Event", StringComparison.Ordinal))
        {
            if (action == "voiceLoadedEvent") return Set(CurrentVoice, Str(message.Object, "voiceID"), v => CurrentVoice = v);
            var enabled = action.Contains("Enabled", StringComparison.Ordinal);
            if (!enabled && !action.Contains("Disabled", StringComparison.Ordinal)) return false;
            return action switch
            {
                _ when action.StartsWith("voiceChanger", StringComparison.Ordinal) => Set(VoiceChanger, enabled, v => VoiceChanger = v),
                _ when action.StartsWith("hearMySelf", StringComparison.OrdinalIgnoreCase) => Set(HearMyself, enabled, v => HearMyself = v),
                _ when action.StartsWith("backgroundEffects", StringComparison.Ordinal) => Set(Background, enabled, v => Background = v),
                _ when action.StartsWith("muteMemeForMe", StringComparison.Ordinal) => Set(MuteSoundsForMe, enabled, v => MuteSoundsForMe = v),
                _ when action.StartsWith("muteMic", StringComparison.Ordinal) => Set(MuteMic, enabled, v => MuteMic = v),
                _ => false
            };
        }

        // Réponse à une lecture ou une bascule : actionObject.value porte l'état.
        if (message.ActionType is not { } type) return false;
        if (type == "getCurrentVoice") return Set(CurrentVoice, Str(message.Object, "voiceID"), v => CurrentVoice = v);
        if (type == "getVoices" && Str(message.Object, "currentVoice") is { } current) return Set(CurrentVoice, current, v => CurrentVoice = v);
        if (Value(message.Object) is not { } value) return false;
        return type switch
        {
            "getVoiceChangerStatus" or "toggleVoiceChanger" => Set(VoiceChanger, value, v => VoiceChanger = v),
            "getHearMyselfStatus" or "toggleHearMyVoice" => Set(HearMyself, value, v => HearMyself = v),
            "getBackgroundEffectStatus" or "toggleBackground" => Set(Background, value, v => Background = v),
            "getMuteMicStatus" or "toggleMuteMic" => Set(MuteMic, value, v => MuteMic = v),
            "getMuteMemeForMeStatus" or "toggleMuteMemeForMe" => Set(MuteSoundsForMe, value, v => MuteSoundsForMe = v),
            _ => false
        };
    }

    /// <summary>État « actif » d'une tuile ; null si l'action n'a pas d'état ou s'il est inconnu.</summary>
    public bool? IsOn(VoicemodAction action, string? target) => action switch
    {
        VoicemodAction.VoiceChanger => VoiceChanger,
        VoicemodAction.HearMyself => HearMyself,
        VoicemodAction.Background => Background,
        VoicemodAction.MuteMic => MuteMic,
        VoicemodAction.MuteSoundsForMe => MuteSoundsForMe,
        VoicemodAction.LoadVoice when !string.IsNullOrEmpty(target) && CurrentVoice is { } voice => voice == target,
        _ => null
    };

    private static bool Set<T>(T current, T value, Action<T> set)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        set(value);
        return true;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Value(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("value", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null
        };
    }
}

public sealed class VoicemodConnectionException : Exception
{
    public VoicemodConnectionException(string message) : base(message) { }
}

/// <summary>
/// Client de la Control API de Voicemod (WebSocket local, sans paquet NuGet) : recherche du port parmi ceux que Voicemod
/// peut ouvrir, enregistrement avec la clé de l'application, reconnexion automatique, état tenu par les évènements.
/// </summary>
public sealed class VoicemodService : IDisposable
{
    /// <summary>Voicemod n'a pas de port fixe : il en ouvre un de cette liste.</summary>
    public static readonly int[] Ports = { 59129, 20000, 39273, 42152, 43782, 46667, 35679, 37170, 38501, 33952, 30546 };

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PortTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(90); // le temps d'autoriser SysDeck dans Voicemod
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly VoicemodState _state = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VoicemodMessage>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _configLock = new();

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private CancellationToken _sessionToken;
    private bool _enabled, _disposed, _connected;

    public VoicemodService(string? clientKey) => ClientKey = clientKey;

    /// <summary>Clé de la Control API intégrée à la compilation ; null si absente (dépôt compilé sans le dossier Secrets).</summary>
    public string? ClientKey { get; }

    /// <summary>Levé quand la connexion ou une valeur de l'état change (sur le thread interface).</summary>
    public event Action? StateChanged;

    public bool IsEnabled => _enabled;
    public bool IsConnected => Volatile.Read(ref _connected);
    public int? Port { get; private set; }

    /// <summary>Vrai pendant que Voicemod attend que l'utilisateur autorise SysDeck.</summary>
    public bool AwaitingApproval { get; private set; }

    /// <summary>Dernière raison d'échec de connexion (vide tant que tout va bien).</summary>
    public string? LastError { get; private set; }

    public bool? IsOn(VoicemodAction action, string? target)
    {
        if (!IsConnected) return null;
        lock (_state) return _state.IsOn(action, target);
    }

    /// <summary>Lit la clé brouillée intégrée (ressource « voicemod.key ») ; null si absente ou illisible.</summary>
    public static string? LoadEmbeddedKey()
    {
        try
        {
            using var stream = typeof(VoicemodService).Assembly.GetManifestResourceStream("voicemod.key");
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return Unscramble(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            return null;
        }
    }

    /// <summary>La clé est stockée en base64, chaque octet XOR 0x5A : elle n'apparaît pas en clair dans l'exe.</summary>
    public static string? Unscramble(string? scrambled)
    {
        if (string.IsNullOrWhiteSpace(scrambled)) return null;
        var bytes = Convert.FromBase64String(scrambled.Trim());
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5A;
        var key = Encoding.UTF8.GetString(bytes);
        return key.Length == 0 ? null : key;
    }

    // ---------- Messages (purs, testés) ----------

    public static string BuildMessage(string action, string id, JsonObject? payload = null)
        => new JsonObject { ["action"] = action, ["id"] = id, ["payload"] = payload ?? new JsonObject() }.ToJsonString();

    public static VoicemodMessage ParseMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        JsonElement E(string name) => root.TryGetProperty(name, out var v) ? v.Clone() : default;
        return new VoicemodMessage(S("action"), S("id") ?? S("actionID"), S("actionType"), E("actionObject"), E("payload"), S("msg"));
    }

    /// <summary>Code de statut de la réponse à <c>registerClient</c> (200 = accepté) ; null s'il est absent.</summary>
    public static int? RegisterStatus(VoicemodMessage message)
    {
        // Selon la version, le statut est dans actionObject ou dans payload.
        var holder = message.Object.ValueKind == JsonValueKind.Object && message.Object.TryGetProperty("status", out _) ? message.Object : message.Payload;
        if (holder.ValueKind != JsonValueKind.Object || !holder.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("code", out var code)) return null;
        return code.ValueKind switch
        {
            JsonValueKind.Number => code.GetInt32(),
            JsonValueKind.String when int.TryParse(code.GetString(), out var value) => value,
            _ => null
        };
    }

    public static List<VoicemodVoice> ParseVoices(JsonElement actionObject)
    {
        var list = new List<VoicemodVoice>();
        if (actionObject.ValueKind != JsonValueKind.Object || !actionObject.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var v in voices.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } voiceId) continue;
            var name = v.TryGetProperty("friendlyName", out var n) ? n.GetString() : null;
            list.Add(new VoicemodVoice(voiceId, string.IsNullOrWhiteSpace(name) ? voiceId : name!,
                !v.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False,
                Flag(v, "favorited"), Flag(v, "isNew"), Flag(v, "isCustom"), Text(v, "imageURL")));
        }
        return list;
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// Sons rangés par soundboard (<c>getAllSoundboard</c>) : l'identifiant du son est celui qu'attend <c>playMeme</c>.
    /// Un son présent dans plusieurs soundboards apparaît une fois par soundboard.
    /// </summary>
    public static List<VoicemodSound> ParseSoundboards(JsonElement actionObject)
    {
        var list = new List<VoicemodSound>();
        if (actionObject.ValueKind != JsonValueKind.Object || !actionObject.TryGetProperty("soundboards", out var boards) || boards.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var board in boards.EnumerateArray())
        {
            if (board.ValueKind != JsonValueKind.Object || board.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.False) continue;
            var boardName = Text(board, "name") ?? "Soundboard";
            var custom = Flag(board, "isCustom");
            if (!board.TryGetProperty("sounds", out var sounds) || sounds.ValueKind != JsonValueKind.Array) continue;
            foreach (var sound in sounds.EnumerateArray())
                if (sound.ValueKind == JsonValueKind.Object && Text(sound, "id") is { } id)
                    list.Add(new VoicemodSound(id, Text(sound, "name") ?? id, boardName, custom, Text(sound, "imageURL")));
        }
        return list;
    }

    /// <summary>Adresse d'image acceptée : https sur le stockage de contenu de Voicemod uniquement.</summary>
    public static bool IsVoicemodImageUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.EndsWith(".voicemod.net", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sons du soundboard (<c>getMemes</c> → <c>listOfMemes</c>) ; les deux casses des champs existent selon la version.</summary>
    public static List<VoicemodSound> ParseSounds(JsonElement actionObject)
    {
        var list = new List<VoicemodSound>();
        if (actionObject.ValueKind != JsonValueKind.Object || !actionObject.TryGetProperty("listOfMemes", out var memes) || memes.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var m in memes.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            string? Field(string upper, string lower)
                => m.TryGetProperty(upper, out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()
                    : m.TryGetProperty(lower, out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (Field("FileName", "fileName") is not { Length: > 0 } file) continue;
            var name = Field("Name", "name");
            list.Add(new VoicemodSound(file, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file) : name!, Field("Profile", "profile") ?? ""));
        }
        return list;
    }

    /// <summary>
    /// Image d'une voix ou d'un son (<c>getBitmap</c>) : variante transparente de préférence, en base64 (préfixe « data: »
    /// accepté), directement dans <c>actionObject</c> ou sous <c>result</c> selon la version. Null si absente ou illisible.
    /// </summary>
    public static byte[]? ParseBitmap(JsonElement actionObject)
    {
        if (actionObject.ValueKind == JsonValueKind.Object && actionObject.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            actionObject = result;
        if (actionObject.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "transparent", "default", "selected" })
        {
            if (!actionObject.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text) continue;
            var comma = text.IndexOf("base64,", StringComparison.Ordinal);
            if (comma >= 0) text = text[(comma + 7)..];
            try
            {
                var bytes = Convert.FromBase64String(text.Trim());
                if (bytes.Length > 8) return bytes;
            }
            catch (FormatException) { }
        }
        return null;
    }

    /// <summary>Extension du fichier image d'après sa signature (PNG par défaut).</summary>
    public static string ImageExtension(byte[] bytes)
        => bytes is [0xFF, 0xD8, ..] ? ".jpg"
            : bytes is [(byte)'G', (byte)'I', (byte)'F', ..] ? ".gif"
            : bytes is [(byte)'B', (byte)'M', ..] ? ".bmp"
            : ".png";

    // ---------- Connexion ----------

    /// <summary>Démarre ou arrête la connexion d'arrière-plan.</summary>
    public void Configure(bool enabled)
    {
        lock (_configLock)
        {
            if (_disposed || enabled == _enabled) return;
            _enabled = enabled;
            _cts?.Cancel();
            _cts = null;
            if (enabled)
            {
                var cts = _cts = new CancellationTokenSource();
                _ = Task.Run(() => RunAsync(cts.Token));
            }
        }
        LastError = null;
        SetConnected(false, CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ServeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (VoicemodConnectionException ex)
            {
                LastError = ex.Message;
            }
            catch (Exception)
            {
                LastError = "Voicemod injoignable.";
            }
            AwaitingApproval = false;
            if (ct.IsCancellationRequested) return;
            SetConnected(false, ct);
            try { await Task.Delay(ReconnectDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ClientKey))
            throw new VoicemodConnectionException("Clé Voicemod absente de cette version de SysDeck.");

        var (ws, port) = await OpenAsync(ct) ?? throw new VoicemodConnectionException("Voicemod n'est pas lancé (ou sa Control API est fermée).");
        using (ws)
        {
            _ws = ws;
            _sessionToken = ct;
            Port = port;
            try
            {
                await RegisterAsync(ws, ct);
                LastError = null;
                SetConnected(true, ct);
                _ = LoadInitialStateAsync(ct);
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    HandleMessage(await ReceiveTextAsync(ws, ct), ct);
            }
            finally
            {
                _ws = null;
                foreach (var pending in _pending.Values) pending.TrySetException(new VoicemodConnectionException("Connexion à Voicemod perdue."));
                _pending.Clear();
            }
        }
    }

    /// <summary>Essaie chaque port de Voicemod ; renvoie la première connexion ouverte, ou null.</summary>
    private static async Task<(ClientWebSocket, int)?> OpenAsync(CancellationToken ct)
    {
        foreach (var port in Ports)
        {
            var ws = new ClientWebSocket();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(PortTimeout);
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1"), attempt.Token);
                return (ws, port);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or System.Net.Http.HttpRequestException && !ct.IsCancellationRequested)
            {
                ws.Dispose();
            }
        }
        ct.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>
    /// Envoie la clé ; Voicemod peut répondre « Pending authentication » le temps que l'utilisateur autorise SysDeck
    /// (une seule fois), puis la réponse à <c>registerClient</c>.
    /// </summary>
    private async Task RegisterAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        await SendTextAsync(ws, BuildMessage("registerClient", id, new JsonObject { ["clientKey"] = ClientKey }), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RegisterTimeout);
        try
        {
            while (true)
            {
                VoicemodMessage message;
                try { message = ParseMessage(await ReceiveTextAsync(ws, timeout.Token)); }
                catch (JsonException) { continue; }

                if (message.Msg is { } msg && msg.Contains("pending", StringComparison.OrdinalIgnoreCase))
                {
                    // Envoyé à chaque connexion, même déjà autorisée : rien au journal, la page des Paramètres l'affiche.
                    if (!AwaitingApproval)
                    {
                        AwaitingApproval = true;
                        RaiseStateChanged();
                    }
                    continue;
                }
                if (message.Action != "registerClient" && message.Id != id) continue;
                AwaitingApproval = false;
                var status = RegisterStatus(message);
                if (status == 200) return;
                throw new VoicemodConnectionException(status is null ? "Voicemod a refusé la connexion." : $"Voicemod a refusé la clé (code {status}).");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VoicemodConnectionException("Autorisation non donnée dans Voicemod.");
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) throw new VoicemodConnectionException("Voicemod a fermé la connexion.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

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
        Log.Info(connected ? $"Voicemod connecté (port {Port})." : "Voicemod déconnecté.");
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        if (_context is null) StateChanged?.Invoke();
        else _context.Post(_ => StateChanged?.Invoke(), null);
    }

    private void HandleMessage(string text, CancellationToken ct)
    {
        VoicemodMessage message;
        try { message = ParseMessage(text); }
        catch (JsonException) { return; }

        bool changed;
        lock (_state)
        {
            if (ct.IsCancellationRequested) return;
            changed = _state.Apply(message);
        }
        if (message.Id is { } id && _pending.TryRemove(id, out var pending)) pending.TrySetResult(message);
        if (changed) RaiseStateChanged();
    }

    // ---------- Requêtes ----------

    /// <summary>Envoie une action et attend sa réponse (5 s).</summary>
    private async Task<VoicemodMessage> RequestAsync(string action, JsonObject? payload = null)
    {
        var ws = _ws;
        if (ws is null || !IsConnected) throw new VoicemodConnectionException("Voicemod n'est pas connecté.");
        var id = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<VoicemodMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _sendLock.WaitAsync(_sessionToken);
            try { await SendTextAsync(ws, BuildMessage(action, id, payload), _sessionToken); }
            finally { _sendLock.Release(); }
            return await completion.Task.WaitAsync(RequestTimeout, _sessionToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task<VoicemodMessage?> TryRequestAsync(string action, JsonObject? payload = null)
    {
        try
        {
            return await RequestAsync(action, payload);
        }
        catch (Exception ex) when (ex is VoicemodConnectionException or TimeoutException or OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Envoie une action sans attendre de réponse (certaines n'en ont pas, comme jouer un son).</summary>
    private async Task SendAsync(string action, JsonObject? payload = null)
    {
        var ws = _ws ?? throw new VoicemodConnectionException("Voicemod n'est pas connecté.");
        await _sendLock.WaitAsync(_sessionToken);
        try { await SendTextAsync(ws, BuildMessage(action, Guid.NewGuid().ToString(), payload), _sessionToken); }
        finally { _sendLock.Release(); }
    }

    private async Task LoadInitialStateAsync(CancellationToken ct)
    {
        foreach (var action in new[] { "getVoiceChangerStatus", "getHearMyselfStatus", "getBackgroundEffectStatus", "getMuteMicStatus", "getMuteMemeForMeStatus", "getCurrentVoice" })
        {
            if (ct.IsCancellationRequested) return;
            await TryRequestAsync(action);
        }
    }

    // ---------- Listes pour l'éditeur ----------

    /// <summary>Voix disponibles, favorites en tête puis par nom (relues à chaque appel : la liste change dans Voicemod).</summary>
    public async Task<List<VoicemodVoice>> GetVoicesAsync()
        => await TryRequestAsync("getVoices") is { } reply
            ? ParseVoices(reply.Object).Where(v => v.Enabled).OrderByDescending(v => v.Favorite).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : new List<VoicemodVoice>();

    /// <summary>Sons rangés par soundboard ; repli sur la liste à plat des anciennes versions de Voicemod.</summary>
    public async Task<List<VoicemodSound>> GetSoundsAsync()
    {
        if (await TryRequestAsync("getAllSoundboard") is { } boards && ParseSoundboards(boards.Object) is { Count: > 0 } sounds) return sounds;
        return await TryRequestAsync("getMemes") is { } reply ? ParseSounds(reply.Object) : new List<VoicemodSound>();
    }

    /// <summary>Image d'une voix (LoadVoice) ou d'un son (PlaySound) telle que Voicemod l'affiche ; null si indisponible.</summary>
    public async Task<byte[]?> GetBitmapAsync(VoicemodAction action, string id, string? imageUrl = null)
    {
        // Image officielle en ligne d'abord (voix et sons Voicemod), image locale de Voicemod sinon (sons perso).
        if (IsVoicemodImageUrl(imageUrl))
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(imageUrl);
                if (bytes.Length > 8) return bytes;
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException) { }
        }
        var payload = action == VoicemodAction.PlaySound ? new JsonObject { ["memeID"] = id } : new JsonObject { ["voiceID"] = id };
        return await TryRequestAsync("getBitmap", payload) is { } reply ? ParseBitmap(reply.Object) : null;
    }

    // ---------- Actions ----------

    /// <summary>Exécute l'action d'une tuile ; renvoie une erreur lisible, ou null.</summary>
    public async Task<string?> ExecuteAsync(VoicemodAction action, string? target)
    {
        if (!IsConnected) return "Voicemod n'est pas connecté.";
        try
        {
            switch (action)
            {
                case VoicemodAction.VoiceChanger:
                    await RequestAsync("toggleVoiceChanger", new JsonObject { ["value"] = IsOn(action, null) != true });
                    break;
                case VoicemodAction.HearMyself:
                    await RequestAsync("toggleHearMyVoice", new JsonObject { ["value"] = IsOn(action, null) != true });
                    break;
                case VoicemodAction.Background:
                    await RequestAsync("toggleBackground");
                    break;
                case VoicemodAction.MuteMic:
                    await RequestAsync("toggleMuteMic");
                    break;
                case VoicemodAction.MuteSoundsForMe:
                    await RequestAsync("toggleMuteMemeForMe");
                    break;
                case VoicemodAction.LoadVoice:
                    if (string.IsNullOrEmpty(target)) return "Aucune voix n'est choisie pour cette tuile.";
                    await SendAsync("loadVoice", new JsonObject { ["voiceID"] = target });
                    break;
                case VoicemodAction.RandomVoice:
                    await SendAsync("selectRandomVoice", new JsonObject { ["mode"] = "AllVoices" });
                    break;
                case VoicemodAction.PlaySound:
                    if (string.IsNullOrEmpty(target)) return "Aucun son n'est choisi pour cette tuile.";
                    await SendAsync("playMeme", new JsonObject { ["FileName"] = target, ["IsKeyDown"] = true });
                    break;
                case VoicemodAction.StopSounds:
                    await SendAsync("stopAllMemeSounds");
                    break;
            }
            return null;
        }
        catch (VoicemodConnectionException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return "Voicemod ne répond pas.";
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            return "Connexion à Voicemod perdue.";
        }
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
    }
}
