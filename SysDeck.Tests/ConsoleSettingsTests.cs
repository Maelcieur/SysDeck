using System.Text.Json;
using SysDeck.Interop;
using SysDeck.Models;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class ConsoleSettingsTests
{
    [Fact]
    public void Defaults_AreSafe()
    {
        var settings = new ConsoleSettings();
        Assert.Equal("", settings.TvTargetId);
        Assert.Equal("", settings.TvAudioDeviceId);
        Assert.False(settings.DisableTvWhenIdle);
        Assert.Equal("", settings.SteamGridDbKey);
        Assert.Equal("Ctrl+Alt+End", settings.EmergencyHotkey);
        Assert.NotNull(HotkeyGesture.Parse(settings.EmergencyHotkey));
    }

    [Fact]
    public void RoundTrip_KeepsValues_AndEncryptsTheKey()
    {
        var config = new AppConfig();
        config.Console.TvTargetId = "0000000000000001|4321|\\\\?\\DISPLAY#TV";
        config.Console.TvAudioDeviceId = "{0.0.0.00000000}.{abc}";
        config.Console.DisableTvWhenIdle = true;
        config.Console.SteamGridDbKey = "secret-key";
        config.Console.EmergencyHotkey = "Control+Alt+F12";

        var json = JsonSerializer.Serialize(config);
        Assert.DoesNotContain("secret-key", json); // jamais en clair dans config.json
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal(config.Console.TvTargetId, back.Console.TvTargetId);
        Assert.Equal(config.Console.TvAudioDeviceId, back.Console.TvAudioDeviceId);
        Assert.True(back.Console.DisableTvWhenIdle);
        Assert.Equal("secret-key", back.Console.SteamGridDbKey);
        Assert.Equal("Control+Alt+F12", back.Console.EmergencyHotkey);
    }

    [Fact]
    public void OldConfig_WithoutConsole_GetsDefaults()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"DockIconSize":40}""")!;
        Assert.NotNull(config.Console);
        Assert.False(config.Console.DisableTvWhenIdle);
        Assert.Equal(ConsoleSettings.DefaultEmergencyHotkey, config.Console.EmergencyHotkey);
    }

    [Fact]
    public void MigrateLegacySettings_RepairsNullConsoleValues()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"Console":{"TvTargetId":null,"TvAudioDeviceId":null,"EmergencyHotkey":""}}""")!;
        config.MigrateLegacySettings();
        Assert.Equal("", config.Console.TvTargetId);
        Assert.Equal("", config.Console.TvAudioDeviceId);
        Assert.Equal(ConsoleSettings.DefaultEmergencyHotkey, config.Console.EmergencyHotkey);

        var nullConsole = JsonSerializer.Deserialize<AppConfig>("""{"Console":null}""")!;
        nullConsole.MigrateLegacySettings();
        Assert.NotNull(nullConsole.Console);
    }

    [Fact]
    public void ConsoleModeTile_IsLastKind_AndRoundTripsAsText()
    {
        var kinds = Enum.GetValues<TileKind>();
        Assert.Equal(TileKind.ConsoleMode, kinds[^1]);
        Assert.Equal((int)TileKind.TilePageNavigation + 1, (int)TileKind.ConsoleMode); // les valeurs existantes ne bougent pas

        var json = JsonSerializer.Serialize(new ShortcutItem { Name = "Mode console", Kind = TileKind.ConsoleMode });
        Assert.Contains("\"ConsoleMode\"", json);
        Assert.Equal(TileKind.ConsoleMode, JsonSerializer.Deserialize<ShortcutItem>(json)!.Kind);
    }

    [Fact]
    public void ConsoleTile_IsHighlightedOnlyWhileActive()
    {
        var tile = new ShortcutItem { Kind = TileKind.ConsoleMode, Name = "Mode console" };
        try
        {
            TileRuntime.ConsoleActive = false;
            Assert.Equal(IntegrationLook.Normal, TileRuntime.IntegrationLookOf(tile));
            TileRuntime.ConsoleActive = true;
            Assert.Equal(IntegrationLook.Highlight, TileRuntime.IntegrationLookOf(tile));
            Assert.False(TileRuntime.IntegrationOffline(tile));
            Assert.Equal(TileRuntime.ConsoleGlyph, TileRuntime.ForDisplay(tile).IconGlyph);
        }
        finally
        {
            TileRuntime.ConsoleActive = false;
        }
    }
}
