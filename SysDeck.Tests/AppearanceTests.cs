using System.Text.Json;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class AppearanceTests
{
    [Fact]
    public void Defaults_KeepPreviousRendering()
    {
        var t = new ThemeSettings();
        Assert.False(t.PanelGradient);
        Assert.Equal(1, t.BorderThickness);
        Assert.Equal("Solid", t.BorderStyle);
        Assert.False(t.ShadowEnabled);
        Assert.Equal("Slide", t.EntranceStyle);
        Assert.True(t.FadeIn);
        Assert.Equal("None", t.HoverEffect);
    }

    [Fact]
    public void CopyCardFrom_CopiesNewFields()
    {
        var src = new ThemeSettings
        {
            PanelGradient = true, PanelColor2 = "#FF112233", PanelGradientAngle = 45, BorderThickness = 3, BorderStyle = "Accent",
            ShadowEnabled = true, ShadowColor = "#FF445566", ShadowSize = 30, ShadowOpacity = 0.7
        };
        var dst = new ThemeSettings();
        dst.CopyCardFrom(src);
        Assert.True(dst.PanelGradient);
        Assert.Equal("#FF112233", dst.PanelColor2);
        Assert.Equal(45, dst.PanelGradientAngle);
        Assert.Equal(3, dst.BorderThickness);
        Assert.Equal("Accent", dst.BorderStyle);
        Assert.True(dst.ShadowEnabled);
        Assert.Equal("#FF445566", dst.ShadowColor);
        Assert.Equal(30, dst.ShadowSize);
        Assert.Equal(0.7, dst.ShadowOpacity);
    }

    [Fact]
    public void CopyAnimationsFrom_CopiesEntranceAndHover()
    {
        var dst = new ThemeSettings();
        dst.CopyAnimationsFrom(new ThemeSettings { EntranceStyle = "Zoom", HoverEffect = "Glow" });
        Assert.Equal("Zoom", dst.EntranceStyle);
        Assert.Equal("Glow", dst.HoverEffect);
    }

    [Fact]
    public void OldConfig_FadeInFalse_MigratesToNone()
    {
        var t = JsonSerializer.Deserialize<ThemeSettings>("{\"FadeIn\":false}")!;
        Assert.Equal("None", t.EntranceStyle);
        Assert.False(t.FadeIn);
        Assert.Equal("Slide", JsonSerializer.Deserialize<ThemeSettings>("{\"FadeIn\":true}")!.EntranceStyle);
    }

    [Fact]
    public void Json_RoundTrip_KeepsNewFields()
    {
        var t = new ThemeSettings { EntranceStyle = "Fade", HoverEffect = "Lift", ShadowEnabled = true, ShadowSize = 22, PanelGradient = true };
        var back = JsonSerializer.Deserialize<ThemeSettings>(JsonSerializer.Serialize(t))!;
        Assert.Equal("Fade", back.EntranceStyle);
        Assert.True(back.FadeIn);
        Assert.Equal("Lift", back.HoverEffect);
        Assert.True(back.ShadowEnabled);
        Assert.Equal(22, back.ShadowSize);
        Assert.True(back.PanelGradient);

        var none = JsonSerializer.Deserialize<ThemeSettings>(JsonSerializer.Serialize(new ThemeSettings { EntranceStyle = "None" }))!;
        Assert.Equal("None", none.EntranceStyle);
    }
}
