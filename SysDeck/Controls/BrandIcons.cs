using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace SysDeck.Controls;

/// <summary>Logo de marque (Simple Icons, CC0) : identifiant, nom, couleur officielle (RRGGBB), groupe et tracé SVG 24 x 24.</summary>
public sealed class BrandIcon
{
    private readonly Lazy<Geometry?> _geometry;

    public BrandIcon(string slug, string title, string hex, string category, string pathData)
    {
        Slug = slug;
        Title = title;
        Hex = hex;
        Category = category;
        PathData = pathData;
        _geometry = new Lazy<Geometry?>(Parse, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Slug { get; }
    public string Title { get; }
    /// <summary>Couleur officielle, sans « # ».</summary>
    public string Hex { get; }
    public string Category { get; }
    public string PathData { get; }

    /// <summary>Valeur à stocker dans une icône choisie : « brand:slug ».</summary>
    public string Value => BrandIcons.Prefix + Slug;

    /// <summary>Couleur officielle au format #AARRGGBB.</summary>
    public string OfficialColor => "#FF" + Hex.ToUpperInvariant();

    /// <summary>Géométrie figée (partageable entre threads), ou null si le tracé est illisible.</summary>
    public Geometry? Geometry => _geometry.Value;

    /// <summary>Vrai si la couleur officielle serait peu lisible sur un fond sombre (ex. Steam, GitHub).</summary>
    public bool IsDark
    {
        get
        {
            if (Hex.Length != 6 || !int.TryParse(Hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
            double r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255 < 0.2;
        }
    }

    private Geometry? Parse()
    {
        try
        {
            var geometry = System.Windows.Media.Geometry.Parse(PathData);
            if (geometry.CanFreeze) geometry.Freeze();
            return geometry;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Logos de marques embarqués (<c>Assets\brand-icons.json</c>), chargés une seule fois à la première demande.
/// Une icône choisie s'écrit « brand:&lt;slug&gt; » (ex. brand:discord).
/// </summary>
public static class BrandIcons
{
    public const string Prefix = "brand:";

    private sealed record Row(string Slug, string Title, string Hex, string Category, string Path);

    private static readonly Lazy<IReadOnlyList<BrandIcon>> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Dictionary<string, BrandIcon>> Index = new(
        () => All.GroupBy(b => b.Slug, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Tous les logos, dans l'ordre du fichier (liste vide si la ressource est illisible).</summary>
    public static IReadOnlyList<BrandIcon> All => Loaded.Value;

    /// <summary>Groupes de logos, dans l'ordre d'apparition.</summary>
    public static IReadOnlyList<string> Categories => All.Select(b => b.Category).Distinct().ToList();

    /// <summary>Vrai pour une valeur d'icône au format « brand:... » (le slug peut être inconnu).</summary>
    public static bool IsBrand(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Logo correspondant à « brand:slug » ; null si ce n'est pas une marque ou si le slug est inconnu.</summary>
    public static BrandIcon? Resolve(string? value)
        => IsBrand(value) && Index.Value.TryGetValue(value![Prefix.Length..].Trim(), out var brand) ? brand : null;

    /// <summary>
    /// Couleur d'un logo en #AARRGGBB : <paramref name="userColor"/> si elle est renseignée, sinon la couleur officielle ;
    /// null si la valeur n'est pas une marque connue.
    /// </summary>
    public static string? ResolveColor(string? value, string? userColor)
    {
        var brand = Resolve(value);
        if (brand is null) return null;
        return string.IsNullOrWhiteSpace(userColor) ? brand.OfficialColor : userColor;
    }

    /// <summary>Logos dont le nom ou l'identifiant contient <paramref name="text"/> (sans tenir compte de la casse ni des accents).</summary>
    public static IEnumerable<BrandIcon> Search(string? text)
    {
        var needle = Fold(text);
        if (needle.Length == 0) return All;
        return All.Where(b => Fold(b.Title).Contains(needle, StringComparison.Ordinal) || Fold(b.Slug).Contains(needle, StringComparison.Ordinal));
    }

    /// <summary>Minuscules sans accents ni espaces superflus.</summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static IReadOnlyList<BrandIcon> Load()
    {
        try
        {
            using var stream = typeof(BrandIcons).Assembly.GetManifestResourceStream("brand-icons.json");
            if (stream is null) return Array.Empty<BrandIcon>();
            var rows = JsonSerializer.Deserialize<List<Row>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return rows is null
                ? Array.Empty<BrandIcon>()
                : rows.Where(r => !string.IsNullOrWhiteSpace(r.Slug) && !string.IsNullOrWhiteSpace(r.Path))
                    .Select(r => new BrandIcon(r.Slug, r.Title ?? r.Slug, r.Hex ?? "FFFFFF", r.Category ?? "", r.Path))
                    .ToList();
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn("Logos de marques illisibles", ex);
            return Array.Empty<BrandIcon>();
        }
    }
}
