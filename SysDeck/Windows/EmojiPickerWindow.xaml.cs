using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EmojiData = Emoji.Wpf.EmojiData;
using SysDeck.Infrastructure;
using SysDeck.Interop;
using SysDeck.Services;

namespace SysDeck;

/// <summary>
/// Sélecteur d'émojis flottant et transparent, ouvert par un raccourci global. La fenêtre ne s'active pas au clic
/// (WS_EX_NOACTIVATE) : le champ de texte de l'application en cours garde le focus, et l'émoji cliqué
/// y est tapé directement. Seule la recherche prend le clavier ; le focus est alors rendu à
/// l'application avant l'insertion. La fenêtre reste ouverte jusqu'au clic sur la croix.
/// </summary>
public partial class EmojiPickerWindow : Window
{
    /// <summary>Un émoji vient d'être inséré (pour la liste des récents).</summary>
    public event Action<string>? EmojiInserted;

    private readonly Func<IReadOnlyList<string>> _recents;
    private readonly EmojiPickerSettings _settings;
    private readonly DispatcherTimer _targetTracker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IntPtr _hwnd;

    /// <summary>Mode « choix » (Paramètres) : l'émoji cliqué est rendu à l'appelant au lieu d'être tapé, puis la fenêtre se ferme.</summary>
    private Action<string>? _pick;
    private IntPtr _lastTarget;
    private RadioButton? _recentTab;

    private sealed record EmojiItem(string Text, string Name);

