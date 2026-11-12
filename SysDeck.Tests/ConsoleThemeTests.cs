using SysDeck.ConsoleMode;
using Xunit;

namespace SysDeck.Tests;

public class ConsoleThemeTests
{
    [Fact]
    public void Themes_HaveUniqueIds_AndDefaultFallback()
    {
        Assert.True(ConsoleThemes.All.Count >= 8);
        Assert.Equal(ConsoleThemes.All.Count, ConsoleThemes.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal("nuit", ConsoleThemes.Default.Id);
        Assert.Same(ConsoleThemes.Default, ConsoleThemes.Find("inconnu"));
        Assert.Same(ConsoleThemes.Default, ConsoleThemes.Find(null));
        Assert.Equal(0, ConsoleThemes.IndexOf("inconnu"));
    }

    [Fact]
    public void LightThemes_UseDarkText()
    {
        foreach (var t in ConsoleThemes.All.Where(t => t.Light))
            Assert.True(t.Text.R + t.Text.G + t.Text.B < 300, t.Id);
    }

    [Fact]
    public void IntroSound_IsStereo_AndBounded()
    {
        var pcm = IntroSound.Build(11.5);
        Assert.Equal((int)(11.5 * IntroSound.Rate) * 2, pcm.Length);
        Assert.All(pcm.Where((_, i) => i % 997 == 0), v => Assert.InRange(v, -1f, 1f));
        Assert.Contains(pcm, v => Math.Abs(v) > 0.01f);
    }

    [Fact]
    public void ConsoleSettings_Defaults()
    {
        var s = new SysDeck.Services.ConsoleSettings();
        Assert.Equal("nuit", s.ThemeId);
        Assert.True(s.IntroEnabled);
        Assert.Equal("", s.IntroFolder);
    }

    [Fact]
    public void Emulator_FullscreenCheckbox_AddsOrRemovesTheFlag()
    {
        var e = new ConsoleEmulator { ArgumentsTemplate = "-g \"{rom}\"", FullscreenFlag = "-f", Fullscreen = true };
        Assert.Equal("-f -g \"{rom}\"", EmulatorCommandBuilder.WithFullscreen(e));
        e.ArgumentsTemplate = "-f -g \"{rom}\"";
        Assert.Equal("-f -g \"{rom}\"", EmulatorCommandBuilder.WithFullscreen(e));   // déjà présente : pas de doublon
        e.Fullscreen = false;
        Assert.Equal("-g \"{rom}\"", EmulatorCommandBuilder.WithFullscreen(e));
        e.FullscreenFlag = "";
        Assert.Equal("-f -g \"{rom}\"", EmulatorCommandBuilder.WithFullscreen(e));   // option inconnue : modèle intact
    }

    [Fact]
    public void Emulator_KnownPresetGetsItsFlag()
    {
        var e = new ConsoleEmulator { Name = "PCSX2", ExePath = @"C:\nulle\part\pcsx2.exe" };
        EmulatorPresets.EnsureFullscreenFlag(e);
        Assert.Equal("-fullscreen", e.FullscreenFlag);
        var game = new ConsoleGame { RomPath = @"C:\Jeux\a b.iso" };
        e.ArgumentsTemplate = "-nogui \"{rom}\"";
        Assert.Equal("-fullscreen -nogui \"C:\\Jeux\\a b.iso\"", EmulatorCommandBuilder.BuildEmulatorCommand(e, game).Arguments);
    }

    [Fact]
    public void Themes_AreTwenty_AndEveryAmbientIsUsedOrNone()
    {
        Assert.Equal(21, ConsoleThemes.All.Count);
        foreach (ConsoleAmbient a in Enum.GetValues(typeof(ConsoleAmbient)))
            if (a != ConsoleAmbient.None) Assert.Contains(ConsoleThemes.All, t => t.Ambient == a);
    }

    [Fact]
    public void UiSounds_AreShortStereoClips_WithinRange()
    {
        foreach (UiSound s in Enum.GetValues(typeof(UiSound)))
        {
            var clip = UiSounds.Build(s);
            Assert.True(clip.Length > 0 && clip.Length % 2 == 0, s.ToString());
            Assert.True(clip.Length / 2 <= 48000, s.ToString());   // une seconde au plus
            Assert.All(clip, v => Assert.InRange(v, -1f, 1f));
        }
    }

    [Fact]
    public void IntroStyle_AutoFollowsGlitchTheme()
    {
        Assert.True(ConsoleIntro.IsCyber("auto", ConsoleThemes.Find("glitch")));
        Assert.False(ConsoleIntro.IsCyber("auto", ConsoleThemes.Find("nuit")));
        Assert.True(ConsoleIntro.IsCyber("cyber", ConsoleThemes.Find("nuit")));
        Assert.False(ConsoleIntro.IsCyber("elegant", ConsoleThemes.Find("glitch")));
    }

    [Fact]
    public void CyberSounds_AreValid()
    {
        foreach (UiSound s in Enum.GetValues(typeof(UiSound)))
        {
            var clip = UiSounds.BuildCyber(s);
            Assert.True(clip.Length > 0 && clip.Length / 2 <= 48000, s.ToString());
            Assert.All(clip, v => Assert.InRange(v, -1f, 1f));
        }
        var intro = IntroSound.BuildCyber();
        Assert.Contains(intro, v => Math.Abs(v) > 0.01f);
    }
}
