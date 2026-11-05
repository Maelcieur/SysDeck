using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;
using Image = System.Windows.Controls.Image;

namespace SysDeck;

/// <summary>Image, glyphe (symbole, émoji), icône d'origine, ou icône inchangée (seules les couleurs changent).</summary>
public enum IconChoiceKind { Image, Glyph, Reset, Keep }

/// <summary>
/// Choix fait dans <see cref="IconChooserWindow"/> : icône (chemin d'image, glyphe, origine ou inchangée)
/// et couleurs de la tuile (null = couleurs du thème).
/// </summary>
/// <remarks>IconScale et HideName ne valent que pour une tuile de la Switch (HideName null ailleurs).</remarks>
public sealed record IconChoice(IconChoiceKind Kind, string? Value, string? IconColor = null, string? TileColor = null,
    double? IconScale = null, bool? HideName = null, string? ImageTint = null, TileActiveLook? Active = null);

/// <summary>
/// Apparence d'une tuile de la Switch quand elle est active : couleur, deuxième icône (symbole, ou image dont le chemin
/// est encore l'original tant qu'elle n'est pas recopiée), couleurs de cette icône, bouton qui reste enfoncé.
/// </summary>
public sealed record TileActiveLook(string? Color, string? Glyph, string? ImagePath, string? IconColor, string? ImageTint, bool Pressed)
{
    public static TileActiveLook From(ShortcutItem item)
        => new(item.ActiveColor, item.ActiveIconGlyph, item.ActiveIconPath, item.ActiveIconColor, item.ActiveImageTint, item.ActivePressed);

    public void WriteTo(ShortcutItem item)
    {
        item.ActiveColor = Color;
        item.ActiveIconGlyph = Glyph;
        item.ActiveIconPath = Glyph is null ? ImagePath : null;
        item.ActiveIconColor = IconColor;
        item.ActiveImageTint = Glyph is null && ImagePath is not null ? ImageTint : null;
        item.ActivePressed = Pressed;
    }
}

/// <summary>Choix de l'icône d'une tuile : symboles par catégorie avec recherche, émoji, image, couleurs et aperçu.</summary>
public partial class IconChooserWindow : Window
{
    private const string AllCategories = "Tous";
    private const string BrandsCategory = "Marques";
    private const string PackPrefix = "pack:"; // clé de catégorie d'un pack d'icônes : « pack:<id> »
    private const int MaxPackIcons = 300;
    private static readonly Dictionary<string, ImageSource?> Thumbnails = new(StringComparer.OrdinalIgnoreCase);

    // Palette proposée pour le symbole et le fond ; « Thème » (null) reprend les couleurs du thème.
    private static readonly string[] IconPalette =
    {
        "#FFFFFFFF", "#FF00E0FF", "#FF4DA3FF", "#FF8B5CFF", "#FFD45CFF", "#FFFF5C9A",
        "#FFFF4D4D", "#FFFF8A00", "#FFFFD23F", "#FF3FD97F", "#FF1DE9B6", "#FF9AA0B4"
    };
    private static readonly string[] TilePalette =
    {
        "#FF1B1D29", "#FF0F2A3A", "#FF14244A", "#FF2A1A4A", "#FF3A1440", "#FF4A1430",
        "#FF4A1616", "#FF4A2A0E", "#FF3D3510", "#FF103A22", "#FF0E3A36", "#FF2A2D38", TransparentHex
    };

    /// <summary>Fond de tuile transparent : sur la Switch, seuls le bord du verre, l'icône et le nom restent.</summary>
    private const string TransparentHex = "#00000000";

    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private readonly ShortcutItem _preview; // copie de la tuile, modifiée au fil des choix pour l'aperçu
    private IconChoiceKind _kind = IconChoiceKind.Keep;
    private readonly bool _tileOptions;
    private Action? _refreshIconPalette;

    // Couleurs proposées pour une tuile active : vives, lisibles de loin (rouge, orange, jaune, vert, cyan, bleu, violet, rose).
    private static readonly string[] ActivePalette =
    {
        "#FFC63A32", "#FFE5711E", "#FFD9A514", "#FF2E9E55", "#FF0FA3A3", "#FF2F6FDB", "#FF7A4BD6", "#FFC2408C"
    };

    /// <summary>Vrai quand l'aperçu montre une image choisie (ni symbole ni émoji) : la palette la teinte.</summary>
    private bool ShowsImage => string.IsNullOrEmpty(_preview.IconGlyph) && !string.IsNullOrWhiteSpace(_preview.CustomIconPath);
    private bool _loadingOptions;
    private bool _isEmbedded;
    private Action<bool>? _embeddedCompleted;
    private string? _value;
    private string _category = AllCategories;
    private Button? _selectedSymbol;
    private bool _buildingCategories;

