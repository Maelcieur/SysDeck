using System.Text.Json;
using SysDeck.Controls;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class ButtonStyleTests
{
    [Fact]
    public void Default_IsOriginalRendering()
    {
        var style = new ButtonPartStyle();
        Assert.True(style.IsDefault);
        Assert.True(style.Visible);
        Assert.Null(style.Glyph);
        Assert.Null(style.Background);
        Assert.Null(style.Foreground);
        Assert.Null(style.HoverBackground);
        Assert.Empty(new ModuleAppearance().Buttons);
    }

    [Fact]
    public void OldConfig_WithoutButtons_GivesEmptyDictionary()
    {
        var old = JsonSerializer.Deserialize<ModuleAppearance>("""{"AutoSize":true,"Width":300,"Texts":{"title":{"Hidden":true}}}""")!;
        Assert.NotNull(old.Buttons);
        Assert.Empty(old.Buttons);
        Assert.True(old.Texts["title"].Hidden);
    }

    [Fact]
    public void NullButtonsInFile_AreReplacedByMigration()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"Modules":{"media":{"Buttons":null}},"AudioDevices":null,"AppIcons":null}""")!;
        config.MigrateLegacySettings();
        Assert.Empty(config.Modules["media"].Buttons);
        Assert.Empty(config.AudioDevices);
        Assert.Empty(config.AppIcons);
    }

    [Fact]
    public void Json_RoundTrip_KeepsButtons()
    {
        var a = new ModuleAppearance();
        var play = a.GetButton("play");
        play.Background = "#FF112233";
        play.Foreground = "#FFFFFFFF";
        play.HoverBackground = "#FF445566";
        play.Glyph = "\uE768";
        a.GetButton("hide").Hidden = true;

        var back = JsonSerializer.Deserialize<ModuleAppearance>(JsonSerializer.Serialize(a))!;
        Assert.Equal("#FF112233", back.Buttons["play"].Background);
        Assert.Equal("#FFFFFFFF", back.Buttons["play"].Foreground);
        Assert.Equal("#FF445566", back.Buttons["play"].HoverBackground);
        Assert.Equal("\uE768", back.Buttons["play"].Glyph);
        Assert.True(back.Buttons["hide"].Hidden);
        Assert.False(back.Buttons["hide"].Visible);
        Assert.False(back.Buttons["play"].IsDefault);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var a = new ModuleAppearance();
        a.GetButton("next").Background = "#FF000000";
        var copy = a.Clone();
        copy.GetButton("next").Background = "#FFFFFFFF";
        copy.GetButton("previous").Hidden = true;

        Assert.Equal("#FF000000", a.Buttons["next"].Background);
        Assert.False(a.Buttons.ContainsKey("previous"));
        Assert.Equal("#FFFFFFFF", copy.Buttons["next"].Background);
    }

    [Fact]
    public void CopyInto_ReplacesButtonsOnSameInstance()
    {
        var source = new ModuleAppearance();
        source.GetButton("hide").Glyph = "\U0001F5D1";
        var target = new ModuleAppearance();
        target.GetButton("old").Hidden = true;
        var raised = new List<string?>();
        target.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        SettingsCopier.CopyInto(source, target);

        Assert.Single(target.Buttons);
        Assert.Equal("\U0001F5D1", target.Buttons["hide"].Glyph);
        Assert.Contains(nameof(ModuleAppearance.Buttons), raised);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var style = new ButtonPartStyle { Hidden = true, Glyph = "x", Background = "#1", Foreground = "#2", HoverBackground = "#3" };
        style.Reset();
        Assert.True(style.IsDefault);

        var a = new ModuleAppearance();
        a.GetButton("play").Background = "#FF000000";
        var raised = false;
        a.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(ModuleAppearance.Buttons);
        a.ResetButtons();
        Assert.Empty(a.Buttons);
        Assert.True(raised);
    }

    [Fact]
    public void EmptyStrings_AreStoredAsNull()
    {
        var style = new ButtonPartStyle { Background = "", Glyph = "  " };
        Assert.Null(style.Background);
        Assert.Null(style.Glyph);
        Assert.True(style.IsDefault);
    }

    [Fact]
    public void ButtonTable_CoversButtonedModules_WithUniqueKeys()
    {
        foreach (var (module, parts) in ButtonPart.ByModule)
        {
            Assert.True(ModuleCapabilities.HasButtons(module));
            Assert.Equal(parts.Length, parts.Select(p => p.Key).Distinct().Count());
            Assert.All(parts, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));
        }
        Assert.Equal(new[] { "audio", "media", "volumemixer" }, ButtonPart.ByModule.Keys.OrderBy(k => k));
        Assert.Contains(ButtonPart.ByModule["media"], p => p.Key == "play" && p.Disc && !p.HasHover);
    }

    [Theory]
    [InlineData("\uE768", false)]
    [InlineData("\U0001F50A", false)]
    [InlineData(@"C:\Users\x\Icons\a.png", true)]
    public void IsImagePath_TellsFilesFromGlyphs(string value, bool expected)
        => Assert.Equal(expected, ShortcutIcons.IsImagePath(value));
}

public class AudioDeviceCustomTests
{
    [Fact]
    public void ResolveName_UsesShortNameWhenSet()
    {
        var custom = new AudioDeviceCustom { Name = "  Casque " };
        Assert.Equal("Casque", AudioDeviceCustom.ResolveName("Haut-parleurs (Realtek High Definition Audio)", custom));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveName_FallsBackToOriginal(string? shortName)
    {
        const string original = "Casque (USB Audio Device)";
        Assert.Equal(original, AudioDeviceCustom.ResolveName(original, new AudioDeviceCustom { Name = shortName }));
        Assert.Equal(original, AudioDeviceCustom.ResolveName(original, null));
    }

    [Fact]
    public void DeviceInfo_DisplayName_AndIconFlag()
    {
        var plain = new AudioDeviceInfo("{id}", "Enceintes (Realtek)", false);
        Assert.Equal("Enceintes (Realtek)", plain.DisplayName);
        Assert.False(plain.HasIcon);

        var custom = plain with { ShortName = "Enceinte", Icon = "\uE7F5" };
        Assert.Equal("Enceinte", custom.DisplayName);
        Assert.True(custom.HasIcon);
        Assert.Equal("Enceintes (Realtek)", custom.Name); // le nom d'origine reste disponible
        Assert.NotEqual(plain, custom); // un changement de nom court déclenche le rafraîchissement du widget
    }

    [Fact]
    public void OldConfig_WithoutNewFields_GivesEmptyDictionaries()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"HiddenAudioDeviceIds":["a"],"DockIconSize":40}""")!;
        config.MigrateLegacySettings();
        Assert.Empty(config.AudioDevices);
        Assert.Empty(config.AppIcons);
        Assert.Equal(new[] { "a" }, config.HiddenAudioDeviceIds);
    }

    [Fact]
    public void Json_RoundTrip_KeepsDevicesAndAppIcons()
    {
        var config = new AppConfig();
        config.AudioDevices["{0.0.0.00000000}.{abc}"] = new AudioDeviceCustom { Name = "Casque", Icon = "\uE7F6", IconColor = "#FF00E0FF", OriginalName = "Casque (USB)" };
        config.AppIcons["spotify"] = new AppIconCustom { Icon = "\U0001F3B5", DisplayName = "Spotify" };

        var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
        var device = back.AudioDevices["{0.0.0.00000000}.{abc}"];
        Assert.Equal("Casque", device.Name);
        Assert.Equal("\uE7F6", device.Icon);
        Assert.Equal("#FF00E0FF", device.IconColor);
        Assert.Equal("Casque (USB)", device.OriginalName);
        Assert.Equal("\U0001F3B5", back.AppIcons["spotify"].Icon);
        Assert.False(device.IsEmpty);
        Assert.DoesNotContain("IsEmpty", JsonSerializer.Serialize(device));
        Assert.True(new AudioDeviceCustom { OriginalName = "x" }.IsEmpty);
    }

    [Fact]
    public void SessionInfo_IconFlags_FollowCustomIcon()
    {
        var plain = new AudioSessionInfo(1, "Spotify", "Spotify", null, 50, false, "d");
        Assert.Equal("spotify", plain.AppKey);
        Assert.False(plain.HasCustomIcon);
        Assert.False(plain.ShowAutoLogo);   // pas de logo trouvé
        Assert.True(plain.ShowFallbackGlyph);

        var custom = plain with { CustomIcon = "\uE8D6" };
        Assert.True(custom.HasCustomIcon);
        Assert.False(custom.ShowFallbackGlyph);
        Assert.NotEqual(plain, custom);
    }
}
