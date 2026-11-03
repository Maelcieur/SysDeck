using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SysDeck.Plugins;

/// <summary>Message reçu d'un plugin : évènement, tuile visée (context), contenu, et jeton pour <c>registerPlugin</c>.</summary>
public sealed record PluginMessage(string Event, string? Context, string? Action, JsonObject? Payload, string? Uuid);

/// <summary>
/// Protocole entre SysDeck et ses plugins (WebSocket local, JSON). Noms d'évènements et champs repris du Stream Deck
/// (<c>keyDown</c>, <c>willAppear</c>, <c>setTitle</c>, <c>context</c>…). Partie pure, testée.
/// </summary>
public static class PluginProtocol
{
    /// <summary>Taille maximale d'un message reçu (une image en base64 compte pour l'essentiel).</summary>
    public const int MaxMessageBytes = 2 * 1024 * 1024;

    /// <summary>Taille maximale d'une image décodée envoyée par <c>setImage</c>.</summary>
    public const int MaxImageBytes = 1024 * 1024;

    // Évènements envoyés aux plugins.
    public const string KeyDown = "keyDown", KeyUp = "keyUp", WillAppear = "willAppear", WillDisappear = "willDisappear",
        DidReceiveSettings = "didReceiveSettings", DidReceiveGlobalSettings = "didReceiveGlobalSettings";

    // Évènements reçus des plugins.
    public const string RegisterPlugin = "registerPlugin", SetTitle = "setTitle", SetImage = "setImage", ShowOk = "showOk",
        ShowAlert = "showAlert", SetSettings = "setSettings", GetSettings = "getSettings", SetGlobalSettings = "setGlobalSettings",
        GetGlobalSettings = "getGlobalSettings", OpenUrl = "openUrl", LogMessage = "logMessage", SetState = "setState";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>Évènement envoyé à un plugin pour une tuile : <c>{event, action, context, device, payload:{settings, …}}</c>.</summary>
    public static string BuildTileEvent(string eventName, string action, string context, JsonObject? settings, JsonObject? extra = null)
    {
        var payload = new JsonObject { ["settings"] = settings?.DeepClone() ?? new JsonObject() };
        if (extra is not null)
            foreach (var (key, value) in extra) payload[key] = value?.DeepClone();
        var message = new JsonObject
        {
            ["event"] = eventName,
            ["action"] = action,
            ["context"] = context,
            ["device"] = "sysdeck",
            ["payload"] = payload
        };
        return message.ToJsonString(Compact);
    }

    /// <summary>Évènement visant une tuile sans réglages joints : <c>{event, action, context, device, payload?}</c>.</summary>
    public static string BuildContextEvent(string eventName, string action, string context, JsonObject? payload)
    {
        var message = new JsonObject { ["event"] = eventName, ["action"] = action, ["context"] = context, ["device"] = DeviceId };
        if (payload is not null) message["payload"] = payload.DeepClone();
        return message.ToJsonString(Compact);
    }

    /// <summary>Réglages globaux du plugin (communs à toutes ses tuiles).</summary>
    public static string BuildGlobalSettings(JsonObject? settings)
        => new JsonObject
        {
            ["event"] = DidReceiveGlobalSettings,
            ["payload"] = new JsonObject { ["settings"] = settings?.DeepClone() ?? new JsonObject() }
        }.ToJsonString(Compact);

