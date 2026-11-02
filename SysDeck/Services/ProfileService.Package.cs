using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace SysDeck.Services;

// Export / import d'un profil sous forme de fichier .sysdeckprofile (archive zip) :
//   profile.json           le profil ; images désignées par « Backgrounds/<nom> », polices par « @fichier#Famille »
//   Backgrounds/<nom>      images de fond (png, jpg, bmp, gif, tif)
//   Fonts/<fichier>        polices importées utilisées par le profil (ttf, otf, ttc)
// Un fichier .sysdeckprofile vient d'un tiers : l'import le traite comme non fiable.
public sealed partial class ProfileService
{
    /// <summary>Extension conseillée du fichier d'export.</summary>
    public const string PackageExtension = ".sysdeckprofile";

    /// <summary>Taille totale maximale du contenu décompressé (et de l'archive).</summary>
    public const long MaxPackageBytes = 100L * 1024 * 1024;

    private const int MaxEntries = 200;
    private const long MaxProfileJsonBytes = 5L * 1024 * 1024;
    private const string ProfileEntry = "profile.json";
    private const string BackgroundsPrefix = "Backgrounds/";
    private const string FontsPrefix = "Fonts/";

    // ---------- Export ----------

    /// <summary>Exporte un profil et les fichiers qu'il référence (images de fond, polices importées) dans une archive.</summary>
    public void Export(string name, string filePath)
    {
        var profile = ReadStoredOrThrow(name);
        var temp = filePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Référence dont le fichier a disparu : retirée, pour que l'archive reste cohérente.
                foreach (var appearance in profile.AllImageSlots())
                {
                    if (string.IsNullOrWhiteSpace(appearance.Value)) continue;
                    if (TryResolveBackground(appearance.Value, out var source) && File.Exists(source))
                    {
                        if (added.Add(appearance.Value))
                            zip.CreateEntryFromFile(source, appearance.Value, CompressionLevel.Fastest);
                    }
                    else
                    {
                        appearance.Value = null;
                    }
                }

                MapFontKeys(profile, key =>
                {
                    if (TryParseFontKey(key, out var file, out _) && added.Add(FontsPrefix + file))
                    {
                        var path = Path.Combine(_fontsDir, file);
                        if (File.Exists(path)) zip.CreateEntryFromFile(path, FontsPrefix + file, CompressionLevel.Optimal);
                    }
                    return key;
                });

                var entry = zip.CreateEntry(ProfileEntry, CompressionLevel.Optimal);
                using var writer = entry.Open();
                JsonSerializer.Serialize(writer, profile, PackageJson);
            }
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw new ProfileException($"Impossible d'exporter le profil : {ex.Message}", ex);
        }
    }

    // ---------- Import ----------

    /// <summary>
    /// Importe un fichier .sysdeckprofile et renvoie le nom sous lequel le profil a été enregistré
    /// (suffixé « (2) »… si ce nom existait déjà : jamais d'écrasement). Rien n'est écrit si le fichier est invalide.
    /// </summary>
    public string Import(string filePath)
    {
        var package = ReadPackage(filePath);

        var name = package.Profile.Name;
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
        name = ValidateName(name);
        var unique = name;
        for (var i = 2; Exists(unique); i++)
            unique = $"{name[..Math.Min(name.Length, MaxNameLength - 6)]} ({i})";

        // Écritures seulement maintenant que tout est validé ; en cas d'échec, on retire ce qu'on vient de créer.
        var created = new List<string>();
        try
        {
            var profile = package.Profile;
            foreach (var appearance in profile.AllImageSlots())
            {
                if (appearance.Value is not { } reference) continue;
                var data = package.Images[reference[BackgroundsPrefix.Length..]];
                // Une image déjà présente (même contenu, peut-être d'un autre profil) ne doit jamais être supprimée en cas d'échec.
                var existed = File.Exists(Path.Combine(_filesDir, ImageStoreName(data, Path.GetExtension(reference))));
                var stored = StoreImage(data, Path.GetExtension(reference));
                if (!existed) created.Add(Path.Combine(_filesDir, stored));
                appearance.Value = BackgroundsPrefix + stored;
            }

            MapFontKeys(profile, key =>
            {
                TryParseFontKey(key, out var file, out var family);
                if (!package.Fonts.TryGetValue(file, out var data)) return key; // police non fournie : retombera sur Segoe UI
                var target = InstallFont(file, data, created);
                return $"@{target}#{family}";
            });

            profile.Name = unique;
            profile.SavedAtUtc = DateTime.UtcNow;
            WriteStored(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var path in created)
                try { File.Delete(path); } catch { /* best effort */ }
            throw new ProfileException($"Import impossible : {ex.Message}", ex);
        }
        catch (ProfileException)
        {
            foreach (var path in created)
                try { File.Delete(path); } catch { /* best effort */ }
            throw;
        }
        return unique;
    }

    /// <summary>
    /// Copie une police dans Fonts\ sous un nom qui n'en écrase aucune : si un fichier de même nom et de même
    /// contenu existe déjà on le réutilise, sinon on ajoute un suffixe unique.
    /// </summary>
    private string InstallFont(string file, byte[] data, List<string> created)
    {
        var path = Path.Combine(_fontsDir, file);
        if (File.Exists(path))
        {
            if (File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return file;
            file = $"{Path.GetFileNameWithoutExtension(file)}-{Guid.NewGuid().ToString("N")[..8]}{Path.GetExtension(file)}";
            path = Path.Combine(_fontsDir, file);
        }
        WriteAtomic(path, data);
        created.Add(path);
        return file;
    }

    private sealed record ImportedPackage(Profile Profile, Dictionary<string, byte[]> Images, Dictionary<string, byte[]> Fonts);

    /// <summary>Lit et valide entièrement l'archive en mémoire ; ne touche pas au disque.</summary>
    private static ImportedPackage ReadPackage(string filePath)
    {
        try
        {
            if (new FileInfo(filePath).Length > MaxPackageBytes)
                throw TooBig();

            using var zip = ZipFile.OpenRead(filePath);
            if (zip.Entries.Count > MaxEntries)
                throw new ProfileException("Ce fichier contient trop d'éléments pour être un profil SysDeck.");

            var budget = MaxPackageBytes;
            byte[]? json = null;
            var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var fonts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in zip.Entries)
            {
                // Les dossiers n'ont pas de contenu : on les ignore, seuls les fichiers sont examinés.
                if (entry.FullName.EndsWith('/') && entry.Length == 0) continue;

                var (kind, file) = Classify(entry.FullName);
                if (entry.Length > budget) throw TooBig();

                switch (kind)
                {
                    case EntryKind.Profile:
                        if (json is not null) throw Invalid($"« {entry.FullName} » est présent deux fois.");
                        json = ReadLimited(entry, ref budget, MaxProfileJsonBytes);
                        break;
                    case EntryKind.Image:
                    {
                        if (images.ContainsKey(file)) throw Invalid($"« {entry.FullName} » est présent deux fois.");
                        var data = ReadLimited(entry, ref budget, MaxPackageBytes);
                        if (!LooksLikeImage(data, Path.GetExtension(file)))
                            throw Invalid($"« {entry.FullName} » n'est pas une image valide.");
                        images[file] = data;
                        break;
                    }
                    default:
                    {
                        if (fonts.ContainsKey(file)) throw Invalid($"« {entry.FullName} » est présent deux fois.");
                        var data = ReadLimited(entry, ref budget, MaxPackageBytes);
                        if (!LooksLikeFont(data))
                            throw Invalid($"« {entry.FullName} » n'est pas une police valide.");
                        fonts[file] = data;
                        break;
                    }
                }
            }

            if (json is null) throw Invalid("profile.json est absent.");

            Profile? profile;
            try { profile = JsonSerializer.Deserialize<Profile>(json); }
            catch (JsonException ex) { throw new ProfileException("Le fichier profile.json de ce profil est illisible.", ex); }
            if (profile is null) throw Invalid("profile.json est vide.");
            if (profile.FormatVersion > Profile.CurrentFormat)
                throw new ProfileException("Ce profil a été créé par une version plus récente de SysDeck. Mettez SysDeck à jour pour l'importer.");
            profile.Normalize();

            // Les chemins de l'archive ne sont jamais crus : seule une référence à une image DE L'ARCHIVE est acceptée.
            foreach (var appearance in profile.AllImageSlots())
            {
                if (string.IsNullOrWhiteSpace(appearance.Value)) { appearance.Value = null; continue; }
                var reference = appearance.Value;
                if (!reference.StartsWith(BackgroundsPrefix, StringComparison.Ordinal)
                    || !images.ContainsKey(reference[BackgroundsPrefix.Length..]))
                    throw Invalid("une image de fond référencée par le profil n'est pas dans l'archive.");
            }

            var fontError = false;
            MapFontKeys(profile, key =>
            {
                if (!TryParseFontKey(key, out _, out _)) fontError = true;
                return key;
            });
            if (fontError) throw Invalid("une police référencée par le profil a un nom non valide.");

            return new ImportedPackage(profile, images, fonts);
        }
        catch (InvalidDataException ex)
        {
            throw new ProfileException("Ce fichier n'est pas un profil SysDeck valide (archive illisible).", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileException($"Impossible de lire le fichier : {ex.Message}", ex);
        }
    }

    private enum EntryKind { Profile, Image, Font }

    /// <summary>
    /// Autorise uniquement profile.json, Backgrounds/&lt;image&gt; et Fonts/&lt;police&gt;. Tout chemin avec « .. »,
    /// séparateur inversé, lecteur ou sous-dossier est refusé (zip-slip) ; les types de fichiers sont limités.
    /// </summary>
    private static (EntryKind Kind, string File) Classify(string fullName)
    {
        if (fullName.Contains('\\') || fullName.Contains(':') || fullName.StartsWith('/'))
            throw Invalid($"le chemin « {fullName} » est interdit.");
        var parts = fullName.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".."))
            throw Invalid($"le chemin « {fullName} » est interdit.");

        if (parts.Length == 1 && parts[0] == ProfileEntry) return (EntryKind.Profile, ProfileEntry);

        if (parts.Length == 2 && IsPlainFileName(parts[1]))
        {
            if (parts[0] == "Backgrounds" && HasExtension(parts[1], ImageExtensions)) return (EntryKind.Image, parts[1]);
            if (parts[0] == "Fonts" && HasExtension(parts[1], FontExtensions)) return (EntryKind.Font, parts[1]);
        }
        throw Invalid($"le fichier « {fullName} » n'est pas autorisé dans un profil.");
    }

    /// <summary>Lit une entrée en comptant les octets réellement décompressés : la taille déclarée par l'archive peut mentir.</summary>
    private static byte[] ReadLimited(ZipArchiveEntry entry, ref long budget, long entryMax)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            budget -= read;
            if (budget < 0 || total > entryMax) throw TooBig();
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static ProfileException TooBig()
        => new($"Ce fichier est trop volumineux pour être un profil SysDeck (limite : {MaxPackageBytes / (1024 * 1024)} Mo).");

    private static ProfileException Invalid(string detail)
        => new($"Profil invalide : {detail}");

    /// <summary>Vérifie les premiers octets : l'extension seule ne prouve pas que le contenu est une image.</summary>
    private static bool LooksLikeImage(byte[] d, string extension)
    {
        bool Starts(params byte[] sig) => d.Length >= sig.Length && d.AsSpan(0, sig.Length).SequenceEqual(sig);
        return extension.ToLowerInvariant() switch
        {
            ".png" => Starts(0x89, 0x50, 0x4E, 0x47),
            ".jpg" or ".jpeg" => Starts(0xFF, 0xD8, 0xFF),
            ".gif" => Starts(0x47, 0x49, 0x46, 0x38),
            ".bmp" => Starts(0x42, 0x4D),
            ".tif" or ".tiff" => Starts(0x49, 0x49, 0x2A, 0x00) || Starts(0x4D, 0x4D, 0x00, 0x2A),
            _ => false
        };
    }

    private static bool LooksLikeFont(byte[] d)
    {
        bool Starts(params byte[] sig) => d.Length >= sig.Length && d.AsSpan(0, sig.Length).SequenceEqual(sig);
        return Starts(0x00, 0x01, 0x00, 0x00) || Starts((byte)'O', (byte)'T', (byte)'T', (byte)'O')
            || Starts((byte)'t', (byte)'r', (byte)'u', (byte)'e') || Starts((byte)'t', (byte)'t', (byte)'c', (byte)'f');
    }

    // ---------- Polices ----------

    /// <summary>Décompose « @fichier#Famille » ; refuse un nom de fichier qui sortirait du dossier Fonts ou qui n'est pas une police.</summary>
    private static bool TryParseFontKey(string key, out string file, out string family)
    {
        file = family = "";
        if (!key.StartsWith('@') || key.IndexOf('#') is not (var hash and > 1)) return false;
        file = key[1..hash];
        family = key[(hash + 1)..];
        return IsPlainFileName(file) && HasExtension(file, FontExtensions);
    }

    /// <summary>
    /// Applique <paramref name="map"/> à chaque clé de police importée (« @... ») du profil : thème global,
    /// thèmes propres des modules et police de chaque texte. Générique : toute propriété de ThemeSettings
    /// dont le nom finit par « Font » est couverte, y compris celles ajoutées plus tard.
    /// </summary>
    private static void MapFontKeys(Profile profile, Func<string, string> map)
    {
        static IEnumerable<PropertyInfo> FontProperties() => typeof(ThemeSettings).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("Font", StringComparison.Ordinal) && p.CanRead && p.CanWrite);

        var themes = new List<ThemeSettings> { profile.Theme };
        foreach (var appearance in profile.AllModuleAppearances())
            if (appearance.Theme is { } own) themes.Add(own);
        foreach (var theme in themes)
            foreach (var property in FontProperties())
                if (property.GetValue(theme) is string { Length: > 0 } value && value[0] == '@')
                    property.SetValue(theme, map(value));

        foreach (var style in profile.AllModuleAppearances().SelectMany(a => a.Texts.Values).OfType<TextPartStyle>())
            if (style.Font is { Length: > 0 } font && font[0] == '@')
                style.Font = map(font);
    }
}
