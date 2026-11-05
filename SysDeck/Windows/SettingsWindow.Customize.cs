using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck;

// Personnalisation d'un module (ouverte depuis la page Modules) : taille, éléments déplaçables, textes, style.
public partial class SettingsWindow
{
    // ---------- Personnaliser ----------

    private ModuleRow? SelectedModule => ModuleSelector.SelectedItem as ModuleRow;
    private ModulePreviewPart? _selectedPreviewPart;
    private List<ElementRow> _elementRows = new();
    private List<TextRow> _textRows = new();
    private List<ButtonRow> _buttonRows = new();
    private StackPanel? _widgetOptions;

    private void InitializeCustomizeGroups()
    {
        MoveToHost(SizeCard, SizeExpanderHost);
        MoveToHost(FitCard, FitExpanderHost);
        MoveToHost(ElementRowsPanel, SelectedElementListHost);
        MoveToHost(TextRowsPanel, SelectedTextListHost);
        MoveToHost(ButtonRowsPanel, SelectedButtonListHost);
        ElementRowsBorder.Visibility = Visibility.Collapsed;
        ButtonRowsBorder.Visibility = Visibility.Collapsed;
        MoveToHost(TabElements, ElementsExpanderHost);
        MoveToHost(TabTexts, TextsExpanderHost);
        MoveToHost(TabButtons, ButtonsExpanderHost);
        MoveToHost(StyleColorsSection, ColorsExpanderHost);

        var widgetOptions = _widgetOptions = new StackPanel();
        OptionsExpanderHost.Content = widgetOptions;
        MoveToPanel(StyleCardSection, widgetOptions);
        foreach (var child in TabSize.Children.OfType<FrameworkElement>().ToList())
        {
            TabSize.Children.Remove(child);
            widgetOptions.Children.Add(child);
        }
        MoveChildren(TabStyle, widgetOptions);
        MoveToPanel(StyleGaugesSection, widgetOptions);
        MoveToPanel(StyleAnimationsSection, widgetOptions);
    }

    private static void MoveToHost(FrameworkElement element, ContentControl host)
    {
        MoveFromParent(element);
        host.Content = element;
    }

    private static void MoveToPanel(FrameworkElement element, Panel panel)
    {
        MoveFromParent(element);
        panel.Children.Add(element);
    }

    private static void MoveFromParent(FrameworkElement element)
    {
        if (element.Parent is Panel parent) parent.Children.Remove(element);
        else if (element.Parent is ContentControl contentParent) contentParent.Content = null;
        else if (element.Parent is Decorator decoratorParent) decoratorParent.Child = null;
    }

