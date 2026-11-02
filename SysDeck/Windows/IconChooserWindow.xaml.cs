using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

/// <summary>Image, glyphe (symbole, émoji), icône d'origine, ou icône inchangée (seules les couleurs changent).</summary>
public enum IconChoiceKind { Image, Glyph, Reset, Keep }

/// <summary>
/// Choix fait dans <see cref="IconChooserWindow"/> : icône (chemin d'image, glyphe, origine ou inchangée)
/// et couleurs de la tuile (null = couleurs du thème).
/// </summary>
public sealed record IconChoice(IconChoiceKind Kind, string? Value, string? IconColor = null, string? TileColor = null);

/// <summary>Choix de l'icône d'une tuile : symboles par catégorie avec recherche, émoji, image, couleurs et aperçu.</summary>
public partial class IconChooserWindow : Window
{
    private const string AllCategories = "Tous";
    private const string BrandsCategory = "Marques";

    // Palette proposée pour le symbole et le fond ; « Thème » (null) reprend les couleurs du thème.
    private static readonly string[] IconPalette =
    {
        "#FFFFFFFF", "#FF00E0FF", "#FF4DA3FF", "#FF8B5CFF", "#FFD45CFF", "#FFFF5C9A",
        "#FFFF4D4D", "#FFFF8A00", "#FFFFD23F", "#FF3FD97F", "#FF1DE9B6", "#FF9AA0B4"
    };
    private static readonly string[] TilePalette =
    {
        "#FF1B1D29", "#FF0F2A3A", "#FF14244A", "#FF2A1A4A", "#FF3A1440", "#FF4A1430",
        "#FF4A1616", "#FF4A2A0E", "#FF3D3510", "#FF103A22", "#FF0E3A36", "#FF2A2D38"
    };

    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private readonly ShortcutItem _preview; // copie de la tuile, modifiée au fil des choix pour l'aperçu
    private IconChoiceKind _kind = IconChoiceKind.Keep;
    private string? _value;
    private string _category = AllCategories;
    private Button? _selectedSymbol;

    /// <summary>Null si l'utilisateur a annulé.</summary>
    public IconChoice? Result { get; private set; }

    /// <param name="current">Tuile actuelle (icône et couleurs de départ de l'aperçu) ; null pour une nouvelle tuile.</param>
    public IconChooserWindow(Window owner, string itemName, Func<EmojiPickerWindow> createEmojiPicker, ShortcutItem? current = null)
    {
        InitializeComponent();
        Owner = owner;
        TitleText.Text = $"Icône de « {itemName} »";
        PreviewName.Text = itemName;
        _createEmojiPicker = createEmojiPicker;
        _preview = new ShortcutItem
        {
            Name = itemName,
            Path = current?.Path ?? "",
            IconGlyph = current?.IconGlyph,
            CustomIconPath = current?.CustomIconPath,
            IconColor = current?.IconColor,
            TileColor = current?.TileColor
        };

        BuildCategories();
        BuildSymbols();
        BuildPalette(IconColorsPanel, IconPalette, () => _preview.IconColor, c => _preview.IconColor = c, round: true);
        BuildPalette(TileColorsPanel, TilePalette, () => _preview.TileColor, c => _preview.TileColor = c, round: false);
        RefreshPreview();
        Loaded += (_, _) => SearchBox.Focus();
    }

    // ---------- Symboles ----------

