using System.IO;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SysDeck.Infrastructure;
using SysDeck.Models;

namespace SysDeck.Services;

/// <summary>Message obs-websocket : code d'opération et contenu (<c>d</c>).</summary>
public sealed record ObsMessage(int Op, JsonElement Data);

/// <summary>Réponse (op 7) à une requête.</summary>
public sealed record ObsResponse(string Type, string Id, bool Success, int Code, string? Comment, JsonElement Data);

/// <summary>Évènement (op 5) envoyé par OBS.</summary>
public sealed record ObsEvent(string Type, JsonElement Data);

/// <summary>Entrée audio d'OBS.</summary>
public sealed record ObsInput(string Name, string Kind);

/// <summary>Élément d'une scène : source et identifiant de l'élément.</summary>
public sealed record ObsSceneItem(string SourceName, int SceneItemId);

public enum ObsTestOutcome { Connected, WrongPassword, Unreachable }

/// <summary>Résultat du bouton « Tester la connexion ».</summary>
public sealed record ObsTestResult(ObsTestOutcome Outcome, string? Version, string? Detail);

/// <summary>Connexion refusée ou interrompue par OBS ; <see cref="AuthFailed"/> : mot de passe absent ou incorrect.</summary>
public sealed class ObsConnectionException : Exception
{
    public bool AuthFailed { get; }

    public ObsConnectionException(string message, bool authFailed = false) : base(message) => AuthFailed = authFailed;
}

