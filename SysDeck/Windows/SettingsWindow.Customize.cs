using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck;

// Personnalisation d'un module (ouverte depuis la page Modules) : taille, éléments déplaçables, textes, style.
public partial class SettingsWindow
{
    // ---------- Personnaliser ----------

    private ModuleRow? SelectedModule => ModuleSelector.SelectedItem as ModuleRow;

    /// <summary>Apparence modifiée par la page : celle du bureau ou celle de la Switch, selon la cible choisie.</summary>
    private ModuleAppearance? CurrentAppearance
        => SelectedModule is not { } module ? null
            : _customizeSwitch ? SwitchAppearance(module.Id)
            : module.Appearance;

    private void ModuleSelector_SelectionChanged(object sender, SelectionChangedEventArgs? e)
    {
        ModuleEditor.DataContext = CurrentAppearance;
        var id = SelectedModule?.Id;
        MediaBackgroundCard.Visibility = MediaIconsCard.Visibility = id == "media" ? Visibility.Visible : Visibility.Collapsed;
        VolumeStyleCard.Visibility = id == "volumemixer" ? Visibility.Visible : Visibility.Collapsed;
        VisualizerCard.Visibility = id == "visualizer" ? Visibility.Visible : Visibility.Collapsed;
        DiskStyleCard.Visibility = id == "disk" ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbModuleText.Text = SelectedModule?.Label ?? "";
        BuildTextRows();
        BuildButtonRows();
        BuildElementRows();
        BuildAudioDeviceRows();
        BuildAppIconRows();
        UpdateFitCard();
        UpdateCustomizeOptions();
    }