    private static void MoveChildren(Panel source, Panel destination)
    {
        foreach (var child in source.Children.OfType<FrameworkElement>().ToList())
        {
            source.Children.Remove(child);
            destination.Children.Add(child);
        }
    }

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
        TileClockCard.Visibility = id == "tileclock" ? Visibility.Visible : Visibility.Collapsed;
        WeatherCard.Visibility = id is "weather" or "airquality" or "sun" ? Visibility.Visible : Visibility.Collapsed;
        SimHubCard.Visibility = id == "simhub" ? Visibility.Visible : Visibility.Collapsed;
        SensorBarsCard.Visibility = id == "sensorbars" ? Visibility.Visible : Visibility.Collapsed;
        if (id == "sensorbars") BuildSensorBarsEditor();
        TemperatureCard.Visibility = id == "temperatures" ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbModuleText.Text = SelectedModule?.Label ?? "";
        _selectedPreviewPart = null;
        SelectedPartTitle.Text = "Sélectionnez un élément dans l'aperçu";
        SelectedPartEmptyText.Visibility = Visibility.Visible;
        SelectedElementListHost.Visibility = Visibility.Collapsed;
        SelectedTextListHost.Visibility = Visibility.Collapsed;
        SelectedButtonListHost.Visibility = Visibility.Collapsed;
        SelectedElementAdvancedHost.Children.Clear();
        RestoreAdvancedOptions();
        RefreshPreviewElements();
        BuildTextRows();
        BuildButtonRows();
        BuildElementRows();
        BuildAudioDeviceRows();
        BuildAppIconRows();
        UpdateFitCard();
        UpdateCustomizeOptions();
        ShowModuleSpecificOptions();
    }

    /// <summary>
    /// « Adaptation du contenu » (une seule propriété, StretchMode) : sur la Switch toujours, sur le bureau
    /// seulement en taille manuelle (en taille automatique le contenu n'est pas contraint).
    /// </summary>
    private void UpdateFitCard()
    {
        FitCard.Visibility = _customizeSwitch || CurrentAppearance?.AutoSize == false ? Visibility.Visible : Visibility.Collapsed;
        FitExpander.Visibility = FitCard.Visibility;
        SizeExpander.Visibility = SizeCard.Visibility;
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
        StyleAnimationsSection.Visibility = ModuleCapabilities.HasAnyGaugeOption(id) ? Visibility.Visible : Visibility.Collapsed;

        var hasTexts = id is not null && Controls.TextPart.ByModule.ContainsKey(id);
        TabTextsButton.Visibility = hasTexts ? Visibility.Visible : Visibility.Collapsed;
        TabTexts.Visibility = hasTexts ? Visibility.Visible : Visibility.Collapsed;
        TextsExpander.Visibility = hasTexts ? Visibility.Visible : Visibility.Collapsed;
        // Onglet Boutons : seulement les modules qui ont des boutons (Sortie audio, Volume par application, Lecture en cours).
        var hasButtons = id is not null && ModuleCapabilities.HasButtons(id);
        TabButtonsButton.Visibility = hasButtons ? Visibility.Visible : Visibility.Collapsed;
        TabButtons.Visibility = hasButtons ? Visibility.Visible : Visibility.Collapsed;
        ButtonsExpander.Visibility = hasButtons ? Visibility.Visible : Visibility.Collapsed;
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

    private void PreviewSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string section }) return;

        if (section == "background")
        {
            ShowBackgroundSettings();
            return;
        }

        var (expander, target) = section switch
        {
            "size" => (SizeExpander, (FrameworkElement)(SizeCard.Visibility == Visibility.Visible ? SizeCard : FitCard)),
            "elements" => (ElementsExpander, (FrameworkElement)TabElements),
            "texts" when TextsExpander.Visibility == Visibility.Visible => (TextsExpander, (FrameworkElement)TabTexts),
            "buttons" when ButtonsExpander.Visibility == Visibility.Visible => (ButtonsExpander, (FrameworkElement)TabButtons),
            "buttons" => (ElementsExpander, (FrameworkElement)TabElements),
            _ => (OptionsExpander, (FrameworkElement)StyleCardSection)
        };

        expander.IsExpanded = true;
        target.BringIntoView();
    }

    private void RefreshPreviewElements()
    {
        PreviewWidgetParts.Children.Clear();
        if (SelectedModule is not { } module)
        {
            PreviewWidgetViewbox.Visibility = Visibility.Collapsed;
            PreviewWidgetEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var card = _customizeSwitch
            ? _widgetManager.Deck.Screen.CardOf(module.Id)
            : _widgetManager.GetModuleCard(module.Id);
        if (card is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 })
        {
            PreviewWidgetViewbox.Visibility = Visibility.Collapsed;
            PreviewWidgetEmptyText.Visibility = Visibility.Visible;
            PreviewWidgetOptionsButton.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewWidgetEmptyText.Visibility = Visibility.Collapsed;
        PreviewWidgetOptionsButton.Visibility = Visibility.Visible;
        PreviewWidgetViewbox.Visibility = Visibility.Visible;
        PreviewWidgetSurface.Width = card.ActualWidth;
        PreviewWidgetSurface.Height = card.ActualHeight;
        PreviewWidgetSnapshot.Background = new VisualBrush(card)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, card.ActualWidth, card.ActualHeight)
        };

        var background = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Tag = "background" };
        background.Click += PreviewSettings_Click;
        PreviewWidgetParts.Children.Add(background);
        Canvas.SetLeft(background, 0);
        Canvas.SetTop(background, 0);
        background.Width = card.ActualWidth;
        background.Height = card.ActualHeight;

        foreach (var part in card.GetPreviewParts())
        {
            var bounds = part.Bounds;
            bounds.Intersect(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2) continue;

            var selected = _selectedPreviewPart is { } current
                && current.Key == part.Key && current.Section == part.Section;
            var hotspot = new Button
            {
                Style = (Style)FindResource("PreviewHotspot"),
                Tag = part,
                ToolTip = $"Modifier : {part.Label}",
                BorderBrush = selected ? (Brush)FindResource("UiAccent") : Brushes.Transparent,
                BorderThickness = selected ? new Thickness(2) : new Thickness(1)
            };
            hotspot.Click += PreviewPart_Click;
            PreviewWidgetParts.Children.Add(hotspot);
            Canvas.SetLeft(hotspot, bounds.X);
            Canvas.SetTop(hotspot, bounds.Y);
            hotspot.Width = bounds.Width;
            hotspot.Height = bounds.Height;
        }
    }

    private void PreviewPart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModulePreviewPart part }) return;
        _selectedPreviewPart = part;
        RefreshPreviewElements();
        RestoreAdvancedOptions();
        SelectedElementListHost.Visibility = Visibility.Collapsed;
        SelectedTextListHost.Visibility = Visibility.Collapsed;
        SelectedButtonListHost.Visibility = Visibility.Collapsed;
        SelectedElementAdvancedHost.Children.Clear();

        switch (part.Section)
        {
            case "texts":
                ShowSelectedPart(part.Label, SelectedTextListHost);
                var textRow = _textRows.FirstOrDefault(row => row.Key == part.Key);
                TextPartsList.ItemsSource = textRow is null ? Array.Empty<TextRow>() : new[] { textRow };
                break;
            case "buttons":
                ShowSelectedPart(part.Label, SelectedButtonListHost);
                var buttonRow = _buttonRows.FirstOrDefault(row => row.Info.Key == part.Key);
                ButtonPartsList.ItemsSource = buttonRow is null ? Array.Empty<ButtonRow>() : new[] { buttonRow };
                break;
            default:
                ShowSelectedPart(part.Label, SelectedElementListHost);
                var elementRow = _elementRows.FirstOrDefault(row => row.Key == part.Key);
                ElementsList.ItemsSource = elementRow is null ? Array.Empty<ElementRow>() : new[] { elementRow };
                ShowElementAdvancedOptions(part.Key);
                break;
        }
    }

    private void PreviewWidgetOptions_Click(object sender, RoutedEventArgs e)
    {
        _selectedPreviewPart = null;
        RefreshPreviewElements();
        RestoreAdvancedOptions();
        ShowSelectedPart($"Options du widget · {SelectedModule?.Label}", null);
        OptionsExpander.IsExpanded = true;
    }

    private void ShowBackgroundSettings()
    {
        _selectedPreviewPart = null;
        RefreshPreviewElements();
        RestoreAdvancedOptions();
        ShowSelectedPart($"Fond et carte · {SelectedModule?.Label}", SelectedElementAdvancedHost);
        ShowBackgroundOptions();
    }

    private void ShowSelectedPart(string? title, FrameworkElement? selectedSettings)
    {
        SelectedPartTitle.Text = title ?? "";
        SelectedPartEmptyText.Visibility = selectedSettings is null ? Visibility.Visible : Visibility.Collapsed;
        SelectedElementListHost.Visibility = selectedSettings == SelectedElementListHost ? Visibility.Visible : Visibility.Collapsed;
        SelectedTextListHost.Visibility = selectedSettings == SelectedTextListHost ? Visibility.Visible : Visibility.Collapsed;
        SelectedButtonListHost.Visibility = selectedSettings == SelectedButtonListHost ? Visibility.Visible : Visibility.Collapsed;
        SelectedElementAdvancedHost.Visibility = selectedSettings == SelectedElementAdvancedHost ? Visibility.Visible : Visibility.Collapsed;
        SelectedPartPanel.BringIntoView();
    }

    private void ShowBackgroundOptions()
    {
        if (_widgetOptions is not { } options) return;
        StyleCardSection.Visibility = Visibility.Visible;
        MoveToPanel(StyleCardSection, SelectedElementAdvancedHost);
        if (SelectedModule is not { } module) return;
        if (module.Id == "media")
        {
            MediaBackgroundCard.Visibility = Visibility.Visible;
            MoveToPanel(MediaBackgroundCard, SelectedElementAdvancedHost);
        }
    }

    private void ShowModuleSpecificOptions()
    {
        if (_widgetOptions is not { } options) return;
        RestoreAdvancedOptions();
        foreach (var child in options.Children.OfType<FrameworkElement>())
            child.Visibility = Visibility.Collapsed;

        if (SelectedModule is not { } module) return;
        foreach (var child in options.Children.OfType<FrameworkElement>())
        {
            var applies = child switch
            {
                FrameworkElement element when element == VolumeStyleCard => module.Id == "volumemixer",
                FrameworkElement element when element == DiskStyleCard => module.Id == "disk",
                FrameworkElement element when element == VisualizerCard => module.Id == "visualizer",
                FrameworkElement element when element == MediaIconsCard || element == MediaBackgroundCard => module.Id == "media",
                FrameworkElement element when element == TileClockCard => module.Id == "tileclock",
                FrameworkElement element when element == WeatherCard => module.Id is "weather" or "airquality" or "sun",
                FrameworkElement element when element == SimHubCard => module.Id == "simhub",
                FrameworkElement element when element == SensorBarsCard => module.Id == "sensorbars",
                FrameworkElement element when element == TemperatureCard => module.Id == "temperatures",
                FrameworkElement element when element == StyleGaugesSection => ModuleCapabilities.HasAnyGaugeOption(module.Id),
                FrameworkElement element when element == StyleAnimationsSection => ModuleCapabilities.HasAnyGaugeOption(module.Id),
                _ => false
            };
            if (applies) child.Visibility = Visibility.Visible;
        }
    }

    private void RestoreAdvancedOptions()
    {
        if (_widgetOptions is not { } options) return;
        foreach (var element in new FrameworkElement[]
        {
            DiskStyleCard, VisualizerCard, StyleGaugesSection, StyleAnimationsSection,
            StyleCardSection, MediaBackgroundCard
        })
            if (element.Parent != options) MoveToPanel(element, options);
    }

    private void ShowElementAdvancedOptions(string key)
    {
        if (SelectedModule is not { } module) return;

        var gaugeElement = key is "ring" or "temp"
            || module.Id == "disk" && key.StartsWith("drive:", StringComparison.Ordinal)
            || module.Id == "claude" && key is ("session" or "week");
        if (gaugeElement && ModuleCapabilities.HasAnyGaugeOption(module.Id))
        {
            StyleGaugesSection.DataContext = CurrentAppearance;
            StyleAnimationsSection.DataContext = CurrentAppearance;
            if (module.Id == "disk")
            {
                MoveToPanel(DiskStyleCard, SelectedElementAdvancedHost);
                DiskStyleCard.Visibility = Visibility.Visible;
            }
            StyleGaugesSection.Visibility = Visibility.Visible;
            MoveToPanel(StyleGaugesSection, SelectedElementAdvancedHost);
            StyleAnimationsSection.Visibility = Visibility.Visible;
            MoveToPanel(StyleAnimationsSection, SelectedElementAdvancedHost);
        }

        if (module.Id == "visualizer" && key == "spectrum")
        {
            MoveToPanel(VisualizerCard, SelectedElementAdvancedHost);
            VisualizerCard.Visibility = Visibility.Visible;
        }
        if (SelectedElementAdvancedHost.Children.Count > 0)
        {
            SelectedElementAdvancedHost.Visibility = Visibility.Visible;
            SelectedPartEmptyText.Visibility = Visibility.Collapsed;
        }
    }

    private void SelectedElementReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ElementRow row }) row.ResetAll();
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
            _elementRows = new();
            ElementsList.ItemsSource = null;
            return;
        }

        var appearance = CurrentAppearance!;
        var keys = _customizeSwitch ? Deck.GetElementKeys(module.Id) : _widgetManager.GetElementKeys(module.Id);
        ElementsEmptyText.Text = _customizeSwitch
            ? "Affiche ce module sur la Switch (page Switch) pour régler ses éléments."
            : "Affiche ce module (page Modules) pour régler ses éléments.";        ElementsEmptyText.Visibility = keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _elementRows = keys
            .Select(key => new ElementRow(key, appearance.GetElement(key), appearance.NotifyElementsChanged))
            .ToList();
        ElementsList.ItemsSource = _elementRows;
    }

    private void ResetElement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ElementRow row }) row.ResetAll();
    }

    // ---------- Textes du module ----------

    private sealed record TextRow(string Key, string Label, TextPartStyle Style, bool Editable, string? DefaultText);

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
            _textRows = new();
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
            rows.Add(new TextRow(part.Key, part.Label, style, part.IsEditable, part.DefaultText));
        }
        _textRows = rows;
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
