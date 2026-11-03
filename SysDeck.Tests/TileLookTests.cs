using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysDeck.Deck;
using SysDeck.Models;
using Xunit;

namespace SysDeck.Tests;

/// <summary>Apparence des tuiles de la Switch : verre 3D, taille d'icône, nom, état appuyé.</summary>
public class TileLookTests
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

    [Theory]
    [InlineData(null), InlineData(0.1), InlineData(0.7), InlineData(5.0), InlineData(double.NaN)]
    public void IconScale_IsClamped(double? scale)
    {
        var clamped = DeckSettings.ClampIconScale(scale);
        if (scale is null || double.IsNaN(scale.Value)) Assert.Null(clamped);
        else Assert.InRange(clamped!.Value, DeckSettings.MinIconScale, DeckSettings.MaxIconScale);
    }

    [Fact]
    public void OldSettings_GetGlassAndHalfIcon()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>("""{"TileSize":120,"TileIconScale":9}""")!;
        settings.Migrate();
        Assert.Equal(DeckTileStyle.Glass, settings.TileStyle);
        Assert.Equal(1, settings.TileIconScale);
        Assert.True(settings.ShowTileNames);
    }

    [Fact]
    public void PageLook_MigratedThenIndependentPerPage()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>(
            """{"TileSize":150,"BackgroundColor":"#FF112233","TileStyle":"Flat","Pages":[{"Name":"A"},{"Name":"B"}]}""")!;
        settings.Migrate();
        Assert.All(settings.Pages, p => Assert.True(p.OwnLook && p.TileSize == 150 && p.BackgroundColor == "#FF112233" && p.TileStyle == DeckTileStyle.Flat));

        // Page B : grandes icônes en verre ; la page A ne bouge pas.
        settings.CurrentPage = 1;
        settings.TileStyle = DeckTileStyle.Glass;
        settings.TileIconScale = 1;
        settings.BackgroundColor = "#FF445566";
        settings.CurrentPage = 0;
        Assert.Equal(DeckTileStyle.Flat, settings.TileStyle);
        Assert.Equal(0.5, settings.TileIconScale);
        Assert.Equal("#FF112233", settings.BackgroundColor);

        var copy = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        copy.Migrate();
        Assert.Equal(DeckTileStyle.Glass, copy.Pages[1].TileStyle);
        Assert.Equal(1, copy.Pages[1].TileIconScale);
        Assert.Equal(DeckTileStyle.Flat, copy.Pages[0].TileStyle);

        settings.ApplyLookToAllPages();
        Assert.Equal(DeckTileStyle.Flat, settings.Pages[1].TileStyle);
        Assert.Equal("B", settings.Pages[1].Name);
    }

    [Fact]
    public void OldSettings_LoseShortcutsBar()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>(
            """{"Pages":[{"Name":"P","Elements":[{"Id":"shortcuts","Visible":true},{"Id":"apps","Visible":true}]}]}""")!;
        settings.Migrate();
        Assert.Equal(new[] { "apps" }, settings.Pages[0].Elements.Select(e => e.Id));
        Assert.DoesNotContain(Deck.DeckController.AllElements, e => e.Id == "shortcuts");
    }

    [Fact]
    public void ActiveLook_DefaultsAndRoundTrip()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<ShortcutItem>("""{"Name":"Micro","Kind":"MicMute"}""")!;
        Assert.True(old.ActivePressed);
        Assert.False(old.HasActiveIcon);

        var item = new ShortcutItem { IconColor = "#FF00FF00" };
        new TileActiveLook("#FFC63A32", "\uF781", @"C:\image.png", null, "#FF112233", false).WriteTo(item);
        Assert.Equal("\uF781", item.ActiveIconGlyph);
        Assert.Null(item.ActiveIconPath); // un symbole passe avant l'image
        Assert.Null(item.ActiveImageTint);
        Assert.False(item.ActivePressed);
        Assert.Equal("#FF00FF00", item.ActiveDisplay().IconColor); // couleur du symbole normal à défaut
        Assert.Equal(TileActiveLook.From(item), TileActiveLook.From(Copy(item)));
    }

    private static ShortcutItem Copy(ShortcutItem item)
    {
        var copy = new ShortcutItem();
        copy.CopyActiveFrom(item);
        return copy;
    }

    [Fact]
    public void ActiveMuteTile_UsesSecondIconColorAndStaysDown() => RunOnSta(() =>
    {
        var settings = new DeckSettings { TileStyle = DeckTileStyle.Glass };
        var normal = new ShortcutItem { Name = "Micro", Kind = TileKind.MicMute, IconGlyph = "\uE720" };
        var muted = new ShortcutItem
        {
            Name = "Micro coupé", Kind = TileKind.MicMute, IconGlyph = "\uE720",
            ActiveIconGlyph = "\uF781", ActiveColor = "#FF7A4BD6"
        };
        var flat = new ShortcutItem { Name = "Enfoncée non", Kind = TileKind.MicMute, IconGlyph = "\uE720", ActivePressed = false };
        Services.TileRuntime.Muted[muted] = true;
        Services.TileRuntime.Muted[flat] = true;
        try
        {
            var tiles = new List<ShortcutItem> { normal, muted, flat };
            var panel = new DeckShortcutsPanel(() => tiles, null, () => 120, look: () => settings) { Width = 3 * 132, Height = 132 };
            panel.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x29));
            panel.Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
            panel.Resources["TextBrush"] = Brushes.White;
            var host = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), Children = { panel } };
            panel.Rebuild();

            Assert.Same(Transform.Identity, ((Border)panel.Children[0]).RenderTransform);
            Assert.IsType<ScaleTransform>(((Border)panel.Children[1]).RenderTransform); // active : reste enfoncée
            Assert.Same(Transform.Identity, ((Border)panel.Children[2]).RenderTransform);
            panel.SetPressed(null); // relâcher le doigt ne la relève pas
            Assert.IsType<ScaleTransform>(((Border)panel.Children[1]).RenderTransform);

            host.Measure(new Size(panel.Width, panel.Height));
            host.Arrange(new Rect(0, 0, panel.Width, panel.Height));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)panel.Width, (int)panel.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(Path.GetTempPath(), "sysdeck-tuiles-actives.png"));
            encoder.Save(stream);
        }
        finally
        {
            Services.TileRuntime.Muted.Remove(muted);
            Services.TileRuntime.Muted.Remove(flat);
        }
    });

    /// <summary>Icône noire sur fond transparent, comme celles des packs « Dark ».</summary>
    private static string TintSample()
    {
        var path = Path.Combine(Path.GetTempPath(), "sysdeck-teinte.png");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawEllipse(Brushes.Black, null, new Point(32, 32), 26, 26);
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    [Fact]
    public void GlassTiles_RenderAndPress() => RunOnSta(() =>
    {
        var settings = new DeckSettings { TileStyle = DeckTileStyle.Glass, TileIconScale = 0.5 };
        var tiles = new List<ShortcutItem>
        {
            new() { Name = "Thème", IconGlyph = "" },
            new() { Name = "Couleur", IconGlyph = "", TileColor = "#FF14244A" },
            new() { Name = "Grande", IconGlyph = "", TileColor = "#FF4A1430", IconScale = 0.9 },
            new() { Name = "Sans nom", IconGlyph = "", IconScale = 1, HideName = true },
            new() { Name = "Transparent", IconGlyph = "", TileColor = "#00000000" },
            new() { Name = "Teintée", CustomIconPath = TintSample(), ImageTint = "#FFFF8A00" }
        };
        var panel = new DeckShortcutsPanel(() => tiles, null, () => 120, look: () => settings)
        {
            Width = 6 * 132,
            Height = 132
        };
        panel.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x29));
        panel.Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
        panel.Resources["TextBrush"] = Brushes.White;
        panel.Resources["BorderBrush2"] = Brushes.Gray;
        var host = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), Children = { panel } };
        panel.Rebuild();

        void Render(string file)
        {
            host.Measure(new Size(panel.Width, panel.Height));
            host.Arrange(new Rect(0, 0, panel.Width, panel.Height));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)panel.Width, (int)panel.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(Path.GetTempPath(), file));
            encoder.Save(stream);
        }

        Assert.Equal(6, panel.Children.Count);
        Assert.True(TileGlass.Available);
        Render("sysdeck-tuiles-verre.png");

        var first = (Border)panel.Children[0];
        panel.SetPressed(first);
        Assert.IsType<ScaleTransform>(first.RenderTransform);
        Render("sysdeck-tuiles-verre-appui.png");
        panel.SetPressed(null);
        Assert.Same(Transform.Identity, first.RenderTransform);
    });
}
