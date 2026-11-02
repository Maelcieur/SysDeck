using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace SysDeck;

/// <summary>
/// Création ou modification d'une tuile Twitch : action, texte selon l'action (message, titre, description du marqueur),
/// durée de la pub, catégorie cherchée sur Twitch (si connecté), nom, confirmation et icône.
/// </summary>
public partial class AddTwitchTileWindow : Window
{
    /// <summary>Actions proposées, avec leur libellé.</summary>
    public static readonly (TwitchAction Action, string Label)[] Actions =
    {
        (TwitchAction.Clip, "Créer un clip"),
        (TwitchAction.Marker, "Poser un marqueur"),
        (TwitchAction.ChatMessage, "Envoyer un message dans le tchat"),
        (TwitchAction.Commercial, "Lancer une pub"),
        (TwitchAction.SlowMode, "Mode lent (activer / couper)"),
        (TwitchAction.EmoteOnly, "Mode émotes seulement"),
        (TwitchAction.FollowersOnly, "Mode followers seulement"),
        (TwitchAction.SubscribersOnly, "Mode abonnés seulement"),
        (TwitchAction.ShieldMode, "Mode bouclier (activer / couper)"),
        (TwitchAction.SetTitle, "Changer le titre du direct"),
        (TwitchAction.SetCategory, "Changer la catégorie du direct")
    };

    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private readonly TwitchService _twitch;
    private readonly TileIconState _icon = new();
    private bool _loading;
    private bool _nameManual;    // le nom a été saisi à la main : il n'est plus proposé automatiquement
    private bool _confirmManual; // la confirmation a été réglée à la main : elle ne suit plus l'action
    private string? _categoryId, _categoryName;

    public ShortcutItem? Result { get; private set; }

    public AddTwitchTileWindow(Window owner, Func<EmojiPickerWindow> createEmojiPicker, TwitchService twitch, ShortcutItem? existing = null)
    {
        InitializeComponent();
        Owner = owner;
        _createEmojiPicker = createEmojiPicker;
        _twitch = twitch;

        _loading = true;
        foreach (var (_, label) in Actions) ActionBox.Items.Add(label);
        foreach (var length in TwitchService.CommercialLengths) DurationBox.Items.Add($"{length} s");
        ActionBox.SelectedIndex = existing?.TwitchAction is { } current ? Math.Max(0, Array.FindIndex(Actions, a => a.Action == current)) : 0;
        DurationBox.SelectedIndex = Array.IndexOf(TwitchService.CommercialLengths, TwitchService.CommercialLength(existing?.TwitchText));
        if (existing is not null)
        {
            Title = "SysDeck — Modifier la tuile Twitch";
            TitleText.Text = "Modifier la tuile Twitch";
            AddButton.Content = "Enregistrer";
            if (existing.TwitchAction == TwitchAction.SetCategory)
            {
                _categoryId = existing.TwitchId;
                _categoryName = existing.TwitchText;
            }
            else if (existing.TwitchAction is TwitchAction.Marker or TwitchAction.ChatMessage or TwitchAction.SetTitle)
                TextBoxValue.Text = existing.TwitchText ?? "";
            (existing.TwitchAsk ? AskModeRadio : FixedModeRadio).IsChecked = true;
            NameBox.Text = existing.Name;
            _nameManual = existing.Name != Suggest(Current, Argument());
            _confirmManual = true;
            ConfirmCheck.IsChecked = existing.ConfirmTap;
            _icon.Load(existing);
        }
        else
        {
            AskModeRadio.IsChecked = true;
            ConfirmCheck.IsChecked = ConfirmByDefault(Current);
        }
        _loading = false;

        UpdateRows();
        ShowStatus();
        UpdateSuggestion();
        ShowIcon();
    }

    private TwitchAction Current => Actions[Math.Max(0, ActionBox.SelectedIndex)].Action;

    private int DurationSeconds => TwitchService.CommercialLengths[Math.Max(0, DurationBox.SelectedIndex)];

    /// <summary>Pub et mode bouclier : coup d'envoi lourd de conséquences, la confirmation est proposée d'office.</summary>
    public static bool ConfirmByDefault(TwitchAction action) => action is TwitchAction.Commercial or TwitchAction.ShieldMode;

