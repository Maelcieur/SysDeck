using System.IO;
using SysDeck.Deck;
using SysDeck.Models;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class SwitchThemeTests
{
    private static DeckSettings Settings()
    {
        var s = new DeckSettings();
        s.Migrate();
        s.Page.Elements.Add(new DeckElement { Id = "cpu", X = 10, Y = 20, Width = 300, Height = 200 });
        s.Page.Elements.Add(new DeckElement { Id = "apps", X = 400, Y = 20 });
        s.Page.Apps.Add(new ShortcutItem { Name = "Notepad" });
        s.Page.BackgroundColor = "#FF112233";
        s.Modules["cpu"] = new ModuleAppearance();
        return s;
    }

    [Fact]
    public void CaptureThenApply_RestoresPositionsLookAndAppearance_KeepsTiles()
    {
        var s = Settings();
        var theme = SwitchTheme.Capture(s, "Bleu");

        s.Page.Elements[0].X = 999;
        s.Page.BackgroundColor = "#FF000000";
        s.Modules["cpu"] = new ModuleAppearance { Width = 999 };

        theme.ApplyTo(s);
        Assert.Equal(10, s.Find("cpu")!.X);
        Assert.Equal("#FF112233", s.Page.BackgroundColor);
        Assert.NotEqual(999, s.Modules["cpu"].Width);
        Assert.Single(s.Page.Apps); // les tuiles de la page restent

        // Le thème est une copie : modifier la page ensuite ne le change pas.
        s.Page.Elements[0].X = 5;
        Assert.Equal(10, theme.Elements[0].X);
    }

    [Fact]
    public void Studio_End_PutsEverythingBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sysdeck-studio-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = Settings();
        var studio = new SwitchThemeStudio();
        studio.Begin(s, "Essai");
        s.Page.Elements[0].X = 777;
        s.Page.BackgroundColor = "#FFFFFFFF";
        s.Modules["gpu"] = new ModuleAppearance();
        s.Pages.Add(new DeckPage { Name = "Brouillon" });

        studio.End(s);
        Assert.False(studio.Active);
        Assert.Equal(10, s.Find("cpu")!.X);
        Assert.Equal("#FF112233", s.Page.BackgroundColor);
        Assert.False(s.Modules.ContainsKey("gpu"));
        Assert.Single(s.Pages);
        Assert.False(File.Exists(SwitchThemeStudio.BackupPath));
    }

    [Fact]
    public void Store_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sysdeck-themes-{Guid.NewGuid()}.json");
        try
        {
            SwitchThemeStore.Save(new List<SwitchTheme> { SwitchTheme.Capture(Settings(), "Rouge") }, path);
            var loaded = SwitchThemeStore.Load(path);
            Assert.Equal("Rouge", Assert.Single(loaded).Name);
            Assert.Equal(2, loaded[0].Elements.Count);
            Assert.True(loaded[0].Modules.ContainsKey("cpu"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
