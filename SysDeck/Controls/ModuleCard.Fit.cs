using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SysDeck.Controls;

// Carte en taille automatique ajustée aux éléments déplacés : dès qu'un élément est déplacé, agrandi ou masqué, la carte
// prend la taille de ce qui est affiché (au lieu de la mise en page d'origine).
public partial class ModuleCard
{
    // Variation tolérée avant de resserrer la carte : évite qu'elle « respire » quand une valeur change de largeur.
    private const double ShrinkTolerance = 6;

    private bool _fitActive;
    private Rect _fitBounds = Rect.Empty;

    /// <summary>
    /// Le contenu s'est décalé dans la carte (élément déplacé au-delà du bord gauche ou haut) : la fenêtre
    /// se décale d'autant en sens inverse pour que rien ne bouge à l'écran.
    /// </summary>
    public event Action<double, double>? ContentShifted;

    /// <summary>Le contenu recadré a changé de taille (Switch : le cadre en taille automatique se recalcule).</summary>
    public event Action? NaturalSizeChanged;

    /// <summary>Taille du contenu recadré sur ses éléments, à l'échelle 1.</summary>
    public Size ContentNaturalSize => _fitBounds.IsEmpty ? ContentHost.DesiredSize : _fitBounds.Size;

    /// <summary>Espace pris par la carte autour du contenu (marge intérieure, contour), largeur et hauteur cumulées.</summary>
    public Size ChromeInset
    {
        get
        {
            var m = ContentScaler.Margin;
            var b = Chrome.BorderThickness;
            var p = Chrome.Padding;
            return new Size(m.Left + m.Right + b.Left + b.Right + p.Left + p.Right,
                m.Top + m.Bottom + b.Top + b.Bottom + p.Top + p.Bottom);
        }
    }

    /// <summary>Active ou coupe l'ajustement selon la disposition actuelle (appelé après chaque changement).</summary>
    private void UpdateFit()
    {
        // Taille automatique (bureau) ou cadre imposé (Switch, le contenu recadré est mis à l'échelle du cadre) ;
        // une taille fixe choisie sur le bureau est respectée.
        var active = _useDefaultChrome && _appearance is not null && (_fillFrame || _appearance.AutoSize)
            && _appearance.Elements.Values.Any(l => !l.IsDefault);
        if (active != _fitActive)
        {
            _fitActive = active;
            if (active) LayoutUpdated += OnFitLayoutUpdated;
            else
            {
                LayoutUpdated -= OnFitLayoutUpdated;
                ApplyFit(Rect.Empty);
            }
        }
        if (active) FitToElements();
    }

    /// <summary>Carte retirée de l'écran : plus d'abonnement à LayoutUpdated (repris au prochain chargement).</summary>
    private void StopFit()
    {
        if (!_fitActive) return;
        _fitActive = false;
        LayoutUpdated -= OnFitLayoutUpdated;
    }

    private void OnFitLayoutUpdated(object? sender, EventArgs e) => FitToElements();

    private void FitToElements()
    {
        if (!IsLoaded || ContentHost.ActualWidth <= 0) return;
        var bounds = ElementsBounds();
        if (bounds.IsEmpty) return;

        // Grandir tout de suite, se resserrer seulement au-delà de la tolérance (immédiat pendant l'édition).
        if (!_fitBounds.IsEmpty)
        {
            var tolerance = _editMode ? 0.5 : ShrinkTolerance;
            double Pick(double next, double current, bool grows)
                => grows || Math.Abs(next - current) > tolerance ? next : current;
            var left = Pick(bounds.Left, _fitBounds.Left, bounds.Left < _fitBounds.Left);
            var top = Pick(bounds.Top, _fitBounds.Top, bounds.Top < _fitBounds.Top);
            var right = Pick(bounds.Right, _fitBounds.Right, bounds.Right > _fitBounds.Right);
            var bottom = Pick(bounds.Bottom, _fitBounds.Bottom, bounds.Bottom > _fitBounds.Bottom);
            bounds = new Rect(new Point(left, top), new Point(right, bottom));
        }
        if (!_fitBounds.IsEmpty && Near(bounds, _fitBounds)) return;
        ApplyFit(bounds);
    }

