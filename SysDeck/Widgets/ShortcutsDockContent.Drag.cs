using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SysDeck.Models;

namespace SysDeck.Widgets;

// Réorganisation des icônes par appui long (glisser pour échanger les places).
public partial class ShortcutsDockContent
{
    private DispatcherTimer? _longPressTimer;
    private ShortcutItem? _pressedItem;
    private FrameworkElement? _pressedRoot;
    private Button? _pressedButton;
    private Point _pressStart;
    private bool _isDragging;
    private ShortcutItem? _draggedItem;
    private FrameworkElement? _draggedRoot;

    // ---------- Réorganisation par appui long ----------
    // Un clic bref reste un lancement (Launch_Click, inchangé). Si le bouton reste enfoncé au-delà
    // du délai ci-dessous sans bouger de plus de quelques pixels, l'icône devient "attrapée" : elle
    // suit le curseur et échange sa place avec l'icône la plus proche, comme sur le dock macOS ou un
    // springboard iOS.

    private void Icon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: ShortcutItem item } button) return;
        if (VisualTreeHelper.GetParent(button) is not FrameworkElement root) return;

        _pressedItem = item;
        _pressedRoot = root;
        _pressedButton = button;
        _pressStart = e.GetPosition(DockPill);

        _longPressTimer?.Stop();
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer!.Stop();
            BeginDrag();
        };
        _longPressTimer.Start();
    }

    private void Icon_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null) return;
        var pos = e.GetPosition(DockPill);

        if (!_isDragging)
        {
            // Un mouvement trop ample avant la fin du délai signifie que l'utilisateur fait glisser
            // rapidement plutôt que de maintenir en place : on annule l'appui long et on laisse le
            // clic normal (ou rien) se produire.
            if ((pos - _pressStart).Length > 6)
            {
                _longPressTimer?.Stop();
                _pressedItem = null;
                _pressedRoot = null;
                _pressedButton = null;
            }
            return;
        }

        if (_draggedRoot?.FindName("IconDrag") is TranslateTransform dragT)
        {
            var delta = pos - _pressStart;
            dragT.X = delta.X;
            dragT.Y = delta.Y;
        }

        // Une fois la souris capturée par le Button (BeginDrag), les MouseMove ne remontent plus
        // jusqu'à Dock_MouseMove (câblé sur DockPill, un ancêtre) : WPF ne fait pas remonter le
        // bouillonnement au-delà de l'élément qui a la capture dans ce cas. On calcule donc ici
        // l'icône la plus proche du curseur pour décider si un échange de place doit avoir lieu.
        if (_shortcuts is not null && _draggedItem is not null)
        {
            var closestItem = FindClosestIcon(pos)?.Item;
            if (closestItem is not null && !ReferenceEquals(closestItem, _draggedItem))
            {
                int oldIndex = _shortcuts.IndexOf(_draggedItem);
                int newIndex = _shortcuts.IndexOf(closestItem);
                if (oldIndex >= 0 && newIndex >= 0)
                    _shortcuts.Move(oldIndex, newIndex);
            }
        }
    }

    private void Icon_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _longPressTimer?.Stop();

        if (_isDragging)
        {
            EndDrag();
            e.Handled = true; // ce relâchement conclut un glisser, pas un clic : on supprime le Click
        }

        _pressedItem = null;
        _pressedRoot = null;
        _pressedButton = null;
    }

    private void Icon_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Filet de sécurité : si la capture souris est perdue de façon inattendue (alt-tab, etc.)
        // pendant un glisser, on revient proprement à l'état normal au lieu de rester bloqué.
        if (_isDragging)
            EndDrag();
    }

    private void BeginDrag()
    {
        if (_pressedRoot is null || _pressedItem is null || _pressedButton is null) return;

        _isDragging = true;
        _draggedRoot = _pressedRoot;
        _draggedItem = _pressedItem;

        Panel.SetZIndex(_draggedRoot, 1000);
        // La capture reste sur le Button (celui qui porte nos gestionnaires Preview*), pas sur le
        // Grid parent : capturer un ancêtre aurait empêché ces événements de continuer à atteindre
        // le Button, puisqu'avec la capture, le tunneling part de la racine jusqu'à l'élément capturé
        // seulement, sans redescendre vers ses propres descendants.
        Mouse.Capture(_pressedButton);
        _draggedRoot.Opacity = 0.85;

        // Une fois la capture posée, Dock_MouseMove (câblé sur DockPill, un ancêtre du Button
        // capturé) cesse de recevoir les MouseMove — voir Icon_PreviewMouseMove, qui prend le relais
        // pour la détection d'échange. Il ne rafraîchira donc plus l'étiquette ni le survol normal
        // des autres icônes pendant le glisser : on les remet ici à leur état de repos une bonne
        // fois pour toutes.
        HideNameLabel();
        ForEachIconRoot(root =>
        {
            if (ReferenceEquals(root, _draggedRoot)) return;
            if (root.FindName("IconScale") is ScaleTransform s) { s.ScaleX = 1; s.ScaleY = 1; }
            if (root.FindName("IconLift") is TranslateTransform l) { l.X = 0; l.Y = 0; }
        });
    }

    private void EndDrag()
    {
        // Garde de réentrance : Mouse.Capture(null) déclenche LostMouseCapture de façon synchrone, qui
        // rappellerait EndDrag (et lèverait ReorderRequested deux fois). On passe donc l'état à faux AVANT.
        if (!_isDragging) return;
        _isDragging = false;

        if (_draggedRoot is not null)
        {
            _draggedRoot.Opacity = 1.0;
            Panel.SetZIndex(_draggedRoot, 0);
            if (_draggedRoot.FindName("IconDrag") is TranslateTransform dragT)
            {
                dragT.X = 0;
                dragT.Y = 0;
            }
        }

        Mouse.Capture(null);
        _draggedRoot = null;
        _draggedItem = null;

        ReorderRequested?.Invoke();
    }
}
