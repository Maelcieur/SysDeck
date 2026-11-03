using System.IO;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using SysDeck.Infrastructure;
using SysDeck.Models;

namespace SysDeck.Plugins;

/// <summary>Ce qu'il faut à la page de réglages Stream Deck pour se connecter (appel de <c>connectElgatoStreamDeckSocket</c>).</summary>
public sealed record InspectorLaunch(string Page, int Port, string Context, string Info, string ActionInfo);

// Pages de réglages des plugins Stream Deck (« Property Inspector ») : une page web affichée par les Paramètres, reliée au
// même WebSocket ; ses réglages vont à la tuile et au plugin, et le plugin peut lui écrire (sendToPropertyInspector).
public sealed partial class PluginHost
{
    public const string RegisterPropertyInspector = "registerPropertyInspector";

    /// <summary>Page de réglages ouverte, par contexte de tuile (une seule à la fois).</summary>
    private readonly Dictionary<string, Inspector> _inspectors = new();

    /// <summary>
    /// Prépare l'affichage de la page de réglages d'une tuile de plugin Stream Deck : null si l'action n'en a pas ou si
    /// le plugin ne tourne pas. Prévient le plugin (<c>propertyInspectorDidAppear</c>) ; toute autre page est fermée.
    /// </summary>
    public InspectorLaunch? OpenInspector(ShortcutItem tile)
    {
        if (tile.PluginId is null || !_runners.TryGetValue(tile.PluginId, out var runner) || runner.Socket is not { State: WebSocketState.Open }
            || runner.Info.Manifest is not { } manifest || manifest.FindAction(tile.PluginAction) is not { PropertyInspectorPath: { } relative } action)
            return null;
        if (PluginManifest.InsideFolder(manifest.Folder, relative) is not { } page || !File.Exists(page)) return null;

        CloseInspector();
        var context = tile.EnsurePluginContext();
        _inspectors[context] = new Inspector(runner, tile);
        _ = SendAsync(runner, PluginProtocol.BuildContextEvent("propertyInspectorDidAppear", action.Id, context, null));

        var (columns, rows) = _grid();
        var info = PluginProtocol.BuildInfo(StreamDeckVersion, manifest.Id, manifest.Version, columns, rows);
        var actionInfo = new JsonObject
        {
            ["action"] = action.Id,
            ["context"] = context,
            ["device"] = PluginProtocol.DeviceId,
            ["payload"] = Extra(tile).Also(p => p["settings"] = tile.PluginSettings?.DeepClone() ?? new JsonObject())
        }.ToJsonString();
        return new InspectorLaunch(page, _port, context, info, actionInfo);
    }

    /// <summary>Ferme la page de réglages ouverte (tuile désélectionnée, fenêtre fermée) et prévient le plugin.</summary>
    public void CloseInspector()
    {
        foreach (var (context, inspector) in _inspectors.ToList())
        {
            _inspectors.Remove(context);
            _ = SendAsync(inspector.Runner, PluginProtocol.BuildContextEvent("propertyInspectorDidDisappear", inspector.Tile.PluginAction ?? "", context, null));
            if (inspector.Socket is { } socket)
            {
                try { socket.Abort(); } catch { /* déjà fermée */ }
            }
        }
    }

    /// <summary>Page de réglages connectée (après <c>registerPropertyInspector</c> avec le contexte d'une page ouverte).</summary>
    private async Task ServeInspectorAsync(Inspector inspector, WebSocket socket)
    {
        inspector.Socket = socket;
        try
        {
            while (socket.State == WebSocketState.Open && !_disposed)
            {
                var text = await ReceiveAsync(socket, CancellationToken.None);
                if (text is null) break;
                if (PluginProtocol.Parse(text) is { } message) HandleInspector(inspector, message);
            }
        }
        finally
        {
            if (inspector.Socket == socket) inspector.Socket = null;
        }
    }

    private Inspector? InspectorFor(string? uuid)
        => uuid is not null && _inspectors.TryGetValue(uuid, out var inspector) && inspector.Socket is null ? inspector : null;

    private void HandleInspector(Inspector inspector, PluginMessage message)
    {
        var (runner, tile) = (inspector.Runner, inspector.Tile);
        var id = runner.Info.Id;
        var context = tile.EnsurePluginContext();
        switch (message.Event)
        {
            case PluginProtocol.SetSettings:
                // Réglages saisis dans la page : enregistrés dans la tuile, puis annoncés au plugin.
                tile.PluginSettings = (JsonObject)(message.Payload?.DeepClone() ?? new JsonObject());
                runner.Appeared[context] = (tile.PluginAction!, tile.PluginSettings.ToJsonString());
                _saveTiles();
                _ = SendAsync(runner, PluginProtocol.BuildTileEvent(PluginProtocol.DidReceiveSettings, tile.PluginAction!, context, tile.PluginSettings, Extra(tile)));
                break;
            case PluginProtocol.GetSettings:
                _ = SendInspectorAsync(inspector, PluginProtocol.BuildTileEvent(PluginProtocol.DidReceiveSettings, tile.PluginAction!, context, tile.PluginSettings, Extra(tile)));
                break;
            case "sendToPlugin":
                _ = SendAsync(runner, PluginProtocol.BuildContextEvent("sendToPlugin", tile.PluginAction!, context, message.Payload));
                break;
            case PluginProtocol.SetGlobalSettings:
                _config.Global[id] = (JsonObject)(message.Payload?.DeepClone() ?? new JsonObject());
                _saveConfig();
                _ = SendAsync(runner, PluginProtocol.BuildGlobalSettings(GlobalSettings(id)));
                break;
            case PluginProtocol.GetGlobalSettings:
                _ = SendInspectorAsync(inspector, PluginProtocol.BuildGlobalSettings(GlobalSettings(id)));
                break;
            case PluginProtocol.OpenUrl:
            case PluginProtocol.LogMessage:
                Handle(runner, message);
                break;
            default:
                if (_ignoredEvents.Add($"{id}|pi|{message.Event}")) PluginLog.Write(id, $"Page de réglages : évènement non pris en charge, ignoré : {message.Event}");
                break;
        }
    }

    /// <summary>Message du plugin à la page de réglages ouverte de cette tuile (sans effet si elle est fermée).</summary>
    private void ForwardToInspector(string context, string json)
    {
        if (_inspectors.TryGetValue(context, out var inspector)) _ = SendInspectorAsync(inspector, json);
    }

    private static Task SendInspectorAsync(Inspector inspector, string json) => SendRawAsync(inspector.Socket, inspector.SendLock, json);

    private sealed class Inspector
    {
        public Inspector(Runner runner, ShortcutItem tile)
        {
            Runner = runner;
            Tile = tile;
        }

        public Runner Runner { get; }
        public ShortcutItem Tile { get; }
        public WebSocket? Socket;
        public readonly SemaphoreSlim SendLock = new(1, 1);
    }
}

internal static class JsonObjectExtensions
{
    /// <summary>Modifie l'objet puis le renvoie (construction en une expression).</summary>
    public static JsonObject Also(this JsonObject obj, Action<JsonObject> change)
    {
        change(obj);
        return obj;
    }
}
