using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class ModuleCapabilitiesTests
{
    [Theory]
    [InlineData("cpu", true)]
    [InlineData("gpu", true)]
    [InlineData("ram", true)]
    [InlineData("power", true)]
    [InlineData("disk", false)]
    [InlineData("mouse", false)]
    [InlineData("network", false)]
    [InlineData(null, true)]
    public void Ticks_OnlyOnChargeGauges(string? id, bool expected)
        => Assert.Equal(expected, ModuleCapabilities.HasTicks(id));

    [Fact]
    public void Disk_RingVersusBar()
    {
        Assert.True(ModuleCapabilities.HasRingGauge("disk", "Ring"));
        Assert.False(ModuleCapabilities.HasRingGauge("disk", "Bar"));
        Assert.True(ModuleCapabilities.IsLinearDisk("disk", "Bar"));
        Assert.False(ModuleCapabilities.IsLinearDisk("cpu", "Bar"));
        Assert.False(ModuleCapabilities.HasIntro("disk", "Bar"));
        Assert.True(ModuleCapabilities.HasIntro("disk", "Ring"));
        Assert.False(ModuleCapabilities.HasIntro("claude", null));
    }

    [Fact]
    public void GaugeStyles_LinearOnlyForClaudeAndBarDisk()
    {
        Assert.Null(ModuleCapabilities.SupportedGaugeStyles("cpu", null));
        Assert.Null(ModuleCapabilities.SupportedGaugeStyles("disk", "Ring"));
        Assert.Null(ModuleCapabilities.SupportedGaugeStyles(null, null));
        foreach (var (id, disk) in new[] { ("claude", (string?)null), ("disk", "Bar") })
        {
            var styles = ModuleCapabilities.SupportedGaugeStyles(id, disk)!;
            Assert.Contains("Smooth", styles);
            Assert.Contains("Capsule", styles);
            Assert.DoesNotContain("Needle", styles);
            Assert.DoesNotContain("Pie", styles);
            Assert.DoesNotContain("Double", styles);
        }
    }

    [Fact]
    public void TilesAndButtons_PerModule()
    {
        foreach (var id in new[] { "cpu", "gpu", "ram", "power", "volumemixer", "audio", null })
            Assert.True(ModuleCapabilities.HasTiles(id));
        foreach (var id in new[] { "network", "disk", "mouse", "media", "claude", "visualizer" })
            Assert.False(ModuleCapabilities.HasTiles(id));
        foreach (var id in new[] { "audio", "volumemixer", "media", null })
            Assert.True(ModuleCapabilities.HasButtons(id));
        foreach (var id in new[] { "cpu", "network", "claude", "visualizer" })
            Assert.False(ModuleCapabilities.HasButtons(id));
    }

    [Fact]
    public void Easing_ShownForRingsOrZoomEntrance()
    {
        Assert.True(ModuleCapabilities.HasEasing("cpu", null, "Fade"));
        Assert.False(ModuleCapabilities.HasEasing("network", null, "Slide"));
        Assert.True(ModuleCapabilities.HasEasing("network", null, "Zoom"));
        Assert.False(ModuleCapabilities.HasEasing("disk", "Bar", "Slide"));
        Assert.True(ModuleCapabilities.HasEasing("disk", "Bar", "Zoom"));
    }
}
