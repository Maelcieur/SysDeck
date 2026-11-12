using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SysDeck.ConsoleMode;

/// <summary>Un émulateur configuré (exécutable + modèle de ligne de commande).</summary>
public sealed class ConsoleEmulator
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";

    /// <summary>Modèle d'arguments (réglage avancé) : jetons {rom}, {romDir}, {romName} et {core} (RetroArch).</summary>
    public string ArgumentsTemplate { get; set; } = "\"{rom}\"";
    public string WorkingDir { get; set; } = "";
    public string Platform { get; set; } = "";
    /// <summary>Remarque affichée dans les Paramètres (ex. « active le plein écran dans l'émulateur »).</summary>
    public string FullscreenHint { get; set; } = "";
    /// <summary>Cœur par défaut (RetroArch uniquement).</summary>
    public string Core { get; set; } = "";

    /// <summary>Case « Démarrer les jeux en plein écran ».</summary>
    public bool Fullscreen { get; set; } = true;

    /// <summary>Option de l'émulateur qui demande le plein écran (ex. « -f ») ; vide = il ne sait pas le faire ; null = à deviner.</summary>
    public string? FullscreenFlag { get; set; }
}

/// <summary>Modèle d'émulateur proposé : l'utilisateur choisit seulement l'exécutable.</summary>
public sealed record EmulatorPreset(string Name, string ArgumentsTemplate, string Platform, bool NeedsCore = false, string Hint = "", string FullscreenFlag = "");

public static class EmulatorPresets
{
    /// <summary>Modèles SANS l'option plein écran : elle est ajoutée par la case « plein écran ».</summary>
    public static readonly IReadOnlyList<EmulatorPreset> All = new[]
    {
        new EmulatorPreset("RetroArch", "-L \"{core}\" \"{rom}\"", "", true, "Choisis aussi le cœur (.dll).", "-f"),
        new EmulatorPreset("Dolphin", "-b -e \"{rom}\"", "GameCube / Wii", FullscreenFlag: "-C Dolphin.Display.Fullscreen=True"),
        new EmulatorPreset("PCSX2", "-nogui \"{rom}\"", "PS2", FullscreenFlag: "-fullscreen"),
        new EmulatorPreset("DuckStation", "-batch \"{rom}\"", "PS1", FullscreenFlag: "-fullscreen"),
        new EmulatorPreset("PPSSPP", "\"{rom}\"", "PSP", FullscreenFlag: "--fullscreen"),
        new EmulatorPreset("RPCS3", "--no-gui \"{rom}\"", "PS3", Hint: "RPCS3 : active « Démarrer les jeux en plein écran » dans ses propres réglages."),
        new EmulatorPreset("Cemu", "-g \"{rom}\"", "Wii U", FullscreenFlag: "-f"),
        new EmulatorPreset("Ryujinx", "\"{rom}\"", "Switch", FullscreenFlag: "--fullscreen"),
        new EmulatorPreset("Eden / yuzu", "-g \"{rom}\"", "Switch", FullscreenFlag: "-f"),
        new EmulatorPreset("Xemu", "-dvd_path \"{rom}\"", "Xbox", FullscreenFlag: "-full-screen"),
        new EmulatorPreset("mGBA", "\"{rom}\"", "Game Boy Advance", FullscreenFlag: "-f"),
        new EmulatorPreset("Personnalisé", "\"{rom}\"", ""),
    };

