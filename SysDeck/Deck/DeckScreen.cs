using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Deck;

/// <summary>
/// Écran de la Switch (1280x720) dessiné par WPF : fond puis éléments placés selon <see cref="DeckSettings"/>.
/// Les modules sont des instances propres à la Switch, alimentées par WidgetManager comme celles du bureau.
/// </summary>
public sealed partial class DeckScreen : Grid
{
    public const double ScreenWidth = DeckProtocol.ScreenWidth;
    public const double ScreenHeight = DeckProtocol.ScreenHeight;

    /// <summary>Taille minimale d'un élément (côté), partagée avec l'éditeur de disposition pour qu'aperçu et Switch concordent.</summary>
    public const double MinElementSize = 60;

    private readonly DeckSettings _settings;
    private readonly AppConfig _config;
    private readonly Action _save;
    private readonly Func<string, UIElement?> _buildModule;
    private readonly Func<string, ModuleAppearance> _appearance;
    private readonly Action<UIElement, ModuleAppearance> _applyModuleSettings;
    private readonly ObservableCollection<ShortcutItem> _shortcuts;
    private ImageSource? _mediaArtwork;

    private readonly Border _backgroundImage = new();
    private readonly Border _backgroundDim = new();
    private readonly Canvas _canvas = new() { ClipToBounds = true };

