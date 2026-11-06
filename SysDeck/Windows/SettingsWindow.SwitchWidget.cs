using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SysDeck.Deck;

namespace SysDeck;

// Inspecteur d'un widget de la page Switch : l'éditeur de Personnaliser (cible Switch) est emprunté tant qu'un widget
// est sélectionné dans l'aperçu, puis rendu à sa page quand on rouvre Personnaliser. Taille d'icône propre à une tuile.
public partial class SettingsWindow
{
    private const string DefaultInspectorEmptyText = "Sélectionne une tuile ou un widget dans l'aperçu pour le configurer.";

    private string? _inspectedWidget;
    private bool _widgetLoading;

    // Éditeur emprunté à Personnaliser : emplacement d'origine pour le rendre tel quel.
    private bool _editorBorrowed;
    private bool _borrowedFromDesktop;
    private Panel? _customizeEditorParent;
    private int _previewAreaIndex, _moduleEditorIndex;
    private Thickness _previewAreaMargin, _customizeScrollerPadding, _selectionScrollerPadding;

    /// <summary>Élément de l'écran cliqué dans l'aperçu (hors tuile) : réglages du widget, ou indication pour les autres.</summary>
    private void InspectElement(string? id)
    {
        var module = id is null ? null : ModuleSelector.Items.Cast<ModuleRow>().FirstOrDefault(m => m.Id == id);
        if (module is null || Deck.Screen.CardOf(module.Id) is null)
        {
            HideWidgetInspector();
            if (_inspected is not null) return;
            DeckInspectorEmpty.Text = id switch
            {
                "apps" => "Bloc des tuiles : clique sur une tuile pour la configurer. Taille des tuiles et des icônes, opacité : onglet Réglages.",
                "clock" => "Horloge : son opacité se règle dans l'onglet Réglages.",
                null => DefaultInspectorEmptyText,
                _ => "Cet élément n'a pas d'autres réglages."
            };
            DeckInspectorEmpty.Visibility = Visibility.Visible;
            return;
        }

        _inspectedWidget = module.Id;
        BorrowModuleEditor();
        if (!ReferenceEquals(ModuleSelector.SelectedItem, module)) ModuleSelector.SelectedItem = module;
        if (TargetSwitch.IsChecked != true) TargetSwitch.IsChecked = true; // recharge l'éditeur sur l'apparence Switch
        else if (ReferenceEquals(ModuleSelector.SelectedItem, module)) ModuleSelector_SelectionChanged(ModuleSelector, null);
        DeckWidgetTitle.Text = module.Label;
        EditOnSwitchRow.Visibility = Visibility.Collapsed; // déjà sur la page Switch : la liste des éléments suffit
        ElementsExpander.IsExpanded = false;
        UpdateWidgetScale();
        DeckInspector.Visibility = Visibility.Collapsed;
        DeckInspectorEmpty.Visibility = Visibility.Collapsed;
        DeckWidgetInspector.Visibility = Visibility.Visible;
        // L'aperçu cliquable a besoin de sa taille réelle : recalculé après la mise en page.
        SchedulePreviewRefresh();
    }

    private DispatcherTimer? _previewRefreshTimer;
    private bool _previewRefreshHooked;

