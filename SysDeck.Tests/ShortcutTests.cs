using System.Text.Json;
using System.Windows.Input;
using SysDeck.Deck;
using SysDeck.Interop;
using SysDeck.Models;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class ShortcutItemTests
{
    [Fact]
    public void HotkeyAndGlyph_RoundTripThroughJson()
    {
        var item = new ShortcutItem { Name = "Muet micro", Hotkey = "Control+Shift+M", IconGlyph = "\uE74F" };
        var back = JsonSerializer.Deserialize<ShortcutItem>(JsonSerializer.Serialize(item))!;
        Assert.Equal("Control+Shift+M", back.Hotkey);
        Assert.Equal("\uE74F", back.IconGlyph);
        Assert.True(back.IsHotkey);
        Assert.Equal("", back.Path);
    }

    [Fact]
    public void OldConfig_WithoutNewFields_StaysNormalApplication()
    {
        var back = JsonSerializer.Deserialize<ShortcutItem>("""{"Name":"Bloc-notes","Path":"C:\notepad.exe","Arguments":"","CustomIconPath":"x.png"}""")!;
        Assert.Null(back.Hotkey);
        Assert.Null(back.IconGlyph);
        Assert.False(back.IsHotkey);
        Assert.Equal("x.png", back.CustomIconPath);
    }

    [Fact]
    public void IsHotkey_IsNotWritten()
        => Assert.DoesNotContain("IsHotkey", JsonSerializer.Serialize(new ShortcutItem { Hotkey = "F5" }));

    [Fact]
    public void DeckPage_KeepsHotkeyTilesInOrder()
    {
        var settings = new DeckSettings();
        settings.Migrate();
        settings.Page.Apps.Add(new ShortcutItem { Name = "App", Path = "a.exe" });
        settings.Page.Apps.Add(new ShortcutItem { Name = "Touche", Hotkey = "MediaPlayPause", IconGlyph = "\U0001F3B5" });
        var back = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!;
        back.Migrate();
        Assert.Equal(new[] { "App", "Touche" }, back.Page.Apps.Select(a => a.Name));
        Assert.Equal("\U0001F3B5", back.Page.Apps[1].IconGlyph);
        Assert.False(back.Page.Apps[0].IsHotkey);
    }
}

public class AppsOpacityTests
{
    [Fact]
    public void Default_IsOpaque_AndAbsentFieldKeepsIt()
    {
        Assert.Equal(1, new DeckSettings().AppsOpacity);
        var old = JsonSerializer.Deserialize<DeckSettings>("""{"Brightness":40}""")!;
        old.Migrate();
        Assert.Equal(1, old.AppsOpacity);
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(-2, 0)]
    [InlineData(0.4, 0.4)]
    [InlineData(double.NaN, 1)]
    public void Migrate_ClampsOpacity(double stored, double expected)
    {
        var settings = new DeckSettings { AppsOpacity = stored };
        settings.Migrate();
        Assert.Equal(expected, settings.AppsOpacity);
    }

    [Fact]
    public void Profile_CapturesAndAppliesOpacity_Clamped()
    {
        var config = new AppConfig();
        config.Deck.Migrate();
        config.Deck.AppsOpacity = 0.3;
        var profile = Profile.Capture(config);
        Assert.Equal(0.3, profile.DeckScreen!.AppsOpacity);

        config.Deck.AppsOpacity = 1;
        profile.ApplyTo(config, includeLayout: false);
        Assert.Equal(0.3, config.Deck.AppsOpacity);

        profile.DeckScreen.AppsOpacity = 7;
        profile.ApplyTo(config, includeLayout: false);
        Assert.Equal(1, config.Deck.AppsOpacity);
    }
}

public class HotkeyParsingTests
{
    [Theory]
    [InlineData("F5", Key.F5)]
    [InlineData("F24", Key.F24)]
    [InlineData("MediaPlayPause", Key.MediaPlayPause)]
    [InlineData("VolumeMute", Key.VolumeMute)]
    public void SingleKey_AcceptedOnlyWithoutModifierRequirement(string text, Key key)
    {
        Assert.Null(HotkeyGesture.Parse(text));
        var gesture = HotkeyGesture.Parse(text, requireModifier: false);
        Assert.Equal(new HotkeyGesture(ModifierKeys.None, key), gesture);
        Assert.Equal(gesture, HotkeyGesture.Parse(gesture!.Serialize(), requireModifier: false));
    }

    [Fact]
    public void Combination_RoundTrips()
    {
        var gesture = new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows, Key.D5);
        Assert.Equal(gesture, HotkeyGesture.Parse(gesture.Serialize(), requireModifier: false));
        Assert.Equal(gesture, HotkeyGesture.Parse(gesture.Serialize()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Pasunetouche")]
    public void Garbage_GivesNull(string? text) => Assert.Null(HotkeyGesture.Parse(text, requireModifier: false));

    [Fact]
    public void Sequence_PressesModifiersFirst_ReleasesInReverse()
    {
        var steps = InputSimulator.HotkeySequence(new HotkeyGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.M));
        Assert.Equal(new (ushort, bool, bool)[]
        {
            (0x11, false, false), (0x12, false, false), (0x4D, false, false),
            (0x4D, true, false), (0x12, true, false), (0x11, true, false)
        }, steps);
    }

    [Fact]
    public void Sequence_MediaKeyAlone_AndExtendedKey()
    {
        Assert.Equal(new (ushort, bool, bool)[] { (0xB3, false, false), (0xB3, true, false) },
            InputSimulator.HotkeySequence(new HotkeyGesture(ModifierKeys.None, Key.MediaPlayPause)));
        Assert.All(InputSimulator.HotkeySequence(new HotkeyGesture(ModifierKeys.None, Key.Left)), s => Assert.True(s.Extended));
    }

    [Fact]
    public void Sequence_UnsendableKey_Throws()
        => Assert.Throws<InvalidOperationException>(() => InputSimulator.HotkeySequence(new HotkeyGesture(ModifierKeys.Control, Key.None)));
}