    public EmojiPickerWindow(Func<IReadOnlyList<string>> recents, EmojiPickerSettings settings)
    {
        InitializeComponent();
        _recents = recents;
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;

        // Dernière fenêtre d'une autre application au premier plan : c'est là que l'émoji sera tapé.
        _targetTracker.Tick += (_, _) =>
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != _hwnd) _lastTarget = foreground;
        };

        Closed += (_, _) =>
        {
            _targetTracker.Stop();
            _settings.PropertyChanged -= OnSettingsChanged;
        };

        BuildCategories();
        ApplyAppearance();
    }

    /// <summary>Ouvre le sélecteur au-dessus de <paramref name="owner"/> pour choisir un émoji (rien n'est tapé nulle part).</summary>
    public void ShowForPick(Window owner, Action<string> picked)
    {
        _pick = picked;
        ApplyAppearance();
        Owner = owner;
        ShowNearCursor();
    }

    // ---------- Apparence : fond transparent teinté, contour, coins arrondis ----------

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => ApplyAppearance();

    private void ApplyAppearance()
    {
        Tint.CornerRadius = Chrome.CornerRadius = new CornerRadius(Math.Clamp(_settings.CornerRadius, 0, 40));
        // Opacité minimale à peine visible : un pixel totalement transparent laisserait passer le clic
        // vers la fenêtre de derrière (entre deux émojis, par exemple).
        // Mode « choix » : posé sur une fenêtre des Paramètres, le fond translucide rendrait la grille illisible.
        Tint.Background = TintBrush(_settings.BackgroundColor, Math.Max(_pick is null ? 0.01 : 0.94, _settings.BackgroundOpacity));
        Chrome.BorderBrush = TintBrush(_settings.BorderColor, _settings.BorderOpacity);
    }

    private static SolidColorBrush TintBrush(string hex, double opacity)
    {
        var c = ThemeService.Parse(hex, Colors.Black);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
    private void BuildCategories()
    {
        _recentTab = AddCategory("\U0001F552", "Récents", ShowRecents);
        foreach (var group in EmojiData.AllGroups)
        {
            var items = group.EmojiList.Select(e => new EmojiItem(e.Text, e.Name)).ToList();
            AddCategory(group.Icon, TranslateGroup(group.Name), () => ShowItems(TranslateGroup(group.Name), items));
        }
    }

    private RadioButton AddCategory(string icon, string title, Action show)
    {
        var image = new System.Windows.Controls.Image { Width = 20, Height = 20 };
        Emoji.Wpf.Image.SetSource(image, icon);
        var tab = new RadioButton { Content = image, Style = (Style)Resources["CategoryTab"], GroupName = "EmojiCategories", ToolTip = title };
        tab.Checked += (_, _) =>
        {
            ClearSearch();
            show();
        };
        CategoriesPanel.Children.Add(tab);
        return tab;
    }

    private void ShowRecents()
    {
        var items = _recents()
            .Select(text => new EmojiItem(text, EmojiData.LookupByText.TryGetValue(text, out var e) ? e.Name : text))
            .ToList();
        ShowItems("Récents", items, "Aucun émoji utilisé pour l'instant : ceux que tu insères apparaîtront ici.");
    }

    private void ShowItems(string title, IReadOnlyList<EmojiItem> items, string? emptyMessage = null)
    {
        CategoryTitle.Text = title;
        EmojiList.ItemsSource = items;
        EmptyText.Text = emptyMessage ?? "Aucun résultat.";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string TranslateGroup(string name) => name switch
    {
        "Smileys & Emotion" => "Smileys et émotions",
        "People & Body" => "Personnes et corps",
        "Animals & Nature" => "Animaux et nature",
        "Food & Drink" => "Nourriture et boissons",
        "Travel & Places" => "Voyages et lieux",
        "Activities" => "Activités",
        "Objects" => "Objets",
        "Symbols" => "Symboles",
        "Flags" => "Drapeaux",
        _ => name
    };

    /// <summary>Affiche le sélecteur près du curseur (sans le déplacer s'il est déjà ouvert).</summary>
    public void ShowNearCursor()
    {
        // Raccourci pressé pendant une recherche : le premier plan, c'est nous ; la cible reste la précédente.
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != _hwnd) _lastTarget = foreground;
        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            // Récents s'il y en a, sinon les smileys.
            var tab = _recents().Count > 0 ? _recentTab : CategoriesPanel.Children.OfType<RadioButton>().ElementAtOrDefault(1);
            if (tab is not null)
            {
                if (tab.IsChecked == true && tab == _recentTab) ShowRecents(); // déjà choisi : on rafraîchit la liste
                tab.IsChecked = true;
            }
            Show();
        }
        _targetTracker.Start();
        if (!wasVisible) PlaceNearCursor();
    }

    /// <summary>Positionné en pixels écran (pas en unités WPF) : correct quel que soit l'écran et sa mise à l'échelle.</summary>
    private void PlaceNearCursor()
    {
        if (!GetCursorPos(out var cursor) || !GetWindowRect(_hwnd, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;

        var x = Math.Clamp(cursor.X - width / 2, area.Left, area.Right - width);
        var y = cursor.Y + 20 + height <= area.Bottom ? cursor.Y + 20 : cursor.Y - height - 10;
        y = Math.Clamp(y, area.Top, area.Bottom - height);
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private async void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text }) return;
        if (_pick is { } pick)
        {
            HidePicker();
            pick(text);
            return;
        }
        try
        {
            // Après une recherche, le clavier est chez nous : on le rend à l'application cible d'abord.
            if (GetForegroundWindow() == _hwnd)
            {
                if (_lastTarget != IntPtr.Zero) SetForegroundWindow(_lastTarget);
                await Task.Delay(80);
                // Windows peut refuser de rendre la main (fenêtre cible fermée…) : on n'écrit pas dans la recherche.
                if (GetForegroundWindow() == _hwnd)
                {
                    Log.Warn("Émoji non inséré : impossible de rendre le focus à l'application cible.");
                    return;
                }
            }
            InputSimulator.SendText(text);
            EmojiInserted?.Invoke(text);
        }
        catch (Exception ex)
        {
            Log.Warn("Insertion d'un émoji impossible", ex);
        }
    }

    // ---------- Recherche ----------

    private void SearchBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // La fenêtre ne s'active pas d'elle-même : on la rend active pour pouvoir taper la recherche.
        SetForegroundWindow(_hwnd);
        Activate();
        SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (query.Length == 0)
        {
            // Retour à la catégorie choisie.
            var current = CategoriesPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
            if (current is not null) { current.IsChecked = false; current.IsChecked = true; }
            return;
        }

        var results = EmojiData.AllEmoji
            .Where(em => em.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(300)
            .Select(em => new EmojiItem(em.Text, em.Name))
            .ToList();
        ShowItems($"Résultats pour « {query} »", results, "Aucun émoji trouvé. Les noms sont en anglais (ex. heart, cat, party).");
    }

    private void ClearSearch()
    {
        if (SearchBox.Text.Length == 0) return;
        SearchBox.TextChanged -= SearchBox_TextChanged;
        SearchBox.Text = "";
        SearchHint.Visibility = Visibility.Visible;
        SearchBox.TextChanged += SearchBox_TextChanged;
    }

    // ---------- Fenêtre ----------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch (InvalidOperationException) { /* bouton relâché avant le déplacement */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HidePicker();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HidePicker();
    }

    private void HidePicker()
    {
        _targetTracker.Stop();
        ClearSearch();
        var hadFocus = GetForegroundWindow() == _hwnd;
        Hide();
        // Si la recherche avait pris le clavier, on le rend à l'application d'où vient l'utilisateur
        // (sinon on ne touche à rien : l'utilisateur est peut-être déjà passé à une autre fenêtre).
        if (hadFocus && _lastTarget != IntPtr.Zero) SetForegroundWindow(_lastTarget);
        if (_pick is not null) Close(); // instance créée pour un seul choix
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Jamais activée par un clic, absente d'Alt+Tab : le focus reste dans l'application où l'on écrit.
        var style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        ApplyAppearance();
    }
}