    /// <summary>Message, titre et catégorie peuvent être saisis au moment de l'appui.</summary>
    public static bool CanAsk(TwitchAction action) => action is TwitchAction.ChatMessage or TwitchAction.SetTitle or TwitchAction.SetCategory;

    private bool Asking => CanAsk(Current) && AskModeRadio.IsChecked == true;

    /// <summary>Paramètre saisi pour l'action courante (texte, durée ou nom de catégorie) ; vide si l'action n'en a pas ou le demande à l'appui.</summary>
    private string Argument() => Asking ? "" : Current switch
    {
        TwitchAction.Marker or TwitchAction.ChatMessage or TwitchAction.SetTitle => TextBoxValue.Text.Trim(),
        TwitchAction.Commercial => DurationSeconds.ToString(),
        TwitchAction.SetCategory => _categoryName ?? "",
        _ => ""
    };

    /// <summary>Nom proposé pour une tuile selon son action et son paramètre.</summary>
    public static string Suggest(TwitchAction action, string argument) => action switch
    {
        TwitchAction.Clip => "Clip",
        TwitchAction.Marker => "Marqueur",
        TwitchAction.ChatMessage => "Message",
        TwitchAction.Commercial => argument.Length > 0 ? $"Pub {argument} s" : "Pub",
        TwitchAction.SlowMode => "Mode lent",
        TwitchAction.EmoteOnly => "Émotes",
        TwitchAction.FollowersOnly => "Followers",
        TwitchAction.SubscribersOnly => "Abonnés",
        TwitchAction.ShieldMode => "Bouclier",
        TwitchAction.SetTitle => "Titre",
        TwitchAction.SetCategory => argument.Length > 0 ? argument : "Catégorie",
        _ => "Twitch"
    };

    /// <summary>Précision affichée dans la liste des tuiles d'une page.</summary>
    public static string Describe(ShortcutItem tile)
    {
        var detail = tile.TwitchAction switch
        {
            _ when tile.AsksTwitchText => tile.TwitchAction switch
            {
                TwitchAction.SetTitle => "titre saisi à l'appui",
                TwitchAction.SetCategory => "catégorie choisie à l'appui",
                _ => "message saisi à l'appui"
            },
            TwitchAction.Marker when !string.IsNullOrWhiteSpace(tile.TwitchText) => $"marqueur « {tile.TwitchText} »",
            TwitchAction.ChatMessage => $"message « {tile.TwitchText} »",
            TwitchAction.Commercial => $"pub de {TwitchService.CommercialLength(tile.TwitchText)} s",
            TwitchAction.SetTitle => $"titre « {tile.TwitchText} »",
            TwitchAction.SetCategory => $"catégorie « {tile.TwitchText} »",
            { } action => Actions.First(a => a.Action == action).Label.ToLowerInvariant(),
            _ => "action non définie"
        };
        return "Twitch · " + detail + (tile.ConfirmTap ? " · confirmation" : "");
    }

    private void UpdateRows()
    {
        var action = Current;
        var asking = Asking;
        var hasText = !asking && action is TwitchAction.Marker or TwitchAction.ChatMessage or TwitchAction.SetTitle;
        AskRow.Visibility = CanAsk(action) ? Visibility.Visible : Visibility.Collapsed;
        TextRow.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        DurationRow.Visibility = action == TwitchAction.Commercial ? Visibility.Visible : Visibility.Collapsed;
        CategoryRow.Visibility = !asking && action == TwitchAction.SetCategory ? Visibility.Visible : Visibility.Collapsed;

        TextLabel.Text = action switch
        {
            TwitchAction.Marker => "Description du marqueur (facultative, 140 caractères)",
            TwitchAction.SetTitle => "Titre (140 caractères)",
            _ => "Message (500 caractères)"
        };
        TextBoxValue.MaxLength = action == TwitchAction.ChatMessage ? 500 : 140;
        ShowCategory();
    }