    /// <summary>
    /// Aperçu du widget recalculé une fois la mise en page de l'écran Switch terminée (élément affiché ou masqué,
    /// taille changée) : sinon l'image et les zones cliquables gardent l'ancienne taille et se décalent.
    /// </summary>
    private void SchedulePreviewRefresh()
    {
        if (!_previewRefreshHooked)
        {
            _previewRefreshHooked = true;
            // Un module annonce ses éléments changés (masqués, réaffichés, déplacés) : l'aperçu suit.
            _widgetManager.Deck.Screen.AddHandler(Controls.Movable.ElementsChangedEvent,
                new RoutedEventHandler((_, _) => { if (_inspectedWidget is not null) SchedulePreviewRefresh(); }), handledEventsToo: true);
        }
        _previewRefreshTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _previewRefreshTimer!.Stop();
            _widgetManager.Deck.Screen.UpdateLayout();
            if (_inspectedWidget is { } id)
            {
                _widgetManager.Deck.Place(id); // module en taille automatique : cadre recalé sur ses éléments
                _widgetManager.Deck.Screen.UpdateLayout();
                DeckEditor.RefreshFrames();
            }
            RefreshPreviewElements();
        }, Dispatcher);
        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Start();
    }

    /// <summary>Élément d'un widget cliqué dans le grand aperçu : le widget est inspecté et l'élément sélectionné à droite.</summary>
    private void InspectModuleElement(string moduleId, string key)
    {
        if (_inspectedWidget != moduleId)
        {
            InspectTile(null);
            InspectElement(moduleId);
        }
        if (_inspectedWidget != moduleId) return;
        var part = Deck.Screen.CardOf(moduleId)?.GetPreviewParts()
                       .FirstOrDefault(p => p.Key == key && p.Section == "elements")
                   ?? new Controls.ModulePreviewPart(key, Controls.Movable.LabelFor(key), "elements", Rect.Empty);
        SelectPreviewPart(part);
        SchedulePreviewRefresh(); // cadre de sélection dans le petit aperçu
    }

    private void HideWidgetInspector()
    {
        _inspectedWidget = null;
        DeckWidgetInspector.Visibility = Visibility.Collapsed;
    }

    /// <summary>Place l'aperçu cliquable et l'éditeur de Personnaliser dans l'inspecteur de la page Switch.</summary>
    private void BorrowModuleEditor()
    {
        if (_editorBorrowed || CustomizePreviewArea.Parent is not Panel parent) return;
        _editorBorrowed = true;
        _borrowedFromDesktop = TargetSwitch.IsChecked != true;
        _customizeEditorParent = parent;
        _previewAreaIndex = parent.Children.IndexOf(CustomizePreviewArea);
        _moduleEditorIndex = parent.Children.IndexOf(ModuleEditor);
        _previewAreaMargin = CustomizePreviewArea.Margin;
        _customizeScrollerPadding = CustomizeScroller.Padding;
        _selectionScrollerPadding = SelectionScroller.Padding;

        MoveToHost(CustomizePreviewArea, DeckWidgetPreviewHost);
        MoveToHost(ModuleEditor, DeckWidgetEditorHost);
        CustomizePreviewArea.Margin = new Thickness(0, 0, 14, 0);
        CustomizeScroller.Padding = new Thickness(0, 0, 8, 8);
        SelectionScroller.Padding = new Thickness(8, 0, 0, 8);
    }

    /// <summary>Rend l'aperçu et l'éditeur à la page Personnaliser (à son ouverture), avec la cible d'avant l'emprunt.</summary>
    private void ReturnModuleEditor()
    {
        if (!_editorBorrowed || _customizeEditorParent is not { } parent) return;
        _editorBorrowed = false;
        HideWidgetInspector();
        if (_inspected is null)
        {
            DeckInspectorEmpty.Text = DefaultInspectorEmptyText;
            DeckInspectorEmpty.Visibility = Visibility.Visible;
        }

        MoveFromParent(CustomizePreviewArea);
        MoveFromParent(ModuleEditor);
        // Indices croissants : l'aperçu (avant) est réinséré en premier.
        foreach (var (element, index) in new[] { (CustomizePreviewArea, _previewAreaIndex), ((FrameworkElement)ModuleEditor, _moduleEditorIndex) }
                     .OrderBy(pair => pair.Item2))
            parent.Children.Insert(Math.Clamp(index, 0, parent.Children.Count), element);
        CustomizePreviewArea.Margin = _previewAreaMargin;
        CustomizeScroller.Padding = _customizeScrollerPadding;
        SelectionScroller.Padding = _selectionScrollerPadding;
        if (_borrowedFromDesktop) TargetDesktop.IsChecked = true;
        EditOnSwitchRow.Visibility = _customizeSwitch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateWidgetScale()
    {
        if (_inspectedWidget is not { } id || Deck.Settings.Find(id) is not { } element) return;
        if (element.Scale is null) Deck.Place(id); // échelle calculée à la première mise en place
        _widgetLoading = true;
        DeckWidgetScaleSlider.Value = Math.Clamp(element.Scale ?? 1, DeckWidgetScaleSlider.Minimum, DeckWidgetScaleSlider.Maximum);
        _widgetLoading = false;
        DeckWidgetScaleText.Text = $"{DeckWidgetScaleSlider.Value * 100:0} %";
    }

    private void DeckWidgetScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DeckWidgetScaleText is null) return;
        DeckWidgetScaleText.Text = $"{e.NewValue * 100:0} %";
        if (_widgetLoading || _inspectedWidget is not { } id || Deck.Settings.Find(id) is not { } element) return;
        element.Scale = Math.Round(e.NewValue, 2);
        Deck.Place(id);
        DeckEditor.RefreshFrames();
        ScheduleSave();
        SchedulePreviewRefresh();
    }

    private void DeckWidgetCopyDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectedWidget is null) return;
        CopyFromDesktop_Click(sender, e);
        SchedulePreviewRefresh();
    }

    private void DeckWidgetHide_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectedWidget is not { } id) return;
        HideWidgetInspector();
        Deck.SetVisible(id, false);
        DeckEditor.Rebuild();
        RefreshDeckElementsList();
        InspectTile(null);
        ScheduleSave();
    }

    // ---------- Taille d'icône propre à une tuile ----------

    private void LoadTileIconSize(Models.ShortcutItem item)
    {
        var own = DeckSettings.ClampIconScale(item.IconScale);
        DeckInspectorOwnIconSize.IsChecked = own is not null;
        DeckInspectorIconSizeSlider.Value = own ?? Deck.Settings.TileIconScale;
        DeckInspectorIconSizeRow.Visibility = own is null ? Visibility.Collapsed : Visibility.Visible;
        DeckInspectorIconSizeText.Text = $"{DeckInspectorIconSizeSlider.Value * 100:0} %";
    }

    /// <summary>Nom de la tuile tel qu'il s'affiche sur la Switch (choix propre à la tuile, sinon réglage de la page).</summary>
    private void LoadTileShowName(Models.ShortcutItem item)
        => DeckInspectorShowName.IsChecked = item.ShowName ?? (Deck.Settings.ShowTileNames && !item.HideName);

    private void DeckInspectorShowName_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectorLoading || _inspected is not { } item) return;
        var show = DeckInspectorShowName.IsChecked == true;
        item.ShowName = show; // désormais indépendant du réglage de la page
        item.HideName = !show;
        Deck.RefreshShortcuts();
        ScheduleSave();
    }

    private void DeckInspectorOwnIconSize_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectorLoading || _inspected is not { } item) return;
        var own = DeckInspectorOwnIconSize.IsChecked == true;
        if (own) DeckInspectorIconSizeSlider.Value = Deck.Settings.TileIconScale; // part de la taille actuelle
        item.IconScale = own ? DeckSettings.ClampIconScale(DeckInspectorIconSizeSlider.Value) : null;
        DeckInspectorIconSizeRow.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        Deck.RefreshShortcuts();
        ScheduleSave();
    }

    private void DeckInspectorIconSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DeckInspectorIconSizeText is null) return;
        DeckInspectorIconSizeText.Text = $"{e.NewValue * 100:0} %";
        if (_inspectorLoading || _inspected is not { } item || DeckInspectorOwnIconSize.IsChecked != true) return;
        item.IconScale = DeckSettings.ClampIconScale(e.NewValue);
        Deck.RefreshShortcuts();
        ScheduleSave();
    }
}
