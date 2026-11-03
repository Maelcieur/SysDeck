using System.IO;
using System.Text;
using System.Text.Json;
using SysDeck.Controls;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>Icône d'un pack : chemin complet de l'image, nom affiché et mots-clés de recherche.</summary>
public sealed record PackIcon(string Path, string Name, IReadOnlyList<string> Tags);

/// <summary>Pack d'icônes installé (dossier <c>IconPacks\&lt;Id&gt;</c>).</summary>
public sealed class IconPack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Author { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public required string Folder { get; init; }
    /// <summary>Image du pack (manifest « Icon »), null si absente.</summary>
    public string? IconPath { get; init; }
    public required IReadOnlyList<PackIcon> Icons { get; init; }
    /// <summary>Images SVG qui n'ont pas pu être converties en PNG (ignorées).</summary>
    public int SkippedSvg { get; init; }
}

/// <summary>
/// Packs d'icônes au format Elgato Stream Deck (<c>.streamDeckIconPack</c> = zip avec <c>manifest.json</c>,
/// <c>icons.json</c> et un dossier <c>icons\</c>) ou simple dossier d'images. Une icône choisie dans un pack est
/// ensuite recopiée comme une image importée : la tuile ne dépend pas du pack.
/// </summary>
public static class IconPackStore
{
    /// <summary>Taille décompressée maximale d'un pack.</summary>
    internal const long MaxBytes = 300L * 1024 * 1024;
    internal const int MaxFiles = 20_000;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico" };
    private static List<IconPack>? _cache;

    public static string Dir => AppPaths.Folder("IconPacks");

    /// <summary>Packs installés, lus une fois puis gardés en mémoire.</summary>
    public static IReadOnlyList<IconPack> All
    {
        get
        {
            if (_cache is not null) return _cache;
            var packs = new List<IconPack>();
            if (Directory.Exists(Dir))
                foreach (var folder in Directory.EnumerateDirectories(Dir))
                {
                    var id = Path.GetFileName(folder);
                    if (id.StartsWith('.')) continue; // import en cours ou abandonné
                    try
                    {
                        if (Read(folder, id) is { } pack) packs.Add(pack);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Pack d'icônes illisible ({id}) : {ex.Message}");
                    }
                }
            return _cache = packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Installe un pack depuis un fichier zip (.streamDeckIconPack) ou un dossier ; un pack du même nom est remplacé.
    /// Lève <see cref="InvalidDataException"/> avec un message lisible si ce n'est pas un pack valide.
    /// </summary>
    public static IconPack Import(string source)
    {
        Directory.CreateDirectory(Dir);
        var staging = Path.Combine(Dir, $".import-{Guid.NewGuid():N}");
        try
        {
            if (Directory.Exists(source)) SafeZip.CopyFolder(source, staging, MaxBytes, MaxFiles);
            else if (File.Exists(source)) ExtractSafely(source, staging);
            else throw new InvalidDataException("Fichier introuvable.");

            var root = FindRoot(staging);
            SvgRasterizer.ConvertFolder(root); // SVG (fréquents dans les packs du Stream Deck) convertis en PNG
            var probe = Read(root, "probe") ?? throw new InvalidDataException("Aucune image utilisable dans ce pack.");
            var id = MakeId(probe.Name);
            var dest = Path.Combine(Dir, id);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(root, dest);

            _cache = null;
            return All.First(p => p.Id == id);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Impossible d'installer le pack : {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* reliquat sans conséquence, ignoré à la lecture (nom en « . ») */ }
        }
    }

    /// <summary>Supprime un pack installé (les tuiles gardent leur copie de l'icône).</summary>
    public static void Delete(string id)
    {
        var dest = Path.Combine(Dir, id);
        if (!IsInside(Dir, dest)) return;
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        _cache = null;
    }

    /// <summary>Icônes des packs dont le nom, les mots-clés ou le nom du pack contiennent la recherche (sans casse ni accents).</summary>
    public static IEnumerable<(IconPack Pack, PackIcon Icon)> Search(string? text, IEnumerable<IconPack>? packs = null)
    {
        var needle = BrandIcons.Fold(text);
        foreach (var pack in packs ?? All)
            foreach (var icon in pack.Icons)
                if (needle.Length == 0 || Matches(icon, pack, needle)) yield return (pack, icon);
    }

    private static bool Matches(PackIcon icon, IconPack pack, string needle)
        => BrandIcons.Fold(icon.Name).Contains(needle, StringComparison.Ordinal)
           || icon.Tags.Any(t => BrandIcons.Fold(t).Contains(needle, StringComparison.Ordinal))
           || BrandIcons.Fold(pack.Name).Contains(needle, StringComparison.Ordinal);

    // ---------- Lecture d'un pack (partie testée) ----------

    /// <summary>Lit le pack d'un dossier : manifest et icons.json si présents, sinon les images trouvées. Null s'il n'y a aucune image.</summary>
    internal static IconPack? Read(string folder, string id)
    {
        JsonElement? manifest = ReadJson(Path.Combine(folder, "manifest.json"));
        var iconsDir = Directory.Exists(Path.Combine(folder, "icons")) ? Path.Combine(folder, "icons") : folder;

        var icons = new List<PackIcon>();
        var skipped = 0;
        if (ReadJson(Path.Combine(folder, "icons.json")) is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || Text(entry, "path") is not { } relative) continue;
                var path = Resolve(folder, iconsDir, relative);
                if (path is null) continue;
                if (Displayable(path) is not { } shown)
                {
                    if (IsSvg(path)) skipped++;
                    continue;
                }
                icons.Add(new PackIcon(shown, Text(entry, "name") ?? Path.GetFileNameWithoutExtension(path), Tags(entry)));
            }
        }
        else
        {
            foreach (var path in Directory.EnumerateFiles(iconsDir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (path.EndsWith(".svg.png", StringComparison.OrdinalIgnoreCase) || IsManifestIcon(manifest, folder, path)) continue; // PNG converti : listé avec son SVG
                if (Displayable(path) is not { } shown)
                {
                    if (IsSvg(path)) skipped++;
                    continue;
                }
                icons.Add(new PackIcon(shown, Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' '), Array.Empty<string>()));
            }
        }
        if (icons.Count == 0) return null;

        string? iconPath = null;
        if (manifest is { } m && Text(m, "icon") is { } packIcon)
            iconPath = Resolve(folder, folder, packIcon) is { } p ? Displayable(p) : null;

        return new IconPack
        {
            Id = id,
            Name = (manifest is { } mm ? Text(mm, "name") : null) ?? CleanFolderName(folder),
            Author = manifest is { } a ? Text(a, "author") : null,
            Version = manifest is { } v ? Text(v, "version") : null,
            Description = manifest is { } d ? Text(d, "description") : null,
            Folder = folder,
            IconPath = iconPath ?? icons[0].Path,
            Icons = icons,
            SkippedSvg = skipped
        };
    }

