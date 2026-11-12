using System.IO;
using System.Text.RegularExpressions;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>Lit la cible des raccourcis Windows (.lnk) et Internet (.url, ex. steam://rungameid/...).</summary>
public static class ShortcutResolver
{
    public sealed record LnkTarget(string Target, string Arguments, string WorkingDir);

    /// <summary>Cible d'un raccourci .lnk (null si illisible ou sans cible fichier, ex. raccourci d'application du Store).</summary>
    public static LnkTarget? ResolveLnk(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                string target = link.TargetPath ?? "";
                if (string.IsNullOrWhiteSpace(target)) return null;
                return new LnkTarget(target, (string?)link.Arguments ?? "", (string?)link.WorkingDirectory ?? "");
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            Log.WarnOnce("lnk-" + path, "Raccourci illisible : " + path, ex);
            return null;
        }
    }

    /// <summary>Adresse d'un raccourci Internet .url (ligne URL=...).</summary>
    public static string? ResolveUrl(string path)
    {
        try { return ParseUrl(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            Log.WarnOnce("url-" + path, "Raccourci Internet illisible : " + path, ex);
            return null;
        }
    }

    public static string? ParseUrl(string content)
    {
        var m = Regex.Match(content, @"^\s*URL\s*=\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
