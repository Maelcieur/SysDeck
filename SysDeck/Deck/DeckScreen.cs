using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
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
        Action<UIElement, ModuleAppearance> applyModuleSettings)
    {
        _settings = config.Deck;
        _config = config;
        _save = save;
        _buildModule = buildModule;
        _appearance = appearance;
        _applyModuleSettings = applyModuleSettings;

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

    private FrameworkElement? _overlay;

    /// <summary>Panneau modal (saisie de texte) posé au-dessus de tout, absent de l'écran de nuit ; null le retire.</summary>
    public void SetOverlay(FrameworkElement? overlay)
    {
        if (_overlay is not null) Children.Remove(_overlay);
        _overlay = overlay;
        if (overlay is null) return;
        Panel.SetZIndex(overlay, int.MaxValue);
        Children.Add(overlay);
    }

    /// <summary>
    /// Fond et horloge de la page affichée (chaque page a les siens) ; le fond n'est recréé que s'il a changé, pour ne
    /// pas relancer un GIF animé à chaque synchronisation.
    /// </summary>
    public void ApplyPageLook()
    {
        var key = $"{_settings.BackgroundColor}|{_settings.BackgroundImage}|{_settings.BackgroundDim}";
        if (key != _appliedBackground) ApplyBackground();
        ApplyClockOpacity();
        ApplyAppsBackground();
    }

    /// <summary>Redessine le fond du widget des tuiles (<see cref="DeckSettings.AppsBackgroundEnabled"/>, couleur, opacité).</summary>
    public void ApplyAppsBackground()
    {
        if (_elements.TryGetValue("apps", out var e) && e.Frame is DeckShortcutsPanel panel)
            panel.InvalidateVisual();
    }

    private string? _appliedBackground;

    public void ApplyBackground()
    {
        _appliedBackground = $"{_settings.BackgroundColor}|{_settings.BackgroundImage}|{_settings.BackgroundDim}";
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

    /// <summary>Anime l'arrivée du contenu de la nouvelle page selon les préférences configurées.</summary>
    public void AnimatePageTransition(int direction, DeckPageTransition style, int durationMs, DeckTransitionEasing easing)
    {
        _canvas.BeginAnimation(UIElement.OpacityProperty, null);
        _canvas.RenderTransform = Transform.Identity;
        if (direction == 0 || style == DeckPageTransition.None) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(Math.Clamp(durationMs, 100, 1000)));
        IEasingFunction? curve = easing == DeckTransitionEasing.Linear
            ? null
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var completed = new EventHandler((_, _) =>
        {
            _canvas.RenderTransform = Transform.Identity;
            _canvas.BeginAnimation(UIElement.OpacityProperty, null);
        });

        if (style is DeckPageTransition.Slide or DeckPageTransition.SlideFade)
        {
            var transform = new TranslateTransform(direction * 72, 0);
            _canvas.RenderTransform = transform;
            var animation = new DoubleAnimation(direction * 72, 0, duration) { EasingFunction = curve };
            animation.Completed += completed;
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        else if (style == DeckPageTransition.Zoom)
        {
            var transform = new ScaleTransform(0.92, 0.92);
            _canvas.RenderTransformOrigin = new Point(0.5, 0.5);
            _canvas.RenderTransform = transform;
            var animation = new DoubleAnimation(0.92, 1, duration) { EasingFunction = curve };
            animation.Completed += completed;
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            var verticalAnimation = new DoubleAnimation(0.92, 1, duration) { EasingFunction = curve };
            verticalAnimation.Completed += completed;
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, verticalAnimation);
        }

        if (style is DeckPageTransition.Fade or DeckPageTransition.SlideFade or DeckPageTransition.Zoom)
        {
            var animation = new DoubleAnimation(style == DeckPageTransition.SlideFade ? 0.55 : 0, 1, duration)
            {
                EasingFunction = curve
            };
            if (style != DeckPageTransition.Zoom) animation.Completed += completed;
            _canvas.BeginAnimation(UIElement.OpacityProperty, animation);
        }
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
            case "apps":
                return (new DeckShortcutsPanel(() => _settings.Page.Apps.Where(app => app.AppsSubpage == _settings.Page.CurrentAppsSubpage),
                    null, () => _settings.TileSize, () => _settings.AppsOpacity, grid: true, look: () => _settings), null);
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
    private readonly Func<DeckSettings?>? _look;
    private readonly bool _grid;

    /// <param name="changes">Liste à suivre pour se reconstruire seul, sinon null (Rebuild appelé de l'extérieur).</param>
    /// <param name="backgroundOpacity">Opacité du fond des tuiles (null = opaque).</param>
    /// <param name="grid">Vrai : tuiles placées dans leur case de la grille (<see cref="TileGrid"/>) ; faux : alignées à la suite.</param>
    /// <param name="look">Apparence des tuiles (style verre, taille d'icône, noms) ; null = à plat, icône à moitié, noms affichés.</param>
    public DeckShortcutsPanel(Func<IEnumerable<ShortcutItem>> items, INotifyCollectionChanged? changes, Func<double> tileSize,
        Func<double>? backgroundOpacity = null, bool grid = false, Func<DeckSettings?>? look = null)
    {
        _look = look;
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
        _stuck.Clear();
        var size = Math.Clamp(_tileSize(), 60, 300);
        foreach (var item in _items())
            Children.Add(BuildTile(item, size));
        ApplyCursor();
        InvalidateMeasure();
    }

    // ---------- Curseur des Joy-Con ----------

    private ShortcutItem? _cursor;
    private const string CursorTag = "joycon-cursor";

    /// <summary>Tuile entourée par le curseur des Joy-Con (null : aucun curseur).</summary>
    public void SetCursor(ShortcutItem? tile)
    {
        _cursor = tile;
        ApplyCursor();
    }

    private void ApplyCursor()
    {
        foreach (var tile in Children.OfType<Border>())
        {
            if (tile.Child is not Grid root) continue;
            var ring = root.Children.OfType<Border>().FirstOrDefault(b => CursorTag.Equals(b.Tag));
            var wanted = _cursor is not null && ReferenceEquals(tile.Tag, _cursor);
            if (wanted && ring is null)
            {
                ring = new Border
                {
                    Tag = CursorTag,
                    BorderThickness = new Thickness(Math.Max(4, tile.Width * 0.04)),
                    CornerRadius = tile.CornerRadius,
                    IsHitTestVisible = false
                };
                ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                Panel.SetZIndex(ring, 100);
                root.Children.Add(ring);
            }
            else if (!wanted && ring is not null) root.Children.Remove(ring);
        }
    }

    /// <summary>Enfonce brièvement une tuile (appui par un Joy-Con), comme sous le doigt.</summary>
    public void FlashPressed(ShortcutItem item)
    {
        if (Children.OfType<Border>().FirstOrDefault(t => ReferenceEquals(t.Tag, item)) is not { } tile) return;
        SetPressed(tile);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SetPressed(null);
        };
        timer.Start();
    }

    /// <summary>Fond du widget des tuiles (grille seulement) : rectangle arrondi de la couleur choisie, sous les tuiles.</summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!_grid || _look?.Invoke() is not { } look || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (look.AppsBackgroundEnabled) DrawPlate(drawingContext, look);
        if (look.TileStyle == DeckTileStyle.Glass && TileGlass.Available) DrawBlankButtons(drawingContext, look);
    }

    /// <summary>Cases occupées par une tuile (dernière mise en page) : les autres reçoivent un bouton vide en verre.</summary>
    private readonly HashSet<(int Col, int Row)> _occupied = new();

    /// <summary>
    /// Style verre : un bouton vide dans chaque case libre, comme les touches sans action d'un vrai Stream Deck.
    /// Purement visuel (aucun appui) ; même fond, face et bord qu'une tuile sans couleur.
    /// </summary>
    private void DrawBlankButtons(DrawingContext dc, DeckSettings look)
    {
        var tile = Math.Clamp(_tileSize(), 60, 300);
        var columns = TileGrid.Columns(ActualWidth, tile, look.Page.AppsColumns);
        var rows = TileGrid.Rows(ActualHeight, tile);
        var layout = TileGrid.Layout(ActualWidth, ActualHeight, columns, rows, tile);
        var inset = tile * TileGlass.FaceInset;
        var corner = tile * TileGlass.FaceCorner;
        var panel = TryFindResource("PanelBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x30));
        var opacity = BackgroundOpacity;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                if (_occupied.Contains((c, r))) continue;
                var rect = new Rect(layout.Left(c) + TileGrid.TileMargin, layout.Top(r) + TileGrid.TileMargin, tile, tile);
                var face = Rect.Inflate(rect, -inset, -inset);
                dc.PushOpacity(opacity);
                dc.DrawRoundedRectangle(panel, null, face, corner, corner);
                if (TileGlass.Face(false) is { } faceImage) dc.DrawImage(faceImage, rect);
                dc.Pop();
                if (TileGlass.Rim(false) is { } rim) dc.DrawImage(rim, rect);
            }
    }

    /// <summary>Plaque du widget (couleur ou texture), sa tranche et les logements creusés sous les cases.</summary>
    private void DrawPlate(DrawingContext drawingContext, DeckSettings look)
    {
        var opacity = Math.Clamp(look.AppsBackgroundOpacity, 0, 1);
        // Texture (façon vrai Stream Deck) si choisie et lisible, sinon couleur unie.
        Brush brush = DeckTextures.Load(look.AppsBackgroundTexture) is { } texture
            ? new ImageBrush(texture) { Stretch = Stretch.UniformToFill, Opacity = opacity }
            : new SolidColorBrush(ThemeService.Parse(look.AppsBackgroundColor, Color.FromRgb(0x1E, 0x21, 0x30))) { Opacity = opacity };
        brush.Freeze();
        var tile = Math.Clamp(_tileSize(), 60, 300);
        var radius = Math.Min(24, tile * 0.2);
        var plate = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(brush, null, plate, radius, radius);
        // Tranche de la plaque : léger reflet en haut, ombre en bas.
        drawingContext.DrawRoundedRectangle(null, PlateEdge, new Rect(0.75, 0.75, ActualWidth - 1.5, ActualHeight - 1.5), radius, radius);
        if (look.AppsWells) DrawWells(drawingContext, look, tile);
    }

    private static readonly Pen PlateEdge = Frozen(new Pen(VerticalGradient((0, 0x30, true), (0.5, 0x00, true), (1, 0x80, false)), 1.5));
    private static readonly Brush WellFill = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0x04, 0x04, 0x06)));
    // Ombre portée par la plaque sur le haut du logement.
    private static readonly Brush WellTopShadow = VerticalGradient((0, 0xD0, false), (0.22, 0x40, false), (0.45, 0x00, false));
    private static readonly Pen WellInnerEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 5));
    // Rebord du logement : sombre en haut (arête dans l'ombre), éclairé en bas (lumière qui accroche la tranche).
    private static readonly Pen WellLip = Frozen(new Pen(VerticalGradient((0, 0x90, false), (0.55, 0x00, false), (1, 0x48, true)), 1.6));

    /// <summary>Un logement creusé dans la plaque sous chaque case de la grille (cases vides comprises).</summary>
    private void DrawWells(DrawingContext dc, DeckSettings look, double tile)
    {
        var columns = TileGrid.Columns(ActualWidth, tile, look.Page.AppsColumns);
        var rows = TileGrid.Rows(ActualHeight, tile);
        var layout = TileGrid.Layout(ActualWidth, ActualHeight, columns, rows, tile);
        var inflate = TileGrid.TileMargin * 0.45; // creux à peine plus grand que le bouton
        var corner = tile * TileGlass.FaceCorner + inflate;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                var hole = new Rect(layout.Left(c) + TileGrid.TileMargin - inflate, layout.Top(r) + TileGrid.TileMargin - inflate,
                    tile + 2 * inflate, tile + 2 * inflate);
                var geometry = new RectangleGeometry(hole, corner, corner);
                geometry.Freeze();
                dc.DrawGeometry(WellFill, null, geometry);
                dc.PushClip(geometry);
                dc.DrawRectangle(WellTopShadow, null, hole);
                dc.DrawGeometry(null, WellInnerEdge, geometry); // bords intérieurs assombris
                dc.Pop();
                dc.DrawGeometry(null, WellLip, geometry);
            }
    }

    private static LinearGradientBrush VerticalGradient(params (double Offset, byte Alpha, bool White)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (offset, alpha, white) in stops)
            brush.GradientStops.Add(new GradientStop(white ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 0, 0, 0), offset));
        brush.Freeze();
        return brush;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
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
            var columns = TileGrid.Columns(finalSize.Width, size, _look?.Invoke()?.Page.AppsColumns ?? 0);
            var rows = TileGrid.Rows(finalSize.Height, size);
            var items = tiles.Select(t => (ShortcutItem)t.Tag).ToList();
            var arrangement = TileGrid.Arrange(items, columns, rows);
            var layout = TileGrid.Layout(finalSize.Width, finalSize.Height, columns, rows, size);
            _occupied.Clear();
            foreach (var cellOf in arrangement.Cells.Values) _occupied.Add(cellOf);
            foreach (var tile in tiles)
            {
                // Tuile sans case : jamais dessinée (taille nulle, hors du bloc).
                if (arrangement.Cells.TryGetValue((ShortcutItem)tile.Tag, out var at))
                    tile.Arrange(new Rect(layout.Left(at.Col), layout.Top(at.Row), cell, cell));
                else tile.Arrange(new Rect(-cell, -cell, 0, 0));
            }
            InvalidateVisual(); // logements creusés sous les cases : suivent la grille
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
        // Verre : l'opacité de la face dépend aussi de la couleur et de l'état de la tuile, plus simple de tout redessiner.
        if (Children.OfType<Border>().Any(t => t.Child is Grid root && root.Children.OfType<Image>().Any()))
        {
            Rebuild();
            return;
        }
        foreach (var tile in Children.OfType<Border>())
            if (tile.Child is Grid { Children: [Border background, ..] })
                background.Opacity = BackgroundOpacity;
    }

    private Border BuildTile(ShortcutItem item, double size)
    {
        var look = _look?.Invoke();
        var glass = look is { TileStyle: DeckTileStyle.Glass } && TileGlass.Available;
        var iconScale = DeckSettings.ClampIconScale(item.IconScale) ?? look?.TileIconScale ?? 0.5;
        var showName = item.ShowName ?? ((look?.ShowTileNames ?? true) && !item.HideName);

        // Face de la tuile : toute la tuile à plat ; en verre, l'intérieur du bord chromé.
        var inset = glass ? size * TileGlass.FaceInset : 0;
        var face = size - 2 * inset;
        var corner = glass ? size * TileGlass.FaceCorner : size * 0.16;

        // Pas de fond sur la tuile elle-même : son opacité s'appliquerait aussi à l'icône et au nom.
        var background = new Border { CornerRadius = new CornerRadius(corner), Opacity = BackgroundOpacity, Margin = new Thickness(inset) };
        var armed = TileRuntime.Confirm.IsArmed(item);
        var stateLook = TileRuntime.IntegrationLookOf(item);
        var muted = (item.IsMuteTile && TileRuntime.IsMuted(item)) || stateLook == IntegrationLook.Alert;
        var highlighted = stateLook == IntegrationLook.Highlight;
        var active = muted || highlighted;
        var alert = Color.FromRgb(0xE5, 0x48, 0x3E);
        var colored = true;
        // Fond « Transparent » choisi pour la tuile : ni couleur ni face de verre, seuls le bord, l'icône et le nom restent.
        var transparent = IsTransparent(item.TileColor);
        if (active && !string.IsNullOrWhiteSpace(item.ActiveColor) && !IsTransparent(item.ActiveColor))
        {
            // Couleur active choisie pour la tuile.
            background.Background = Gradient(ThemeService.Parse(item.ActiveColor, alert));
            background.Opacity = 1;
        }
        else if (muted)
        {
            // Micro / sortie coupé : fond rouge, bien visible de loin.
            background.Background = new LinearGradientBrush(Color.FromRgb(0xC6, 0x3A, 0x32), Color.FromRgb(0x8E, 0x24, 0x1F), 90);
            background.Opacity = 1;
        }
        else if (highlighted)
        {
            // Scène OBS active, mode du tchat Twitch actif : fond d'accent du thème.
            background.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            background.Opacity = 1;
        }
        else if (transparent) colored = false;
        else if (!string.IsNullOrWhiteSpace(item.TileColor))
        {
            // Couleur propre : légèrement plus claire en haut, comme le fond du thème.
            background.Background = Gradient(ThemeService.Parse(item.TileColor, Colors.Black));
        }
        else
        {
            background.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            colored = false;
        }

        var name = new TextBlock
        {
            Text = armed ? "Appuyer encore pour confirmer" : TileRuntime.DisplayName(item),
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
        else if (muted || highlighted || glass) name.Foreground = Brushes.White;
        else name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        // Active : sa deuxième icône si elle en a une (ex. micro barré), sinon l'icône habituelle (qui suit déjà l'état par défaut).
        var display = active && item.HasActiveIcon ? item.ActiveDisplay() : TileRuntime.ForDisplay(item);
        if (active && string.IsNullOrWhiteSpace(display.IconColor) && display != item) display.IconColor = "#FFFFFFFF";

        var iconSize = face * iconScale;
        var icon = ShortcutIcons.Create(display, iconSize) ?? new Border { Width = iconSize, Height = iconSize };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;

        // Contenu de la face, découpé à son arrondi : une image à 100 % remplit la tuile sans déborder du verre.
        var content = new Grid
        {
            Tag = FaceTag,
            Width = face,
            Height = face,
            Margin = new Thickness(inset),
            Clip = new RectangleGeometry(new Rect(0, 0, face, face), corner, corner)
        };
        if (!showName && !armed) content.Children.Add(icon);
        else if (iconScale <= StackedLimit && !armed)
            // Petite icône : nom en dessous, l'ensemble centré.
            content.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { icon, name } });
        else
        {
            // Grande icône (ou confirmation) : l'icône garde sa place, le texte passe par-dessus sur un fond sombre.
            content.Children.Add(icon);
            name.Margin = new Thickness(6, 0, 6, face * 0.06);
            if (armed) name.VerticalAlignment = VerticalAlignment.Center;
            else name.Foreground = Brushes.White;
            content.Children.Add(new Border
            {
                VerticalAlignment = armed ? VerticalAlignment.Stretch : VerticalAlignment.Bottom,
                Padding = new Thickness(0, armed ? 0 : face * 0.14, 0, 0),
                Background = armed
                    ? new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x14))
                    : new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xB4, 0, 0, 0), 90),
                Child = name
            });
        }

        var root = new Grid { Children = { background } };
        if (glass)
        {
            // Face du verre posée sur la couleur (un peu plus légère sur une tuile colorée pour que la couleur reste vive) ;
            // elle suit l'opacité du fond et disparaît sur une tuile transparente. Le bord chromé, lui, reste toujours.
            var faceOpacity = active ? 0.8 : transparent ? 0 : BackgroundOpacity * (colored ? 0.8 : 1);
            root.Children.Add(new Image { Tag = GlassFaceTag, Source = TileGlass.Face(false), Stretch = Stretch.Fill, Opacity = faceOpacity });
            root.Children.Add(new Image { Tag = GlassRimTag, Source = TileGlass.Rim(false), Stretch = Stretch.Fill });
        }
        root.Children.Add(content);

        var tile = new Border
        {
            Tag = item,
            Width = size,
            Height = size,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(glass ? corner + inset : size * 0.16),
            BorderThickness = new Thickness(glass ? 0 : 1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = root
        };
        if (armed)
        {
            tile.BorderBrush = new SolidColorBrush(alert);
            tile.BorderThickness = new Thickness(3);
        }
        else if (!glass) tile.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
        if (TileRuntime.IntegrationOffline(item)) tile.Opacity = 0.4; // OBS, Twitch, Voicemod ou plugin déconnecté : tuile atténuée
        RenderOptions.SetBitmapScalingMode(tile, BitmapScalingMode.HighQuality);
        // Active : le bouton en verre reste enfoncé, comme une touche verrouillée.
        if (glass && active && item.ActivePressed)
        {
            _stuck.Add(tile);
            ApplyDown(tile, true);
        }
        return tile;
    }

    /// <summary>Tuiles actives qui restent enfoncées (reconstruites à chaque <see cref="Rebuild"/>).</summary>
    private readonly HashSet<Border> _stuck = new();

    /// <summary>Dégradé de fond d'une couleur : légèrement plus claire en haut, comme le fond du thème.</summary>
    private static LinearGradientBrush Gradient(Color color)
    {
        var lighter = Color.FromArgb(color.A, (byte)Math.Min(255, color.R + 28), (byte)Math.Min(255, color.G + 28), (byte)Math.Min(255, color.B + 28));
        return new LinearGradientBrush(lighter, color, 90);
    }

    /// <summary>Au-delà de cette taille d'icône, le nom passe par-dessus l'icône au lieu d'en dessous.</summary>
    internal const double StackedLimit = 0.6;

    private const string GlassFaceTag = "glass-face", GlassRimTag = "glass-rim", FaceTag = "face";

    /// <summary>Vrai pour une couleur de tuile entièrement transparente (choix « Transparent » de la palette).</summary>
    internal static bool IsTransparent(string? hex)
        => !string.IsNullOrWhiteSpace(hex) && ThemeService.Parse(hex, Colors.Black).A == 0;

    /// <summary>
    /// Tuile enfoncée (doigt posé dessus) ; null relâche toutes les tuiles. À plat : légèrement réduite. En verre :
    /// image « appuyée », tuile un peu réduite et contenu enfoncé de quelques pixels, comme une vraie touche.
    /// </summary>
    public void SetPressed(Border? pressed)
    {
        foreach (var tile in Children.OfType<Border>())
            ApplyDown(tile, tile == pressed || _stuck.Contains(tile));
    }

    private static void ApplyDown(Border tile, bool down)
    {
        var layers = (tile.Child as Grid)?.Children;
        var glass = layers?.OfType<Image>().ToList() ?? new List<Image>();
        if (layers is null || glass.Count == 0)
        {
            tile.RenderTransform = down ? new ScaleTransform(0.92, 0.92) : Transform.Identity;
            return;
        }
        foreach (var layer in glass)
            layer.Source = GlassFaceTag.Equals(layer.Tag) ? TileGlass.Face(down) : TileGlass.Rim(down);
        tile.RenderTransform = down ? new ScaleTransform(0.96, 0.96) : Transform.Identity;
        if (layers.OfType<Grid>().FirstOrDefault(g => FaceTag.Equals(g.Tag)) is { } content)
            content.RenderTransform = down ? new TranslateTransform(0, tile.Width * 0.015) : Transform.Identity;
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