    /// <summary>Dossier racine du pack : celui du manifest.json (ou icons.json) le moins profond, sinon le dossier lui-même.</summary>
    internal static string FindRoot(string folder)
    {
        foreach (var marker in new[] { "manifest.json", "icons.json" })
        {
            var found = Directory.EnumerateFiles(folder, marker, SearchOption.AllDirectories)
                .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
                .FirstOrDefault();
            if (found is not null) return Path.GetDirectoryName(found)!;
        }
        // Zip contenant un seul dossier : c'est lui le pack.
        var dirs = Directory.GetDirectories(folder);
        return dirs.Length == 1 && Directory.GetFiles(folder).Length == 0 ? dirs[0] : folder;
    }

    /// <summary>Décompresse en refusant tout chemin qui sortirait du dossier (zip slip) et les archives trop grosses.</summary>
    internal static void ExtractSafely(string zipPath, string destination) => SafeZip.Extract(zipPath, destination, MaxBytes, MaxFiles);

    /// <summary>Identifiant de dossier tiré du nom : minuscules sans accents, lettres, chiffres et tirets.</summary>
    internal static string MakeId(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in BrandIcons.Fold(name))
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var id = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return id.Length == 0 ? "pack" : id.Length > 60 ? id[..60].TrimEnd('-') : id;
    }

    internal static bool IsInside(string folder, string path) => SafeZip.IsInside(folder, path);

    /// <summary>Chemin d'une image citée par le pack, cherché dans icons\ puis à la racine ; null s'il sort du pack ou n'existe pas.</summary>
    private static string? Resolve(string root, string iconsDir, string relative)
    {
        relative = relative.Replace('/', Path.DirectorySeparatorChar);
        foreach (var baseDir in new[] { iconsDir, root })
        {
            var path = Path.GetFullPath(Path.Combine(baseDir, relative));
            if (IsInside(root, path) && File.Exists(path)) return path;
        }
        return null;
    }

    private static bool IsManifestIcon(JsonElement? manifest, string folder, string path)
        => manifest is { } m && Text(m, "icon") is { } icon
           && string.Equals(Path.GetFullPath(Path.Combine(folder, icon)), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    /// <summary>Image affichable : l'image elle-même, le PNG converti d'un SVG (<see cref="SvgRasterizer"/>), sinon null.</summary>
    private static string? Displayable(string path)
        => IsSvg(path) ? File.Exists(SvgRasterizer.PngPathOf(path)) ? SvgRasterizer.PngPathOf(path) : null
            : IsImage(path) ? path : null;

    private static bool IsSvg(string path) => string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

    private static string CleanFolderName(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        foreach (var suffix in new[] { ".sdIconPack", ".streamDeckIconPack" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        return name.Length == 0 ? "Pack d'icônes" : name;
    }

    private static JsonElement? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Texte d'une propriété, nom insensible à la casse ; null si absente ou vide.</summary>
    private static string? Text(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return string.IsNullOrWhiteSpace(p.Value.GetString()) ? null : p.Value.GetString()!.Trim();
        return null;
    }

    private static IReadOnlyList<string> Tags(JsonElement obj)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (!string.Equals(p.Name, "tags", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.Array)
                return p.Value.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList();
            if (p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return Array.Empty<string>();
    }
}
