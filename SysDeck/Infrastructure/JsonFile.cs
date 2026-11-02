using System.IO;
using System.Text.Json;

namespace SysDeck.Infrastructure;

/// <summary>
/// Lecture et écriture des fichiers de données JSON, conçues pour ne jamais perdre ni bloquer :
/// <list type="bullet">
/// <item>écriture atomique (fichier temporaire puis remplacement) : un arrêt brutal pendant
/// l'écriture laisse l'ancienne version intacte au lieu d'un fichier tronqué ;</item>
/// <item>fichier illisible : il est mis de côté (*.corrupt-date.json) puis on repart des valeurs
/// par défaut, au lieu de planter au démarrage ou d'écraser définitivement les données.</item>
/// </list>
/// </summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Fichiers existants mais illisibles (verrouillés...) : on refuse de les réécrire pour la session,
    // sinon le Save suivant écraserait la vraie configuration par les valeurs par défaut.
    private static readonly HashSet<string> UnreliablePaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object UnreliableLock = new();

    private static bool IsUnreliable(string path)
    {
        lock (UnreliableLock) return UnreliablePaths.Contains(path);
    }

    public static T Load<T>(string path, Func<T> createDefault)
    {
        if (!File.Exists(path)) return createDefault();

        try
        {
            return JsonSerializer.Deserialize<T>(ReadAllTextWithRetry(path)) ?? createDefault();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            var backup = Path.Combine(Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(path, backup, overwrite: true); } catch { /* best effort */ }
            Log.Error($"{Path.GetFileName(path)} illisible : copie conservée dans {Path.GetFileName(backup)}, valeurs par défaut utilisées.", ex);
            return createDefault();
        }
        catch (Exception ex)
        {
            // Fichier verrouillé ou accès refusé : on ne le met pas de côté, il n'est pas forcément abîmé.
            lock (UnreliableLock) UnreliablePaths.Add(path);
            Log.Error($"Lecture impossible de {path} : valeurs par défaut utilisées et écriture désactivée pour cette session (le fichier n'est pas écrasé).", ex);
            return createDefault();
        }
    }

    /// <summary>Relit quelques fois : un antivirus ou une sauvegarde verrouille souvent le fichier très brièvement.</summary>
    private static string ReadAllTextWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100 * attempt); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(100 * attempt); }
        }
    }

    /// <summary>Écrit <paramref name="value"/> ; renvoie false (et journalise) en cas d'échec, sans lever d'exception.</summary>
    public static bool Save<T>(string path, T value)
    {
        if (IsUnreliable(path))
        {
            Log.WarnOnce("save-blocked:" + path, $"Écriture de {Path.GetFileName(path)} refusée : le fichier n'a pas pu être lu au démarrage.");
            return false;
        }

        var tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, WriteOptions));
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("save:" + path, $"Écriture impossible de {Path.GetFileName(path)}", ex);
            try { File.Delete(tempPath); } catch { /* best effort */ }
            return false;
        }
    }
}
