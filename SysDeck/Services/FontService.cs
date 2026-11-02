using System.Globalization;
using System.IO;
using System.Windows.Media;

namespace SysDeck.Services;

public sealed record FontChoice(string Key, string Display, FontFamily Family, bool Imported);

/// <summary>
/// Polices utilisables par les modules : celles installées sur Windows, plus celles importées
/// par l'utilisateur (copiées dans %AppData%\SysDeck\Fonts, sans installation système).
/// Une police importée est désignée par la clé "@nomDeFichier#Famille".
/// </summary>
public static class FontService
{
    private static readonly ManagedFileStore Store = new("Fonts");
    private static readonly string[] FontExtensions = { ".ttf", ".otf", ".ttc" };

    private static List<FontChoice>? _systemFonts;
    private static List<FontChoice>? _choices;

    /// <summary>
    /// Polices importées puis installées. Calculé une seule fois et partagé par tous les sélecteurs :
    /// la page d'un module en affiche une quinzaine, chacune relisant des centaines de polices sinon.
    /// </summary>
    public static IReadOnlyList<FontChoice> GetChoices()
    {
        _systemFonts ??= Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Select(name => new FontChoice(name, name, new FontFamily(name), false))
            .ToList();

        return _choices ??= GetImportedFonts().Concat(_systemFonts).ToList();
    }

    private static IEnumerable<FontChoice> GetImportedFonts()
    {
        if (!Directory.Exists(Store.Dir)) yield break;

        foreach (var file in Directory.EnumerateFiles(Store.Dir))
        {
            if (!FontExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            var family = ReadFamilyName(file);
            if (family is null) continue;

            var key = $"@{Path.GetFileName(file)}#{family}";
            yield return new FontChoice(key, $"{family} (importée)", Resolve(key), true);
        }
    }

    /// <summary>Copie un fichier de police dans SysDeck et renvoie sa clé, ou null si le fichier n'est pas une police lisible.</summary>
    public static string? Import(string path)
    {
        var family = ReadFamilyName(path);
        if (family is null) return null;

        var copy = Store.SaveCopy(path, Path.GetFileName(path));
        _choices = null; // la nouvelle police doit apparaître dans les listes
        return $"@{Path.GetFileName(copy)}#{family}";
    }

    /// <summary>Résout une clé de police ; retombe sur Segoe UI si la police est introuvable.</summary>
    public static FontFamily Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new FontFamily("Segoe UI");

        if (key.StartsWith('@') && key.IndexOf('#') is var hash and > 1)
        {
            var family = key[(hash + 1)..];
            var folder = ToFileUri(Store.Dir.TrimEnd('\\') + "\\");
            return new FontFamily(folder, $"./#{family}, Segoe UI");
        }

        return new FontFamily($"{key}, Segoe UI");
    }

    /// <summary>
    /// Adresse file:// d'un chemin local. new Uri(chemin) prendrait un « # » pour un fragment et
    /// un « %xx » pour un code déjà échappé ; UriBuilder échappe ces caractères.
    /// </summary>
    private static Uri ToFileUri(string path)
        => new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = path }.Uri;

    private static string? ReadFamilyName(string path)
    {
        try
        {
            var glyph = new GlyphTypeface(ToFileUri(path));
            return glyph.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var name)
                ? name
                : glyph.FamilyNames.Values.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
