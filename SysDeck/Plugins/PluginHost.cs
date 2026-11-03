using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SysDeck.Infrastructure;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck.Plugins;

/// <summary>État d'un plugin installé, affiché dans les Paramètres.</summary>
public enum PluginStatus { Stopped, Starting, Running, Crashed, Disabled, Invalid }

/// <summary>Plugin installé : manifest (null s'il est invalide), état et détail lisible (erreur, plantages).</summary>
public sealed class PluginInfo
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    public PluginManifest? Manifest { get; init; }
    public PluginStatus Status { get; internal set; }
    public string? Detail { get; internal set; }
    public string Name => Manifest?.Name ?? Id;
}

/// <summary>
/// Hôte des plugins : un serveur WebSocket local (127.0.0.1, port libre), un programme par plugin activé lancé avec les
/// arguments du Stream Deck (<c>-port -pluginUUID -registerEvent -info</c>), et le relais des évènements entre les tuiles
/// et les plugins. Tout l'état vit sur le thread interface (les attentes réseau y reprennent) ; un plugin qui plante est
/// relancé avec un délai croissant, puis abandonné après 3 plantages en une minute.
/// </summary>
public sealed partial class PluginHost : IDisposable
{
    // Plugins Stream Deck : parfois livrés avec leurs node_modules.
    private const int MaxArchiveFiles = 30_000;
    private const long MaxArchiveBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(1.5);

    private readonly Func<IEnumerable<ShortcutItem>> _tiles;
    private readonly Func<(int Columns, int Rows)> _grid;
    private readonly HashSet<string> _ignoredEvents = new();
    private readonly Action _saveTiles;
    private readonly PluginsConfig _config;
    private readonly Action _saveConfig;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<string, Runner> _runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TileState> _tileStates = new();
    private readonly DispatcherTimer _tilesTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly JobObject _job = new();
    private TcpListener? _listener;
    private int _port;
    private bool _disposed;

    /// <summary>Un plugin a changé d'état (démarré, arrêté, planté, installé…).</summary>
    public event Action? StateChanged;

    /// <summary>Un plugin a changé le titre, l'image ou l'indicateur d'une tuile (regroupé pour ne pas tout redessiner à chaque message).</summary>
    public event Action? TilesChanged;

    /// <param name="tiles">Toutes les tuiles (Switch et barre) ; seules celles des plugins sont relayées.</param>
    /// <param name="saveTiles">Enregistre les tuiles après un <c>setSettings</c>.</param>
    /// <param name="grid">Colonnes et lignes de la grille de tuiles (taille de « l'appareil » pour les plugins Stream Deck).</param>
    public PluginHost(Func<IEnumerable<ShortcutItem>> tiles, Action saveTiles, PluginsConfig config, Action saveConfig,
        Func<(int Columns, int Rows)>? grid = null)
    {
        _grid = grid ?? (() => (5, 3));
        _tiles = tiles;
        _saveTiles = saveTiles;
        _config = config;
        _saveConfig = saveConfig;
        _ui = SynchronizationContext.Current ?? new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        _tilesTimer.Tick += (_, _) =>
        {
            _tilesTimer.Stop();
            TilesChanged?.Invoke();
        };
    }

    public static string Dir => AppPaths.Folder("Plugins");

    /// <summary>Plugins installés, par nom.</summary>
    public IReadOnlyList<PluginInfo> Plugins
        => _runners.Values.Select(r => r.Info).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public PluginInfo? Find(string? id) => id is not null && _runners.TryGetValue(id, out var r) ? r.Info : null;

    public bool IsRunning(string? id) => id is not null && _runners.TryGetValue(id, out var r) && r.Socket is { State: WebSocketState.Open };

    // ---------- Démarrage, arrêt ----------

    /// <summary>Lit les plugins installés et lance ceux qui sont activés.</summary>
    public void Start()
    {
        Reload();
        foreach (var runner in _runners.Values) Launch(runner);
    }

