using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysDeck.Controls;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class VisualTests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "délai dépassé");
        if (error is not null) throw new Exception("Échec sur le thread STA", error);
    }

    [Fact]
    public void EverySymbol_ExistsInIconFont()
    {
        // Segoe Fluent Icons (Windows 11) ou, à défaut, Segoe MDL2 Assets (Windows 10).
        var typefaces = ShortcutIcons.SymbolFont.Split(',')
            .Select(name => new Typeface(name.Trim()))
            .Select(t => t.TryGetGlyphTypeface(out var g) ? g : null)
            .OfType<GlyphTypeface>()
            .ToList();
        Assert.NotEmpty(typefaces);

        var missing = ShortcutIcons.Symbols.Where(s => ShortcutIcons.IsSymbol(s.Glyph))
            .Where(s => !typefaces.Any(t => t.CharacterToGlyphMap.ContainsKey(s.Glyph[0])))
            .Select(s => $"{s.Name} (U+{(int)s.Glyph[0]:X4})")
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Library_NoEmptyCategory_AndNamesUniquePerCategory()
    {
        foreach (var (category, items) in ShortcutIcons.Library)
        {
            Assert.NotEmpty(items);
            Assert.Equal(items.Count, items.Select(i => i.Glyph).Distinct().Count());
            Assert.False(string.IsNullOrWhiteSpace(category));
        }
    }

    [Theory]
    [InlineData("Smooth"), InlineData("Segments"), InlineData("Thin"), InlineData("Dots"), InlineData("Needle"), InlineData("Pie")]
    [InlineData("Neon"), InlineData("Rays"), InlineData("Double"), InlineData("Capsule")]
    public void EveryGaugeDesign_Renders(string design) => RunOnSta(() =>
    {
        foreach (var (sweep, glow) in new[] { (270.0, true), (360.0, false), (180.0, true) })
        {
            var radial = new RadialGauge { Design = design, SweepAngle = sweep, GlowEnabled = glow, ShowTicks = true, AnimationScale = 0, Value = 63, DisplayValue = 63 };
            AssertDrawsSomething(radial, 120, 120);
        }
        var linear = new LinearGauge { Design = design, AnimationScale = 0, Value = 63, DisplayValue = 63 };
        AssertDrawsSomething(linear, 200, 8);
    });

    private static void AssertDrawsSomething(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[(int)width * (int)height * 4];
        bitmap.CopyPixels(pixels, (int)width * 4, 0);
        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void CopyAnimations_CopiesIdleEffect()
    {
        var source = new ThemeSettings { IdleEffect = "Float", HoverEffect = "LiftGlow", EntranceStyle = "Pop", AnimationEasing = "Back" };
        var target = new ThemeSettings();
        target.CopyAnimationsFrom(source);
        Assert.Equal("Float", target.IdleEffect);
        Assert.Equal("LiftGlow", target.HoverEffect);
        Assert.Equal("Pop", target.EntranceStyle);
        Assert.Equal("Back", target.AnimationEasing);
        Assert.Equal("Float", source.Clone().IdleEffect);
    }

    [Fact]
    public void ContentBounds_FitsTextNotSlot() => RunOnSta(() =>
    {
        var text = new System.Windows.Controls.TextBlock { Text = "SORTIE AUDIO", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        var panel = new System.Windows.Controls.StackPanel { Background = Brushes.Transparent, Children = { text } };
        panel.Measure(new Size(300, 100));
        panel.Arrange(new Rect(0, 0, 300, 100));
        var bounds = Movable.ContentBounds(panel);
        Assert.True(bounds.Width is > 20 and < 150, $"largeur {bounds.Width}");
        Assert.True(bounds.Height is > 4 and < 25, $"hauteur {bounds.Height}");
    });

    [Fact]
    public void ContentBounds_IgnoresSpinningChild() => RunOnSta(() =>
    {
        var disc = new System.Windows.Shapes.Ellipse { Width = 56, Height = 56, Fill = Brushes.Red };
        var spinner = new System.Windows.Controls.Grid { Width = 56, Height = 56, Children = { disc }, RenderTransformOrigin = new Point(0.5, 0.5) };
        var root = new System.Windows.Controls.Grid { Children = { spinner } };
        root.Measure(new Size(200, 200));
        root.Arrange(new Rect(0, 0, 200, 200));
        var still = Movable.ContentBounds(root);
        spinner.RenderTransform = new RotateTransform(45);
        var turning = Movable.ContentBounds(root);
        Assert.Equal(still.Width, turning.Width, 1);
        Assert.Equal(still.Height, turning.Height, 1);
    });

    [Theory]
    [InlineData("Rays"), InlineData("Pie"), InlineData("Needle"), InlineData("Dots")]
    public void ContentBounds_GaugeValueDoesNotChangeSize(string design) => RunOnSta(() =>
    {
        var gauge = new RadialGauge { Design = design, Width = 80, Height = 80, AnimationScale = 0, Value = 5, DisplayValue = 5 };
        var root = new System.Windows.Controls.Grid { Children = { gauge } };
        root.Measure(new Size(200, 200));
        root.Arrange(new Rect(0, 0, 200, 200));
        var low = Movable.ContentBounds(root);
        gauge.Value = gauge.DisplayValue = 95;
        root.UpdateLayout();
        var high = Movable.ContentBounds(root);
        // Au plus un petit écart (halo au bout de l'arc) : absorbé par la tolérance de la carte.
        Assert.True(Math.Abs(low.Left - high.Left) < 3 && Math.Abs(low.Top - high.Top) < 3
            && Math.Abs(low.Right - high.Right) < 3 && Math.Abs(low.Bottom - high.Bottom) < 3, $"{low} / {high}");
    });

    [Theory]
    [InlineData("Back"), InlineData("Sine"), InlineData("Snap"), InlineData("Elastic"), InlineData("Bounce"), InlineData("Smooth")]
    public void NewEasings_Exist(string name) => Assert.NotNull(ThemeService.CreateEasing(name));
}
