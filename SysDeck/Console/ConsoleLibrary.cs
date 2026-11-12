using System.IO;
using System.Text.Json;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>Contenu du fichier console-games.json.</summary>
public sealed class ConsoleLibraryData
{
    public List<ConsoleGame> Games { get; set; } = new();
    public List<ConsoleEmulator> Emulators { get; set; } = new();
}

/// <summary>
/// Bibliothèque du Mode console : console-games.json dans le dossier de données, images dans
/// console\art. Ajout manuel uniquement. Ne lève jamais d'exception à la lecture/écriture (journalisée).
/// </summary>
public sealed class ConsoleLibrary
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _file;
    private ConsoleLibraryData _data = new();

    public ConsoleLibrary(string? file = null) => _file = file ?? DefaultFile;

    public static string DefaultFile => Path.Combine(AppPaths.DataDir, "console-games.json");

    /// <summary>Cache des jaquettes, fonds et logos.</summary>
    public static string ArtDir => Path.Combine(AppPaths.Folder("console"), "art");

    public IReadOnlyList<ConsoleGame> Games => _data.Games;
    public IReadOnlyList<ConsoleEmulator> Emulators => _data.Emulators;

    public void Load()
    {
        try
        {
            _data = File.Exists(_file) ? Deserialize(File.ReadAllText(_file)) : new();
        }
        catch (Exception ex)
        {
            Log.Warn("Bibliothèque du Mode console illisible", ex);
            _data = new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, Serialize(_data));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Enregistrement de la bibliothèque du Mode console impossible", ex);
        }
    }

    public void Add(ConsoleGame game) => _data.Games.Add(game);
    public bool Remove(string id) => _data.Games.RemoveAll(g => g.Id == id) > 0;

    public void AddEmulator(ConsoleEmulator emulator) => _data.Emulators.Add(emulator);
    public bool RemoveEmulator(string id) => _data.Emulators.RemoveAll(e => e.Id == id) > 0;
    public ConsoleEmulator? FindEmulator(string? id) =>
        string.IsNullOrEmpty(id) ? null : _data.Emulators.FirstOrDefault(e => e.Id == id);

    /// <summary>Jeux triés : dernier joué en premier, jamais joués ensuite par titre.</summary>
    public List<ConsoleGame> Sorted() => Sort(_data.Games);

    public static List<ConsoleGame> Sort(IEnumerable<ConsoleGame> games) =>
        games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
             .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
             .ToList();

    /// <summary>Plateforme affichée d'un jeu : la sienne, sinon celle de l'émulateur, sinon vide (jeu PC).</summary>
    public string PlatformOf(ConsoleGame g) =>
        !string.IsNullOrWhiteSpace(g.Platform) ? g.Platform : FindEmulator(g.EmulatorId)?.Platform ?? "";

    /// <summary>Enregistre une session terminée (début + durée) et sauvegarde.</summary>
    public void MarkPlayed(ConsoleGame game, DateTime start, TimeSpan duration)
    {
        game.LastPlayed = start;
        game.PlayTime += duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        Save();
    }

    public static string Serialize(ConsoleLibraryData data) => JsonSerializer.Serialize(data, Options);

    public static string Serialize(IEnumerable<ConsoleGame> games, IEnumerable<ConsoleEmulator>? emulators = null) =>
        Serialize(new ConsoleLibraryData { Games = games.ToList(), Emulators = emulators?.ToList() ?? new() });

    public static ConsoleLibraryData Deserialize(string json)
    {
        var d = JsonSerializer.Deserialize<ConsoleLibraryData>(json, Options) ?? new();
        d.Games ??= new();
        d.Emulators ??= new();
        foreach (var e in d.Emulators) EmulatorPresets.EnsureFullscreenFlag(e);
        return d;
    }
}

/// <summary>Textes affichés dans l'interface TV (temps de jeu, dernière session).</summary>
public static class ConsoleFormat
{
    public static string PlayTime(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "Jamais lancé";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes} min";
        return $"{(int)t.TotalHours} h {t.Minutes:00} min";
    }

    public static string LastPlayed(DateTime? when, DateTime now)
    {
        if (when is null) return "Jamais joué";
        var days = (now.Date - when.Value.Date).Days;
        return days switch
        {
            <= 0 => "Aujourd'hui",
            1 => "Hier",
            < 30 => $"Il y a {days} jours",
            _ => when.Value.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")),
        };
    }
}