    private static bool Near(Rect a, Rect b)
        => Math.Abs(a.Left - b.Left) < 0.5 && Math.Abs(a.Top - b.Top) < 0.5
        && Math.Abs(a.Right - b.Right) < 0.5 && Math.Abs(a.Bottom - b.Bottom) < 0.5;

    /// <summary>Marges (négatives ou positives) qui recadrent le contenu sur <paramref name="bounds"/> ; vide = mise en page d'origine.</summary>
    private void ApplyFit(Rect bounds)
    {
        var oldLeft = _fitBounds.IsEmpty ? 0 : _fitBounds.Left;
        var oldTop = _fitBounds.IsEmpty ? 0 : _fitBounds.Top;
        _fitBounds = bounds;
        if (bounds.IsEmpty)
        {
            ContentHost.ClearValue(MarginProperty);
        }
        else
        {
            var width = ContentHost.ActualWidth;
            var height = ContentHost.ActualHeight;
            ContentHost.Margin = new Thickness(
                Math.Round(-bounds.Left), Math.Round(-bounds.Top),
                Math.Round(bounds.Right - width), Math.Round(bounds.Bottom - height));
        }

        // Taille automatique, pendant l'édition : la carte garde sa place, seul le bord concerné bouge.
        // (Pas au chargement : la position enregistrée correspond déjà à la carte ajustée.)
        var newLeft = bounds.IsEmpty ? 0 : bounds.Left;
        var newTop = bounds.IsEmpty ? 0 : bounds.Top;
        var dx = Math.Round(newLeft) - Math.Round(oldLeft);
        var dy = Math.Round(newTop) - Math.Round(oldTop);
        if (_editMode && (dx != 0 || dy != 0)) ContentShifted?.Invoke(dx, dy);
        NaturalSizeChanged?.Invoke();
    }

    /// <summary>
    /// Contour de tout ce qui est affiché dans le contenu, éléments déplacés compris, dans le repère du contenu.
    /// Chaque élément compte pour ce qu'il dessine, pas pour la place qui lui est réservée.
    /// </summary>
    private Rect ElementsBounds()
    {
        var bounds = Rect.Empty;
        AddBounds(ContentHost, ref bounds);
        return bounds;
    }

    private void AddBounds(Visual node, ref Rect bounds)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (VisualTreeHelper.GetChild(node, i) is not Visual child) continue;
            if (child is UIElement { Visibility: not Visibility.Visible } or UIElement { Opacity: <= 0.01 }) continue;

            if (child is FrameworkElement fe && Movable.GetKey(fe) is not null)
            {
                if (!Movable.IsDisplayed(fe)) continue;
                // Contour réellement dessiné, le même que le cadre d'édition (une tuile étirée ne compte que pour son texte).
                bounds.Union(fe.TransformToAncestor(ContentHost).TransformBounds(Movable.ContentBounds(fe)));
                continue;
            }

            if (!Movable.IsContainer(child) || child is UIElement { RenderTransform.Value.IsIdentity: false })
            {
                // Texte, jauge, graphe… hors des éléments déplaçables : même règle que leurs cadres.
                Movable.AddContent(ContentHost, child, ref bounds);
                continue;
            }
            if (child is Border { Background: SolidColorBrush { Color.A: > 0 } } or Panel { Background: SolidColorBrush { Color.A: > 0 } })
            {
                // Fond réellement visible (bandeau, séparateur…) : son emplacement compte.
                bounds.Union(((Visual)child).TransformToAncestor(ContentHost).TransformBounds(new Rect(((UIElement)child).RenderSize)));
            }
            AddBounds(child, ref bounds);
        }
    }
}