    /// <summary>Points de page en bas de l'écran (s'il y a plusieurs pages) ; un point touché ouvre sa page.</summary>
    private readonly StackPanel _dots = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 2)
    };

    /// <summary>Cadre posé sur l'écran pour chaque élément affiché, et contenu du module s'il y en a un.</summary>
    private readonly Dictionary<string, (FrameworkElement Frame, UIElement? Module)> _elements = new();

    /// <param name="appearance">Apparence du module propre à la Switch.</param>
    /// <param name="applyModuleSettings">Réglages propres à certains modules (icônes, style des jauges…) appliqués au contenu.</param>
    public DeckScreen(AppConfig config, Action save, Func<string, UIElement?> buildModule, Func<string, ModuleAppearance> appearance,
        Action<UIElement, ModuleAppearance> applyModuleSettings, ObservableCollection<ShortcutItem> shortcuts)
    {
        _settings = config.Deck;
        _config = config;
        _save = save;
        _buildModule = buildModule;
        _appearance = appearance;
        _applyModuleSettings = applyModuleSettings;
        _shortcuts = shortcuts;

        Width = ScreenWidth;
        Height = ScreenHeight;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Children.Add(_backgroundImage);
        Children.Add(_backgroundDim);
        Children.Add(_canvas);
        Children.Add(_dots);
        ApplyBackground();
    }

    /// <summary>Contenus des modules affichés sur la Switch (à rafraîchir avec ceux du bureau).</summary>
    public IEnumerable<UIElement> ModuleContents => _elements.Values.Select(e => e.Module).OfType<UIElement>();

    public bool Contains(string id) => _elements.ContainsKey(id);

    public void ApplyBackground()
    {
        Background = new SolidColorBrush(ThemeService.Parse(_settings.BackgroundColor, Color.FromRgb(0x0B, 0x0C, 0x10)));
        // GIF : fond animé (l'écran entier change à chaque image, envoyé au rythme des Fps).
        var brush = AnimatedGif.CreateBrush(_settings.BackgroundImage, (int)ScreenWidth, ApplyBackground)
                    ?? (ModuleCard.LoadImage(_settings.BackgroundImage) is { } image ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : null);
        _backgroundImage.Background = brush;
        _backgroundDim.Background = brush is null ? null
            : new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(_settings.BackgroundDim, 0, 1) * 255), 0, 0, 0));
    }

    /// <summary>
    /// Met l'écran en accord avec les réglages : crée les éléments devenus visibles, retire les autres,
    /// replace tout le monde. Renvoie vrai si des modules ont été ajoutés ou retirés.
    /// </summary>
    public bool Sync()
    {
        var changed = false;
        var wanted = _settings.Elements.Where(e => e.Visible).Select(e => e.Id).ToHashSet();

        foreach (var id in _elements.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            _canvas.Children.Remove(_elements[id].Frame);
            changed |= _elements[id].Module is not null;
            _elements.Remove(id);
        }

        foreach (var element in _settings.Elements.Where(e => e.Visible && !_elements.ContainsKey(e.Id)))
        {
            if (Build(element.Id) is not { } built) continue;
            _elements[element.Id] = built;
            _canvas.Children.Add(built.Frame);
            changed |= built.Module is not null;
        }

        // Ordre d'affichage = ordre de la liste (le dernier passe devant).
        var z = 0;
        foreach (var element in _settings.Elements)
            if (_elements.TryGetValue(element.Id, out var e)) Panel.SetZIndex(e.Frame, z++);

        foreach (var id in _elements.Keys) Place(id);
        // Même élément d'une page à l'autre : ses applications changent avec la page.
        if (_elements.TryGetValue("apps", out var apps)) ((DeckShortcutsPanel)apps.Frame).Rebuild();
        UpdateDots();
        return changed;
    }

    private void UpdateDots()
    {
        _dots.Children.Clear();
        if (_settings.Pages.Count < 2) return;
        for (var i = 0; i < _settings.Pages.Count; i++)
            _dots.Children.Add(new DeckPageDot(i, i == _settings.CurrentPage));
    }

    public void Place(string id)
    {
        if (!_elements.TryGetValue(id, out var e) || _settings.Find(id) is not { } layout) return;
        Canvas.SetLeft(e.Frame, layout.X);
        Canvas.SetTop(e.Frame, layout.Y);
        var width = Math.Max(MinElementSize, layout.Width);
        var height = Math.Max(MinElementSize, layout.Height);

        // Module (toujours en taille automatique sur la Switch) : le cadre épouse ses éléments, à l'échelle retenue (calculée une fois
        // depuis le cadre actuel pour garder la même taille d'affichage, puis à chaque redimensionnement dans l'éditeur).
        if (e.Frame is ModuleCard card && card.ContentNaturalSize is { Width: > 1, Height: > 1 } natural)
        {
            var inset = card.ChromeInset;
            layout.Scale ??= Math.Clamp(Math.Min((width - inset.Width) / natural.Width, (height - inset.Height) / natural.Height), 0.2, 6);
            width = layout.Width = Math.Max(MinElementSize, Math.Round(natural.Width * layout.Scale.Value + inset.Width));
            height = layout.Height = Math.Max(MinElementSize, Math.Round(natural.Height * layout.Scale.Value + inset.Height));
        }

        e.Frame.Width = width;
        e.Frame.Height = height;
    }

    private (FrameworkElement Frame, UIElement? Module)? Build(string id)
    {
        switch (id)
        {
            case "clock":
                var clock = Card(new DeckClock());
                clock.Chrome.Opacity = _settings.ClockOpacity;
                return (clock.Frame, null);
            case "shortcuts":
                return (new DeckShortcutsPanel(() => _shortcuts, _shortcuts, () => _settings.TileSize), null);
            case "apps":
                return (new DeckShortcutsPanel(() => _settings.Page.Apps, null, () => _settings.TileSize, () => _settings.AppsOpacity, grid: true), null);
        }

        var module = _buildModule(id);
        if (module is null) return null;
        // Fond de carte comme sur le bureau ; le contenu se recadre sur ses éléments pour rester dans le cadre.
        var card = new ModuleCard(id, module, _config, _save, fillFrame: true);
        card.NaturalSizeChanged += () => Place(id);
        if (id == "media") card.SetMediaArtwork(_mediaArtwork);
        var appearance = _appearance(id);
        card.ApplyAppearance(appearance);
        _applyModuleSettings(module, appearance);
        return (card, module);
    }

    /// <summary>
    /// Carte au style des modules du bureau ; le contenu garde ses proportions dans le cadre choisi.
    /// Le cadre (Chrome) est sous le contenu pour pouvoir le rendre transparent sans toucher au contenu.
    /// </summary>
    private static (Grid Frame, Border Chrome) Card(UIElement content)
    {
        var chrome = new Border();
        chrome.SetResourceReference(StyleProperty, "CardChromeStyle");
        var frame = new Grid { Children = { chrome, new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(14), Child = content } } };
        return (frame, chrome);
    }

    /// <summary>Applique l'opacité du fond de l'horloge (<see cref="DeckSettings.ClockOpacity"/>).</summary>
    public void ApplyClockOpacity()
    {
        if (_elements.TryGetValue("clock", out var e) && e.Frame is Grid { Children: [Border chrome, ..] })
            chrome.Opacity = Math.Clamp(_settings.ClockOpacity, 0, 1);
    }

    /// <summary>Applique l'opacité du fond des tuiles d'applications (<see cref="DeckSettings.AppsOpacity"/>).</summary>
    public void ApplyAppsOpacity()
    {
        if (_elements.TryGetValue("apps", out var e) && e.Frame is DeckShortcutsPanel panel)
            panel.ApplyBackgroundOpacity();
    }

    /// <summary>Apparence du module propre à la Switch (style, textes, fond, éléments), comme sur le bureau.</summary>
    public void ApplyAppearance(string id)
    {
        if (CardOf(id) is not { } card) return;
        var appearance = _appearance(id);
        card.ApplyAppearance(appearance);
        _applyModuleSettings(card.ModuleContent, appearance);
    }

    /// <summary>Carte du module affiché sur la Switch, ou null.</summary>
    public ModuleCard? CardOf(string id) => _elements.TryGetValue(id, out var e) ? e.Frame as ModuleCard : null;

    /// <summary>Pochette du morceau en cours, pour le fond du module Lecture en cours.</summary>
    public void SetMediaArtwork(ImageSource? artwork)
    {
        _mediaArtwork = artwork;
        CardOf("media")?.SetMediaArtwork(artwork);
    }

    public void ApplyAllAppearances()
    {
        foreach (var id in _elements.Keys) ApplyAppearance(id);
    }

    /// <summary>Panneaux de tuiles affichés (raccourcis de la barre, applications de la page).</summary>
    public IEnumerable<DeckShortcutsPanel> TilePanels => _elements.Values.Select(e => e.Frame).OfType<DeckShortcutsPanel>();
}

