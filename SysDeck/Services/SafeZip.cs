using System.IO;
using System.IO.Compression;

namespace SysDeck.Services;

/// <summary>Décompression d'archives fournies par l'utilisateur (packs d'icônes, plugins) sans jamais écrire hors du dossier cible.</summary>
public static class SafeZip
{
    /// <summary>
    /// Décompresse <paramref name="zipPath"/> dans <paramref name="destination"/>. Lève <see cref="InvalidDataException"/>
    /// (message lisible) si l'archive est illisible, trop grosse, trop fournie ou si un chemin sort du dossier (zip slip).
    /// </summary>
    public static void Extract(string zipPath, string destination, long maxBytes, int maxFiles)
    {
        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new InvalidDataException("Ce fichier n'est pas une archive valide (zip illisible)."); }

        using (archive)
        {
            if (archive.Entries.Count > maxFiles) throw new InvalidDataException("Cette archive contient trop de fichiers.");
            if (archive.Entries.Sum(e => e.Length) > maxBytes) throw new InvalidDataException("Cette archive est trop volumineuse.");

            Directory.CreateDirectory(destination);
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!IsInside(destination, target)) throw new InvalidDataException("Archive refusée : un fichier sort du dossier de destination.");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
    }

    /// <summary>Copie un dossier avec les mêmes limites que <see cref="Extract"/>.</summary>
    public static void CopyFolder(string source, string destination, long maxBytes, int maxFiles)
    {
        long total = 0;
        var count = 0;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (++count > maxFiles) throw new InvalidDataException("Ce dossier contient trop de fichiers.");
            total += new FileInfo(file).Length;
            if (total > maxBytes) throw new InvalidDataException("Ce dossier est trop volumineux.");
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Vrai si <paramref name="path"/> est dans <paramref name="folder"/> (séparateur final : « Icons » n'autorise pas « Icons2 »).</summary>
    public static bool IsInside(string folder, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