    /// <summary>Relit le dossier des plugins : ajoute les nouveaux, oublie ceux qui ont disparu (arrêtés).</summary>
    private void Reload()
    {
        var found = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(Dir))
            foreach (var folder in Directory.EnumerateDirectories(Dir))
            {
                var name = Path.GetFileName(folder);
                if (name.StartsWith('.')) continue; // installation en cours ou abandonnée
                try
                {
                    var manifest = PluginManifest.Load(folder);
                    // Le dossier doit porter l'identifiant : deux copies d'un même plugin ne peuvent pas coexister.
                    if (!string.Equals(manifest.Id, name, StringComparison.OrdinalIgnoreCase))
                        found[name] = Invalid(name, folder, $"Le dossier devrait s'appeler « {manifest.Id} ».");
                    else found[manifest.Id] = new PluginInfo { Id = manifest.Id, Folder = folder, Manifest = manifest };
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    found[name] = Invalid(name, folder, ex.Message);
                }
            }

        foreach (var id in _runners.Keys.Where(id => !found.ContainsKey(id)).ToList())
        {
            Stop(_runners[id], wait: true);
            _runners.Remove(id);
        }
        foreach (var (id, info) in found)
        {
            if (_runners.TryGetValue(id, out var existing) && existing.Info.Manifest is not null && info.Manifest is not null) continue;
            if (existing is not null) Stop(existing, wait: true);
            info.Status = info.Manifest is null ? PluginStatus.Invalid : IsDisabled(id) ? PluginStatus.Disabled : PluginStatus.Stopped;
            _runners[id] = new Runner(info);
        }
        StateChanged?.Invoke();
    }

    private static PluginInfo Invalid(string id, string folder, string error)
        => new() { Id = id, Folder = folder, Status = PluginStatus.Invalid, Detail = error };

    private bool IsDisabled(string id) => _config.Disabled.Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(string id, bool enabled)
    {
        _config.Disabled.RemoveAll(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase));
        if (!enabled) _config.Disabled.Add(id);
        _saveConfig();
        if (!_runners.TryGetValue(id, out var runner) || runner.Info.Manifest is null) return;
        if (enabled)
        {
            runner.Info.Status = PluginStatus.Stopped;
            Launch(runner);
        }
        else
        {
            Stop(runner, wait: true);
            runner.Info.Status = PluginStatus.Disabled;
            runner.Info.Detail = null;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Arrête puis relance un plugin (remet à zéro le compte de plantages).</summary>
    public void Restart(string id)
    {
        if (!_runners.TryGetValue(id, out var runner) || runner.Info.Manifest is null || IsDisabled(id)) return;
        Stop(runner, wait: true);
        runner.Crashes.Clear();
        runner.Info.Status = PluginStatus.Stopped;
        Launch(runner);
    }

    private void Launch(Runner runner)
    {
        if (_disposed || runner.Info.Manifest is not { } manifest || runner.Process is not null) return;
        if (IsDisabled(manifest.Id))
        {
            runner.Info.Status = PluginStatus.Disabled;
            return;
        }
        runner.Stopping = false; // relance après Stop (Redémarrer, réactivation) : y compris après le téléchargement de Node.js

        string? node = null;
        if (IsJavaScript(manifest))
        {
            var major = manifest.NodeMajor ?? 20;
            node = NodeRuntime.Find(major);
            if (node is null)
            {
                DownloadNodeThenLaunch(runner, major);
                return;
            }
        }

        try
        {
            EnsureListener();
            runner.Token = PluginProtocol.NewToken();
            runner.Stopping = false;
            var (columns, rows) = _grid();
            // Plugins Stream Deck : ils vérifient parfois la version du logiciel d'Elgato ; on annonce une version récente.
            var info = PluginProtocol.BuildInfo(manifest.IsElgato ? StreamDeckVersion : AppVersion, manifest.Id, manifest.Version, columns, rows);
            var start = BuildStartInfo(manifest, PluginProtocol.BuildArguments(_port, runner.Token, info), node);
            start.Environment["SYSDECK_PLUGIN_PORT"] = _port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["SYSDECK_PLUGIN_UUID"] = runner.Token;

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var id = manifest.Id;
            process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) PluginLog.Write(id, line); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) PluginLog.Write(id, "[erreur] " + line); };
            process.Exited += (_, _) => _ui.Post(_ => OnExited(runner, process), null);
            process.Start();
            _job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            runner.Process = process;
            runner.Info.Status = PluginStatus.Starting;
            runner.Info.Detail = null;
            PluginLog.Write(id, $"--- Démarrage (version {manifest.Version}, port {_port}) ---");
            Log.Info($"Plugin « {manifest.Name} » démarré");
        }
        catch (Win32Exception ex)
        {
            runner.Info.Status = PluginStatus.Crashed;
            runner.Info.Detail = MissingInterpreter(manifest) ?? $"Lancement impossible : {ex.Message}";
            PluginLog.Write(manifest.Id, runner.Info.Detail);
            Log.Warn($"Plugin « {manifest.Name} » : lancement impossible", ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException)
        {
            runner.Info.Status = PluginStatus.Crashed;
            runner.Info.Detail = $"Lancement impossible : {ex.Message}";
            Log.Warn($"Plugin « {manifest.Name} » : lancement impossible", ex);
        }
        StateChanged?.Invoke();
    }

    private static bool IsJavaScript(PluginManifest manifest) => Path.GetExtension(manifest.CodePath).ToLowerInvariant() is ".js" or ".mjs" or ".cjs";

    /// <summary>Node.js absent ou trop ancien : téléchargé une fois (nodejs.org), puis le plugin démarre.</summary>
    private async void DownloadNodeThenLaunch(Runner runner, int major)
    {
        runner.Info.Status = PluginStatus.Starting;
        runner.Info.Detail = $"Téléchargement de Node.js {major} (une seule fois, environ 30 Mo)…";
        StateChanged?.Invoke();
        try
        {
            await NodeRuntime.EnsureAsync(major);
        }
        catch (Exception ex)
        {
            runner.Info.Status = PluginStatus.Crashed;
            runner.Info.Detail = $"Node.js {major} n'a pas pu être téléchargé : {ex.Message}";
            Log.Warn("Téléchargement de Node.js impossible", ex);
            StateChanged?.Invoke();
            return;
        }
        if (!_disposed && runner.Process is null && !runner.Stopping && _runners.ContainsValue(runner)) Launch(runner);
    }

    /// <summary>Version du logiciel Stream Deck annoncée aux plugins d'Elgato (au moins celle qu'ils demandent d'habitude).</summary>
    private const string StreamDeckVersion = "7.1.0";

    /// <summary>Programme à lancer selon l'extension de CodePath : .exe direct, sinon l'interpréteur du langage.</summary>
    internal static ProcessStartInfo BuildStartInfo(PluginManifest manifest, IEnumerable<string> arguments, string? node = null)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = manifest.Folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        switch (Path.GetExtension(manifest.CodePath).ToLowerInvariant())
        {
            case ".js" or ".mjs" or ".cjs":
                start.FileName = node ?? "node";
                start.ArgumentList.Add(manifest.CodePath);
                break;
            case ".py":
                start.FileName = "python";
                start.ArgumentList.Add(manifest.CodePath);
                break;
            case ".ps1":
                start.FileName = "powershell.exe";
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", manifest.CodePath }) start.ArgumentList.Add(a);
                break;
            default:
                start.FileName = manifest.CodePath;
                break;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static string? MissingInterpreter(PluginManifest manifest) => Path.GetExtension(manifest.CodePath).ToLowerInvariant() switch
    {
        ".js" => "Node.js est introuvable : installe-le (nodejs.org) pour utiliser ce plugin.",
        ".py" => "Python est introuvable : installe-le (python.org) pour utiliser ce plugin.",
        _ => null
    };

    private static string AppVersion => typeof(PluginHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private async void OnExited(Runner runner, Process process)
    {
        if (runner.Process != process) return;
        int? code = null;
        try { code = process.ExitCode; } catch (InvalidOperationException) { }
        process.Dispose();
        runner.Process = null;
        CloseSocket(runner);
        var id = runner.Info.Id;
        PluginLog.Write(id, $"--- Arrêt (code {code?.ToString() ?? "?"}) ---");

        if (runner.Stopping || _disposed)
        {
            if (runner.Info.Status != PluginStatus.Disabled) runner.Info.Status = PluginStatus.Stopped;
            StateChanged?.Invoke();
            return;
        }

        var now = DateTime.UtcNow;
        runner.Crashes.RemoveAll(t => now - t > CrashWindow);
        runner.Crashes.Add(now);
        if (runner.Crashes.Count >= 3)
        {
            runner.Info.Status = PluginStatus.Crashed;
            runner.Info.Detail = $"Le plugin s'est arrêté {runner.Crashes.Count} fois en une minute (code {code?.ToString() ?? "?"}) : consulte son journal.";
            Log.Warn($"Plugin « {runner.Info.Name} » abandonné après plusieurs plantages");
            StateChanged?.Invoke();
            return;
        }

        runner.Info.Status = PluginStatus.Starting;
        runner.Info.Detail = $"Arrêt inattendu (code {code?.ToString() ?? "?"}) : relance…";
        StateChanged?.Invoke();
        await Task.Delay(TimeSpan.FromSeconds(2 * runner.Crashes.Count));
        if (!runner.Stopping && !_disposed && runner.Process is null && _runners.ContainsValue(runner)) Launch(runner);
    }

    /// <summary>Arrête le programme du plugin (et ses enfants) ; <paramref name="wait"/> attend sa fin (avant de toucher à ses fichiers).</summary>
    private void Stop(Runner runner, bool wait)
    {
        runner.Stopping = true;
        CloseSocket(runner);
        if (runner.Process is not { } process) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (wait) process.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // déjà terminé
        }
        if (wait)
        {
            process.Dispose();
            runner.Process = null;
            if (runner.Info.Status is PluginStatus.Running or PluginStatus.Starting) runner.Info.Status = PluginStatus.Stopped;
        }
    }

    private void CloseSocket(Runner runner)
    {
        if (runner.Socket is { } socket)
        {
            runner.Socket = null;
            try { socket.Abort(); } catch { /* déjà fermé */ }
            socket.Dispose();
        }
        runner.Appeared.Clear();
        foreach (var context in _tileStates.Keys.ToList())
            if (_tileStates[context].PluginId == runner.Info.Id) _tileStates.Remove(context);
        QueueTilesChanged();
    }

    // ---------- Installation ----------

    /// <summary>
    /// Installe un plugin depuis un fichier .sysdeckplugin (zip) ou un dossier ; une version déjà installée est arrêtée et
    /// remplacée. Lève <see cref="InvalidDataException"/> avec un message lisible si ce n'est pas un plugin valide.
    /// </summary>
    public PluginManifest Install(string source)
    {
        Directory.CreateDirectory(Dir);
        var staging = Path.Combine(Dir, $".install-{Guid.NewGuid():N}");
        try
        {
            if (Directory.Exists(source)) SafeZip.CopyFolder(source, staging, MaxArchiveBytes, MaxArchiveFiles);
            else if (File.Exists(source)) SafeZip.Extract(source, staging, MaxArchiveBytes, MaxArchiveFiles);
            else throw new InvalidDataException("Fichier introuvable.");

            var root = Directory.EnumerateFiles(staging, "manifest.json", SearchOption.AllDirectories)
                .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                .Select(Path.GetDirectoryName)
                .FirstOrDefault() ?? throw new InvalidDataException("manifest.json introuvable : ce n'est pas un plugin.");
            SvgRasterizer.ConvertFolder(root!); // images des plugins Stream Deck souvent en SVG
            var manifest = PluginManifest.Load(root!);

            var dest = Path.Combine(Dir, manifest.Id);
            if (_runners.TryGetValue(manifest.Id, out var existing)) Stop(existing, wait: true);
            string? backup = null;
            try
            {
                if (Directory.Exists(dest))
                {
                    backup = Path.Combine(Dir, $".old-{Guid.NewGuid():N}");
                    Directory.Move(dest, backup);
                }
                Directory.Move(root!, dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fichier encore verrouillé : l'ancienne version reprend sa place et redémarre.
                if (backup is not null && !Directory.Exists(dest)) Directory.Move(backup, dest);
                if (existing is not null) Launch(existing);
                throw;
            }
            _runners.Remove(manifest.Id);
            if (backup is not null)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Ancienne version du plugin non supprimée : {backup}", ex); }
            }
            Log.Info($"Plugin installé : {manifest.Name} {manifest.Version} ({manifest.Id})");

            Reload();
            if (_runners.TryGetValue(manifest.Id, out var runner)) Launch(runner);
            return runner?.Info.Manifest ?? manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Impossible d'installer le plugin : {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* reliquat ignoré à la lecture (nom en « . ») */ }
        }
    }

    /// <summary>Arrête et supprime un plugin (ses tuiles restent, atténuées, tant qu'il n'est pas réinstallé).</summary>
    public void Uninstall(string id)
    {
        if (!_runners.TryGetValue(id, out var runner)) return;
        Stop(runner, wait: true);
        _runners.Remove(id);
        if (SafeZip.IsInside(Dir, runner.Info.Folder) && Directory.Exists(runner.Info.Folder))
            Directory.Delete(runner.Info.Folder, recursive: true);
        Log.Info($"Plugin désinstallé : {id}");
        StateChanged?.Invoke();
    }

    // ---------- Serveur WebSocket ----------

    private void EnsureListener()
    {
        if (_listener is not null) return;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listener = listener;
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(listener);
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_disposed)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = ServeAsync(client);
        }
    }

    /// <summary>Une connexion : poignée de main, enregistrement obligatoire avec le jeton d'un plugin lancé, puis messages.</summary>
    private async Task ServeAsync(TcpClient client)
    {
        Runner? runner = null;
        WebSocket? socket = null;
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var key = PluginProtocol.ParseHandshake(await ReadHeadersAsync(stream, timeout.Token));
                if (key is null)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"), timeout.Token);
                    return;
                }
                var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                               $"Sec-WebSocket-Accept: {PluginProtocol.HandshakeAccept(key)}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
                socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });

                // Premier message : registerPlugin avec le jeton secret passé au programme, ou registerPropertyInspector
                // avec le contexte d'une page de réglages ouverte par SysDeck ; sinon on raccroche.
                var first = PluginProtocol.Parse(await ReceiveAsync(socket, timeout.Token) ?? "");
                if (first is { Event: RegisterPropertyInspector } && InspectorFor(first.Uuid) is { } inspector)
                {
                    await ServeInspectorAsync(inspector, socket);
                    return;
                }
                runner = first is { Event: PluginProtocol.RegisterPlugin }
                    ? _runners.Values.FirstOrDefault(r => r.Process is not null && PluginProtocol.TokenMatches(r.Token, first.Uuid))
                    : null;
                if (runner is null)
                {
                    Log.Warn("Connexion de plugin refusée : jeton absent ou inconnu.");
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unknown plugin", CancellationToken.None);
                    return;
                }
                CloseSocket(runner); // une seule connexion par plugin : la plus récente
                runner.Socket = socket;
                runner.Info.Status = PluginStatus.Running;
                runner.Info.Detail = null;
                PluginLog.Write(runner.Info.Id, "Connecté à SysDeck");
                StateChanged?.Invoke();
                var (gridColumns, gridRows) = _grid();
                await SendAsync(runner, PluginProtocol.BuildDeviceDidConnect(gridColumns, gridRows));
                await SendAsync(runner, PluginProtocol.BuildGlobalSettings(GlobalSettings(runner.Info.Id)));
                SyncTiles();

                while (socket.State == WebSocketState.Open && !_disposed)
                {
                    var text = await ReceiveAsync(socket, CancellationToken.None);
                    if (text is null) break;
                    if (PluginProtocol.Parse(text) is { } message) Handle(runner, message);
                }
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // connexion coupée : le plugin se reconnectera ou sera relancé
            }
            finally
            {
                if (runner is not null && runner.Socket == socket)
                {
                    CloseSocket(runner);
                    if (runner.Info.Status == PluginStatus.Running) runner.Info.Status = PluginStatus.Starting;
                    StateChanged?.Invoke();
                }
                socket?.Dispose();
            }
        }
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), token); // octet par octet : rien n'est lu au-delà des en-têtes
            if (read == 0) break;
            length += read;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n') break;
        }
        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    /// <summary>Message texte complet ; null à la fermeture. Un message trop gros ferme la connexion.</summary>
    private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > PluginProtocol.MaxMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None);
                return null;
            }
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private static Task SendAsync(Runner runner, string json) => SendRawAsync(runner.Socket, runner.SendLock, json);

    private static async Task SendRawAsync(WebSocket? target, SemaphoreSlim sendLock, string json)
    {
        if (target is not { State: WebSocketState.Open } socket) return;
        await sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            // connexion perdue : la boucle de réception s'en charge
        }
        finally
        {
            sendLock.Release();
        }
    }

    // ---------- Tuiles ----------

    private IEnumerable<ShortcutItem> TilesOf(string pluginId)
        => _tiles().Where(t => t.Kind == TileKind.Plugin && string.Equals(t.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) && t.PluginAction is not null);

    /// <summary>
    /// Annonce aux plugins connectés les tuiles apparues (<c>willAppear</c>), retirées (<c>willDisappear</c>) ou dont les
    /// réglages ont changé (<c>didReceiveSettings</c>). Appelé à la connexion et à chaque changement de tuiles.
    /// </summary>
    public void SyncTiles()
    {
        foreach (var runner in _runners.Values)
        {
            if (runner.Socket is not { State: WebSocketState.Open }) continue;
            var current = TilesOf(runner.Info.Id).GroupBy(t => t.EnsurePluginContext()).ToDictionary(g => g.Key, g => g.First());

            foreach (var (context, (action, settings)) in runner.Appeared.ToList())
                if (!current.TryGetValue(context, out var tile) || !string.Equals(tile.PluginAction, action, StringComparison.OrdinalIgnoreCase))
                {
                    runner.Appeared.Remove(context);
                    _ = SendAsync(runner, PluginProtocol.BuildTileEvent(PluginProtocol.WillDisappear, action, context, JsonNode.Parse(settings) as JsonObject,
                        PluginProtocol.KeyPayload(null, 0)));
                }

            foreach (var (context, tile) in current)
            {
                var settings = tile.PluginSettings?.ToJsonString() ?? "{}";
                if (!runner.Appeared.TryGetValue(context, out var known))
                    _ = SendAsync(runner, PluginProtocol.BuildTileEvent(PluginProtocol.WillAppear, tile.PluginAction!, context, tile.PluginSettings, Extra(tile)));
                else if (known.Settings != settings)
                    _ = SendAsync(runner, PluginProtocol.BuildTileEvent(PluginProtocol.DidReceiveSettings, tile.PluginAction!, context, tile.PluginSettings, Extra(tile)));
                runner.Appeared[context] = (tile.PluginAction!, settings);
            }
        }
    }

    /// <summary>Appui sur une tuile de plugin : <c>keyDown</c> puis <c>keyUp</c>. Faux si le plugin n'est pas connecté.</summary>
    public bool Press(ShortcutItem tile)
    {
        if (tile.PluginId is null || tile.PluginAction is null || !_runners.TryGetValue(tile.PluginId, out var runner)
            || runner.Socket is not { State: WebSocketState.Open })
            return false;
        SyncTiles(); // tuile toute neuve : willAppear avant son premier appui
        var context = tile.EnsurePluginContext();
        _ = SendAsync(runner, PluginProtocol.BuildTileEvent(PluginProtocol.KeyDown, tile.PluginAction, context, tile.PluginSettings, Extra(tile)));
        _ = SendAsync(runner, PluginProtocol.BuildTileEvent(PluginProtocol.KeyUp, tile.PluginAction, context, tile.PluginSettings, Extra(tile)));
        // Action à deux états : comme le Stream Deck, l'état bascule seul après l'appui.
        if (runner.Info.Manifest?.FindAction(tile.PluginAction) is { AutoStates: true })
        {
            var state = StateFor(tile, runner.Info.Id);
            state.State = state.State == 0 ? 1 : 0;
            QueueTilesChanged();
        }
        return true;
    }

    /// <summary>Position de la tuile dans la grille et état courant (payload des évènements de tuile).</summary>
    private JsonObject Extra(ShortcutItem tile) => PluginProtocol.KeyPayload(
        tile.GridCol is { } col && tile.GridRow is { } row ? (col, row) : null, StateOf(tile));

    /// <summary>État de la tuile (0 ou 1 : <c>setState</c>, ou bascule automatique d'une action à deux états).</summary>
    public int StateOf(ShortcutItem tile) => State(tile)?.State ?? 0;

    private TileState StateFor(ShortcutItem tile, string pluginId)
        => _tileStates.TryGetValue(tile.EnsurePluginContext(), out var s) ? s : _tileStates[tile.PluginContext!] = new TileState(pluginId);

    /// <summary>Titre imposé par le plugin (null = nom de la tuile).</summary>
    public string? TitleOf(ShortcutItem tile) => State(tile)?.Title;

    /// <summary>Image imposée par le plugin (null = icône de la tuile).</summary>
    public ImageSource? ImageOf(ShortcutItem tile) => State(tile)?.Image;

    /// <summary>Indicateur bref après <c>showOk</c> (vrai) ou <c>showAlert</c> (faux) ; null sinon.</summary>
    public bool? FlashOf(ShortcutItem tile) => State(tile) is { FlashUntil: var until, FlashOk: var ok } && until > DateTime.UtcNow ? ok : null;

    /// <summary>Vrai si le plugin a mis la tuile dans son second état (<c>setState</c> 1).</summary>
    public bool IsActive(ShortcutItem tile) => State(tile) is { State: > 0 };

    private TileState? State(ShortcutItem tile)
        => tile.Kind == TileKind.Plugin && tile.PluginContext is { } context && _tileStates.TryGetValue(context, out var state) ? state : null;

    private void QueueTilesChanged()
    {
        if (_disposed) return;
        _tilesTimer.Stop();
        _tilesTimer.Start();
    }

    // ---------- Messages des plugins ----------

    private void Handle(Runner runner, PluginMessage message)
    {
        var id = runner.Info.Id;
        switch (message.Event)
        {
            case PluginProtocol.LogMessage:
                PluginLog.Write(id, Text(message.Payload, "message") ?? "");
                return;
            case PluginProtocol.OpenUrl:
                if (PluginProtocol.SafeUrl(Text(message.Payload, "url")) is { } url)
                {
                    try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { PluginLog.Write(id, $"openUrl impossible : {ex.Message}"); }
                }
                else PluginLog.Write(id, "openUrl refusé : seules les adresses http et https sont permises.");
                return;
            case PluginProtocol.GetGlobalSettings:
                _ = SendAsync(runner, PluginProtocol.BuildGlobalSettings(GlobalSettings(id)));
                return;
            case PluginProtocol.SetGlobalSettings:
                _config.Global[id] = (JsonObject)(message.Payload?.DeepClone() ?? new JsonObject());
                _saveConfig();
                foreach (var inspector in _inspectors.Values.Where(i => i.Runner == runner))
                    _ = SendInspectorAsync(inspector, PluginProtocol.BuildGlobalSettings(GlobalSettings(id)));
                return;
            case "sendToPropertyInspector":
                if (message.Context is { } target && _inspectors.TryGetValue(target, out var open) && open.Runner == runner)
                    _ = SendInspectorAsync(open, PluginProtocol.BuildContextEvent("sendToPropertyInspector", open.Tile.PluginAction ?? "", target, message.Payload));
                return;
        }

        // Les autres messages visent une tuile : uniquement une tuile de ce plugin.
        var tile = message.Context is null ? null : TilesOf(id).FirstOrDefault(t => t.PluginContext == message.Context);
        if (tile is null)
        {
            PluginLog.Write(id, $"{message.Event} ignoré : tuile inconnue ({message.Context ?? "sans context"}).");
            return;
        }
        var state = StateFor(tile, id);

        switch (message.Event)
        {
            case PluginProtocol.SetTitle:
                state.Title = Text(message.Payload, "title") is { Length: > 0 } title ? title[..Math.Min(title.Length, 60)] : null;
                QueueTilesChanged();
                break;
            case PluginProtocol.SetImage:
                state.Image = DecodeImage(Text(message.Payload, "image"));
                QueueTilesChanged();
                break;
            case PluginProtocol.SetState:
                // Comme le Stream Deck : 0 = état normal, 1 = second état (tuile active, deuxième icône).
                state.State = message.Payload?["state"] is JsonValue v && v.TryGetValue(out int n) ? n : 0;
                QueueTilesChanged();
                break;
            case PluginProtocol.ShowOk:
            case PluginProtocol.ShowAlert:
                state.FlashOk = message.Event == PluginProtocol.ShowOk;
                state.FlashUntil = DateTime.UtcNow + FlashDuration;
                QueueTilesChanged();
                _ = ClearFlashLaterAsync();
                break;
            case PluginProtocol.SetSettings:
                tile.PluginSettings = (JsonObject)(message.Payload?.DeepClone() ?? new JsonObject());
                runner.Appeared[tile.PluginContext!] = (tile.PluginAction!, tile.PluginSettings.ToJsonString());
                _saveTiles();
                ForwardToInspector(tile.PluginContext!, PluginProtocol.BuildTileEvent(PluginProtocol.DidReceiveSettings, tile.PluginAction!, tile.PluginContext!, tile.PluginSettings, Extra(tile)));
                break;
            case PluginProtocol.GetSettings:
                _ = SendAsync(runner, PluginProtocol.BuildTileEvent(PluginProtocol.DidReceiveSettings, tile.PluginAction!, tile.PluginContext!, tile.PluginSettings, Extra(tile)));
                break;
            default:
                // Messages du Stream Deck sans équivalent ici (molettes, profils…) : signalés une fois dans le journal.
                if (_ignoredEvents.Add($"{id}|{message.Event}")) PluginLog.Write(id, $"Évènement non pris en charge, ignoré : {message.Event}");
                break;
        }
    }

    private async Task ClearFlashLaterAsync()
    {
        await Task.Delay(FlashDuration + TimeSpan.FromMilliseconds(50));
        QueueTilesChanged();
    }

    private JsonObject GlobalSettings(string id) => _config.Global.TryGetValue(id, out var settings) ? settings : new JsonObject();

    private static ImageSource? DecodeImage(string? dataUri)
    {
        // SVG (très courant chez les plugins Stream Deck) : dessiné par SharpVectors.
        if (PluginProtocol.DecodeSvg(dataUri) is { } svg) return SvgRasterizer.FromText(svg);
        if (PluginProtocol.DecodeImage(dataUri) is not { } bytes) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject? obj, string name)
        => obj?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tilesTimer.Stop();
        CloseInspector();
        foreach (var runner in _runners.Values) Stop(runner, wait: false);
        try { _listener?.Stop(); } catch (SocketException) { }
        _job.Dispose(); // tue ce qui resterait
    }

    /// <summary>Programme d'un plugin et sa connexion.</summary>
    private sealed class Runner
    {
        public Runner(PluginInfo info) => Info = info;
        public PluginInfo Info { get; }
        public Process? Process;
        public string? Token;
        public WebSocket? Socket;
        public bool Stopping;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly List<DateTime> Crashes = new();
        /// <summary>Tuiles annoncées au plugin : contexte → (action, réglages JSON).</summary>
        public readonly Dictionary<string, (string Action, string Settings)> Appeared = new();
    }

    /// <summary>Ce qu'un plugin affiche sur une de ses tuiles (non enregistré : remis à zéro à son arrêt).</summary>
    private sealed class TileState
    {
        public TileState(string pluginId) => PluginId = pluginId;
        public string PluginId { get; }
        public string? Title;
        public ImageSource? Image;
        public bool FlashOk;
        public DateTime FlashUntil;
        public int State;
    }
}
