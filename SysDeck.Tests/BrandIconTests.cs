using SysDeck.Controls;
using Xunit;

namespace SysDeck.Tests;

public class BrandIconTests
{
    [Fact]
    public void Json_Loads93Logos_WithUniqueSlugs()
    {
        Assert.Equal(93, BrandIcons.All.Count);
        Assert.Equal(93, BrandIcons.All.Select(b => b.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(BrandIcons.All, b =>
        {
            Assert.Matches("^[0-9A-Fa-f]{6}$", b.Hex);
            Assert.False(string.IsNullOrWhiteSpace(b.Title));
            Assert.False(string.IsNullOrWhiteSpace(b.PathData));
        });
        Assert.Contains("Jeux", BrandIcons.Categories);
    }

    [Fact]
    public void EveryPath_ParsesToFrozenGeometry()
    {
        Assert.All(BrandIcons.All, b =>
        {
            Assert.NotNull(b.Geometry);
            Assert.True(b.Geometry!.IsFrozen);
        });
    }

    [Theory]
    [InlineData("discord")]
    [InlineData("DISCORD")]
    [InlineData("  Disc ")]
    public void Search_IgnoresCase(string text) => Assert.Contains(BrandIcons.Search(text), b => b.Slug == "discord");

    [Fact]
    public void Search_IgnoresAccents()
    {
        Assert.Equal("cafe", BrandIcons.Fold("Café"));
        Assert.Equal("ete", BrandIcons.Fold("ÉTÉ"));
        Assert.Equal(BrandIcons.All.Count, BrandIcons.Search("").Count());
        Assert.Empty(BrandIcons.Search("zzzzqqq"));
    }

    [Fact]
    public void Resolve_DiscordUsesOfficialColorUnlessUserColorGiven()
    {
        Assert.Equal("#FF5865F2", BrandIcons.ResolveColor("brand:discord", null));
        Assert.Equal("#FF5865F2", BrandIcons.ResolveColor("brand:discord", "  "));
        Assert.Equal("#FFFF0000", BrandIcons.ResolveColor("brand:discord", "#FFFF0000"));
        Assert.Equal("brand:discord", BrandIcons.Resolve("brand:discord")!.Value);
    }

    [Fact]
    public void UnknownSlug_ResolvesToNull_WithoutException()
    {
        Assert.Null(BrandIcons.Resolve("brand:inconnu-xyz"));
        Assert.Null(BrandIcons.ResolveColor("brand:inconnu-xyz", "#FFFF0000"));
        Assert.Null(BrandIcons.Resolve(""));
        Assert.Null(BrandIcons.Resolve(null));
        Assert.True(BrandIcons.IsBrand("brand:inconnu-xyz"));
        Assert.False(ShortcutIcons.IsImagePath("brand:discord"));
        Assert.False(ShortcutIcons.IsSymbol("brand:discord"));
    }
}
