using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SysDeck.Deck;
using SysDeck.Infrastructure;
using SysDeck.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SysDeck;

/// <summary>Saisie sur le PC d'une tuile Twitch « demander » touchée depuis le bureau (message, titre ou catégorie).</summary>
public partial class TwitchInputWindow : Window
{
    private static TwitchInputWindow? _current;

    private readonly TextEntryRequest _request;
    private readonly TwitchService _twitch;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _busy, _edited;

    private TwitchInputWindow(TextEntryRequest request, TwitchService twitch)
    {
        InitializeComponent();
        _request = request;
        _twitch = twitch;
        TitleText.Text = request.Title;
        InputBox.MaxLength = request.MaxLength;
        var category = request.Kind == TextEntryKind.Category;
        ResultsList.Visibility = category ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Content = request.Kind switch { TextEntryKind.Title => "Appliquer", TextEntryKind.Category => "Choisir", _ => "Envoyer" };
        _searchTimer.Tick += (_, _) => Search();
        Loaded += async (_, _) =>
        {
            InputBox.Focus();
            if (request.LoadInitial is { } load && await load() is { Length: > 0 } initial && !_edited)
            {
                InputBox.Text = initial;
                InputBox.SelectAll();
                _edited = false;
            }
        };
        UpdateCounter();
    }

    /// <summary>Ouvre la saisie (remplace celle déjà ouverte) au premier plan.</summary>
    public static void Open(TextEntryRequest request, TwitchService twitch)
    {
        _current?.Close();
        _current = new TwitchInputWindow(request, twitch);
        _current.Closed += (sender, _) => { if (_current == sender) _current = null; };
        _current.Show();
        _current.Activate();
    }

    private void UpdateCounter() => CounterText.Text = $"{InputBox.Text.Length} / {_request.MaxLength}";

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _edited = true;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateCounter();
        if (_request.Kind != TextEntryKind.Category) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Submit_Click(sender, e);
    }

    private async void Search()
    {
        _searchTimer.Stop();
        var query = InputBox.Text.Trim();
        var found = query.Length == 0 ? new List<TwitchCategory>() : await _twitch.SearchCategoriesAsync(query);
        if (query != InputBox.Text.Trim()) return;
        ResultsList.ItemsSource = found;
        if (found.Count > 0) ResultsList.SelectedIndex = 0;
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e) => Submit_Click(sender, e);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var text = InputBox.Text.Trim();
        TwitchCategory? category = null;
        if (_request.Kind == TextEntryKind.Category)
        {
            if (ResultsList.SelectedItem is not TwitchCategory chosen)
            {
                ShowError("Choisis une catégorie dans la liste.");
                return;
            }
            category = chosen;
            text = chosen.Name;
        }
        else if (text.Length == 0)
        {
            ShowError(_request.Kind == TextEntryKind.Title ? "Le titre ne peut pas être vide." : "Écris d'abord un message.");
            return;
        }

        _busy = true;
        SubmitButton.IsEnabled = false;
        try
        {
            if (await _request.Submit(text, category) is { } error) ShowError(error);
            else Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Saisie Twitch sur le PC en échec", ex);
            ShowError("L'envoi a échoué.");
        }
        finally
        {
            _busy = false;
            SubmitButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _searchTimer.Stop();
        base.OnClosed(e);
    }
}
