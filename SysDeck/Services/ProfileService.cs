using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>
/// Profils : instantanés nommés de la personnalisation, stockés dans %AppData%\SysDeck\Profiles\&lt;nom-sûr&gt;.json.
/// <list type="bullet">
/// <item>Les images de fond d'un profil sont copiées dans Profiles\Files (nom = empreinte du contenu) : le profil
/// reste valable même si l'utilisateur change ou supprime ensuite l'image du module. Dans le fichier JSON elles
/// sont désignées par une référence relative « Backgrounds/&lt;empreinte&gt;.ext » (jamais un chemin absolu).</item>
/// <item>Les polices importées restent dans Fonts\ (SysDeck ne les supprime jamais) ; la clé « @fichier#Famille » est conservée.</item>
/// <item>Toutes les erreurs prévisibles lèvent <see cref="ProfileException"/> avec un message en français.</item>
/// </list>
/// Le dossier racine est injectable (tests) ; par défaut c'est le dossier de données de SysDeck.
/// L'export / import (.sysdeckprofile) est dans ProfileService.Package.cs.
/// </summary>
public sealed partial class ProfileService
{
    private const int MaxNameLength = 80;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    private static readonly string[] FontExtensions = { ".ttf", ".otf", ".ttc" };
    private static readonly string[] ReservedNames =
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    private static readonly JsonSerializerOptions PackageJson = new() { WriteIndented = true };

    private readonly IProfileHost _host;
    private readonly string _profilesDir;
    private readonly string _filesDir;
    private readonly string _backgroundsDir;
    private readonly string _fontsDir;

    /// <param name="host">Source de la personnalisation courante et cible de l'application d'un profil.</param>
    /// <param name="dataDir">Dossier racine des données ; null = %AppData%\SysDeck (AppPaths).</param>
    public ProfileService(IProfileHost host, string? dataDir = null)
    {
        _host = host;
        var root = dataDir ?? AppPaths.DataDir;
        _profilesDir = Path.Combine(root, "Profiles");
        _filesDir = Path.Combine(_profilesDir, "Files");
        _backgroundsDir = Path.Combine(root, "Backgrounds");
        _fontsDir = Path.Combine(root, "Fonts");
    }

    // ---------- API publique ----------

    /// <summary>Profils enregistrés, triés par nom. Les fichiers illisibles sont ignorés.</summary>
    public IReadOnlyList<ProfileInfo> List()
    {
        if (!Directory.Exists(_profilesDir)) return Array.Empty<ProfileInfo>();

        var result = new List<ProfileInfo>();
        foreach (var file in Directory.EnumerateFiles(_profilesDir, "*.json"))
            if (ReadFile(file) is { } profile && profile.Name.Length > 0)
                result.Add(new ProfileInfo(profile.Name, profile.SavedAtUtc));
        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Vrai si un profil porte ce nom (ou un nom qui donne le même fichier) : l'interface demande alors confirmation avant d'écraser.</summary>
    public bool Exists(string name)
    {
        try { return File.Exists(ProfilePath(ValidateName(name))); }
        catch (ProfileException) { return false; }
    }

    /// <summary>Enregistre la personnalisation courante sous ce nom ; écrase le profil existant (confirmer avant, voir <see cref="Exists"/>).</summary>
    public ProfileInfo SaveCurrent(string name)
    {
        name = ValidateName(name);
        var profile = _host.CaptureProfile();
        profile.Normalize();
        profile.Name = name;
        profile.SavedAtUtc = DateTime.UtcNow;
        profile.FormatVersion = Profile.CurrentFormat;
        PackImages(profile);
        WriteStored(profile);
        CollectGarbage();
        return new ProfileInfo(profile.Name, profile.SavedAtUtc);
    }

    /// <summary>Charge le profil prêt à appliquer : ses images sont recopiées dans Backgrounds\ (une copie par module, comme un choix manuel).</summary>
    public Profile Load(string name)
    {
        var profile = ReadStoredOrThrow(name);
        Materialize(profile);
        return profile;
    }

    /// <summary>Applique un profil à l'application en cours. includeLayout : modules affichés et positions.</summary>
    public void Apply(string name, bool includeLayout) => _host.ApplyProfile(Load(name), includeLayout);

    /// <summary>Supprime un profil (sans erreur s'il n'existe pas) et les images qu'il était seul à utiliser.</summary>
    public void Delete(string name)
    {
        string path;
        try { path = ProfilePath(ValidateName(name)); }
        catch (ProfileException) { return; }

        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileException($"Impossible de supprimer le profil « {name} » : {ex.Message}", ex);
        }
        CollectGarbage();
    }

    /// <summary>Renomme un profil ; refuse si un autre profil porte déjà le nouveau nom.</summary>
    public void Rename(string oldName, string newName)
    {
        var profile = ReadStoredOrThrow(oldName);
        newName = ValidateName(newName);
        var oldPath = ProfilePath(ValidateName(oldName));
        var newPath = ProfilePath(newName);

        // Même fichier (changement de casse seulement) : autorisé. Sinon le nom ne doit pas être pris.
        var samePath = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
        if (!samePath && File.Exists(newPath))
            throw new ProfileException($"Un profil nommé « {newName} » existe déjà.");

        profile.Name = newName;
        WriteStored(profile);
        if (!samePath)
        {
            try { File.Delete(oldPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProfileException($"Le profil a été copié sous « {newName} » mais l'ancien fichier n'a pas pu être supprimé : {ex.Message}", ex);
            }
        }
    }

    // ---------- Noms ----------

    private static string ValidateName(string? name)
    {
        name = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) throw new ProfileException("Le nom du profil est vide.");
        if (name.Length > MaxNameLength) throw new ProfileException($"Le nom du profil est trop long ({MaxNameLength} caractères au maximum).");
        return name;
    }