/// <summary>Horloge de l'écran Switch : heure en grand et date.</summary>
public sealed class DeckClock : StackPanel
{
    private readonly TextBlock _time = new() { FontSize = 64, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _date = new() { FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public DeckClock()
    {
        _time.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _date.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        Children.Add(_time);
        Children.Add(_date);
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Texte de l'heure (modèle des glyphes du mode nuit).</summary>
    public TextBlock TimeText => _time;

    /// <summary>Masque l'heure et la date sans changer la mise en page (image de nuit sans aucun texte).</summary>
    public void HideTexts(bool hidden)
    {
        var visibility = hidden ? Visibility.Hidden : Visibility.Visible;
        _time.Visibility = visibility;
        _date.Visibility = visibility;
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString("HH:mm");
        _date.Text = now.ToString("dddd d MMMM");
    }
}

/// <summary>Tuiles de l'écran Switch (une tuile touchée lance le raccourci) : barre de raccourcis ou applications de la page.</summary>
public sealed class DeckShortcutsPanel : Panel
{
    private readonly Func<IEnumerable<ShortcutItem>> _items;
    private readonly Func<double> _tileSize;
    private readonly Func<double>? _backgroundOpacity;
    private readonly bool _grid;

    /// <param name="changes">Liste à suivre pour se reconstruire seul, sinon null (Rebuild appelé de l'extérieur).</param>
    /// <param name="backgroundOpacity">Opacité du fond des tuiles (null = opaque).</param>
    /// <param name="grid">Vrai : tuiles placées dans leur case de la grille (<see cref="TileGrid"/>) ; faux : alignées à la suite.</param>
    public DeckShortcutsPanel(Func<IEnumerable<ShortcutItem>> items, INotifyCollectionChanged? changes, Func<double> tileSize,
        Func<double>? backgroundOpacity = null, bool grid = false)
    {
        _items = items;
        _tileSize = tileSize;
        _backgroundOpacity = backgroundOpacity;
        _grid = grid;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Loaded += (_, _) =>
        {
            if (changes is not null) changes.CollectionChanged += OnShortcutsChanged;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            if (changes is not null) changes.CollectionChanged -= OnShortcutsChanged;
        };
    }

    private void OnShortcutsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    public void Rebuild()
    {
        Children.Clear();
        var size = Math.Clamp(_tileSize(), 60, 300);
        foreach (var item in _items())
            Children.Add(BuildTile(item, size));
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Clamp(_tileSize(), 60, 300);
        foreach (UIElement child in Children) child.Measure(new Size(size + 2 * TileGrid.TileMargin, size + 2 * TileGrid.TileMargin));
        return new Size(0, 0); // la taille vient du cadre (Width/Height posés par l'écran)
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = Math.Clamp(_tileSize(), 60, 300);
        var cell = TileGrid.CellSize(size);
        var tiles = Children.OfType<Border>().ToList();

        if (_grid)
        {
            var columns = TileGrid.Columns(finalSize.Width, size);
            var rows = TileGrid.Rows(finalSize.Height, size);
            var items = tiles.Select(t => (ShortcutItem)t.Tag).ToList();
            var arrangement = TileGrid.Arrange(items, columns, rows);
            foreach (var tile in tiles)
            {
                // Tuile sans case : jamais dessinée (taille nulle, hors du bloc).
                if (arrangement.Cells.TryGetValue((ShortcutItem)tile.Tag, out var at))
                    tile.Arrange(new Rect(at.Col * cell, at.Row * cell, cell, cell));
                else tile.Arrange(new Rect(-cell, -cell, 0, 0));
            }
            return finalSize;
        }

        var perRow = Math.Max(1, (int)Math.Floor(finalSize.Width / cell));
        for (var i = 0; i < tiles.Count; i++)
            tiles[i].Arrange(new Rect(i % perRow * cell, i / perRow * cell, cell, cell));
        return finalSize;
    }

    private double BackgroundOpacity => Math.Clamp(_backgroundOpacity?.Invoke() ?? 1, 0, 1);

    /// <summary>Applique l'opacité du fond aux tuiles déjà construites (l'icône et le nom restent opaques).</summary>
    public void ApplyBackgroundOpacity()
    {
        foreach (var tile in Children.OfType<Border>())
            if (tile.Child is Grid { Children: [Border background, ..] })
                background.Opacity = BackgroundOpacity;
    }

    private Border BuildTile(ShortcutItem item, double size)
    {
        // Pas de fond sur la tuile elle-même : son opacité s'appliquerait aussi à l'icône et au nom.
        var background = new Border { CornerRadius = new CornerRadius(size * 0.16), Opacity = BackgroundOpacity };
        var armed = TileRuntime.Confirm.IsArmed(item);
        var muted = item.IsMuteTile && TileRuntime.IsMuted(item);
        var alert = Color.FromRgb(0xE5, 0x48, 0x3E);
        if (muted)
        {
            // Micro / sortie coupé : fond rouge, bien visible de loin.
            background.Background = new LinearGradientBrush(Color.FromRgb(0xC6, 0x3A, 0x32), Color.FromRgb(0x8E, 0x24, 0x1F), 90);
            background.Opacity = 1;
        }
        else if (!string.IsNullOrWhiteSpace(item.TileColor))
        {
            // Couleur propre : légèrement plus claire en haut, comme le fond du thème.
            var color = ThemeService.Parse(item.TileColor, Colors.Black);
            var lighter = Color.FromArgb(color.A, (byte)Math.Min(255, color.R + 28), (byte)Math.Min(255, color.G + 28), (byte)Math.Min(255, color.B + 28));
            background.Background = new LinearGradientBrush(lighter, color, 90);
        }
        else background.SetResourceReference(Border.BackgroundProperty, "PanelBrush");

        var name = new TextBlock
        {
            Text = armed ? "Appuyer encore pour confirmer" : item.Name,
            FontSize = Math.Max(11, size * 0.11),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6, 6, 6, 0)
        };
        if (armed)
        {
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.FontWeight = FontWeights.SemiBold;
            name.Foreground = new SolidColorBrush(alert);
        }
        else if (muted) name.Foreground = Brushes.White;
        else name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var display = TileRuntime.ForDisplay(item);
        if (muted && string.IsNullOrWhiteSpace(display.IconColor) && display != item) display.IconColor = "#FFFFFFFF";

        var tile = new Border
        {
            Tag = item,
            Width = size,
            Height = size,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(size * 0.16),
            BorderThickness = new Thickness(1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = new Grid
            {
                Children =
                {
                    background,
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { ShortcutIcons.Create(display, size * 0.5) ?? new Border { Width = size * 0.5, Height = size * 0.5 }, name }
                    }
                }
            }
        };
        if (armed)
        {
            tile.BorderBrush = new SolidColorBrush(alert);
            tile.BorderThickness = new Thickness(3);
        }
        else tile.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
        RenderOptions.SetBitmapScalingMode(tile, BitmapScalingMode.HighQuality);
        return tile;
    }

    /// <summary>Tuile enfoncée (doigt posé dessus) : légèrement réduite ; null relâche toutes les tuiles.</summary>
    public void SetPressed(Border? pressed)
    {
        foreach (var tile in Children.OfType<Border>())
            tile.RenderTransform = tile == pressed ? new ScaleTransform(0.92, 0.92) : Transform.Identity;
    }
}

/// <summary>Point de page (zone de toucher plus grande que le point visible).</summary>
public sealed class DeckPageDot : Border
{
    public DeckPageDot(int index, bool current)
    {
        Index = index;
        Width = 34;
        Height = 30;
        Background = Brushes.Transparent;
        Child = new System.Windows.Shapes.Ellipse
        {
            Width = current ? 12 : 9,
            Height = current ? 12 : 9,
            Fill = new SolidColorBrush(current ? Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public int Index { get; }
}
