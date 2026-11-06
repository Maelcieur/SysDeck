using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysDeck.Infrastructure;

namespace SysDeck.Deck;

/// <summary>
/// Textures du fond du widget des tuiles (illusion d'un vrai Stream Deck) : une intégrée, plus celles que l'utilisateur
/// ajoute (copiées dans le dossier Textures des données). Une texture est désignée par « builtin:nom » ou par son chemin.
/// </summary>
public static class DeckTextures
{
    private const string BuiltinPrefix = "builtin:";
    private static readonly (string Id, string Label, string Resource)[] Builtins =
    {
        ("builtin:grain-noir", "Grain noir", "texture-grain-noir.jpg")
    };

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    public static string Folder => Path.Combine(AppPaths.DataDir, "Textures");

    /// <summary>Textures proposées : intégrées puis ajoutées (nom du fichier).</summary>
    public static IReadOnlyList<(string Id, string Label)> All()
    {
        var list = Builtins.Select(b => (b.Id, b.Label)).ToList();
        try
        {
            if (Directory.Exists(Folder))
                list.AddRange(Directory.EnumerateFiles(Folder)
                    .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => (f, Path.GetFileNameWithoutExtension(f))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Dossier des textures illisible", ex);
        }
        return list;
    }

    /// <summary>Copie une image dans le dossier des textures (nom libre trouvé au besoin) ; renvoie son identifiant.</summary>
    public static string Add(string sourcePath)
    {
        Directory.CreateDirectory(Folder);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var target = Path.Combine(Folder, name + extension);
        for (var i = 2; File.Exists(target); i++) target = Path.Combine(Folder, $"{name} ({i}){extension}");
        File.Copy(sourcePath, target);
        return target;
    }

    /// <summary>Image de la texture (mise en cache), null si absente ou illisible.</summary>
    public static ImageSource? Load(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (Cache.TryGetValue(id, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            var image2 = new BitmapImage();
            image2.BeginInit();
            image2.CacheOption = BitmapCacheOption.OnLoad;
            image2.DecodePixelWidth = 1280; // jamais plus large que l'écran de la Switch
            if (id.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
            {
                var resource = Builtins.FirstOrDefault(b => b.Id == id).Resource;
                var stream = resource is null ? null : typeof(DeckTextures).Assembly.GetManifestResourceStream(resource);
                if (stream is null) { Cache[id] = null; return null; }
                image2.StreamSource = stream;
            }
            else
            {
                if (!File.Exists(id)) { Cache[id] = null; return null; }
                image2.UriSource = new Uri(id, UriKind.Absolute);
            }
            image2.EndInit();
            image2.Freeze();
            image = image2;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UriFormatException)
        {
            Log.Warn($"Texture « {id} » illisible", ex);
        }
        Cache[id] = image;
        return image;
    }
}
