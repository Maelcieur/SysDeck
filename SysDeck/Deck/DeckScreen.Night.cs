using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysDeck.Infrastructure;

namespace SysDeck.Deck;

/// <summary>
/// Ce que la Switch affiche la nuit (PC éteint) : image de nuit, position de la ligne d'heure et glyphes
/// '0'..'9' ':' en BGRA prémultiplié (GlyphHeight = 0 : pas d'horloge sur la page, fond seul).
/// </summary>
public sealed record NightParts(byte[] Image, int CenterX, int TopY, int GlyphHeight, int[] GlyphWidths, byte[] Glyphs,
    DeckProtocol.NightBattery? Battery = null, byte[]? BatteryPixels = null);

/// <summary>Rendu du mode nuit de <see cref="DeckScreen"/>.</summary>
public sealed partial class DeckScreen
{
    private const string GlyphChars = "0123456789:";

    /// <summary>
    /// Rend l'écran avec seulement le fond et la carte de l'horloge (aucun texte, aucun module) puis les glyphes
    /// de l'heure, dessinés avec le TextBlock de l'heure de <see cref="DeckClock"/> à l'échelle où il apparaît.
    /// À appeler sur le thread d'interface, comme un rendu normal.
    /// </summary>
    public NightParts RenderNight(RenderTargetBitmap bitmap)
    {
        var clock = FindClock();
        var image = RenderNightImage(bitmap, clock);
        var glyphs = clock is null ? null : RenderGlyphs(clock);
        var battery = RenderBattery();

        // Les glyphes de l'heure et l'icône doivent tenir ensemble dans une commande ; l'icône cède la première.
        var room = DeckProtocol.MaxDataBytes - DeckProtocol.BlockSize - DeckProtocol.NightImageBytes;
        if (battery is not null && (glyphs?.Pixels.Length ?? 0) + battery.Value.Pixels.Length > room) battery = null;

        return new NightParts(image, glyphs?.CenterX ?? 0, glyphs?.TopY ?? 0, glyphs?.Height ?? 0,
            glyphs?.Widths ?? new int[GlyphChars.Length], glyphs?.Pixels ?? Array.Empty<byte>(),
            battery?.Info, battery?.Pixels);
    }

    // ---------- Icône de batterie ----------

    private const string BatteryChars = "0123456789%";
    private const int BatteryMargin = 24;