    private void ShowCategory()
    {
        CategoryCurrent.Text = string.IsNullOrEmpty(_categoryId) ? "Aucune catégorie choisie." : $"Catégorie choisie : {_categoryName}";
        var connected = _twitch.IsConnected;
        CategoryQuery.IsEnabled = CategorySearchButton.IsEnabled = connected;
    }

    private void ShowStatus()
    {
        if (_twitch.IsConnected)
        {
            StatusText.Text = $"Connecté à Twitch ({_twitch.Login}).";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
            return;
        }
        StatusText.Text = "Twitch n'est pas connecté (Paramètres › Applications connectées) : la recherche de catégorie est indisponible. Tu peux créer la tuile quand même.";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xA0, 0x3E));
    }

    private void UpdateSuggestion()
    {
        if (_nameManual) return;
        _loading = true;
        NameBox.Text = Suggest(Current, Argument());
        _loading = false;
    }

    // ---------- Évènements ----------

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        UpdateRows();
        if (!_confirmManual) ConfirmCheck.IsChecked = ConfirmByDefault(Current);
        UpdateSuggestion();
        ShowIcon();
    }

    private void AskMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        UpdateRows();
        UpdateSuggestion();
    }

    private void Duration_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        UpdateSuggestion();
    }

    private void Text_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        UpdateSuggestion();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        _nameManual = NameBox.Text.Trim().Length > 0;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => _confirmManual = true;

    // ---------- Catégorie ----------

    private void CategoryQuery_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        CategorySearch_Click(sender, e);
    }

    private async void CategorySearch_Click(object sender, RoutedEventArgs e)
    {
        var query = CategoryQuery.Text.Trim();
        if (query.Length == 0) return;
        CategorySearchButton.IsEnabled = false;
        try
        {
            var found = await _twitch.SearchCategoriesAsync(query);
            _loading = true;
            CategoryResults.Items.Clear();
            foreach (var category in found) CategoryResults.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category });
            CategoryResults.SelectedIndex = -1;
            CategoryResults.Visibility = Visibility.Visible;
            _loading = false;
            if (found.Count == 0) CategoryCurrent.Text = "Aucune catégorie trouvée pour cette recherche.";
        }
        finally
        {
            CategorySearchButton.IsEnabled = _twitch.IsConnected;
        }
    }

    private void CategoryResults_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategoryResults.SelectedItem is not ComboBoxItem { Tag: TwitchCategory category }) return;
        _categoryId = category.Id;
        _categoryName = category.Name;
        ShowCategory();
        UpdateSuggestion();
    }

    // ---------- Icône et validation ----------

    private string DefaultGlyph => TileRuntime.TwitchGlyph(new ShortcutItem { Kind = TileKind.Twitch, TwitchAction = Current });

    private void ShowIcon() => IconHost.Content = ShortcutIcons.Create(_icon.Preview(DefaultGlyph), 28);

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Twitch" : NameBox.Text.Trim();
        if (_icon.Choose(this, name, _createEmojiPicker, DefaultGlyph)) ShowIcon();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var action = Current;
        var argument = Argument();
        var asking = Asking;
        var missing = asking ? null : action switch
        {
            TwitchAction.ChatMessage when argument.Length == 0 => "Saisis le message à envoyer.",
            TwitchAction.SetTitle when argument.Length == 0 => "Saisis le titre.",
            TwitchAction.SetCategory when string.IsNullOrEmpty(_categoryId) => "Cherche puis choisis une catégorie (il faut être connecté à Twitch).",
            _ => null
        };
        if (missing is not null)
        {
            MessageBox.Show(this, missing, "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = new ShortcutItem
        {
            Kind = TileKind.Twitch,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? Suggest(action, argument) : NameBox.Text.Trim(),
            TwitchAction = action,
            TwitchText = argument.Length > 0 ? argument : null,
            TwitchId = action == TwitchAction.SetCategory && !asking ? _categoryId : null,
            TwitchAsk = asking,
            ConfirmTap = ConfirmCheck.IsChecked == true
        };
        // Icône par défaut laissée vide : elle suit l'action, voir TileRuntime.ForDisplay.
        if (!_icon.ApplyTo(item, this, defaultGlyph: null)) return;

        Result = item;
        DialogResult = true;
    }
}
