using System.IO;
using SysDeck.ConsoleMode;
using Xunit;

namespace SysDeck.Tests;

public class ConsoleModeTests
{
    [Fact]
    public void Library_RoundTrip_WithEmulators()
    {
        var emu = new ConsoleEmulator { Name = "Dolphin", ExePath = @"C:\Dolphin\Dolphin.exe", Platform = "GameCube" };
        var g = new ConsoleGame
        {
            Title = "Melee", EmulatorId = emu.Id, RomPath = @"D:\roms\melee.iso",
            LastPlayed = new DateTime(2026, 11, 1, 20, 0, 0), PlayTime = TimeSpan.FromMinutes(95), SteamAppId = "12",
        };
        var json = ConsoleLibrary.Serialize(new[] { g }, new[] { emu });
        var back = ConsoleLibrary.Deserialize(json);
        Assert.Single(back.Games);
        Assert.Single(back.Emulators);
        Assert.Equal(TimeSpan.FromMinutes(95), back.Games[0].PlayTime);
        Assert.Equal(emu.Id, back.Games[0].EmulatorId);
        Assert.Equal("Dolphin", back.Emulators[0].Name);
    }

    [Fact]
    public void Library_SaveAndLoad_File()
    {
        var file = Path.Combine(Path.GetTempPath(), "sysdeck-console-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var lib = new ConsoleLibrary(file);
            lib.Add(new ConsoleGame { Title = "A" });
            lib.Save();
            var lib2 = new ConsoleLibrary(file);
            lib2.Load();
            Assert.Equal("A", Assert.Single(lib2.Games).Title);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Sort_LastPlayedFirst_ThenTitle()
    {
        var list = ConsoleLibrary.Sort(new[]
        {
            new ConsoleGame { Title = "Zeta" },
            new ConsoleGame { Title = "Alpha" },
            new ConsoleGame { Title = "Old", LastPlayed = new DateTime(2026, 1, 1) },
            new ConsoleGame { Title = "Recent", LastPlayed = new DateTime(2026, 11, 1) },
        });
        Assert.Equal(new[] { "Recent", "Old", "Alpha", "Zeta" }, list.Select(g => g.Title));
    }

    [Fact]
    public void CleanHtml_StripsTagsAndEntities()
    {
        Assert.Equal("Un jeu \"fou\" & rapide. Suite", ArtworkService.CleanHtml("<b>Un jeu</b> &quot;fou&quot; &amp; rapide.<br>Suite"));
        Assert.Equal("", ArtworkService.CleanHtml(null));
    }

    [Fact]
    public void ParseSteam_SearchAndDescription()
    {
        var found = ArtworkService.ParseSteamSearch("{\"total\":1,\"items\":[{\"type\":\"app\",\"name\":\"Portal 2\",\"id\":620}]}");
        Assert.Equal(new ArtworkCandidate("Portal 2", ArtworkSource.Steam, "620"), Assert.Single(found));
        var d = ArtworkService.ParseSteamDescription("{\"620\":{\"success\":true,\"data\":{\"short_description\":\"A &amp; <i>B</i>\"}}}", "620");
        Assert.Equal("A & B", d);
        Assert.Empty(ArtworkService.ParseSteamSearch("pas du json"));
    }

    [Fact]
    public void ParseGrid_SearchAndUrls()
    {
        var c = ArtworkService.ParseGridSearch("{\"success\":true,\"data\":[{\"id\":5,\"name\":\"X\"}]}");
        Assert.Equal(ArtworkSource.SteamGridDb, Assert.Single(c).Source);
        var u = ArtworkService.ParseGridImageUrls("{\"data\":[{\"url\":\"https://a/b.png\"},{\"url\":\"x\"}]}");
        Assert.Equal(new[] { "https://a/b.png" }, u);
    }

    private static byte[] SwitchReport(byte battery, byte right = 0, byte shared = 0, byte left = 0, int sx = 2048, int sy = 2048)
    {
        var r = new byte[64];
        r[0] = 0x30; r[2] = battery; r[3] = right; r[4] = shared; r[5] = left;
        r[6] = (byte)(sx & 0xFF); r[7] = (byte)(((sx >> 8) & 0x0F) | ((sy & 0x0F) << 4)); r[8] = (byte)(sy >> 4);
        return r;
    }

    [Fact]
    public void SwitchPro_Report30_ButtonsAndBattery()
    {
        // batterie pleine (8) en charge : quartet haut = 0x9 ; A (0x08) + Plus (0x02) + Haut (0x02)
        var inp = GamepadDecoder.ParseSwitchReport(SwitchReport(0x90, right: 0x08, shared: 0x02, left: 0x02), 64, ControllerKind.SwitchPro);
        Assert.NotNull(inp);
        Assert.Equal(PadButtons.A | PadButtons.Menu | PadButtons.Up, inp!.Buttons);
        Assert.Equal(100, inp.BatteryPercent);
        Assert.True(inp.Charging);
        Assert.False(inp.Wired);
        var half = GamepadDecoder.ParseSwitchReport(SwitchReport(0x40), 64, ControllerKind.SwitchPro)!;
        Assert.Equal(50, half.BatteryPercent);
        Assert.False(half.Charging);
    }

    [Fact]
    public void SwitchPro_StickAndOtherReports()
    {
        var left = GamepadDecoder.ParseSwitchReport(SwitchReport(0x80, sx: 200), 64, ControllerKind.SwitchPro)!;
        Assert.Equal(PadButtons.Left, left.Buttons);
        var simple = new byte[64]; simple[0] = 0x3F;
        Assert.Null(GamepadDecoder.ParseSwitchReport(simple, 64, ControllerKind.SwitchPro));
    }

    [Fact]
    public void XInput_BatteryAndButtons()
    {
        Assert.Equal((100, false), GamepadDecoder.FromXInputBattery(3, 3));
        Assert.Equal((25, false), GamepadDecoder.FromXInputBattery(3, 1));
        Assert.Equal((5, false), GamepadDecoder.FromXInputBattery(2, 0));
        Assert.Equal((-1, true), GamepadDecoder.FromXInputBattery(1, 3));
        Assert.Equal(4, GamepadDecoder.BatteryBars(100));
        Assert.Equal(3, GamepadDecoder.BatteryBars(60));
        Assert.Equal(1, GamepadDecoder.BatteryBars(5));
        Assert.Equal(PadButtons.A | PadButtons.Up, GamepadDecoder.FromXInput(GamepadDecoder.XA | GamepadDecoder.XDpadUp, 0, 0));
        Assert.Equal(PadButtons.Right, GamepadDecoder.FromXInput(0, 30000, 0));
    }

    [Fact]
    public void EmulatorCommand_QuotesPathsWithSpaces()
    {
        var emu = new ConsoleEmulator { ExePath = @"C:\Emu\dolphin.exe", ArgumentsTemplate = "-b -e \"{rom}\"" };
        var g = new ConsoleGame { RomPath = @"D:\Mes jeux\Super Smash.iso" };
        var c = EmulatorCommandBuilder.BuildEmulatorCommand(emu, g);
        Assert.Equal("-b -e \"D:\\Mes jeux\\Super Smash.iso\"", c.Arguments);
        Assert.Equal(@"C:\Emu", c.WorkingDir);
    }

    [Fact]
    public void EmulatorCommand_UnquotedTokenGetsQuotes_AndCoreAndNames()
    {
        var emu = new ConsoleEmulator { ExePath = @"C:\r\retroarch.exe", ArgumentsTemplate = "-L {core} {rom} --name {romName} --dir \"{romDir}\"", Core = @"C:\cores\a b.dll", WorkingDir = @"C:\r" };
        var g = new ConsoleGame { RomPath = @"C:\roms\zelda.gba" };
        var c = EmulatorCommandBuilder.BuildEmulatorCommand(emu, g);
        Assert.Equal("-L \"C:\\cores\\a b.dll\" C:\\roms\\zelda.gba --name zelda --dir \"C:\\roms\"", c.Arguments);
        Assert.Equal(@"C:\r", c.WorkingDir);
    }

    [Fact]
    public void EmulatorCommand_TrailingBackslashAndMissingTokens()
    {
        var emu = new ConsoleEmulator { ExePath = @"C:\e.exe", ArgumentsTemplate = "--dir \"{romDir}\" {core} {inconnu}" };
        var g = new ConsoleGame { RomPath = @"C:\a.iso" };
        var c = EmulatorCommandBuilder.BuildEmulatorCommand(emu, g);
        Assert.Equal("--dir \"C:\\\\\" {inconnu}", c.Arguments);
        var perGame = EmulatorCommandBuilder.BuildEmulatorCommand(
            new ConsoleEmulator { ExePath = "x", ArgumentsTemplate = "-L \"{core}\"", Core = "A" }, new ConsoleGame { Core = "B" });
        Assert.Equal("-L \"B\"", perGame.Arguments);
    }

    [Fact]
    public void Presets_OnlyRetroArchHasCore()
    {
        Assert.All(EmulatorPresets.All, p => Assert.Equal(p.Name == "RetroArch", p.ArgumentsTemplate.Contains("{core}")));
    }

    [Fact]
    public void Steam_ParsingHelpers()
    {
        Assert.Equal("620", ConsoleGame.ParseSteamAppId("steam://rungameid/620"));
        Assert.Null(ConsoleGame.ParseSteamAppId("epic://x"));
        Assert.Equal("Portal 2", SteamLocator.ParseInstallDir("\"AppState\"\n{\n\t\"installdir\"\t\t\"Portal 2\"\n}"));
        Assert.Equal(@"D:\SteamLibrary", Assert.Single(SteamLocator.ParseLibraryPaths("\"path\"\t\t\"D:\\\\SteamLibrary\"")));
    }

    [Fact]
    public void Format_PlayTimeAndLastPlayed()
    {
        Assert.Equal("1 h 05 min", ConsoleFormat.PlayTime(TimeSpan.FromMinutes(65)));
        Assert.Equal("12 min", ConsoleFormat.PlayTime(TimeSpan.FromMinutes(12)));
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        Assert.Equal("Hier", ConsoleFormat.LastPlayed(now.AddDays(-1), now));
        Assert.Equal("Jamais joué", ConsoleFormat.LastPlayed(null, now));
    }
}