    /// <summary>Nom de fichier valable sous Windows (caractères interdits, noms réservés, point ou espace final).</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "_";
        if (ReservedNames.Contains(safe.Split('.')[0], StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;
        return safe;
    }

    private string ProfilePath(string validName) => Path.Combine(_profilesDir, SafeFileName(validName) + ".json");

    // ---------- Lecture / écriture du stockage ----------

    private static Profile? ReadFile(string path)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path));
            profile?.Normalize();
            return profile;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.WarnOnce("profile-read:" + path, $"Profil illisible ignoré : {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    private Profile ReadStoredOrThrow(string name)
    {
        var path = ProfilePath(ValidateName(name));
        if (!File.Exists(path)) throw new ProfileException($"Le profil « {name} » n'existe pas.");
        return ReadFile(path) ?? throw new ProfileException($"Le profil « {name} » est illisible.");
    }

    private void WriteStored(Profile profile)
    {
        if (!JsonFile.Save(ProfilePath(profile.Name), profile))
            throw new ProfileException($"Impossible d'écrire le profil « {profile.Name} » (voir le journal).");
    }

    // ---------- Images ----------

    /// <summary>Copie les images de fond dans le dossier du profil et remplace leur chemin par une référence relative.</summary>
    private void PackImages(Profile profile)
    {
        foreach (var appearance in profile.AllImageSlots())
        {
            var path = appearance.Value;
            if (string.IsNullOrWhiteSpace(path)) { appearance.Value = null; continue; }
            try
            {
                if (!HasExtension(path, ImageExtensions) || !File.Exists(path)) { appearance.Value = null; continue; }
                appearance.Value = BackgroundsPrefix + StoreImage(File.ReadAllBytes(path), Path.GetExtension(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Image de fond non enregistrée dans le profil : {path}", ex);
                appearance.Value = null;
            }
        }
    }

    /// <summary>Écrit l'image sous un nom tiré de son contenu (deux modules avec la même image la partagent) et renvoie ce nom.</summary>
    private string StoreImage(byte[] data, string extension)
    {
        var name = ImageStoreName(data, extension);
        var path = Path.Combine(_filesDir, name);
        if (!File.Exists(path)) WriteAtomic(path, data);
        return name;
    }

    private static string ImageStoreName(byte[] data, string extension)
        => Convert.ToHexString(SHA256.HashData(data), 0, 16).ToLowerInvariant() + extension.ToLowerInvariant();

    /// <summary>Remplace les références relatives par de vraies copies dans Backgrounds\ (références cassées : image retirée).</summary>
    private void Materialize(Profile profile)
    {
        foreach (var appearance in profile.AllImageSlots())
        {
            var reference = appearance.Value;
            if (string.IsNullOrWhiteSpace(reference)) { appearance.Value = null; continue; }

            if (!TryResolveBackground(reference, out var source) || !File.Exists(source))
            {
                appearance.Value = null;
                continue;
            }
            try
            {
                Directory.CreateDirectory(_backgroundsDir);
                // Une copie par module : l'application supprime l'ancienne image d'un module quand il en change.
                var destination = Path.Combine(_backgroundsDir, Guid.NewGuid().ToString("N") + Path.GetExtension(source));
                File.Copy(source, destination);
                appearance.Value = destination;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Image de fond du profil non copiée : {source}", ex);
                appearance.Value = null;
            }
        }
    }

    /// <summary>« Backgrounds/xxx.png » vers le fichier du dossier Files ; refuse tout ce qui n'est pas un simple nom d'image.</summary>
    private bool TryResolveBackground(string reference, out string path)
    {
        path = "";
        if (!reference.StartsWith(BackgroundsPrefix, StringComparison.Ordinal)) return false;
        var file = reference[BackgroundsPrefix.Length..];
        if (!IsPlainFileName(file) || !HasExtension(file, ImageExtensions)) return false;
        path = Path.Combine(_filesDir, file);
        return true;
    }

    /// <summary>Supprime de Files les images qu'aucun profil ne référence plus.</summary>
    private void CollectGarbage()
    {
        if (!Directory.Exists(_filesDir)) return;
        try
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_profilesDir, "*.json"))
                if (ReadFile(file) is { } profile)
                    foreach (var a in profile.AllImageSlots())
                        if (a.Value is { } r && r.StartsWith(BackgroundsPrefix, StringComparison.Ordinal))
                            used.Add(r[BackgroundsPrefix.Length..]);

            foreach (var file in Directory.EnumerateFiles(_filesDir))
                if (!used.Contains(Path.GetFileName(file)))
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un fichier orphelin de plus n'est pas grave.
            Log.Warn("Nettoyage des images de profils incomplet", ex);
        }
    }

    // ---------- Aides ----------

    private static bool HasExtension(string file, string[] allowed)
        => allowed.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    private static bool IsPlainFileName(string file)
        => file.Length is > 0 and <= 150
           && file == Path.GetFileName(file)
           && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && file is not ("." or "..");

    /// <summary>Écriture atomique (fichier temporaire puis remplacement), comme JsonFile.</summary>
    private static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }
}
