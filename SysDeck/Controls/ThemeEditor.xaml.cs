using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Services;

namespace SysDeck.Controls;

/// <summary>
/// Édite un <see cref="ThemeSettings"/> (DataContext). <see cref="Section"/> limite l'affichage à un
/// groupe de réglages : le thème global montre tout, un module montre chaque groupe séparément.
/// </summary>
public partial class ThemeEditor : UserControl
{
    /// <summary>"All" (défaut), "ColorsFonts", "Card", "Gauges" ou "Animations".</summary>
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(
        nameof(Section), typeof(string), typeof(ThemeEditor),
        new PropertyMetadata("All", (d, _) => ((ThemeEditor)d).ApplySection()));

    public string Section { get => (string)GetValue(SectionProperty); set => SetValue(SectionProperty, value); }

    /// <summary>
    /// Intégré dans une carte existante (onglet Style d'un module) : les sections perdent leur propre
    /// cadre et leur titre, pour ne pas empiler une carte dans une carte.
    /// </summary>
    public static readonly DependencyProperty EmbeddedProperty = DependencyProperty.Register(
        nameof(Embedded), typeof(bool), typeof(ThemeEditor),
        new PropertyMetadata(false, (d, _) => ((ThemeEditor)d).ApplySection()));

    public bool Embedded { get => (bool)GetValue(EmbeddedProperty); set => SetValue(EmbeddedProperty, value); }

    /// <summary>
    /// Module édité : n'affiche que les réglages de jauge et d'animation qui ont un sens pour lui
    /// (voir <see cref="ModuleCapabilities"/>). Null : tout est affiché (thème global).
    /// </summary>
    public static readonly DependencyProperty ModuleIdProperty = DependencyProperty.Register(
        nameof(ModuleId), typeof(string), typeof(ThemeEditor),
        new PropertyMetadata(null, (d, _) => ((ThemeEditor)d).ApplySection()));

    public string? ModuleId { get => (string?)GetValue(ModuleIdProperty); set => SetValue(ModuleIdProperty, value); }

    /// <summary>Mode des disques ("Ring" ou "Bar") du module édité : en lignes, les réglages d'anneau disparaissent.</summary>
    public static readonly DependencyProperty DiskStyleProperty = DependencyProperty.Register(
        nameof(DiskStyle), typeof(string), typeof(ThemeEditor),
        new PropertyMetadata(null, (d, _) => ((ThemeEditor)d).ApplySection()));

    public string? DiskStyle { get => (string?)GetValue(DiskStyleProperty); set => SetValue(DiskStyleProperty, value); }

    /// <summary>Cible « Switch » : ombre, effet au survol et animation au repos y sont ignorés, donc masqués.</summary>
    public static readonly DependencyProperty OnSwitchProperty = DependencyProperty.Register(
        nameof(OnSwitch), typeof(bool), typeof(ThemeEditor),
        new PropertyMetadata(false, (d, _) => ((ThemeEditor)d).ApplySection()));

    public bool OnSwitch { get => (bool)GetValue(OnSwitchProperty); set => SetValue(OnSwitchProperty, value); }

    private ThemeSettings? _watched;

