using System.IO;
using SysDeck.Infrastructure;

namespace SysDeck.Plugins;

/// <summary>Journal d'un plugin (<c>logs\plugins\&lt;id&gt;.log</c>) : sorties du programme, <c>logMessage</c> et évènements de SysDeck. Borné à 1 Mo (l'ancien passe en .old.log).</summary>
public static class PluginLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string PathOf(string pluginId) => Path.Combine(AppPaths.LogDir, "plugins", pluginId + ".log");

    public static void Write(string pluginId, string line)
    {
        try
        {
            lock (Gate)
            {
                var path = PathOf(pluginId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // journal indisponible : sans conséquence pour le plugin
        }
    }
}