    /// <summary>Lit un message d'un plugin ; null s'il n'est pas un objet JSON avec un champ <c>event</c>.</summary>
    public static PluginMessage? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj || Text(obj, "event") is not { } name) return null;
            return new PluginMessage(name, Text(obj, "context"), Text(obj, "action"), obj["payload"] as JsonObject, Text(obj, "uuid"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Identifiant de « l'appareil » (la grille de la Switch) dans les messages, comme un Stream Deck.</summary>
    public const string DeviceId = "sysdeck";

    /// <summary>Argument <c>-info</c> passé au plugin : application, plateforme, langue, version du plugin et appareil (taille de la grille).</summary>
    public static string BuildInfo(string appVersion, string pluginUuid, string pluginVersion, int columns = 5, int rows = 3)
        => new JsonObject
        {
            ["application"] = new JsonObject
            {
                ["font"] = "Segoe UI", ["language"] = "fr", ["platform"] = "windows", ["platformVersion"] = Environment.OSVersion.Version.ToString(),
                ["version"] = appVersion, ["name"] = "SysDeck"
            },
            ["plugin"] = new JsonObject { ["uuid"] = pluginUuid, ["version"] = pluginVersion },
            ["devicePixelRatio"] = 1,
            ["colors"] = new JsonObject
            {
                ["buttonPressedBackgroundColor"] = "#303030FF", ["buttonPressedBorderColor"] = "#646464FF", ["buttonPressedTextColor"] = "#969696FF",
                ["disabledColor"] = "#F7821B59", ["highlightColor"] = "#F7821BFF", ["mouseDownColor"] = "#CF6304FF"
            },
            ["devices"] = new JsonArray(Device(columns, rows))
        }.ToJsonString(Compact);

    private static JsonObject Device(int columns, int rows)
        => new() { ["id"] = DeviceId, ["name"] = "SysDeck (Switch)", ["type"] = 0, ["size"] = new JsonObject { ["columns"] = columns, ["rows"] = rows } };

    /// <summary><c>deviceDidConnect</c> : envoyé juste après l'enregistrement, avant les tuiles.</summary>
    public static string BuildDeviceDidConnect(int columns, int rows)
    {
        var device = Device(columns, rows);
        return new JsonObject
        {
            ["event"] = "deviceDidConnect",
            ["device"] = DeviceId,
            ["deviceInfo"] = new JsonObject { ["name"] = device["name"]!.DeepClone(), ["type"] = 0, ["size"] = device["size"]!.DeepClone() }
        }.ToJsonString(Compact);
    }

    /// <summary>Champs d'une tuile dans le payload (comme le Stream Deck) : position, état, contrôleur, multi-action.</summary>
    public static JsonObject KeyPayload((int Column, int Row)? coordinates, int state)
    {
        var payload = new JsonObject { ["state"] = state, ["isInMultiAction"] = false, ["controller"] = "Keypad" };
        if (coordinates is { } c) payload["coordinates"] = new JsonObject { ["column"] = c.Column, ["row"] = c.Row };
        return payload;
    }

    /// <summary>
    /// Texte d'une image SVG envoyée par <c>setImage</c> : <c>data:image/svg+xml;base64,…</c> ou
    /// <c>data:image/svg+xml;charset=utf8,&lt;svg…</c> (encodé ou non). Null pour tout autre format.
    /// </summary>
    public static string? DecodeSvg(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri) || !dataUri.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = dataUri.IndexOf(',');
        if (comma < 0 || dataUri.Length - comma > MaxImageBytes * 2) return null;
        var header = dataUri[..comma];
        var data = dataUri[(comma + 1)..];
        try
        {
            var text = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(data))
                : Uri.UnescapeDataString(data);
            return text.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? text : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Arguments de lancement, dans l'ordre du Stream Deck : <c>-port -pluginUUID -registerEvent -info</c>.</summary>
    public static IReadOnlyList<string> BuildArguments(int port, string token, string info)
        => new[] { "-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-pluginUUID", token, "-registerEvent", RegisterPlugin, "-info", info };

    /// <summary>Jeton secret d'une instance de plugin : seul le programme lancé par SysDeck le connaît.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Compare deux jetons en temps constant.</summary>
    public static bool TokenMatches(string? expected, string? given)
        => expected is not null && given is not null
           && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

    /// <summary>
    /// Octets d'une image <c>setImage</c> : data URI PNG, JPEG, GIF ou BMP en base64. Null si vide, autre format
    /// (SVG compris) ou trop grosse.
    /// </summary>
    public static byte[]? DecodeImage(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri) || !dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = dataUri.IndexOf(',');
        if (comma < 0) return null;
        var header = dataUri[..comma].ToLowerInvariant();
        if (!header.EndsWith(";base64")) return null;
        var type = header["data:image/".Length..^";base64".Length];
        if (type is not ("png" or "jpeg" or "jpg" or "gif" or "bmp")) return null;
        if ((dataUri.Length - comma) * 3L / 4 > MaxImageBytes) return null;
        try
        {
            return Convert.FromBase64String(dataUri[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Adresse ouverte par <c>openUrl</c> : http ou https seulement (jamais un fichier ni un programme).</summary>
    public static Uri? SafeUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    /// <summary>Réponse à la poignée de main WebSocket (RFC 6455) pour la clé du client.</summary>
    public static string HandshakeAccept(string key)
        => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

    /// <summary>
    /// Analyse la requête HTTP d'ouverture : renvoie la clé WebSocket, ou null si ce n'est pas une ouverture valide.
    /// Une requête venue d'une page web en ligne (en-tête Origin http/https) est refusée ; les pages de réglages locales
    /// des plugins (Origin « file:// » ou « null ») passent, elles doivent ensuite donner le contexte d'une page ouverte.
    /// </summary>
    public static string? ParseHandshake(string request)
    {
        var lines = request.Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal)) return null;
        string? key = null;
        var upgrade = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase) && value.Length > 0 && value != "null"
                && !value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) && value.Equals("websocket", StringComparison.OrdinalIgnoreCase)) upgrade = true;
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
        }
        return upgrade && !string.IsNullOrEmpty(key) ? key : null;
    }

    private static string? Text(JsonObject obj, string name)
        => obj[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