    /// <summary>
    /// « Adaptation du contenu » (une seule propriété, StretchMode) : sur la Switch toujours, sur le bureau
    /// seulement en taille manuelle (en taille automatique le contenu n'est pas contraint).
    /// </summary>
    private void UpdateFitCard()
    {
        FitCard.Visibility = _customizeSwitch || CurrentAppearance?.AutoSize == false ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Ne montre que les options qui ont un sens pour le module affiché (cible Bureau comme Switch) :
    /// section Jauges absente d'un module sans jauge, onglet Textes absent d'un module sans texte réglable.
    /// Si l'onglet sélectionné disparaît, le premier onglet visible est sélectionné.
    /// </summary>
    private void UpdateCustomizeOptions()
    {
        var id = SelectedModule?.Id;
        var diskStyle = CurrentAppearance?.DiskStyle;
        foreach (var editor in new[] { ColorsEditor, CardEditor, GaugesEditor, AnimationsEditor })
        {
            editor.ModuleId = id;
            editor.DiskStyle = diskStyle;
            editor.OnSwitch = _customizeSwitch;
        }
        StyleGaugesSection.Visibility = ModuleCapabilities.HasAnyGaugeOption(id) ? Visibility.Visible : Visibility.Collapsed;

        var hasTexts = id is not null && Controls.TextPart.ByModule.ContainsKey(id);
        TabTextsButton.Visibility = hasTexts ? Visibility.Visible : Visibility.Collapsed;
        // Onglet Boutons : seulement les modules qui ont des boutons (Sortie audio, Volume par application, Lecture en cours).
        TabButtonsButton.Visibility = id is not null && ModuleCapabilities.HasButtons(id) ? Visibility.Visible : Visibility.Collapsed;
        var tabs = new[]
        {
            TabSizeButton, TabElementsButton, TabTextsButton, TabButtonsButton, TabStyleButton
        };
        RefreshStyleEditors();

        // Garde : si aucun onglet n'est visible, il n'y a rien à sélectionner (First lèverait une exception).
        if (tabs.Any(t => t.IsChecked == true && t.Visibility != Visibility.Visible)
            && tabs.FirstOrDefault(t => t.Visibility == Visibility.Visible) is { } visible)
            visible.IsChecked = true;
    }

    /// <summary>
    /// Onglet Style : pour chaque section, l'éditeur n'est visible que si « Propre à ce module » est actif (il
    /// édite alors les réglages du module) ; sinon une ligne « Suit le thème global » le remplace.
    /// </summary>
    private void RefreshStyleEditors()
    {
        var appearance = CurrentAppearance;
        var own = appearance?.Theme;

        void Update(bool custom, ThemeEditor editor, FrameworkElement host, FrameworkElement inheritBar)
        {
            var useOwn = custom && own is not null;
            editor.DataContext = useOwn ? own : _widgetManager.Theme;
            host.Visibility = useOwn ? Visibility.Visible : Visibility.Collapsed;
            inheritBar.Visibility = useOwn ? Visibility.Collapsed : Visibility.Visible;
        }

        Update(appearance?.UseCustomTheme == true, ColorsEditor, ColorsEditorHost, InheritColorsBar);
        Update(appearance?.CustomCard == true, CardEditor, CardEditorHost, InheritCardBar);
        Update(appearance?.CustomGauges == true, GaugesEditor, GaugesEditorHost, InheritGaugesBar);
        Update(appearance?.CustomAnimations == true, AnimationsEditor, AnimationsEditorHost, InheritAnimationsBar);
    }

    // ---------- Éléments du module (disposition) ----------

    /// <summary>
    /// Une ligne par élément déplaçable du module affiché : visibilité, taille, couleur. Les changements
    /// s'appliquent en direct, comme ceux faits à la souris en mode édition.
    /// </summary>
    private sealed class ElementRow : INotifyPropertyChanged
    {
        private readonly ElementLayout _layout;
        private readonly Action _changed;

        public ElementRow(string key, ElementLayout layout, Action changed)
        {
            Key = key;
            _layout = layout;
            _changed = changed;
        }

        public string Key { get; }
        public string Label => Controls.Movable.LabelFor(Key);

        public bool Visible { get => !_layout.Hidden; set { _layout.Hidden = !value; Changed(); } }
        public double Scale { get => _layout.Scale; set { _layout.Scale = Math.Round(value, 3); Changed(); } }
        public string? AccentStart { get => _layout.AccentStart; set { _layout.AccentStart = value; Changed(); } }
        public string? AccentEnd { get => _layout.AccentEnd; set { _layout.AccentEnd = value; Changed(); } }
        public bool HasAccent => _layout.HasAccent;

        public void ResetAll()
        {
            _layout.X = _layout.Y = 0;
            _layout.Scale = 1;
            _layout.Hidden = false;
            _layout.AccentStart = _layout.AccentEnd = null;
            Changed();
        }

        private void Changed()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            _changed();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void BuildElementRows()
    {
        if (SelectedModule is not { } module)
        {
            ElementsList.ItemsSource = null;
            return;
        }

        var appearance = CurrentAppearance!;
        var keys = _customizeSwitch ? Deck.GetElementKeys(module.Id) : _widgetManager.GetElementKeys(module.Id);
        ElementsEmptyText.Text = _customizeSwitch
            ? "Affiche ce module sur la Switch (page Switch) pour régler ses éléments."
            : "Affiche ce module (page Modules) pour régler ses éléments.";        ElementsEmptyText.Visibility = keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ElementsList.ItemsSource = keys
            .Select(key => new ElementRow(key, appearance.GetElement(key), appearance.NotifyElementsChanged))
            .ToList();
    }

    private void ResetElement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ElementRow row }) row.ResetAll();
    }

    // ---------- Textes du module ----------

    private sealed record TextRow(string Label, TextPartStyle Style, bool Editable, string? DefaultText);

    private void ResetTextRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TextRow row }) row.Style.Reset();
    }

    private readonly List<(TextPartStyle Style, PropertyChangedEventHandler Handler)> _textSubscriptions = new();

    /// <summary>Une ligne par texte du module choisi ; chaque changement s'applique en direct au module.</summary>
    private void BuildTextRows()
    {
        foreach (var (style, handler) in _textSubscriptions)
            style.PropertyChanged -= handler;
        _textSubscriptions.Clear();

        if (SelectedModule is not { } module || !Controls.TextPart.ByModule.TryGetValue(module.Id, out var parts))
        {
            TextPartsList.ItemsSource = null;
            return;
        }

        var appearance = CurrentAppearance!;
        var rows = new List<TextRow>();
        foreach (var part in parts)
        {
            var style = appearance.GetText(part.Key);
            PropertyChangedEventHandler handler = (_, _) => OnModuleChanged(module.Id, appearance, nameof(ModuleAppearance.Texts));
            style.PropertyChanged += handler;
            _textSubscriptions.Add((style, handler));
            rows.Add(new TextRow(part.Label, style, part.IsEditable, part.DefaultText));
        }
        TextPartsList.ItemsSource = rows;
    }

    private void ResetTexts_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAppearance is not { } appearance) return;
        appearance.ResetTexts();
        BuildTextRows();
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAppearance is not { } appearance) return;

        var dialog = new OpenFileDialog
        {
            Title = "Choisir une image de fond",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var previous = appearance.BackgroundImage;
            appearance.BackgroundImage = BackgroundImageStore.SaveCopy(dialog.FileName);
            DeleteBackgroundIfUnused(previous);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Impossible d'utiliser cette image :\n{ex.Message}", "SysDeck");
        }
    }

    private void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAppearance is not { } appearance) return;

        var previous = appearance.BackgroundImage;
        appearance.BackgroundImage = null;
        DeleteBackgroundIfUnused(previous);
    }

    private void LayoutEdit_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _widgetManager.SetLayoutEditMode(LayoutEditCheck.IsChecked == true);
    }

    private void OnLayoutEditModeChanged(bool enabled) => LayoutEditCheck.IsChecked = enabled;

    private void ShowHiddenElements_Click(object sender, RoutedEventArgs e)
    {
        CurrentAppearance?.ShowAllElements();
        BuildElementRows();
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        CurrentAppearance?.ResetLayout();
        BuildElementRows();
    }

}
