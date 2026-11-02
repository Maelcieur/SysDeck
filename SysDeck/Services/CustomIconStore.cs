using System.IO;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>
/// Copie les fichiers choisis par l'utilisateur dans un dossier géré par SysDeck,
/// plutôt que de simplement retenir le chemin d'origine : si l'utilisateur déplace ou supprime le
/// fichier qu'il avait choisi, rien ne casse. Les copies devenues orphelines (fichier remplacé)
/// sont supprimées — mais uniquement les fichiers que ce dossier gère lui-même, jamais un fichier
/// en dehors, pour ne jamais toucher autre chose que nos propres copies.
/// </summary>
public sealed class ManagedFileStore
{
    public ManagedFileStore(string folderName)
    {
        Dir = AppPaths.Folder(folderName);
    }

    public string Dir { get; }

    public string SaveCopy(string sourcePath, string? fileName = null)
    {
        Directory.CreateDirectory(Dir);
        fileName ??= $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}";
        var destPath = Path.Combine(Dir, fileName);
        File.Copy(sourcePath, destPath, overwrite: true);
        return destPath;
    }

    public void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            // Séparateur final : « Icons » ne doit pas autoriser un fichier de « Icons2 ».
            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Dir)) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch
        {
            // fichier verrouillé ou déjà supprimé : sans conséquence, juste un fichier orphelin de plus
        }
    }
}

/// <summary>Icônes personnalisées des raccourcis.</summary>
public static class CustomIconStore
{
    private static readonly ManagedFileStore Store = new("Icons");

    public static string SaveCopy(string sourcePath) => Store.SaveCopy(sourcePath);

    public static void TryDelete(string? path) => Store.TryDelete(path);
}

/// <summary>Images de fond des modules.</summary>
public static class BackgroundImageStore
{
    private static readonly ManagedFileStore Store = new("Backgrounds");

    public static string SaveCopy(string sourcePath) => Store.SaveCopy(sourcePath);

    public static void TryDelete(string? path) => Store.TryDelete(path);
}