    /// <summary>
    /// Icône de batterie du kit : contour (corps arrondi + borne), éclair, chiffres et « % » dans la police et la
    /// couleur du thème, en haut à droite (ou un autre coin si l'horloge y est). Pixels = contour, éclair, glyphes.
    /// </summary>
    private (DeckProtocol.NightBattery Info, byte[] Pixels)? RenderBattery()
    {
        const int bodyW = 64, bodyH = 30, iconW = 70, iconH = 30;
        var brush = TryFindResource("TextBrush") as Brush ?? Brushes.White;

        // Contour : corps arrondi (trait de 2 px) et borne pleine à droite.
        var outline = new DrawingVisual();
        using (var dc = outline.RenderOpen())
        {
            dc.DrawRoundedRectangle(null, new Pen(brush, 2), new Rect(1, 1, bodyW - 2, bodyH - 2), 7, 7);
            dc.DrawRoundedRectangle(brush, null, new Rect(bodyW + 1, 9, iconW - bodyW - 1, 12), 2, 2);
        }
        var outlinePixels = RenderToPixels(outline, iconW, iconH);

        // Éclair blanc cerclé de sombre (lisible sur tous les remplissages), 14 x 22.
        const int boltW = 14, boltH = 22;
        var bolt = new DrawingVisual();
        using (var dc = bolt.RenderOpen())
        {
            var figure = new PathFigure(new Point(9, 1), new[]
            {
                new LineSegment(new Point(1, 12), true), new LineSegment(new Point(6, 12), true),
                new LineSegment(new Point(4, 21), true), new LineSegment(new Point(13, 9), true),
                new LineSegment(new Point(8, 9), true)
            }, true);
            dc.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1),
                new PathGeometry(new[] { figure }));
        }
        var boltPixels = RenderToPixels(bolt, boltW, boltH);

        // Texte « NN% » : même police que le thème, hauteur de ligne commune.
        var model = new TextBlock
        {
            FontFamily = System.Windows.Documents.TextElement.GetFontFamily(this),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush
        };
        model.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var lineHeight = model.DesiredSize.Height;
        var textH = Math.Min(iconH, (int)Math.Ceiling(lineHeight));

        var widths = new int[BatteryChars.Length];
        var chunks = new byte[BatteryChars.Length][];
        for (var i = 0; i < BatteryChars.Length; i++)
            (widths[i], chunks[i]) = RenderGlyph(model, BatteryChars[i], 1, textH, lineHeight);

        var gap = 8;
        var areaW = iconW + gap + 3 * widths.Take(10).Max() + widths[10];
        var (x, y) = BatteryPosition(areaW, iconH);

        var info = new DeckProtocol.NightBattery(x, y, iconW, iconH, 4, 4, bodyW - 8, bodyH - 8, 4, boltW, boltH, textH, gap,
            widths, 0x3FB950, 0xF0883E, 0xF85149, 0x56D364);
        var pixels = outlinePixels.Concat(boltPixels).Concat(chunks.SelectMany(c => c)).ToArray();
        return (info, pixels);
    }

    private static byte[] RenderToPixels(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>Coin de l'icône : haut droite par défaut, sinon le premier coin qui ne touche pas l'horloge.</summary>
    private (int X, int Y) BatteryPosition(int width, int height)
    {
        var right = DeckProtocol.ScreenWidth - BatteryMargin - width;
        var bottom = DeckProtocol.ScreenHeight - BatteryMargin - height;
        var candidates = new[] { (right, BatteryMargin), (BatteryMargin, BatteryMargin), (right, bottom), (BatteryMargin, bottom) };

        var clock = _settings.Find("clock");
        if (clock is not { Visible: true }) return candidates[0];

        var avoid = new Rect(clock.X - 12, clock.Y - 12, clock.Width + 24, clock.Height + 24);
        foreach (var c in candidates)
            if (!avoid.IntersectsWith(new Rect(c.Item1, c.Item2, width, height))) return c;
        return candidates[0];
    }

    private DeckClock? FindClock()
        => _elements.TryGetValue("clock", out var e) && e.Frame is Grid grid
            ? grid.Children.OfType<Viewbox>().Select(v => v.Child).OfType<DeckClock>().FirstOrDefault()
            : null;

    /// <summary>Image de nuit : tout est masqué (Hidden garde la mise en page) sauf le fond et la carte de l'horloge.</summary>
    private byte[]? _nightPixels;

    private byte[] RenderNightImage(RenderTargetBitmap bitmap, DeckClock? clock)
    {
        var clockFrame = clock is null ? null : _elements["clock"].Frame;
        var hidden = _elements.Values.Select(e => e.Frame).Where(f => f != clockFrame).Append(_dots)
            .Concat(_overlay is null ? Array.Empty<FrameworkElement>() : new[] { _overlay })
            .Select(f => (Element: f, Visibility: f.Visibility)).ToList();

        foreach (var h in hidden) h.Element.Visibility = Visibility.Hidden;
        clock?.HideTexts(true);
        try
        {
            UpdateLayout();
            bitmap.Clear();
            bitmap.Render(this);
        }
        finally
        {
            clock?.HideTexts(false);
            foreach (var h in hidden) h.Element.Visibility = h.Visibility;
        }

        // Tampon réutilisé (3,6 Mo à chaque rendu sinon). L'appelant en consomme le contenu (hachage, copie dans le kit)
        // avant le rendu suivant, qui se fait sur le même thread d'interface.
        _nightPixels ??= new byte[DeckLink.FrameBytes];
        bitmap.CopyPixels(_nightPixels, DeckProtocol.ScreenWidth * 4, 0);
        return _nightPixels;
    }

    private sealed record GlyphSet(int CenterX, int TopY, int Height, int[] Widths, byte[] Pixels);

    /// <summary>Glyphes de l'heure ; null si la taille dépasse ce qu'une commande peut transporter.</summary>
    private GlyphSet? RenderGlyphs(DeckClock clock)
    {
        var text = clock.TimeText;
        UpdateLayout();
        if (!text.IsDescendantOf(this) || text.ActualHeight <= 0) return null;

        // Position et échelle réelles de l'heure sur l'écran (le Viewbox de la carte la met à l'échelle).
        var toScreen = text.TransformToVisual(this);
        var bounds = toScreen.TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight));
        var matrix = ((MatrixTransform)toScreen).Matrix;
        var scale = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);

        var height = (int)Math.Ceiling(text.ActualHeight * scale);
        var centerX = (int)Math.Round(bounds.X + bounds.Width / 2);
        var top = (int)Math.Round(bounds.Y);
        if (height < 1 || height > DeckProtocol.ScreenHeight || top < 0 || top + height > DeckProtocol.ScreenHeight
            || centerX < 0 || centerX > DeckProtocol.ScreenWidth)
            return null;

        var widths = new int[GlyphChars.Length];
        var chunks = new byte[GlyphChars.Length][];
        long total = 0;
        for (var i = 0; i < GlyphChars.Length; i++)
        {
            (widths[i], chunks[i]) = RenderGlyph(text, GlyphChars[i], scale, height, text.ActualHeight);
            total += chunks[i].Length;
        }

        var room = DeckProtocol.MaxDataBytes - DeckProtocol.BlockSize - DeckProtocol.NightImageBytes;
        if (total > room || widths.Any(w => w < 1 || w > DeckProtocol.ScreenWidth))
        {
            Log.Info($"Horloge de nuit trop grande ({total} octets) : image de nuit sans heure");
            return null;
        }

        var pixels = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(pixels, offset);
            offset += chunk.Length;
        }
        return new GlyphSet(centerX, top, height, widths, pixels);
    }

    /// <summary>Un glyphe, dans le style du texte de l'heure, mis à l'échelle comme dans la carte (BGRA prémultiplié).</summary>
    private static (int Width, byte[] Pixels) RenderGlyph(TextBlock model, char glyph, double scale, int height, double lineHeight)
    {
        var text = new TextBlock
        {
            Text = glyph.ToString(),
            FontFamily = model.FontFamily,
            FontSize = model.FontSize,
            FontWeight = model.FontWeight,
            FontStyle = model.FontStyle,
            FontStretch = model.FontStretch,
            Foreground = model.Foreground,
            Effect = model.Effect,
            Height = lineHeight
        };
        var host = new Grid { LayoutTransform = new ScaleTransform(scale, scale), Children = { text } };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var width = Math.Max(1, (int)Math.Ceiling(host.DesiredSize.Width));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var glyphBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        glyphBitmap.Render(host);
        var pixels = new byte[width * height * 4];
        glyphBitmap.CopyPixels(pixels, width * 4, 0);
        return (width, pixels);
    }
}