    /// <summary>Suit l'apparition choisie : la courbe d'animation n'est utile que si elle s'en sert (« Zoom »).</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnThemePropertyChanged;
        _watched = e.NewValue as ThemeSettings;
        if (_watched is not null) _watched.PropertyChanged += OnThemePropertyChanged;
        ApplySection();
    }

    private void OnThemePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ThemeSettings.EntranceStyle)) ApplySection();
    }

    public ThemeEditor()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        foreach (var (name, start, end) in ThemeService.GetGaugePresets())
        {
            var button = new Button
            {
                Content = name,
                Margin = new Thickness(0, 0, 8, 8),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Background = new LinearGradientBrush(start, end, 0),
                BorderBrush = Brushes.Transparent
            };
            button.Click += (_, _) =>
            {
                if (DataContext is not ThemeSettings theme) return;
                theme.GaugeStartColor = start.ToString();
                theme.GaugeEndColor = end.ToString();
            };
            PresetsPanel.Children.Add(button);
        }
        ApplySection();
    }

    private void ApplySection()
    {
        if (ColorsCard is null) return; // pas encore chargé

        void Show(FrameworkElement card, params string[] sections)
            => card.Visibility = Section == "All" || sections.Contains(Section) ? Visibility.Visible : Visibility.Collapsed;

        Show(ColorsCard, "ColorsFonts");
        Show(FontsCard, "ColorsFonts");
        Show(PanelCard, "Card");
        Show(GaugesCard, "Gauges");
        Show(AnimationsCard, "Animations");

        // Réglages sans objet pour le module : masqués (jauge circulaire, style de jauge, halo).
        static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        var id = ModuleId;
        var entrance = (DataContext as ThemeSettings)?.EntranceStyle;
        GaugeShapePart.Visibility = GaugeRingPart.Visibility = Vis(ModuleCapabilities.HasRingGauge(id, DiskStyle));
        IntroPart.Visibility = Vis(ModuleCapabilities.HasIntro(id, DiskStyle));
        EasingPart.Visibility = EasingLabelPart.Visibility = Vis(ModuleCapabilities.HasEasing(id, DiskStyle, entrance));
        GaugeTicksPart.Visibility = Vis(ModuleCapabilities.HasTicks(id, DiskStyle));
        GaugeStylePart.Visibility = Vis(ModuleCapabilities.HasGaugeStyle(id));
        GaugeGlowPart.Visibility = Vis(ModuleCapabilities.HasGlow(id));
        var supported = ModuleCapabilities.SupportedGaugeStyles(id, DiskStyle);
        foreach (var chip in GaugeStyleChips.Children.OfType<RadioButton>())
            chip.Visibility = Vis(supported is null || supported.Contains((string)chip.Tag));
        TilesPart.Visibility = Vis(ModuleCapabilities.HasTiles(id));
        ButtonColorPart.Visibility = Vis(ModuleCapabilities.HasButtons(id));
        ShadowPart.Visibility = HoverPart.Visibility = IdlePart.Visibility = Vis(!OnSwitch);

        if (!Embedded) return;
        foreach (var card in new[] { ColorsCard, FontsCard, PanelCard, GaugesCard, AnimationsCard })
        {
            card.Style = null;
            card.Background = null;
            card.BorderThickness = new Thickness(0);
            card.Padding = new Thickness(0);
            card.Margin = new Thickness(0, 12, 0, 0);
            // Le titre de section fait doublon avec l'interrupteur de la carte, sauf « Polices » qui suit « Couleurs ».
            if (card != FontsCard && card.Child is Panel panel && FindCardTitle(panel) is { } title)
                title.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Titre de section d'une carte : repéré par son style « CardTitle » plutôt que par sa position,
    /// pour ne pas casser si l'on réordonne ou ajoute un élément en tête de carte.
    /// </summary>
    private ContentControl? FindCardTitle(Panel panel)
    {
        var style = TryFindResource("CardTitle") as Style;
        return panel.Children.OfType<ContentControl>().FirstOrDefault(c => style is not null && ReferenceEquals(c.Style, style));
    }

    /// <summary>
    /// Cartes affichées par la section courante, avec leur titre : sert à l'index de recherche des Paramètres.
    /// Le titre est lu même quand il est masqué (mode intégré), car il reste le meilleur libellé de la carte.
    /// </summary>
    public IEnumerable<(FrameworkElement Card, string Title)> GetSearchCards()
    {
        foreach (var card in new[] { ColorsCard, PanelCard, FontsCard, GaugesCard, AnimationsCard })
        {
            if (card.Visibility != Visibility.Visible) continue;
            var title = card.Child is Panel panel ? FindCardTitle(panel)?.Content as string : null;
            if (!string.IsNullOrEmpty(title)) yield return (card, title);
        }
    }

    /// <summary>
    /// Contour « Couleurs d'accent » : avec une opacité de contour faible l'effet est invisible, on la remonte
    /// (seulement sur un clic de l'utilisateur, jamais au chargement d'un thème).
    /// </summary>
    private void BorderAccent_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ThemeSettings theme) return;
        theme.BorderStyle = "Accent";
        if (theme.BorderOpacity < 0.6) theme.BorderOpacity = 0.8;
    }

    private void ResetFontScale_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ThemeSettings theme) theme.FontScale = 1;
    }

    private void ResetPanelColor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ThemeSettings theme) return;
        var defaults = new ThemeSettings();
        theme.PanelColor = defaults.PanelColor;
        theme.BorderColor = defaults.BorderColor;
        theme.TileColor = defaults.TileColor;
        theme.TileOpacity = defaults.TileOpacity;
    }
}