    /// <summary>Null si l'utilisateur a annulé.</summary>
    public IconChoice? Result { get; private set; }
    public string ChooserTitle => TitleText.Text;
    public event Action<IconChooserWindow, Action<bool>>? NestedChooserRequested;
    private Window DialogOwner => _isEmbedded ? Owner ?? this : this;

    /// <param name="current">Tuile actuelle (icône et couleurs de départ de l'aperçu) ; null pour une nouvelle tuile.</param>
    /// <param name="tileOptions">Vrai pour une tuile de la Switch : taille de l'icône et nom masqué se règlent aussi ici.</param>
    public IconChooserWindow(Window owner, string itemName, Func<EmojiPickerWindow> createEmojiPicker, ShortcutItem? current = null,
        bool tileOptions = false)
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
            ImageTint = current?.ImageTint,
            TileColor = current?.TileColor,
            IconScale = current?.IconScale,
            HideName = current?.HideName ?? false
        };
        if (current is not null) _preview.CopyActiveFrom(current);
        _tileOptions = tileOptions;
        if (tileOptions)
        {
            TileOptionsPanel.Visibility = Visibility.Visible;
            ActivePressedCheck.IsChecked = _preview.ActivePressed;
            BuildPalette(ActiveColorsPanel, ActivePalette, () => _preview.ActiveColor, c => _preview.ActiveColor = c, round: false,
                nullTooltip: "Automatique : rouge (coupé, en cours) ou accent du thème (scène, mode actif)");
            RefreshActiveIcon();
            _loadingOptions = true;
            ScaleSlider.Value = _preview.IconScale ?? 0.5;
            _loadingOptions = false;
            DefaultScaleCheck.IsChecked = _preview.IconScale is null;
            HideNameCheck.IsChecked = _preview.HideName;
            UpdateScaleControls();
        }

        BuildCategories();
        BuildSymbols();
        // Une seule palette : couleur du symbole, ou teinte de l'image quand c'est une image qui est affichée.
        _refreshIconPalette = BuildPalette(IconColorsPanel, IconPalette,
            () => ShowsImage ? _preview.ImageTint : _preview.IconColor,
            c =>
            {
                if (ShowsImage) _preview.ImageTint = c;
                else _preview.IconColor = c;
            }, round: true);
        BuildPalette(TileColorsPanel, TilePalette, () => _preview.TileColor, c => _preview.TileColor = c, round: false);
        RefreshPreview();
        Loaded += (_, _) => SearchBox.Focus();
    }

    public void AttachTo(ContentControl host, Action<bool> completed)
    {
        if (Content is not UIElement content)
            throw new InvalidOperationException("The icon chooser has no content to display.");

        _isEmbedded = true;
        _embeddedCompleted = completed;
        Content = null;
        host.Content = content;
        SearchBox.Focus();
    }

    public void CancelInline() => Complete(false);

    private void Complete(bool accepted)
    {
        if (!_isEmbedded)
        {
            DialogResult = accepted;
            return;
        }

        _embeddedCompleted?.Invoke(accepted);
    }

    // ---------- Symboles ----------

    /// <summary>Catégories de symboles, « Marques », puis une par pack d'icônes installé (clic droit : supprimer le pack).</summary>
    private void BuildCategories()
    {
        _buildingCategories = true;
        CategoriesBox.Items.Clear();
        var categories = new[] { AllCategories }.Concat(ShortcutIcons.Library.Select(c => c.Category)).Append(BrandsCategory)
            .Select(name => (Key: name, Label: name, Pack: (IconPack?)null))
            .Concat(IconPackStore.All.Select(p => (Key: PackPrefix + p.Id, Label: p.Name, Pack: (IconPack?)p)));

        foreach (var (key, label, pack) in categories)
        {
            var item = new ComboBoxItem { Content = label, Tag = key };
            if (pack is not null)
            {
                item.ToolTip = PackTooltip(pack);
                var delete = new MenuItem { Header = "Supprimer ce pack d'icônes" };
                delete.Click += (_, _) => DeletePack(pack);
                item.ContextMenu = new ContextMenu { Items = { delete } };
            }
            CategoriesBox.Items.Add(item);
        }
        var selected = CategoriesBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _category))
            ?? CategoriesBox.Items.Cast<ComboBoxItem>().First();
        _category = (string)selected.Tag;
        CategoriesBox.SelectedItem = selected;
        _buildingCategories = false;
        UpdateSymbolsHeader();
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_buildingCategories || CategoriesBox.SelectedItem is not ComboBoxItem { Tag: string category }) return;
        _category = category;
        UpdateSymbolsHeader();
        BuildSymbols();
    }

    private void UpdateSymbolsHeader()
    {
        var label = (CategoriesBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? AllCategories;
        SymbolsHeader.Text = $"Afficher les icônes — {label}";
    }

    private static string PackTooltip(IconPack pack)
    {
        var lines = new List<string> { $"{pack.Name} — {pack.Icons.Count} icônes" };
        if (pack.Author is not null) lines.Add($"Auteur : {pack.Author}");
        if (pack.Version is not null) lines.Add($"Version : {pack.Version}");
        lines.Add("Clic droit : supprimer le pack");
        return string.Join("\n", lines);
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
            .Where(c => _category == AllCategories || c.Category == _category)
            .SelectMany(c => c.Items.Select(i => (i.Glyph, i.Name, c.Category)))
            .Where(i => search.Length == 0
                        || i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                        || i.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .DistinctBy(i => i.Glyph);

        foreach (var (glyph, name, _) in items)
        {
            var isSymbol = ShortcutIcons.IsSymbol(glyph);
            var button = new Button
            {
                Content = isSymbol ? glyph : ShortcutIcons.CreateGlyph(glyph, 24),
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
        AddPackIcons(search);
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

    /// <summary>
    /// Icônes des packs : onglet du pack (filtré par la recherche) ; dans « Tous », celles qui correspondent à une recherche.
    /// Nombre borné pour garder la fenêtre fluide avec les gros packs.
    /// </summary>
    private void AddPackIcons(string search)
    {
        IEnumerable<IconPack>? packs = null;
        if (_category.StartsWith(PackPrefix, StringComparison.Ordinal))
            packs = IconPackStore.All.Where(p => PackPrefix + p.Id == _category);
        else if (_category != AllCategories || search.Length == 0) return;

        var found = IconPackStore.Search(search, packs).Take(MaxPackIcons + 1).ToList();
        foreach (var (pack, icon) in found.Take(MaxPackIcons))
        {
            var button = new Button
            {
                Content = new Image { Source = Thumbnail(icon.Path), Width = 32, Height = 32 },
                Width = 46,
                Height = 46,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = packs is null ? $"{icon.Name} ({pack.Name})" : icon.Name,
                Tag = icon.Path
            };
            button.Click += PackIcon_Click;
            if (_kind == IconChoiceKind.Image && string.Equals(_value, icon.Path, StringComparison.OrdinalIgnoreCase)) Select(button);
            SymbolsPanel.Children.Add(button);
        }
        if (found.Count > MaxPackIcons)
            SymbolsPanel.Children.Add(new TextBlock
            {
                Text = $"Seules les {MaxPackIcons} premières icônes sont affichées : affine la recherche.",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("Muted"),
                Width = 480,
                Margin = new Thickness(2, 4, 0, 6)
            });
    }

    /// <summary>Miniature figée, décodée en petit et gardée en cache (les packs comptent souvent des centaines d'images).</summary>
    private static ImageSource? Thumbnail(string path)
    {
        if (Thumbnails.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 64;
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException)
        {
            // image abîmée : bouton vide plutôt qu'une erreur
        }
        if (Thumbnails.Count > 4000) Thumbnails.Clear();
        return Thumbnails[path] = image;
    }

    private void PackIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } button) return;
        // Choisie comme une image importée : elle est recopiée, la tuile ne dépend donc plus du pack.
        SetIcon(IconChoiceKind.Image, path);
        Select(button);
    }

    private void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importer un pack d'icônes",
            Filter = "Packs d'icônes (*.streamDeckIconPack;*.zip)|*.streamDeckIconPack;*.zip|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(DialogOwner) != true) return;

        try
        {
            // Les SVG sont convertis en images à l'import : quelques secondes pour un gros pack.
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            IconPack pack;
            try { pack = IconPackStore.Import(dialog.FileName); }
            finally { Mouse.OverrideCursor = null; }
            _category = PackPrefix + pack.Id;
            SearchBox.Text = "";
            BuildCategories();
            BuildSymbols();
            var message = $"Pack « {pack.Name} » installé : {pack.Icons.Count} icônes.";
            if (pack.SkippedSvg > 0) message += $"\n{pack.SkippedSvg} icône(s) SVG n'ont pas pu être converties et sont ignorées.";
            MessageBox.Show(DialogOwner, message, "Pack d'icônes", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(DialogOwner, ex.Message, "Pack d'icônes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeletePack(IconPack pack)
    {
        if (MessageBox.Show(DialogOwner, $"Supprimer le pack « {pack.Name} » ?\nLes tuiles qui utilisent déjà une de ses icônes la gardent.",
                "Pack d'icônes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        // Icône choisie dans ce pack mais pas encore validée : on l'oublie, son fichier va disparaître.
        if (_kind == IconChoiceKind.Image && _value is not null && IconPackStore.IsInside(pack.Folder, _value)) SetIcon(IconChoiceKind.Keep, null);
        try { IconPackStore.Delete(pack.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(DialogOwner, $"Suppression impossible : {ex.Message}", "Pack d'icônes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (_category == PackPrefix + pack.Id) _category = AllCategories;
        BuildCategories();
        BuildSymbols();
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
    /// <returns>Remet les pastilles cochées d'accord avec la valeur (quand elle change ailleurs).</returns>
    private Action BuildPalette(Panel panel, IEnumerable<string> palette, Func<string?> get, Action<string?> set, bool round,
        string nullTooltip = "Couleur du thème")
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
                    : hex == TransparentHex ? CheckerBrush()
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

        panel.Children.Add(Swatch(null, nullTooltip));
        foreach (var hex in palette) panel.Children.Add(Swatch(hex, hex == TransparentHex ? "Transparent (seul le bord du bouton en verre reste)" : hex));

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
        return Refresh;
    }

    /// <summary>Damier gris : pastille « Transparent ».</summary>
    private static Brush CheckerBrush()
    {
        var light = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4));
        var dark = new SolidColorBrush(Color.FromRgb(0x4A, 0x4E, 0x5E));
        var drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))),
                new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 4, 4))),
                new GeometryDrawing(light, null, new RectangleGeometry(new Rect(4, 4, 4, 4)))
            }
        };
        return new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
    }

    // ---------- Taille et nom sur la Switch ----------

    private void ScaleSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || _preview is null || _loadingOptions) return;
        if (DefaultScaleCheck.IsChecked == true) DefaultScaleCheck.IsChecked = false; // toucher au curseur = taille propre
        TileOption_Changed(sender, e);
    }

    private void TileOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _preview is null) return;
        _preview.IconScale = DefaultScaleCheck.IsChecked == true ? null : ScaleSlider.Value;
        _preview.HideName = HideNameCheck.IsChecked == true;
        _preview.ActivePressed = ActivePressedCheck.IsChecked == true;
        UpdateScaleControls();
        RefreshPreview();
    }

    private void UpdateScaleControls()
    {
        ScaleSlider.Opacity = DefaultScaleCheck.IsChecked == true ? 0.5 : 1;
        ScaleValueText.Text = DefaultScaleCheck.IsChecked == true ? "—" : $"{ScaleSlider.Value:P0}";
    }

    // ---------- Tuile active ----------

    /// <summary>Deuxième icône : même fenêtre de choix, sans les réglages de tuile.</summary>
    private void ActiveIcon_Click(object sender, RoutedEventArgs e)
    {
        var current = _preview.HasActiveIcon ? _preview.ActiveDisplay() : new ShortcutItem { IconGlyph = _preview.IconGlyph, CustomIconPath = _preview.CustomIconPath, IconColor = _preview.IconColor, ImageTint = _preview.ImageTint };
        current.TileColor = _preview.TileColor;
        var chooser = new IconChooserWindow(DialogOwner, $"{PreviewName.Text} (active)", _createEmojiPicker, current);
        if (_isEmbedded && NestedChooserRequested is { } requested)
        {
            requested(chooser, accepted =>
            {
                if (accepted && chooser.Result is { } choice) ApplyActiveIconChoice(choice);
            });
            return;
        }
        if (chooser.ShowDialog() != true || chooser.Result is not { } choice) return;

        ApplyActiveIconChoice(choice);
    }

    private void ApplyActiveIconChoice(IconChoice choice)
    {
        switch (choice.Kind)
        {
            case IconChoiceKind.Glyph:
                _preview.ActiveIconGlyph = choice.Value;
                _preview.ActiveIconPath = null;
                break;
            case IconChoiceKind.Image:
                _preview.ActiveIconGlyph = null;
                _preview.ActiveIconPath = choice.Value;
                break;
            case IconChoiceKind.Reset:
                _preview.ActiveIconGlyph = _preview.ActiveIconPath = null;
                break;
            case IconChoiceKind.Keep when !_preview.HasActiveIcon:
                // Couleurs seulement, sans deuxième icône encore : on part de l'icône normale.
                _preview.ActiveIconGlyph = _preview.IconGlyph;
                _preview.ActiveIconPath = _preview.IconGlyph is null ? _preview.CustomIconPath : null;
                break;
        }
        _preview.ActiveIconColor = choice.IconColor;
        _preview.ActiveImageTint = choice.ImageTint;
        PreviewActiveCheck.IsChecked = true; // montrer tout de suite ce qui vient d'être choisi
        RefreshActiveIcon();
        RefreshPreview();
    }

    private void ActiveIconClear_Click(object sender, RoutedEventArgs e)
    {
        _preview.ActiveIconGlyph = _preview.ActiveIconPath = _preview.ActiveIconColor = _preview.ActiveImageTint = null;
        RefreshActiveIcon();
        RefreshPreview();
    }

    private void RefreshActiveIcon()
    {
        ActiveIconHost.Content = _preview.HasActiveIcon ? ShortcutIcons.Create(_preview.ActiveDisplay(), 26) : new TextBlock
        {
            Text = "=",
            FontSize = 16,
            Foreground = (Brush)FindResource("Muted"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            ToolTip = "Même icône que d'habitude"
        };
        ActiveIconClearButton.IsEnabled = _preview.HasActiveIcon;
    }

    // ---------- Icône ----------

    private void SetIcon(IconChoiceKind kind, string? value)
    {
        _kind = kind;
        _value = value;
        _preview.IconGlyph = kind == IconChoiceKind.Glyph ? value : null;
        _preview.CustomIconPath = kind == IconChoiceKind.Image ? value : null;
        // Plus d'image : plus de teinte. D'une image à l'autre (parcours d'un pack), la couleur choisie est gardée.
        if (kind != IconChoiceKind.Image) _preview.ImageTint = null;
        if (kind != IconChoiceKind.Glyph) Select(null);
        _refreshIconPalette?.Invoke();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        // Premier rond de la palette (couleur null) : couleur du thème pour un symbole, couleurs d'origine pour un logo ou une image.
        var brand = BrandIcons.IsBrand(_preview.IconGlyph);
        var original = brand || ShowsImage;
        if (IconColorsPanel.Children.Count > 0 && IconColorsPanel.Children[0] is ToggleButton { Content: Border { Child: TextBlock letter } } nullSwatch)
        {
            nullSwatch.ToolTip = original ? "Couleurs d'origine" : "Couleur du thème";
            letter.Text = original ? "O" : "T";
        }
        if (IconColorLabel is not null)
            IconColorLabel.Text = brand ? "Couleur du logo" : ShowsImage ? "Couleur de l'image (idéal pour les icônes d'une seule couleur)" : "Couleur du symbole";

        // Taille propre de la tuile reflétée dans l'aperçu (84 px de côté ; 42 px = la moitié, réglage par défaut).
        var iconSize = _tileOptions && _preview.IconScale is { } scale ? Math.Round(80 * scale) : 42;
        PreviewIcon.Width = PreviewIcon.Height = iconSize;
        var showActive = _tileOptions && PreviewActiveCheck.IsChecked == true;
        PreviewIcon.Content = ShortcutIcons.Create(showActive && _preview.HasActiveIcon ? _preview.ActiveDisplay() : _preview, iconSize);
        if (showActive)
        {
            // Aperçu actif : couleur active, sinon l'accent (scène, mode) comme exemple de couleur automatique.
            var active = ThemeService.Parse(string.IsNullOrWhiteSpace(_preview.ActiveColor) ? "#FF5B8DEF" : _preview.ActiveColor, Colors.Black);
            var light = Color.FromArgb(active.A, (byte)Math.Min(255, active.R + 28), (byte)Math.Min(255, active.G + 28), (byte)Math.Min(255, active.B + 28));
            PreviewTile.Background = new LinearGradientBrush(light, active, 90);
        }
        else if (string.IsNullOrWhiteSpace(_preview.TileColor))
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
        if (dialog.ShowDialog(DialogOwner) == true) SetIcon(IconChoiceKind.Image, dialog.FileName);
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
        // Le sélecteur d'émojis habituel, en mode « choix » : il rend l'émoji ici au lieu de le taper dans une autre application.
        => _createEmojiPicker().ShowForPick(DialogOwner, emoji => SetIcon(IconChoiceKind.Glyph, emoji));

    private void Reset_Click(object sender, RoutedEventArgs e) => SetIcon(IconChoiceKind.Reset, null);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(false);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new IconChoice(_kind, _value, _preview.IconColor, _preview.TileColor,
            _tileOptions ? _preview.IconScale : null, _tileOptions ? _preview.HideName : null,
            ShowsImage ? _preview.ImageTint : null,
            _tileOptions ? TileActiveLook.From(_preview) : null);
        Complete(true);
    }
}
