using System.IO;
using System.Runtime.CompilerServices;

namespace SysDeck.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Avant tout test : les données de SysDeck (journal, icônes, fonds…) vont dans un dossier temporaire,
    /// jamais dans le vrai %AppData%\SysDeck de l'utilisateur. Doit précéder le premier accès à AppPaths.
    /// </summary>
#pragma warning disable CA2255 // initialiseur de module voulu : xUnit 2 n'a pas de fixture d'assemblage
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void IsolateDataDir()
        => Environment.SetEnvironmentVariable("SYSDECK_DATA_DIR", Path.Combine(Path.GetTempPath(), "sysdeck-tests-data"));
}
