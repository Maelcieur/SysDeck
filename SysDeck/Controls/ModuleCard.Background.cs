using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SysDeck.Services;

namespace SysDeck.Controls;

// Fond de la carte : image choisie, pochette de la lecture en cours (nette, floue ou animée).
public partial class ModuleCard
{
    private ImageSource? _mediaArtwork;

    /// <summary>Pochette du morceau en cours (module Lecture en cours), utilisable comme fond.</summary>
    public void SetMediaArtwork(ImageSource? artwork)
    {
        if (ReferenceEquals(_mediaArtwork, artwork)) return;
        _mediaArtwork = artwork;
        if (_appearance?.MediaBackground is "Cover" or "Animated")
            ApplyBackground();
    }

    private void ApplyBackground()
    {
        if (_appearance is not { } appearance) return;

        var useArtwork = appearance.MediaBackground is "Cover" or "Animated" && _mediaArtwork is not null;
        // Un GIF choisi comme fond est animé ; sinon image fixe ou pochette.
        var brush = useArtwork ? null : AnimatedGif.CreateBrush(appearance.BackgroundImage, 900, ApplyBackground);
        if (brush is null && (useArtwork ? Soften(_mediaArtwork!, appearance.MediaBlur) : LoadImage(appearance.BackgroundImage)) is { } image)
            brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };

        if (brush is null)
        {
            ImageLayer.Background = DimLayer.Background = null;
            return;
        }

        brush.Opacity = appearance.BackgroundImageOpacity;
        // Pochette réduite puis agrandie avec lissage : un flou doux, sans Effect (coûteux sur une fenêtre transparente).
        RenderOptions.SetBitmapScalingMode(ImageLayer, BitmapScalingMode.HighQuality);
        if (useArtwork && appearance.MediaBackground == "Animated")
            AnimateArtwork(brush, appearance);

        ImageLayer.Background = brush;
        DimLayer.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(appearance.BackgroundDim, 0, 1) * 255), 0x10, 0x11, 0x17));
    }

    /// <summary>
    /// Flou par réduction : la pochette est réduite (jusqu'à 8 px de large) puis réagrandie avec lissage.
    /// <paramref name="blur"/> de 0 (nette) à 1 (très floue).
    /// </summary>
    private static ImageSource Soften(ImageSource source, double blur)
    {
        if (blur <= 0.01 || source is not BitmapSource bitmap) return source;
        var targetWidth = Math.Pow(2, 8 - 5 * Math.Clamp(blur, 0, 1)); // 256 px -> 8 px
        if (bitmap.PixelWidth <= targetWidth) return source;
        var scale = targetWidth / bitmap.PixelWidth;
        var small = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        small.Freeze();
        return small;
    }

    /// <summary>Animation lente et en boucle de la pochette de fond, selon le style et les amplitudes choisis.</summary>
    private void AnimateArtwork(ImageBrush brush, ModuleAppearance a)
    {
        var theme = a.EffectiveTheme(_config.Theme!);
        if (!theme.AnimationsEnabled) return;

        var seconds = Math.Clamp(a.MediaAnimDuration, 2, 60);
        var zoom = Math.Clamp(a.MediaAnimZoom, 0, 0.8);
        var pan = Math.Clamp(a.MediaAnimPan, 0, 0.25);

        // Agrandissement de base : l'image doit toujours couvrir la carte malgré le glissement ou la rotation.
        var baseScale = a.MediaAnimStyle switch
        {
            "Zoom" => 1.0,
            "Rotate" => 1.3 + pan * 4,
            _ => 1.05 + pan * 2
        };

        var scale = new ScaleTransform(baseScale, baseScale, 0.5, 0.5);
        var rotate = new RotateTransform(0, 0.5, 0.5);
        var move = new TranslateTransform();
        brush.RelativeTransform = new TransformGroup { Children = { scale, rotate, move } };

        DoubleAnimation Loop(double from, double to, double factor)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds * factor))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            // Fond lent : 24 images/s suffisent largement.
            Timeline.SetDesiredFrameRate(animation, 24);
            return animation;
        }

        void Zoom()
        {
            if (zoom <= 0.001) return;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(baseScale, baseScale + zoom, 1));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(baseScale, baseScale + zoom, 1));
        }

        void Pan()
        {
            if (pan <= 0.001) return;
            // Durées différentes en X et en Y : le trajet ne se répète pas à l'identique, plus naturel.
            move.BeginAnimation(TranslateTransform.XProperty, Loop(-pan, pan, 1.35));
            move.BeginAnimation(TranslateTransform.YProperty, Loop(pan * 0.6, -pan * 0.6, 1.65));
        }

        switch (a.MediaAnimStyle)
        {
            case "Zoom":
                Zoom();
                break;
            case "Pan":
                Pan();
                break;
            case "Rotate":
                // Balancement de quelques degrés (l'amplitude du mouvement règle l'angle).
                rotate.BeginAnimation(RotateTransform.AngleProperty, Loop(-pan * 120, pan * 120, 1));
                Zoom();
                break;
            default: // KenBurns : zoom + glissement
                Zoom();
                Pan();
                break;
        }
    }
}
