using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SysDeck.Plugins;

/// <summary>Type d'un réglage d'action : le formulaire de la tuile est généré d'après lui.</summary>
public enum PluginSettingType { Text, TextArea, Number, Checkbox, Select, Color, File }

/// <summary>Choix d'un réglage <see cref="PluginSettingType.Select"/>.</summary>
public sealed record PluginOption(string Value, string Label);

/// <summary>Réglage d'une action, décrit dans le manifest (clé enregistrée dans les réglages de la tuile).</summary>
public sealed record PluginSetting(string Key, PluginSettingType Type, string Label, JsonNode? Default, IReadOnlyList<PluginOption> Options,
    string? Placeholder, string? Description);

/// <summary>État d'une action Stream Deck (0 = normal, 1 = second état) : image et titre par défaut.</summary>
public sealed record PluginActionState(string? ImagePath, string? Title);

/// <summary>
/// Action proposée par un plugin : un type de tuile. <see cref="IconPath"/> est l'image de la tuile à l'état 0 ;
/// pour un plugin Stream Deck, <see cref="States"/> donne celle de chaque état et <see cref="PropertyInspectorPath"/> sa page de réglages.
/// </summary>
public sealed record PluginAction(string Id, string Name, string? Tooltip, string? IconPath, IReadOnlyList<PluginSetting> Settings)
{
    public IReadOnlyList<PluginActionState> States { get; init; } = Array.Empty<PluginActionState>();

    /// <summary>Le Stream Deck alterne seul entre les deux états à chaque appui (sauf « DisableAutomaticStates »).</summary>
    public bool AutoStates { get; init; }

    public string? PropertyInspectorPath { get; init; }

    /// <summary>Image de la tuile pour un état (celle de l'état 0 à défaut).</summary>
    public string? ImageFor(int state) => state > 0 && state < States.Count && States[state].ImagePath is { } image ? image : IconPath;
}

/// <summary>
/// Manifest d'un plugin (<c>manifest.json</c> à la racine de son dossier). Format proche de celui du Stream Deck
/// (Id inversé, CodePath, Actions) avec des réglages décrits en JSON plutôt qu'une page HTML.
/// </summary>
public sealed class PluginManifest
{
    public const int CurrentSdkVersion = 1;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public string? Url { get; init; }
    public required string Folder { get; init; }
    /// <summary>Chemin complet du programme du plugin (dans son dossier).</summary>
    public required string CodePath { get; init; }
    public string? IconPath { get; init; }
    public required IReadOnlyList<PluginAction> Actions { get; init; }

    /// <summary>Plugin écrit pour le logiciel Stream Deck d'Elgato (manifest « UUID », « CodePathWin », « States »…).</summary>
    public bool IsElgato { get; init; }

    /// <summary>Version majeure de Node.js demandée par un plugin Stream Deck en JavaScript (« Nodejs.Version »), sinon null.</summary>
    public int? NodeMajor { get; init; }

    /// <summary>Actions masquées : celles qui ne fonctionnent qu'avec les molettes du Stream Deck + (pas de touche).</summary>
    public int SkippedActions { get; init; }

