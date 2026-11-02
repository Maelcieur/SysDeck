using System.IO;

namespace SysDeck.Infrastructure;

/// <summary>
/// Emplacements des données de SysDeck, tous sous %AppData%\SysDeck. La variable d'environnement
/// SYSDECK_DATA_DIR permet de pointer ailleurs (tests, profil portable) sans toucher aux vraies données.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string ShortcutsFile => Path.Combine(DataDir, "shortcuts.json");
    public static string PowerUsageFile => Path.Combine(DataDir, "power-usage.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Sous-dossier géré par SysDeck (copies d'icônes, d'images de fond, de polices).</summary>
    public static string Folder(string name) => Path.Combine(DataDir, name);

    private static string ResolveDataDir()
    {
        var overridden = Environment.GetEnvironmentVariable("SYSDECK_DATA_DIR");
        var dir = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysDeck")
            : Path.GetFullPath(overridden);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
