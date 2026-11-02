namespace SysDeck.Controls;

/// <summary>
/// Contrôle réglable au doigt sur l'écran de la Switch (glisser), où aucune souris n'existe :
/// les positions sont données dans le repère du contrôle.
/// </summary>
public interface ITouchDraggable
{
    void TouchStart(Point position);
    void TouchMove(Point position);
    void TouchEnd();
}
