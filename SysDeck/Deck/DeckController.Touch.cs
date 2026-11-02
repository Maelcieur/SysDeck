using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Models;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace SysDeck.Deck;

/// <summary>Tactile de <see cref="DeckController"/> : tap, glissement de page, contrôles réglables au doigt.</summary>
public sealed partial class DeckController
{
    private const double TapTolerance = 30; // déplacement maximal du doigt pour un appui (pixels Switch)
    private const double SwipeDistance = 160; // glissement horizontal qui change de page
    private Point _touchStart;
    private Point _touchLast;
    private bool _touchMoved;

    /// <summary>Contrôle réglé au doigt depuis le dernier appui (volume, curseur), sinon null.</summary>
    private FrameworkElement? _dragTarget;

    // ---------- Tactile ----------

    /// <summary>
    /// Doigt posé : un contrôle réglable (volume, curseur) suit le doigt jusqu'au relâché. Sinon, appui
    /// bref = tap, glissement horizontal franc = page suivante ou précédente.
    /// </summary>
    private void OnTouch(DeckProtocol.TouchEvent e)
    {
        WakeRender();
        var point = new Point(e.X, e.Y);
        if (_textEntry is { } entry)
        {
            // Saisie ouverte : elle reçoit tout (ni page, ni tuile, ni module derrière elle).
            if (e.Type == DeckProtocol.TouchType.Down) entry.FingerDown(point);
            else if (e.Type == DeckProtocol.TouchType.Move) entry.FingerMove(point);
            else entry.FingerUp(point);
            return;
        }
        switch (e.Type)
        {
            case DeckProtocol.TouchType.Down:
                // Un Down alors qu'un contrôle est encore suivi : son Up s'est perdu, on le relâche proprement.
                ResetTouch();
                _touchStart = _touchLast = point;
                _touchMoved = false;
                _dragTarget = FindDraggable(point);
                if (_dragTarget is not null) Drag(_dragTarget, point, DragPhase.Start);
                else SetPressed(FindTile(point));
                break;
            case DeckProtocol.TouchType.Move:
                _touchLast = point;
                if (_dragTarget is not null)
                {
                    Drag(_dragTarget, point, DragPhase.Move);
                }
                else if ((point - _touchStart).Length > TapTolerance)
                {
                    _touchMoved = true;
                    SetPressed(null);
                }
                break;
            case DeckProtocol.TouchType.Up:
                if (_dragTarget is not null)
                {
                    Drag(_dragTarget, _touchLast, DragPhase.End);
                    _dragTarget = null;
                    break;
                }
                SetPressed(null);
                var swipe = _touchLast - _touchStart;
                if (Math.Abs(swipe.X) >= SwipeDistance && Math.Abs(swipe.Y) < Math.Abs(swipe.X) * 0.6)
                    SetPage(_settings.CurrentPage + (swipe.X < 0 ? 1 : -1));
                else if (!_touchMoved)
                    Tap(_touchStart);
                break;
        }
    }

    /// <summary>Plus aucun doigt : relâche la tuile enfoncée et le contrôle suivi (Up perdu, déconnexion).</summary>
    private void ResetTouch()
    {
        SetPressed(null);
        if (_dragTarget is not null)
        {
            var target = _dragTarget;
            _dragTarget = null;
            Drag(target, _touchLast, DragPhase.End);
        }
    }

    private enum DragPhase { Start, Move, End }

    /// <summary>Contrôle réglable au doigt sous le point (volume des applications, curseur), ou null.</summary>
    private FrameworkElement? FindDraggable(Point point)
    {
        for (var d = Screen.InputHitTest(point) as DependencyObject; d is not null; d = Parent(d))
            if (d is FrameworkElement { IsEnabled: true } element and (ITouchDraggable or Slider)) return element;
        return null;
    }

    private void Drag(FrameworkElement target, Point screenPoint, DragPhase phase)
    {
        if (!IsInScreen(target)) return; // élément recréé entre-temps (liste rafraîchie)
        var local = Screen.TranslatePoint(screenPoint, target);
        switch (target)
        {
            case ITouchDraggable draggable:
                if (phase == DragPhase.Start) draggable.TouchStart(local);
                else if (phase == DragPhase.Move) draggable.TouchMove(local);
                else draggable.TouchEnd();
                break;
            case Slider slider when phase != DragPhase.End:
                // Position du doigt le long du curseur -> valeur (sens inversé pour un curseur vertical).
                var ratio = slider.Orientation == Orientation.Horizontal
                    ? local.X / Math.Max(1, slider.ActualWidth)
                    : 1 - local.Y / Math.Max(1, slider.ActualHeight);
                if (slider.IsDirectionReversed) ratio = 1 - ratio;
                slider.Value = slider.Minimum + Math.Clamp(ratio, 0, 1) * (slider.Maximum - slider.Minimum);
                break;
        }
    }

    private Border? FindTile(Point point)
    {
        for (var d = Screen.InputHitTest(point) as DependencyObject; d is not null; d = Parent(d))
            if (d is Border { Tag: ShortcutItem } tile) return tile;
        return null;
    }

    /// <summary>
    /// Appui bref : lance la tuile touchée, sinon « clique » le bouton du module sous le doigt, sinon
    /// simule un clic de souris sur l'élément touché (contrôles qui réagissent au clic sans être des boutons).
    /// </summary>
    private void Tap(Point point)
    {
        try
        {
            var hit = Screen.InputHitTest(point) as DependencyObject;
            for (var d = hit; d is not null; d = Parent(d))
            {
                if (d is DeckPageDot dot)
                {
                    SetPage(dot.Index);
                    return;
                }
                if (d is Border { Tag: ShortcutItem item })
                {
                    _launch(item);
                    return;
                }
                if (d is ButtonBase button)
                {
                    if (!button.IsEnabled) return;
                    // Comportement WPF : un RadioButton déjà coché le reste, seule une case à cocher bascule.
                    if (button is RadioButton radio) radio.IsChecked = true;
                    else if (button is ToggleButton toggle) toggle.IsChecked = toggle.IsChecked != true;
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                    if (button.Command?.CanExecute(button.CommandParameter) == true)
                        button.Command.Execute(button.CommandParameter);
                    return;
                }
            }

            if (hit is UIElement target)
            {
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
            }
        }
        finally
        {
            // Un clic simulé ne doit jamais laisser la souris capturée par la fenêtre invisible.
            if (Mouse.Captured is DependencyObject captured && IsInScreen(captured))
                Mouse.Capture(null);
        }
    }

    private static DependencyObject? Parent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private bool IsInScreen(DependencyObject d)
    {
        for (var current = d; current is not null; current = Parent(current))
            if (current == Screen) return true;
        return false;
    }
}
