using System.IO;
using System.Text.Json;
using SysDeck.Infrastructure;
using SysDeck.Services;

namespace SysDeck.Deck;

/// <summary>
/// Thème de la Switch créé dans l'atelier : position et taille des éléments d'une page, apparence de la page
/// (fond, tuiles, horloge) et apparence de chaque module affiché. Les tuiles (applications) n'en font pas partie.
/// </summary>
public sealed class SwitchTheme
{
    public string Name { get; set; } = "Thème";
    public DateTime Created { get; set; } = DateTime.Now;
    public List<DeckElement> Elements { get; set; } = new();
    /// <summary>Apparence de la page (seuls les champs de <see cref="DeckPage.CopyLookFrom"/> servent).</summary>
    public DeckPage Look { get; set; } = new();
    public Dictionary<string, ModuleAppearance> Modules { get; set; } = new();

    public override string ToString() => Name;

    /// <summary>Thème pris sur la page affichée de la Switch (copie : la Switch peut ensuite changer sans le toucher).</summary>
    public static SwitchTheme Capture(DeckSettings settings, string name)
    {
        var page = settings.Page;
        var look = new DeckPage { OwnLook = true };
        look.CopyLookFrom(page);
        var theme = new SwitchTheme { Name = name, Elements = SettingsCopier.Clone(page.Elements), Look = look };
        foreach (var element in page.Elements.Where(e => e.Visible))
            if (settings.Modules.TryGetValue(element.Id, out var appearance))
                theme.Modules[element.Id] = appearance.Clone();
        return theme;
    }

    /// <summary>
    /// Applique le thème à la page affichée : mêmes éléments aux mêmes places, même apparence de page, et
    /// apparence des modules du thème (les autres modules gardent la leur). Les tuiles de la page sont gardées.
    /// </summary>
    public void ApplyTo(DeckSettings settings)
    {
        var page = settings.Page;
        page.Elements = SettingsCopier.Clone(Elements);
        page.Elements.RemoveAll(e => e.Id == "shortcuts");
        page.CopyLookFrom(Look);
        page.OwnLook = true;
        page.ClampLook();
        foreach (var (id, appearance) in Modules) settings.Modules[id] = appearance.Clone();
    }
}

/// <summary>Thèmes Switch enregistrés, dans switch-themes.json.</summary>
public static class SwitchThemeStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string FilePath => Path.Combine(AppPaths.DataDir, "switch-themes.json");

    public static List<SwitchTheme> Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<SwitchTheme>>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Thèmes Switch illisibles ({ex.Message})");
            return new();
        }
    }

    public static void Save(List<SwitchTheme> themes, string? path = null)
    {
        path ??= FilePath;
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(themes, Options));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Atelier de thème : la vraie Switch est mise de côté (copie de toutes les pages et des apparences de modules,
/// aussi écrite sur le disque en cas d'arrêt brutal), on modifie librement l'aperçu, puis on remet tout comme avant.
/// </summary>
public sealed class SwitchThemeStudio
{
    private sealed class Snapshot
    {
        public List<DeckPage> Pages { get; set; } = new();
        public int CurrentPage { get; set; }
        public Dictionary<string, ModuleAppearance> Modules { get; set; } = new();
    }

    public static string BackupPath => Path.Combine(AppPaths.DataDir, "switch-theme-studio.json");

    private Snapshot? _snapshot;

    public bool Active => _snapshot is not null;

    /// <summary>Nom du thème en cours de création (ou de modification).</summary>
    public string Name { get; set; } = "";

    /// <summary>Thème enregistré qu'on modifie (null pour un nouveau thème).</summary>
    public SwitchTheme? Editing { get; set; }

    public void Begin(DeckSettings settings, string name, SwitchTheme? editing = null)
    {
        _snapshot = new Snapshot
        {
            Pages = SettingsCopier.Clone(settings.Pages),
            CurrentPage = settings.CurrentPage,
            Modules = settings.Modules.ToDictionary(kv => kv.Key, kv => kv.Value.Clone())
        };
        Name = name;
        Editing = editing;
        try
        {
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(_snapshot));
        }
        catch (Exception ex)
        {
            Log.Warn($"Atelier de thème : copie de secours impossible ({ex.Message})");
        }
        editing?.ApplyTo(settings);
    }

    /// <summary>Remet la Switch exactement comme avant l'atelier.</summary>
    public void End(DeckSettings settings)
    {
        if (_snapshot is null) return;
        Restore(settings, _snapshot);
        _snapshot = null;
        Editing = null;
        try { File.Delete(BackupPath); } catch { /* sans gravité : relu puis supprimé au prochain lancement */ }
    }

    /// <summary>Au démarrage : un atelier resté ouvert (arrêt brutal) est annulé, la Switch retrouve son état d'avant.</summary>
    public static bool RecoverAfterCrash(DeckSettings settings)
    {
        if (!File.Exists(BackupPath)) return false;
        try
        {
            if (JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(BackupPath)) is { Pages.Count: > 0 } snapshot)
            {
                Restore(settings, snapshot);
                Log.Info("Atelier de thème resté ouvert : la Switch retrouve son état d'avant");
            }
            File.Delete(BackupPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Atelier de thème : copie de secours illisible ({ex.Message})");
            return false;
        }
    }

    private static void Restore(DeckSettings settings, Snapshot snapshot)
    {
        settings.Pages = SettingsCopier.Clone(snapshot.Pages);
        settings.CurrentPage = Math.Clamp(snapshot.CurrentPage, 0, settings.Pages.Count - 1);
        settings.Modules = snapshot.Modules.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
    }
}