/// <summary>
/// État d'OBS tenu à jour par les évènements. Sans verrou : <see cref="ObsService"/> l'utilise sous verrou,
/// les tests directement.
/// </summary>
public sealed class ObsState
{
    private readonly Dictionary<string, bool> _muted = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Scene, string Source), int> _itemIds = new();
    private readonly Dictionary<(string Scene, int Id), bool> _itemEnabled = new();

    public string? Scene { get; private set; }
    public bool Stream { get; private set; }
    public bool Record { get; private set; }
    public bool RecordPaused { get; private set; }
    public bool VirtualCam { get; private set; }
    public bool ReplayBuffer { get; private set; }

    public void Clear()
    {
        Scene = null;
        Stream = Record = RecordPaused = VirtualCam = ReplayBuffer = false;
        _muted.Clear();
        _itemIds.Clear();
        _itemEnabled.Clear();
    }

    public bool HasMuted(string input) => _muted.ContainsKey(input);

    public bool? Muted(string input) => _muted.TryGetValue(input, out var muted) ? muted : null;

    public void SetMuted(string input, bool muted) => _muted[input] = muted;

    public int? ItemId(string scene, string source) => _itemIds.TryGetValue((scene, source), out var id) ? id : null;

    public void SetItemId(string scene, string source, int id) => _itemIds[(scene, source)] = id;

    public void SetItemEnabled(string scene, int id, bool enabled) => _itemEnabled[(scene, id)] = enabled;

    public bool? ItemEnabled(string scene, string source)
        => ItemId(scene, source) is { } id && _itemEnabled.TryGetValue((scene, id), out var enabled) ? enabled : null;

    public void SetScene(string? scene) => Scene = scene;

    /// <summary>État d'un enregistrement lu par <c>GetRecordStatus</c>.</summary>
    public void SetRecord(bool active, bool paused)
    {
        Record = active;
        RecordPaused = active && paused;
    }

    /// <summary>Applique un évènement ; renvoie vrai si une valeur a changé.</summary>
    public bool Apply(string type, JsonElement data)
    {
        switch (type)
        {
            case "CurrentProgramSceneChanged":
                return Change(Scene, Str(data, "sceneName"), v => Scene = v);
            case "StreamStateChanged":
                return Change(Stream, Bool(data, "outputActive"), v => Stream = v);
            case "RecordStateChanged":
            {
                var active = Bool(data, "outputActive");
                var paused = Str(data, "outputState") == "OBS_WEBSOCKET_OUTPUT_PAUSED";
                var changed = active != Record || (active && paused) != RecordPaused;
                SetRecord(active, paused);
                return changed;
            }
            case "VirtualcamStateChanged":
                return Change(VirtualCam, Bool(data, "outputActive"), v => VirtualCam = v);
            case "ReplayBufferStateChanged":
                return Change(ReplayBuffer, Bool(data, "outputActive"), v => ReplayBuffer = v);
            case "InputMuteStateChanged":
            {
                if (Str(data, "inputName") is not { } input) return false;
                var muted = Bool(data, "inputMuted");
                var changed = !_muted.TryGetValue(input, out var old) || old != muted;
                _muted[input] = muted;
                return changed;
            }
            case "SceneItemEnableStateChanged":
            {
                if (Str(data, "sceneName") is not { } scene || !data.TryGetProperty("sceneItemId", out var idElement)
                    || !idElement.TryGetInt32(out var id)) return false;
                var enabled = Bool(data, "sceneItemEnabled");
                var changed = !_itemEnabled.TryGetValue((scene, id), out var old) || old != enabled;
                _itemEnabled[(scene, id)] = enabled;
                return changed;
            }
            default:
                return false;
        }
    }

    /// <summary>Vrai quand l'action est « en cours » (scène active, sortie qui tourne, entrée coupée, source visible) ; null si inconnu ou sans état.</summary>
    public bool? IsOn(ObsAction action, string? target, string? scene) => action switch
    {
        ObsAction.Scene => string.IsNullOrEmpty(target) || Scene is null ? null : Scene == target,
        ObsAction.Stream => Stream,
        ObsAction.Record => Record,
        ObsAction.RecordPause => RecordPaused,
        ObsAction.VirtualCam => VirtualCam,
        ObsAction.ReplayBuffer => ReplayBuffer,
        ObsAction.InputMute => string.IsNullOrEmpty(target) ? null : Muted(target),
        ObsAction.SourceVisibility => string.IsNullOrEmpty(target) || string.IsNullOrEmpty(scene) ? null : ItemEnabled(scene, target),
        _ => null
    };

    private static bool Change<T>(T current, T value, Action<T> set)
    {
        set(value);
        return !EqualityComparer<T>.Default.Equals(current, value);
    }

    private static string? Str(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static bool Bool(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Client obs-websocket v5 (sans paquet) : connexion et reconnexion en arrière-plan, état tenu à jour par les évènements,
/// listes pour l'éditeur de tuiles et actions. <see cref="StateChanged"/> est levé sur le contexte de synchronisation
/// capturé à la création (thread interface).
/// </summary>
public sealed class ObsService : IDisposable
{
    // General, Config, Scenes, Inputs, Outputs, SceneItems, Ui.
    public const int EventSubscriptions = 1 | 2 | 4 | 8 | 64 | 128 | 1024;

    private const int AuthFailedCloseCode = 4009;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly ObsState _state = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ObsResponse>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _configLock = new();
    private readonly object _watchLock = new();

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private CancellationToken _sessionToken;
    private bool _enabled, _disposed, _connected;
    private string _host = "127.0.0.1";
    private int _port = ObsSettings.DefaultPort;
    private string? _password;
    private HashSet<string> _watchInputs = new(StringComparer.Ordinal);
    private HashSet<(string Scene, string Source)> _watchItems = new();

    /// <summary>Levé quand la connexion ou une valeur de l'état change (sur le thread interface).</summary>
    public event Action? StateChanged;

    public bool IsEnabled => _enabled;
    public bool IsConnected => Volatile.Read(ref _connected);
    public string? ObsVersion { get; private set; }

    /// <summary>Dernière raison d'échec de connexion (vide tant que tout va bien).</summary>
    public string? LastError { get; private set; }

    public string? CurrentScene { get { lock (_state) return _state.Scene; } }

    /// <summary>État « en cours » d'une action (voir <see cref="ObsState.IsOn"/>) ; null si OBS est déconnecté ou si l'état est inconnu.</summary>
    public bool? IsOn(ObsAction action, string? target, string? scene)
    {
        if (!IsConnected) return null;
        lock (_state) return _state.IsOn(action, target, scene);
    }

    // ---------- Pur : authentification, construction et lecture des messages ----------

    /// <summary>Réponse d'authentification v5 : base64(sha256(base64(sha256(mot de passe + sel)) + défi)).</summary>
    public static string ComputeAuth(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>Message Identify (op 1) ; <paramref name="auth"/> null si OBS n'exige pas de mot de passe.</summary>
    public static string BuildIdentify(string? auth, int eventSubscriptions = EventSubscriptions)
    {
        var d = new JsonObject { ["rpcVersion"] = 1 };
        if (auth is not null) d["authentication"] = auth;
        d["eventSubscriptions"] = eventSubscriptions;
        return new JsonObject { ["op"] = 1, ["d"] = d }.ToJsonString();
    }

    /// <summary>Requête (op 6) ; <paramref name="data"/> est sérialisé en <c>requestData</c> s'il est fourni.</summary>
    public static string BuildRequest(string type, string id, object? data = null)
    {
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data is not null) d["requestData"] = JsonSerializer.SerializeToNode(data);
        return new JsonObject { ["op"] = 6, ["d"] = d }.ToJsonString();
    }

    /// <summary>Lit l'enveloppe d'un message ; le contenu est copié (utilisable après la lecture).</summary>
    public static ObsMessage ParseMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var op = root.GetProperty("op").GetInt32();
        var data = root.TryGetProperty("d", out var d) ? d.Clone() : default;
        return new ObsMessage(op, data);
    }

    /// <summary>Lit le contenu d'une réponse (op 7).</summary>
    public static ObsResponse ParseResponse(JsonElement d)
    {
        var status = d.GetProperty("requestStatus");
        return new ObsResponse(
            d.GetProperty("requestType").GetString() ?? "",
            d.GetProperty("requestId").GetString() ?? "",
            status.GetProperty("result").GetBoolean(),
            status.TryGetProperty("code", out var code) ? code.GetInt32() : 0,
            status.TryGetProperty("comment", out var comment) ? comment.GetString() : null,
            d.TryGetProperty("responseData", out var data) ? data : default);
    }

    /// <summary>Lit le contenu d'un évènement (op 5).</summary>
    public static ObsEvent ParseEvent(JsonElement d)
        => new(d.GetProperty("eventType").GetString() ?? "", d.TryGetProperty("eventData", out var data) ? data : default);

    /// <summary>Noms des scènes d'une réponse <c>GetSceneList</c>, dans l'ordre d'affichage d'OBS (OBS les renvoie à l'envers).</summary>
    public static List<string> ParseSceneNames(JsonElement data)
    {
        var names = new List<string>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
            foreach (var scene in scenes.EnumerateArray())
                if (scene.TryGetProperty("sceneName", out var name) && name.GetString() is { } text)
                    names.Add(text);
        names.Reverse();
        return names;
    }

    // ---------- Connexion ----------

    /// <summary>Applique les réglages : démarre, relance ou arrête la connexion d'arrière-plan.</summary>
    public void Configure(bool enabled, string? host, int port, string? password)
    {
        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (port is < 1 or > 65535) port = ObsSettings.DefaultPort;
        lock (_configLock)
        {
            if (_disposed) return;
            if (enabled == _enabled && host == _host && port == _port && password == _password) return;
            _enabled = enabled;
            _host = host;
            _port = port;
            _password = password;
            _cts?.Cancel();
            _cts = null;
            if (enabled)
            {
                var cts = _cts = new CancellationTokenSource();
                _ = Task.Run(() => RunAsync(host, port, password, cts.Token));
            }
        }
        LastError = null;
        SetConnected(false, CancellationToken.None);
    }

    private async Task RunAsync(string host, int port, string? password, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ServeAsync(host, port, password, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObsConnectionException ex)
            {
                LastError = ex.AuthFailed ? "Mot de passe incorrect." : ex.Message;
            }
            catch (Exception)
            {
                LastError = "OBS injoignable.";
            }
            if (ct.IsCancellationRequested) return;
            SetConnected(false, ct);
            try { await Task.Delay(ReconnectDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ServeAsync(string host, int port, string? password, CancellationToken ct)
    {
        using var ws = NewSocket();
        string obsWebSocketVersion;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(ConnectTimeout);
            try
            {
                await ws.ConnectAsync(new Uri($"ws://{host}:{port}"), handshake.Token);
                obsWebSocketVersion = await HandshakeAsync(ws, password, EventSubscriptions, handshake.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ObsConnectionException("OBS ne répond pas.");
            }
        }

        _ws = ws;
        _sessionToken = ct;
        LastError = null;
        ObsVersion = $"obs-websocket {obsWebSocketVersion}";
        SetConnected(true, ct);
        _ = LoadInitialStateAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                HandleMessage(await ReceiveTextAsync(ws, ct), ct);
        }
        finally
        {
            _ws = null;
            foreach (var pending in _pending.Values) pending.TrySetException(new ObsConnectionException("Connexion à OBS perdue."));
            _pending.Clear();
        }
    }

    private static ClientWebSocket NewSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("obswebsocket.json");
        return ws;
    }

    /// <summary>Hello puis Identify puis Identified ; renvoie la version d'obs-websocket annoncée par OBS.</summary>
    private static async Task<string> HandshakeAsync(ClientWebSocket ws, string? password, int subscriptions, CancellationToken ct)
    {
        var hello = ParseMessage(await ReceiveTextAsync(ws, ct));
        if (hello.Op != 0) throw new ObsConnectionException("Réponse inattendue d'OBS.");

        string? auth = null;
        if (hello.Data.TryGetProperty("authentication", out var authentication) && authentication.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrEmpty(password)) throw new ObsConnectionException("OBS demande un mot de passe.", authFailed: true);
            auth = ComputeAuth(password, authentication.GetProperty("salt").GetString() ?? "", authentication.GetProperty("challenge").GetString() ?? "");
        }
        await SendTextAsync(ws, BuildIdentify(auth, subscriptions), ct);

        var identified = ParseMessage(await ReceiveTextAsync(ws, ct));
        if (identified.Op != 2) throw new ObsConnectionException("OBS a refusé l'identification.");
        return hello.Data.TryGetProperty("obsWebSocketVersion", out var version) ? version.GetString() ?? "" : "";
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var code = (int?)ws.CloseStatus;
                throw new ObsConnectionException(code == AuthFailedCloseCode ? "Mot de passe incorrect." : "OBS a fermé la connexion.",
                    authFailed: code == AuthFailedCloseCode);
            }
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <summary>Connexion jetable pour le bouton « Tester la connexion » : n'influe pas sur la connexion principale.</summary>
    public static async Task<ObsTestResult> TestAsync(string host, int port, string? password, CancellationToken ct = default)
    {
        using var ws = NewSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{host.Trim()}:{port}"), cts.Token);
            var webSocketVersion = await HandshakeAsync(ws, password, 0, cts.Token);

            var id = Guid.NewGuid().ToString();
            await SendTextAsync(ws, BuildRequest("GetVersion", id), cts.Token);
            while (true)
            {
                var message = ParseMessage(await ReceiveTextAsync(ws, cts.Token));
                if (message.Op != 7) continue;
                var response = ParseResponse(message.Data);
                if (response.Id != id) continue;
                var obs = response.Data.ValueKind == JsonValueKind.Object && response.Data.TryGetProperty("obsVersion", out var v) ? v.GetString() : null;
                return new ObsTestResult(ObsTestOutcome.Connected, obs is null ? null : $"OBS {obs}", webSocketVersion);
            }
        }
        catch (ObsConnectionException ex) when (ex.AuthFailed)
        {
            return new ObsTestResult(ObsTestOutcome.WrongPassword, null, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ObsTestResult(ObsTestOutcome.Unreachable, null, "délai dépassé");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ObsTestResult(ObsTestOutcome.Unreachable, null, ex.InnerException?.Message ?? ex.Message);
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
        // Une seule ligne par passage connecté <-> déconnecté, pas une par tentative.
        Log.Info(connected ? $"OBS connecté ({_host}:{_port})." : "OBS déconnecté.");
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        if (_context is null) StateChanged?.Invoke();
        else _context.Post(_ => StateChanged?.Invoke(), null);
    }

    // ---------- Messages reçus ----------

    private void HandleMessage(string text, CancellationToken ct)
    {
        ObsMessage message;
        try { message = ParseMessage(text); }
        catch (JsonException) { return; }

        try
        {
            if (message.Op == 7)
            {
                var response = ParseResponse(message.Data);
                if (_pending.TryRemove(response.Id, out var pending)) pending.TrySetResult(response);
            }
            else if (message.Op == 5)
            {
                var obsEvent = ParseEvent(message.Data);
                bool changed;
                lock (_state)
                {
                    if (ct.IsCancellationRequested) return;
                    changed = _state.Apply(obsEvent.Type, obsEvent.Data);
                }
                if (changed) RaiseStateChanged();
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.WarnOnce("obs-message", "Message OBS illisible ignoré.", ex);
        }
    }

    // ---------- Requêtes ----------

    /// <summary>Envoie une requête et attend sa réponse (délai de 5 s). Lève <see cref="ObsConnectionException"/> si OBS n'est pas connecté.</summary>
    public async Task<ObsResponse> RequestAsync(string type, object? data = null)
    {
        var ws = _ws;
        if (ws is null || !IsConnected) throw new ObsConnectionException("OBS n'est pas connecté.");

        var id = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<ObsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _sendLock.WaitAsync(_sessionToken);
            try { await SendTextAsync(ws, BuildRequest(type, id, data), _sessionToken); }
            finally { _sendLock.Release(); }
            return await completion.Task.WaitAsync(RequestTimeout, _sessionToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Données d'une requête réussie, ou null (échec, délai, déconnexion).</summary>
    private async Task<JsonElement?> TryRequestAsync(string type, object? data = null)
    {
        try
        {
            var response = await RequestAsync(type, data);
            if (!response.Success) return null;
            return response.Data.ValueKind == JsonValueKind.Object ? response.Data : EmptyObject;
        }
        catch (Exception ex) when (ex is ObsConnectionException or TimeoutException or OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task LoadInitialStateAsync(CancellationToken ct)
    {
        try
        {
            if (await TryRequestAsync("GetVersion") is { } version && version.TryGetProperty("obsVersion", out var obs))
                ObsVersion = $"OBS {obs.GetString()}";

            var scene = await TryRequestAsync("GetCurrentProgramScene");
            var stream = await TryRequestAsync("GetStreamStatus");
            var record = await TryRequestAsync("GetRecordStatus");
            var virtualCam = await TryRequestAsync("GetVirtualCamStatus");
            var replay = await TryRequestAsync("GetReplayBufferStatus"); // échoue si le tampon de relecture n'est pas activé
            lock (_state)
            {
                if (ct.IsCancellationRequested) return;
                if (scene is { } s)
                    _state.SetScene(Prop(s, "sceneName") ?? Prop(s, "currentProgramSceneName"));
                if (stream is { } st) _state.Apply("StreamStateChanged", st);
                if (record is { } r)
                    _state.SetRecord(Flag(r, "outputActive"), Flag(r, "outputPaused"));
                if (virtualCam is { } v) _state.Apply("VirtualcamStateChanged", v);
                if (replay is { } rb) _state.Apply("ReplayBufferStateChanged", rb);
            }
            RaiseStateChanged();
            await LoadWatchedAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.WarnOnce("obs-initial", "Lecture de l'état initial d'OBS incomplète.", ex);
        }
    }

    private static string? Prop(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static bool Flag(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Indique les cibles des tuiles existantes (entrées audio, éléments de scène) dont l'état doit être lu : à la
    /// connexion, puis à chaque changement de cette liste. Sans effet si la liste est inchangée.
    /// </summary>
    public void Watch(IEnumerable<string> inputs, IEnumerable<(string Scene, string Source)> items)
    {
        var newInputs = inputs.ToHashSet(StringComparer.Ordinal);
        var newItems = items.ToHashSet();
        lock (_watchLock)
        {
            if (newInputs.SetEquals(_watchInputs) && newItems.SetEquals(_watchItems)) return;
            _watchInputs = newInputs;
            _watchItems = newItems;
        }
        if (IsConnected)
        {
            var ct = _sessionToken;
            _ = Task.Run(() => LoadWatchedAsync(ct));
        }
    }

    private async Task LoadWatchedAsync(CancellationToken ct)
    {
        string[] inputs;
        (string Scene, string Source)[] items;
        lock (_watchLock)
        {
            inputs = _watchInputs.ToArray();
            items = _watchItems.ToArray();
        }

        var changed = false;
        foreach (var input in inputs)
        {
            if (ct.IsCancellationRequested) return;
            bool known;
            lock (_state) known = _state.HasMuted(input);
            if (known) continue;
            if (await TryRequestAsync("GetInputMute", new { inputName = input }) is { } data)
            {
                lock (_state) _state.SetMuted(input, Flag(data, "inputMuted"));
                changed = true;
            }
        }

        foreach (var (scene, source) in items)
        {
            if (ct.IsCancellationRequested) return;
            bool known;
            lock (_state) known = _state.ItemEnabled(scene, source) is not null;
            if (known) continue;
            if (await ResolveItemIdAsync(scene, source) is not { } id) continue;
            if (await TryRequestAsync("GetSceneItemEnabled", new { sceneName = scene, sceneItemId = id }) is { } data)
            {
                lock (_state) _state.SetItemEnabled(scene, id, Flag(data, "sceneItemEnabled"));
                changed = true;
            }
        }
        if (changed) RaiseStateChanged();
    }

    private async Task<int?> ResolveItemIdAsync(string scene, string source)
    {
        lock (_state)
            if (_state.ItemId(scene, source) is { } known) return known;
        if (await TryRequestAsync("GetSceneItemId", new { sceneName = scene, sourceName = source }) is not { } data
            || !data.TryGetProperty("sceneItemId", out var idElement) || !idElement.TryGetInt32(out var id))
            return null;
        lock (_state) _state.SetItemId(scene, source, id);
        return id;
    }

    // ---------- Listes pour l'éditeur ----------

    /// <summary>Noms des scènes ; liste vide si OBS ne répond pas.</summary>
    public async Task<List<string>> GetScenesAsync()
        => await TryRequestAsync("GetSceneList") is { } data ? ParseSceneNames(data) : new List<string>();

    /// <summary>
    /// Entrées audio : celles qui ont un état muet (les autres, comme un navigateur sans audio, ne sont pas listées).
    /// Liste complète si aucune n'en a, par prudence.
    /// </summary>
    public async Task<List<ObsInput>> GetAudioInputsAsync()
    {
        var all = new List<ObsInput>();
        if (await TryRequestAsync("GetInputList") is { } data && data.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            foreach (var input in inputs.EnumerateArray())
                if (Prop(input, "inputName") is { } name)
                    all.Add(new ObsInput(name, Prop(input, "inputKind") ?? ""));

        var checks = await Task.WhenAll(all.Select(async input => (Input: input, Data: await TryRequestAsync("GetInputMute", new { inputName = input.Name }))));
        var audio = checks.Where(c => c.Data is not null).Select(c => c.Input).ToList();
        return audio.Count > 0 ? audio : all;
    }

    /// <summary>Éléments d'une scène (source et identifiant) ; liste vide si OBS ne répond pas.</summary>
    public async Task<List<ObsSceneItem>> GetSceneItemsAsync(string scene)
    {
        var items = new List<ObsSceneItem>();
        if (await TryRequestAsync("GetSceneItemList", new { sceneName = scene }) is { } data
            && data.TryGetProperty("sceneItems", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (Prop(item, "sourceName") is { } source && item.TryGetProperty("sceneItemId", out var id) && id.TryGetInt32(out var number))
                    items.Add(new ObsSceneItem(source, number));
        return items;
    }

    // ---------- Actions ----------

    /// <summary>Exécute l'action d'une tuile ; renvoie un message d'erreur lisible, ou null si OBS l'a acceptée.</summary>
    public async Task<string?> ExecuteAsync(ObsAction action, string? target, string? scene)
    {
        if (!IsConnected) return "OBS n'est pas connecté.";
        try
        {
            switch (action)
            {
                case ObsAction.Scene:
                    return string.IsNullOrEmpty(target) ? "Aucune scène n'est choisie pour cette tuile."
                        : await SendAsync("SetCurrentProgramScene", new { sceneName = target });
                case ObsAction.Stream: return await SendAsync("ToggleStream");
                case ObsAction.Record: return await SendAsync("ToggleRecord");
                case ObsAction.RecordPause: return await SendAsync("ToggleRecordPause");
                case ObsAction.VirtualCam: return await SendAsync("ToggleVirtualCam");
                case ObsAction.ReplayBuffer: return await SendAsync("ToggleReplayBuffer");
                case ObsAction.SaveReplay: return await SendAsync("SaveReplayBuffer");
                case ObsAction.StudioTransition: return await SendAsync("TriggerStudioModeTransition");
                case ObsAction.InputMute:
                    return string.IsNullOrEmpty(target) ? "Aucune entrée audio n'est choisie pour cette tuile."
                        : await SendAsync("ToggleInputMute", new { inputName = target });
                case ObsAction.SourceVisibility:
                    return await ToggleSourceAsync(scene, target);
                default:
                    return "Action OBS inconnue.";
            }
        }
        catch (TimeoutException)
        {
            return "OBS ne répond pas.";
        }
        catch (Exception ex) when (ex is ObsConnectionException or OperationCanceledException or WebSocketException)
        {
            return ex is OperationCanceledException ? "Connexion à OBS perdue." : ex.Message;
        }
    }

    private async Task<string?> SendAsync(string type, object? data = null)
    {
        var response = await RequestAsync(type, data);
        return response.Success ? null : response.Comment ?? $"OBS a refusé la requête (code {response.Code}).";
    }

    /// <summary>Bascule la visibilité d'une source : identifiant par nom, état courant, puis l'inverse.</summary>
    private async Task<string?> ToggleSourceAsync(string? scene, string? source)
    {
        if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(source)) return "Aucune source n'est choisie pour cette tuile.";
        if (await ResolveItemIdAsync(scene, source) is not { } id) return $"La source « {source} » est introuvable dans la scène « {scene} ».";

        var current = await RequestAsync("GetSceneItemEnabled", new { sceneName = scene, sceneItemId = id });
        if (!current.Success) return current.Comment ?? $"OBS a refusé la requête (code {current.Code}).";
        var enabled = Flag(current.Data, "sceneItemEnabled");
        return await SendAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = id, sceneItemEnabled = !enabled });
    }

    public void Dispose()
    {
        lock (_configLock)
        {
            _disposed = true;
            _enabled = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        try { _ws?.Abort(); } catch (ObjectDisposedException) { }
    }
}