    /// <summary>Émulateur enregistré avant la case « plein écran » : on devine son option d'après son nom ou ses fichiers.</summary>
    public static void EnsureFullscreenFlag(ConsoleEmulator e)
    {
        if (e.FullscreenFlag != null) return;
        var preset = All.FirstOrDefault(p => p.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase) && p.Name != "Personnalisé");
        e.FullscreenFlag = preset?.FullscreenFlag ?? (IsYuzuFamily(e.ExePath) ? "-f" : "");
    }

    public static ConsoleEmulator Create(EmulatorPreset p, string exePath)
    {
        var e = new ConsoleEmulator
        {
            Name = p.Name,
            ExePath = exePath,
            ArgumentsTemplate = p.ArgumentsTemplate,
            Platform = p.Platform,
            FullscreenHint = p.Hint,
            FullscreenFlag = p.FullscreenFlag,
            WorkingDir = Path.GetDirectoryName(exePath) ?? "",
        };
        // « Personnalisé » : on reconnaît les dérivés d'Eden / yuzu (Lichii, Citron, Sudachi...) à leurs fichiers.
        if (p.Name == "Personnalisé" && IsYuzuFamily(exePath))
        {
            e.ArgumentsTemplate = "-g \"{rom}\"";
            e.FullscreenFlag = "-f";
            e.Platform = "Switch";
            e.Name = Path.GetFileNameWithoutExtension(exePath);
        }
        return e;
    }

    /// <summary>Émulateur de la famille Eden / yuzu : outil en ligne de commande ou nom connu dans le dossier de l'exe.</summary>
    public static bool IsYuzuFamily(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var names = new[] { "eden-cli.exe", "eden-cmd.exe", "yuzu-cmd.exe", "citron-cmd.exe", "sudachi-cmd.exe", "suyu-cmd.exe" };
            if (names.Any(n => File.Exists(Path.Combine(dir, n)))) return true;
            var exe = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
            return exe is "eden" or "yuzu" or "citron" or "sudachi" or "suyu" or "lichii";
        }
        catch { return false; }
    }
}

/// <summary>Ligne de commande prête pour Process.Start.</summary>
public sealed record EmulatorCommand(string FileName, string Arguments, string WorkingDir);

public static class EmulatorCommandBuilder
{
    /// <summary>
    /// Modèle avec ou sans l'option plein écran selon la case : ajoutée devant si absente,
    /// retirée si la case est décochée (même si elle était déjà écrite dans le modèle).
    /// </summary>
    public static string WithFullscreen(ConsoleEmulator emulator)
    {
        var template = emulator.ArgumentsTemplate ?? "";
        var flag = emulator.FullscreenFlag?.Trim();
        if (string.IsNullOrEmpty(flag)) return template;
        var pattern = @"(?<=^|\s)" + Regex.Escape(flag) + @"(?=\s|$)";
        var has = Regex.IsMatch(template, pattern);
        if (emulator.Fullscreen && !has) return flag + " " + template;
        if (!emulator.Fullscreen && has) return Regex.Replace(template, pattern, "").Trim();
        return template;
    }

    /// <summary>
    /// Construit la commande d'un jeu d'émulateur. Les chemins insérés dans des guillemets du modèle ont leurs
    /// antislashs finaux doublés ; hors guillemets, une valeur contenant un espace est mise entre guillemets.
    /// Jeton sans valeur (ex. {core} absent) : remplacé par du vide ; jeton inconnu : laissé tel quel.
    /// </summary>
    public static EmulatorCommand BuildEmulatorCommand(ConsoleEmulator emulator, ConsoleGame game)
    {
        var rom = game.RomPath ?? "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rom"] = rom,
            ["romDir"] = rom.Length == 0 ? "" : Path.GetDirectoryName(rom) ?? "",
            ["romName"] = rom.Length == 0 ? "" : Path.GetFileNameWithoutExtension(rom),
            ["core"] = !string.IsNullOrWhiteSpace(game.Core) ? game.Core : emulator.Core ?? "",
        };

        var template = WithFullscreen(emulator);
        var sb = new StringBuilder();
        var inQuotes = false;
        var last = 0;
        foreach (Match m in Regex.Matches(template, @"\{(\w+)\}"))
        {
            for (var i = last; i < m.Index; i++) if (template[i] == '"') inQuotes = !inQuotes;
            sb.Append(template, last, m.Index - last);
            last = m.Index + m.Length;
            if (!values.TryGetValue(m.Groups[1].Value, out var v)) { sb.Append(m.Value); continue; }
            var trailing = v.Length - v.TrimEnd('\\').Length;
            var escaped = v + new string('\\', trailing);
            var needsQuotes = !inQuotes && v.Any(char.IsWhiteSpace);
            sb.Append(inQuotes ? escaped : needsQuotes ? "\"" + escaped + "\"" : v);
        }
        sb.Append(template, last, template.Length - last);

        var args = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        var dir = !string.IsNullOrWhiteSpace(emulator.WorkingDir) ? emulator.WorkingDir : Path.GetDirectoryName(emulator.ExePath) ?? "";
        return new EmulatorCommand(emulator.ExePath, args, dir);
    }
}