    private void BuildCategories()
    {
        foreach (var name in new[] { AllCategories }.Concat(ShortcutIcons.Library.Select(c => c.Category)).Append(BrandsCategory))
        {
            var chip = new RadioButton
            {
                Content = name,
                GroupName = "IconCategory",
                IsChecked = name == AllCategories,
                Style = (Style)FindResource("Chip")
            };
            chip.Checked += (_, _) =>
            {
                _category = name;
                BuildSymbols();
            };
            CategoriesPanel.Children.Add(chip);
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => BuildSymbols();

    /// <summary>Grille des symboles de la catégorie choisie, filtrée par la recherche (sur le nom et la catégorie).</summary>
    private void BuildSymbols()
    {
        if (SymbolsPanel is null) return;
        SymbolsPanel.Children.Clear();
        _selectedSymbol = null;

        var search = SearchBox.Text.Trim();
        var items = ShortcutIcons.Library
            .Where(c => _category != BrandsCategory && (_category == AllCategories || c.Category == _category))
            .SelectMany(c => c.Items.Select(i => (i.Glyph, i.Name, c.Category)))
            .Where(i => search.Length == 0
                        || i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                        || i.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .DistinctBy(i => i.Glyph);

        foreach (var (glyph, name, _) in items)
        {
            var button = new Button
            {
                Content = glyph,
                FontFamily = new FontFamily(ShortcutIcons.SymbolFont),
                FontSize = 20,
                Width = 46,
                Height = 46,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = name,
                Tag = glyph
            };
            button.Click += Symbol_Click;
            if (_kind == IconChoiceKind.Glyph && _value == glyph) Select(button);
            SymbolsPanel.Children.Add(button);
        }
        AddBrands(search);
        NoSymbolText.Visibility = SymbolsPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Logos de marques dans leur couleur officielle : onglet « Marques » (sous-titres par groupe) ; dans « Tous »,
    /// seulement ceux qui correspondent à une recherche (nom ou identifiant, sans accents).
    /// </summary>
    private void AddBrands(string search)
    {
        if (_category != BrandsCategory && !(_category == AllCategories && search.Length > 0)) return;

        var brands = BrandIcons.Search(search).ToList();
        foreach (var group in brands.GroupBy(b => b.Category))
        {
            if (_category == BrandsCategory && search.Length == 0)
                SymbolsPanel.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("Muted"),
                    Width = 480,
                    Margin = new Thickness(2, 4, 0, 6)
                });

            foreach (var brand in group)
            {
                var button = new Button
                {
                    Content = ShortcutIcons.CreateBrand(brand.Value, 24),
                    Width = 46,
                    Height = 46,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 6, 6),
                    ToolTip = brand.Title,
                    Tag = brand.Value
                };
                button.Click += Symbol_Click;
                if (_kind == IconChoiceKind.Glyph && string.Equals(_value, brand.Value, StringComparison.OrdinalIgnoreCase)) Select(button);
                SymbolsPanel.Children.Add(button);
            }
        }
    }

    private void Select(Button? button)
    {
        if (_selectedSymbol is not null) _selectedSymbol.ClearValue(BorderBrushProperty);
        _selectedSymbol = button;
        if (button is not null)
        {
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
            button.BorderThickness = new Thickness(2);
        }
    }

    private void Symbol_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string glyph } button) return;
        SetIcon(IconChoiceKind.Glyph, glyph);
        Select(button);
    }

    // ---------- Couleurs ----------

    /// <summary>Rangée de pastilles : « Thème » (couleur par défaut), la palette, puis une couleur libre.</summary>
    private void BuildPalette(Panel panel, IEnumerable<string> palette, Func<string?> get, Action<string?> set, bool round)
    {
        var buttons = new List<(ToggleButton Button, string? Hex)>();

        void Refresh()
        {
            foreach (var (b, hex) in buttons)
                b.IsChecked = string.Equals(hex, get(), StringComparison.OrdinalIgnoreCase);
        }

        ToggleButton Swatch(string? hex, string tooltip)
        {
            var dot = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(round ? 11 : 6),
                Background = hex is null
                    ? new LinearGradientBrush(Color.FromRgb(0x2A, 0x2D, 0x3A), Color.FromRgb(0x55, 0x59, 0x6E), 45)
                    : new SolidColorBrush(ThemeService.Parse(hex, Colors.White)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = hex is null ? new TextBlock { Text = "T", FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center } : null
            };
            var toggle = new ToggleButton
            {
                Content = dot,
                Style = (Style)FindResource("ChipToggle"),
                Padding = new Thickness(4),
                ToolTip = tooltip
            };
            toggle.Click += (_, _) =>
            {
                set(hex);
                Refresh();
                RefreshPreview();
            };
            buttons.Add((toggle, hex));
            return toggle;
        }

        panel.Children.Add(Swatch(null, "Couleur du thème"));
        foreach (var hex in palette) panel.Children.Add(Swatch(hex, hex));

        // Couleur libre : le sélecteur Windows.
        var custom = new ColorSwatch { Content = "Autre…", Hex = get() ?? "#FFFFFFFF", Margin = new Thickness(4, 0, 0, 6), ToolTip = "Choisir une autre couleur" };
        // Le Click part avant l'ouverture du sélecteur : on suit plutôt la couleur une fois choisie.
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ColorSwatch.HexProperty, typeof(ColorSwatch))
            .AddValueChanged(custom, (_, _) =>
            {
                set(custom.Hex);
                Refresh();
                RefreshPreview();
            });
        panel.Children.Add(custom);
        Refresh();
    }

    // ---------- Icône ----------

    private void SetIcon(IconChoiceKind kind, string? value)
    {
        _kind = kind;
        _value = value;
        _preview.IconGlyph = kind == IconChoiceKind.Glyph ? value : null;
        _preview.CustomIconPath = kind == IconChoiceKind.Image ? value : null;
        if (kind != IconChoiceKind.Glyph) Select(null);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        // Premier rond de la palette (couleur null) : couleur du thème pour un symbole, couleur d'origine pour un logo.
        var brand = BrandIcons.IsBrand(_preview.IconGlyph);
        if (IconColorsPanel.Children.Count > 0 && IconColorsPanel.Children[0] is ToggleButton { Content: Border { Child: TextBlock letter } } nullSwatch)
        {
            nullSwatch.ToolTip = brand ? "Couleur d'origine" : "Couleur du thème";
            letter.Text = brand ? "O" : "T";
        }
        if (IconColorLabel is not null) IconColorLabel.Text = brand ? "Couleur du logo" : "Couleur du symbole";

        PreviewIcon.Content = ShortcutIcons.Create(_preview, 42);
        if (string.IsNullOrWhiteSpace(_preview.TileColor))
            PreviewTile.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x30));
        else
        {
            var color = ThemeService.Parse(_preview.TileColor, Colors.Black);
            var lighter = Color.FromArgb(color.A, (byte)Math.Min(255, color.R + 28), (byte)Math.Min(255, color.G + 28), (byte)Math.Min(255, color.B + 28));
            PreviewTile.Background = new LinearGradientBrush(lighter, color, 90);
        }
    }

    private void Image_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir une icône",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true) SetIcon(IconChoiceKind.Image, dialog.FileName);
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
        // Le sélecteur d'émojis habituel, en mode « choix » : il rend l'émoji ici au lieu de le taper dans une autre application.
        => _createEmojiPicker().ShowForPick(this, emoji => SetIcon(IconChoiceKind.Glyph, emoji));

    private void Reset_Click(object sender, RoutedEventArgs e) => SetIcon(IconChoiceKind.Reset, null);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new IconChoice(_kind, _value, _preview.IconColor, _preview.TileColor);
        DialogResult = true;
    }
}