    public PluginAction? FindAction(string? id) => Actions.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    private static readonly Regex IdPattern = new(@"^[a-z0-9_]+(?:[.-][a-z0-9_]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] CodeExtensions = { ".exe", ".js", ".mjs", ".cjs", ".py", ".ps1" };

    /// <summary>Lit <c>manifest.json</c> d'un dossier. Lève <see cref="InvalidDataException"/> (message lisible) s'il est absent ou invalide.</summary>
    public static PluginManifest Load(string folder)
    {
        var path = Path.Combine(folder, "manifest.json");
        if (!File.Exists(path)) throw new InvalidDataException("manifest.json introuvable : ce n'est pas un plugin SysDeck.");
        var bytes = File.ReadAllBytes(path);
        if (IsEncryptedElgato(bytes)) throw new InvalidDataException(ElgatoEncrypted);
        return Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿'), folder);
    }

    internal const string ElgatoEncrypted =
        "C'est un plugin du Marketplace d'Elgato : son contenu est chiffré par Elgato et ne peut fonctionner qu'avec le " +
        "logiciel Stream Deck. Seuls les plugins SysDeck (.sysdeckplugin) s'installent ici.";

    internal const string ElgatoHtml =
        "Ce plugin Stream Deck est une page web (ancienne génération de plugins) : SysDeck ne sait pas encore le faire tourner.";

    /// <summary>Manifest chiffré des plugins du Marketplace Elgato (en-tête « ELGATO » au lieu de JSON).</summary>
    internal static bool IsEncryptedElgato(ReadOnlySpan<byte> bytes) => bytes.StartsWith("ELGATO"u8);

    /// <summary>Analyse et valide un manifest (partie testée) : identifiants, programme dans le dossier, actions et réglages.</summary>
    public static PluginManifest Parse(string json, string folder)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new InvalidDataException("manifest.json doit contenir un objet JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"manifest.json illisible : {ex.Message}");
        }

        if (IsElgatoFormat(root)) return ParseElgato(root, folder);

        var id = Required(root, "Id");
        if (!IdPattern.IsMatch(id) || id.Length > 100)
            throw new InvalidDataException($"Identifiant de plugin invalide « {id} » : attendu de la forme com.auteur.nom.");

        var sdk = root["SdkVersion"] is JsonValue v && v.TryGetValue(out int n) ? n : 1;
        if (sdk > CurrentSdkVersion)
            throw new InvalidDataException($"Ce plugin demande une version plus récente de SysDeck (SDK {sdk}).");

        var code = Required(root, "CodePath");
        var codePath = InsideFolder(folder, code)
            ?? throw new InvalidDataException("CodePath doit désigner un fichier du dossier du plugin.");
        if (!CodeExtensions.Contains(Path.GetExtension(codePath), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Programme non pris en charge « {code} » (attendu : {string.Join(", ", CodeExtensions)}).");

        if (root["Actions"] is not JsonArray actionsNode || actionsNode.Count == 0)
            throw new InvalidDataException("Le manifest ne déclare aucune action (Actions).");

        var actions = new List<PluginAction>();
        foreach (var node in actionsNode)
        {
            if (node is not JsonObject a) throw new InvalidDataException("Chaque action doit être un objet JSON.");
            var actionId = Required(a, "Id");
            if (!IdPattern.IsMatch(actionId)) throw new InvalidDataException($"Identifiant d'action invalide « {actionId} ».");
            if (actions.Any(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Action déclarée deux fois : « {actionId} ».");
            actions.Add(new PluginAction(actionId, Required(a, "Name"), Optional(a, "Tooltip"), Image(folder, Optional(a, "Icon")), ParseSettings(a)));
        }

        return new PluginManifest
        {
            Id = id,
            Name = Required(root, "Name"),
            Version = Optional(root, "Version") ?? "1.0",
            Author = Optional(root, "Author"),
            Description = Optional(root, "Description"),
            Url = Optional(root, "URL") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url : null,
            Folder = Path.GetFullPath(folder),
            CodePath = codePath,
            IconPath = Image(folder, Optional(root, "Icon")),
            Actions = actions
        };
    }

    // ---------- Plugins Stream Deck (Elgato) ----------

    /// <summary>Manifest du Stream Deck : actions identifiées par « UUID » (les nôtres par « Id »).</summary>
    private static bool IsElgatoFormat(JsonObject root)
        => root["Actions"] is JsonArray { Count: > 0 } actions && actions[0] is JsonObject first && first["UUID"] is not null && first["Id"] is null;

    /// <summary>
    /// Manifest d'un plugin Stream Deck : identifiant « UUID » (ou nom du dossier sans « .sdPlugin »), programme Windows
    /// (« CodePathWin », sinon « CodePath »), actions avec leurs états et leur page de réglages. Les actions réservées aux
    /// molettes sont écartées ; les images sont citées sans extension (variantes « @2x », SVG converti en PNG).
    /// </summary>
    private static PluginManifest ParseElgato(JsonObject root, string folder)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        var id = Optional(root, "UUID")
                 ?? (folderName.EndsWith(".sdPlugin", StringComparison.OrdinalIgnoreCase) ? folderName[..^".sdPlugin".Length] : folderName);
        if (!IdPattern.IsMatch(id) || id.Length > 100)
            throw new InvalidDataException($"Identifiant de plugin invalide « {id} ».");

        if (root["OS"] is JsonArray os && os.Count > 0
            && !os.OfType<JsonObject>().Any(o => string.Equals(Optional(o, "Platform"), "windows", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Ce plugin Stream Deck n'existe que pour macOS.");

        var code = Optional(root, "CodePathWin") ?? Required(root, "CodePath");
        var codePath = InsideFolder(folder, code) ?? throw new InvalidDataException("CodePath doit désigner un fichier du dossier du plugin.");
        var extension = Path.GetExtension(codePath).ToLowerInvariant();
        if (extension is ".html" or ".htm") throw new InvalidDataException(ElgatoHtml);
        if (!CodeExtensions.Contains(extension)) throw new InvalidDataException($"Programme non pris en charge « {code} ».");
        if (!File.Exists(codePath)) throw new InvalidDataException($"Programme du plugin introuvable : « {code} ».");

        var pluginInspector = Optional(root, "PropertyInspectorPath");
        var actions = new List<PluginAction>();
        var skipped = 0;
        foreach (var a in ((JsonArray)root["Actions"]!).OfType<JsonObject>())
        {
            var actionId = Required(a, "UUID");
            // Molettes seules (Stream Deck +) : rien à faire sur une touche.
            if (a["Controllers"] is JsonArray controllers && controllers.Count > 0
                && !controllers.Any(c => c is JsonValue v && v.TryGetValue(out string? s) && string.Equals(s, "Keypad", StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            if (actions.Any(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase))) continue;

            var states = a["States"] is JsonArray list
                ? list.OfType<JsonObject>().Select(s => new PluginActionState(ElgatoImage(folder, Optional(s, "Image")), Optional(s, "Title"))).ToList()
                : new List<PluginActionState>();
            var icon = states.FirstOrDefault()?.ImagePath ?? ElgatoImage(folder, Optional(a, "Icon"));
            var autoStates = states.Count == 2 && !(a["DisableAutomaticStates"] is JsonValue d && d.TryGetValue(out bool disabled) && disabled);
            actions.Add(new PluginAction(actionId, Optional(a, "Name") ?? actionId, Optional(a, "Tooltip"), icon, Array.Empty<PluginSetting>())
            {
                States = states,
                AutoStates = autoStates,
                PropertyInspectorPath = Optional(a, "PropertyInspectorPath") ?? pluginInspector
            });
        }
        if (actions.Count == 0)
            throw new InvalidDataException(skipped > 0
                ? "Ce plugin Stream Deck ne fonctionne qu'avec les molettes du Stream Deck + : aucune action de touche."
                : "Le manifest ne déclare aucune action (Actions).");

        int? node = root["Nodejs"] is JsonObject nodejs && Optional(nodejs, "Version") is { } nodeVersion
                    && int.TryParse(nodeVersion.Split('.')[0], out var major) ? major : null;
        if (node is null && extension is ".js" or ".mjs" or ".cjs") node = 20;

        return new PluginManifest
        {
            Id = id,
            Name = Required(root, "Name"),
            Version = Optional(root, "Version") ?? "1.0",
            Author = Optional(root, "Author"),
            Description = Optional(root, "Description"),
            Url = Optional(root, "URL") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url : null,
            Folder = Path.GetFullPath(folder),
            CodePath = codePath,
            IconPath = ElgatoImage(folder, Optional(root, "Icon")),
            Actions = actions,
            IsElgato = true,
            NodeMajor = node,
            SkippedActions = skipped
        };
    }

    private static readonly string[] ImageSuffixes = { "@2x.png", ".png", ".svg", "@2x.jpg", ".jpg", ".jpeg", ".gif" };

    /// <summary>
    /// Image citée par un manifest Stream Deck, souvent sans extension (« imgs/action ») : on essaie les variantes
    /// habituelles. Un SVG est remplacé par son PNG converti (<see cref="Services.SvgRasterizer"/>) s'il existe.
    /// </summary>
    internal static string? ElgatoImage(string folder, string? relative)
    {
        if (relative is null || InsideFolder(folder, relative) is not { } basePath) return null;
        var candidates = Path.HasExtension(basePath) && File.Exists(basePath)
            ? new[] { basePath }
            : ImageSuffixes.Select(s => basePath + s);
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return path;
            var png = Services.SvgRasterizer.PngPathOf(path);
            if (File.Exists(png)) return png;
        }
        return null;
    }

    private static IReadOnlyList<PluginSetting> ParseSettings(JsonObject action)
    {
        if (action["Settings"] is not JsonArray list) return Array.Empty<PluginSetting>();
        var settings = new List<PluginSetting>();
        foreach (var node in list)
        {
            if (node is not JsonObject s) continue;
            var key = Required(s, "Key");
            var typeText = Optional(s, "Type") ?? "text";
            if (!Enum.TryParse<PluginSettingType>(typeText, ignoreCase: true, out var type))
                throw new InvalidDataException($"Type de réglage inconnu « {typeText} » pour « {key} ».");
            var options = s["Options"] is JsonArray opts
                ? opts.OfType<JsonObject>().Select(o => new PluginOption(Optional(o, "Value") ?? "", Optional(o, "Label") ?? Optional(o, "Value") ?? "")).ToList()
                : new List<PluginOption>();
            if (type == PluginSettingType.Select && options.Count == 0)
                throw new InvalidDataException($"Le réglage « {key} » (select) n'a aucune option.");
            settings.Add(new PluginSetting(key, type, Optional(s, "Label") ?? key, s["Default"]?.DeepClone(), options,
                Optional(s, "Placeholder"), Optional(s, "Description")));
        }
        return settings;
    }

    /// <summary>Réglages d'une nouvelle tuile : valeur par défaut de chaque réglage déclaré.</summary>
    public static JsonObject DefaultSettings(PluginAction action)
    {
        var settings = new JsonObject();
        foreach (var s in action.Settings)
            if (s.Default is not null) settings[s.Key] = s.Default.DeepClone();
        return settings;
    }

    private static string Required(JsonObject obj, string name)
        => Optional(obj, name) ?? throw new InvalidDataException($"Champ obligatoire manquant dans le manifest : « {name} ».");

    private static string? Optional(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string? Image(string folder, string? relative)
        => relative is null ? null : InsideFolder(folder, relative) is { } p && File.Exists(p) ? p : null;

    /// <summary>Chemin complet d'un fichier du plugin ; null s'il sort du dossier (« .. », chemin absolu).</summary>
    internal static string? InsideFolder(string folder, string relative)
    {
        if (Path.IsPathRooted(relative)) return null;
        var path = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        return Services.SafeZip.IsInside(folder, path) ? path : null;
    }
}
