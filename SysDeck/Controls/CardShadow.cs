using System.Windows;
using System.Windows.Media;

namespace SysDeck.Controls;

/// <summary>
/// Ombre portée d'une carte, dessinée une seule fois : une pile de rectangles arrondis de plus en plus
/// transparents vers l'extérieur. Aucun Effect (DropShadowEffect, flou) : sur une fenêtre AllowsTransparency
/// ils sont rendus par le processeur et recalculés à chaque mise à jour du contenu. Ici le dessin ne change
/// que si les réglages ou la taille de la carte changent.
/// </summary>
public sealed class CardShadow : FrameworkElement
{
    private const int Layers = 12;

    private Color _color = Colors.Black;
    private double _size;
    private double _opacity;
    private double _radius;

    public CardShadow()
    {
        IsHitTestVisible = false; // la zone d'ombre ne doit pas capter la souris ni le doigt
    }

    /// <summary>Redessine l'ombre ; une taille nulle ou une opacité nulle la supprime.</summary>
    public void Configure(Color color, double size, double opacity, double cornerRadius)
    {
        size = Math.Max(0, size);
        opacity = Math.Clamp(opacity, 0, 1);
        if (_color == color && _size == size && _opacity == opacity && _radius == cornerRadius) return;
        _color = color;
        _size = size;
        _opacity = opacity;
        _radius = cornerRadius;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_size < 1 || _opacity <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var card = new Rect(0, 0, ActualWidth, ActualHeight);

        // L'ombre n'est pas peinte sous la carte : son fond translucide ne doit pas en être assombri.
        var hole = new RectangleGeometry(card, _radius, _radius);
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(Rect.Inflate(card, _size + 2, _size + 2)), hole);
        outside.Freeze();
        dc.PushClip(outside);

        // Opacité par couche telle que l'empilement donne l'opacité demandée contre la carte.
        var alpha = 1 - Math.Pow(1 - _opacity, 1.0 / Layers);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * _color.A), _color.R, _color.G, _color.B));
        brush.Freeze();

        for (var i = 0; i < Layers; i++)
        {
            // Répartition non linéaire : couches serrées près de la carte, ombre qui s'éteint doucement au loin.
            var grow = _size * Math.Pow(1 - (double)i / Layers, 1.8);
            dc.DrawRoundedRectangle(brush, null, Rect.Inflate(card, grow, grow), _radius + grow, _radius + grow);
        }

        dc.Pop();
    }
}
