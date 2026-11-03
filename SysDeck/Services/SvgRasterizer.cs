using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>
/// Conversion d'images SVG en PNG carrés (SharpVectors) : WPF ne sait pas afficher le SVG, les packs d'icônes du
/// Stream Deck en sont pourtant souvent faits. Le PNG est rangé à côté (« nom.svg.png ») et sert partout comme une image.
/// </summary>
public static class SvgRasterizer
{
    public const int Size = 256;

    /// <summary>Nombre maximal de SVG convertis par dossier (un pack d'icônes en compte rarement plus de quelques centaines).</summary>
    private const int MaxFiles = 2000;

    /// <summary>Chemin du PNG tiré d'un SVG.</summary>
    public static string PngPathOf(string svgPath) => svgPath + ".png";

    /// <summary>
    /// Convertit tous les SVG d'un dossier qui n'ont pas encore leur PNG ; renvoie le nombre d'échecs. Doit tourner sur un
    /// thread STA (le thread de l'interface, ou celui d'un test).
    /// </summary>
    public static int ConvertFolder(string folder)
    {
        var failures = 0;
        var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
        // Les SVG des dépendances d'un plugin (node_modules) ne sont jamais des icônes : inutile de les convertir.
        var svgs = Directory.EnumerateFiles(folder, "*.svg", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Take(MaxFiles);
        foreach (var svg in svgs)
        {
            var png = PngPathOf(svg);
            if (File.Exists(png)) continue;
            try
            {
                using var reader = new FileSvgReader(settings);
                if (reader.Read(svg) is not { } drawing) { failures++; continue; }
                Save(drawing, png);
            }
            catch (Exception ex)
            {
                // SVG abîmé ou exotique : l'icône est simplement ignorée.
                failures++;
                Log.WarnOnce("svg-" + Path.GetFileName(svg), $"SVG illisible ({Path.GetFileName(svg)}) : {ex.Message}");
            }
        }
        return failures;
    }

    /// <summary>Image d'un SVG donné en texte (setImage d'un plugin) ; null s'il est illisible. Thread de l'interface.</summary>
    public static ImageSource? FromText(string svg)
    {
        try
        {
            using var reader = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true });
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
            if (reader.Read(stream) is not { } drawing) return null;
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("svg-plugin", $"SVG envoyé par un plugin illisible : {ex.Message}");
            return null;
        }
    }

    /// <summary>Dessin centré dans un carré transparent, proportions gardées.</summary>
    private static void Save(Drawing drawing, string path)
    {
        var bounds = drawing.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidDataException("dessin vide");
        var scale = Size / Math.Max(bounds.Width, bounds.Height);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform((Size - bounds.Width * scale) / 2, (Size - bounds.Height * scale) / 2));
            context.PushTransform(new ScaleTransform(scale, scale));
            context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            context.DrawDrawing(drawing);
        }
        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
