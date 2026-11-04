using System.Windows;
using System.Windows.Controls;
using SysDeck.Services;

namespace SysDeck.Controls;

// Disposition libre des éléments déplaçables (Movable) et mode édition avec leurs cadres.
public partial class ModuleCard
{
    /// <summary>Clés des éléments déplaçables du module, dans l'ordre d'affichage.</summary>
    public IReadOnlyList<string> MovableKeys
        => Movables.Select(e => Movable.GetKey(e)!).Distinct().ToList();

    /// <summary>Éléments déplaçables du module (édition depuis l'aperçu de la Switch).</summary>
    public IReadOnlyList<FrameworkElement> MovableElements => Movables.ToList();

    /// <summary>Taille actuelle de la carte, pour initialiser la taille manuelle à partir de l'automatique.</summary>
    public Size CurrentCardSize => new(Chrome.ActualWidth, Chrome.ActualHeight);

    // ---------- Disposition libre des éléments ----------

    private ModuleAppearance? _appearance;
    private List<FrameworkElement>? _movables;
    private readonly List<(FrameworkElement Element, MovableAdorner Adorner)> _adorners = new();
    private bool _editMode;

    // Mis en cache une fois la carte chargée (l'arbre visuel existe) ; vidé par ElementsChanged et à chaque
    // chargement/déchargement. Avant le chargement, ou si rien n'est encore trouvé (modèles pas générés),
    // on ne retient rien : un cache vide figerait le résultat.
    private List<FrameworkElement> Movables
    {
        get
        {
            if (_movables is not null) return _movables;
            var found = Movable.FindAll(ContentHost);
            if (IsLoaded && found.Count > 0) _movables = found;
            return found;
        }
    }

    /// <summary>Recherche à nouveau les éléments déplaçables (le contenu du module a changé).</summary>
    private void RefreshMovables()
    {
        _movables = null;
        // Positions d'abord (nouveaux éléments à leur place), puis les cadres d'édition éventuels.
        ApplyElementLayouts();
        if (_editMode)
        {
            SetEditMode(false);
            SetEditMode(true);
        }
    }

    public void ApplyElementLayouts()
    {
        if (!IsLoaded || _appearance is null) return;
        foreach (var element in Movables)
            ApplyElementLayout(element);
        UpdateFit();
    }

    private void ApplyElementLayout(FrameworkElement element)
    {
        var key = Movable.GetKey(element)!;
        _appearance!.Elements.TryGetValue(key, out var layout);
        Movable.Apply(element, layout);
        UpdateAdornerVisibility(element);
        UpdateFit(); // un déplacement (transformation) ne relance pas la mise en page : on recadre ici
        // Le cadre d'édition suit la transformation de l'élément, mais seulement après une mise à jour de sa couche.
        // (Update lève une exception si l'élément n'a encore aucun cadre.)
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(element);
        if (layer?.GetAdorners(element) is { Length: > 0 })
            layer.Update(element);
    }

    /// <summary>
    /// Pas de cadre pour un élément masqué (il se réaffiche par le menu « Éléments masqués ») ni pour un
    /// élément désactivé par une option (titre décoché…) : seuls les éléments réellement affichés se déplacent.
    /// </summary>
    private void UpdateAdornerVisibility(FrameworkElement element)
    {
        var hidden = _appearance is not null && _appearance.Elements.TryGetValue(Movable.GetKey(element)!, out var layout) && layout.Hidden;
        var visible = !hidden && Movable.IsDisplayed(element);
        foreach (var (e, adorner) in _adorners)
            if (e == element) adorner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MovableIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element) UpdateAdornerVisibility(element);
    }

    private void MovableSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element) UpdateAdornerVisibility(element);
    }

    /// <summary>Clés des éléments actuellement masqués du module.</summary>
    public IReadOnlyList<string> HiddenKeys()
        => _appearance is null ? Array.Empty<string>()
            : MovableKeys.Where(k => _appearance.Elements.TryGetValue(k, out var l) && l.Hidden).ToList();

    private void ShowElement(string key)
    {
        if (_appearance is null) return;
        _appearance.GetElement(key).Hidden = false;
        ApplyElementLayouts();
        _save();
    }

    private ContextMenu BuildHiddenElementsMenu()
    {
        var menu = new ContextMenu();
        Movable.AddHiddenMenu(menu, HiddenKeys, ShowElement);
        return menu;
    }

    /// <summary>Mode édition : chaque élément déplaçable reçoit un cadre pour le déplacer, le redimensionner ou le masquer.</summary>
    public void SetEditMode(bool enabled)
    {
        if (!_useDefaultChrome || _editMode == enabled) return;
        _editMode = enabled;
        if (!IsLoaded) return; // appliqué au chargement

        foreach (var (element, adorner) in _adorners)
        {
            System.Windows.Documents.AdornerLayer.GetAdornerLayer(element)?.Remove(adorner);
            element.IsVisibleChanged -= MovableIsVisibleChanged;
            element.SizeChanged -= MovableSizeChanged;
        }
        _adorners.Clear();

        // Menu du module en édition : seul moyen de réafficher quand tous les éléments sont masqués.
        ContextMenu = enabled ? BuildHiddenElementsMenu() : null;

        if (enabled && _appearance is not null)
        {
            foreach (var element in Movables)
            {
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(element);
                if (layer is null) continue;
                var key = Movable.GetKey(element)!;
                var adorner = new MovableAdorner(element, key,
                    k => _appearance!.GetElement(k),
                    _ => ApplyElementLayout(element),
                    _save,
                    HiddenKeys,
                    ShowElement);
                layer.Add(adorner);
                // Le cadre suit l'affichage de l'élément quand une option l'active ou le désactive.
                element.IsVisibleChanged += MovableIsVisibleChanged;
                element.SizeChanged += MovableSizeChanged;
                _adorners.Add((element, adorner));
            }
        }

        ApplyElementLayouts();
    }
}
