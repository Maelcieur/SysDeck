using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysDeck.Infrastructure;

namespace SysDeck.Deck;

/// <summary>
/// Bouton en verre 3D des tuiles de la Switch (style Stream Deck), relâché ou appuyé. Chaque état est en deux
/// couches semi-transparentes : la face (dont l'opacité suit celle du fond de la tuile, jusqu'à disparaître) et le
/// bord chromé (toujours visible). Lues une fois depuis les ressources intégrées.
/// </summary>
internal static class TileGlass
{
    /// <summary>Bord du verre, en part du côté : la face (couleur, icône) commence là.</summary>
    public const double FaceInset = 0.04;

    /// <summary>Arrondi de la face, en part du côté.</summary>
    public const double FaceCorner = 0.21;

    private static ImageSource? _face, _rim, _pressedFace, _pressedRim;
    private static bool _loaded;

    /// <summary>Vrai si les images sont disponibles (sinon les tuiles restent à plat).</summary>
    public static bool Available
    {
        get
        {
            Load();
            return _face is not null && _rim is not null;
        }
    }

    public static ImageSource? Face(bool pressed)
    {
        Load();
        return pressed ? _pressedFace ?? _face : _face;
    }

    public static ImageSource? Rim(bool pressed)
    {
        Load();
        return pressed ? _pressedRim ?? _rim : _rim;
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        _face = Read("glass-normal-face.png");
        _rim = Read("glass-normal-rim.png");
        _pressedFace = Read("glass-pressed-face.png");
        _pressedRim = Read("glass-pressed-rim.png");
    }

    private static ImageSource? Read(string name)
    {
        try
        {
            using var stream = typeof(TileGlass).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            Log.Warn($"Image de bouton « {name} » illisible : tuiles à plat", ex);
            return null;
        }
    }
}
