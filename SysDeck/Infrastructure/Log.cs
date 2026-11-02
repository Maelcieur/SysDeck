using System.IO;
using System.Text;

namespace SysDeck.Infrastructure;

/// <summary>
/// Journal texte minimal (logs\sysdeck.log), sûr entre threads. Quand il dépasse 1 Mo, il devient
/// sysdeck.old.log (l'ancien est écrasé) : la taille sur disque reste bornée à ~2 Mo.
/// Ne lève jamais d'exception : un journal en panne ne doit pas faire tomber l'application.
/// </summary>
public static class Log
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly HashSet<string> ReportedOnce = new();

    private static string FilePath => Path.Combine(AppPaths.LogDir, "sysdeck.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>
    /// Pour les erreurs qui se répètent à chaque rafraîchissement (capteur absent, périphérique
    /// débranché) : seule la première occurrence par clé est écrite, pour ne pas noyer le journal.
    /// </summary>
    public static void WarnOnce(string key, string message, Exception? ex = null)
    {
        lock (Gate)
        {
            if (!ReportedOnce.Add(key)) return;
        }
        Write("WARN ", message + " (signalé une seule fois par session)", ex);
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                .Append(level).Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
                .Append(message);
            if (ex is not null) line.AppendLine().Append(ex);
            line.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxSize)
                    File.Move(FilePath, Path.Combine(AppPaths.LogDir, "sysdeck.old.log"), overwrite: true);
                File.AppendAllText(FilePath, line.ToString());
            }
        }
        catch
        {
            // disque plein ou dossier verrouillé : on renonce silencieusement à journaliser
        }
    }
}
