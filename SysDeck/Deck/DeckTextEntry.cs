using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SysDeck.Infrastructure;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Deck;

public enum TextEntryKind { Chat, Title, Category }

/// <summary>
/// Demande de saisie sur la Switch. <paramref name="Submit"/> reçoit le texte (ou la catégorie choisie) et renvoie une erreur
/// lisible, ou null si c'est fait ; <paramref name="LoadInitial"/> fournit un texte de départ lu en différé (titre actuel).
/// </summary>
public sealed record TextEntryRequest(TextEntryKind Kind, string Title, int MaxLength,
    Func<string, TwitchCategory?, Task<string?>> Submit, Func<Task<string?>>? LoadInitial = null);

/// <summary>
/// Panneau modal plein écran de la Switch : champ de texte, clavier AZERTY tactile, onglets Émotes Twitch et Émojis,
/// ou recherche de catégorie avec jaquettes. Les touchers lui sont routés par <see cref="DeckController"/> tant qu'il est ouvert.
/// </summary>
public sealed class DeckTextEntry : Grid, IDisposable
{
    private enum Tab { Keyboard, Emotes, Emojis }

    /// <summary>Zone touchable : action à l'appui ; <see cref="Repeat"/> = répétée tant que le doigt reste posé (retour arrière).</summary>
    private sealed record Hit(Action Action, bool Repeat = false);

    // Pinceaux figés partagés : le clavier est reconstruit à chaque changement de couche ou de majuscule.
    private static readonly Brush PanelBrush = Frozen(Color.FromRgb(0x18, 0x19, 0x1F));
    private static readonly Brush FieldBrush = Frozen(Color.FromRgb(0x23, 0x24, 0x2B));
    private static readonly Brush KeyBrush = Frozen(Color.FromRgb(0x2C, 0x2D, 0x35));
    private static readonly Brush SpecialKeyBrush = Frozen(Color.FromRgb(0x3A, 0x3B, 0x45));
    private static readonly Brush ShiftOnceBrush = Frozen(Color.FromArgb(0x88, 0x5B, 0x8D, 0xEF));
    private static readonly Brush TextColor = Frozen(Color.FromRgb(0xF2, 0xF3, 0xF5));
    private static readonly Brush MutedColor = Frozen(Color.FromRgb(0x9A, 0x9D, 0xA7));
    private static readonly Brush ErrorColor = Frozen(Color.FromRgb(0xF0, 0x7A, 0x75));

    private static readonly string[] Emojis =
    {
        "😀", "😃", "😄", "😁", "😆", "😅", "🤣", "😂", "🙂", "😉", "😊", "😇", "🥰", "😍", "🤩", "😘", "😋", "😛", "😜", "🤪",
        "🤑", "🤗", "🤭", "🤫", "🤔", "🤐", "🤨", "😐", "😑", "😶", "😏", "😒", "🙄", "😬", "😌", "😔", "😪", "😴", "😷", "🤒",
        "🥵", "🥶", "🥴", "😵", "🤯", "🤠", "🥳", "😎", "🤓", "🧐", "😕", "😟", "🙁", "😮", "😯", "😲", "😳", "🥺", "😦", "😧",
        "😨", "😰", "😥", "😢", "😭", "😱", "😖", "😣", "😞", "😓", "😩", "😫", "🥱", "😤", "😡", "😠", "🤬", "😈", "👿", "💀",
        "💩", "🤡", "👻", "👽", "🤖", "😺", "🙈", "🙉", "🙊", "💯", "💥", "💫", "💦", "💨", "🔥", "✨", "⭐", "🌟", "⚡", "🎉",
        "🎊", "🎁", "🏆", "🥇", "🎮", "🕹️", "🎧", "🎤", "🎬", "📸", "💻", "📱", "⏰", "💡", "💰", "💎", "❤️", "🧡", "💛", "💚",
        "💙", "💜", "🖤", "🤍", "💔", "❣️", "💕", "👍", "👎", "👌", "✌️", "🤞", "🤟", "🤘", "👏", "🙌", "👐", "🙏", "💪", "👀",
        "👋", "🤙", "✅", "❌", "⚠️", "❓", "❗", "🍕", "🍔", "🍟", "🍿", "☕", "🍺", "🍻", "🥂", "🐐", "🐸", "🐱", "🐶", "🦄"
    };

    private const int ImageCacheSize = 300;
    private static readonly Dictionary<string, ImageSource> ImageCache = new();
    private static readonly LinkedList<string> ImageOrder = new();

    private readonly TextEntryRequest _request;
    private readonly TwitchService _twitch;
    private readonly Action _changed;
    private readonly Action _close;
    private readonly TextEntryModel _model;
    private readonly DispatcherTimer _repeatTimer = new(DispatcherPriority.Input);
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private readonly Emoji.Wpf.TextBlock _textView = new() { FontSize = 26, TextWrapping = TextWrapping.Wrap, MaxHeight = 70, Margin = new Thickness(0, 0, 100, 0) };
    private readonly TextBlock _counter = new() { FontSize = 14, Foreground = MutedColor, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _error = new() { FontSize = 16, Foreground = ErrorColor, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 0) };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
    private readonly Border _content = new();
    private readonly Grid _keyboard = new();
    private readonly UniformGrid _results = new() { Rows = 1, Columns = 8 };
    private readonly TextBlock _resultsStatus = new() { FontSize = 18, Foreground = MutedColor, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _submitButton;

    private Tab _tab = Tab.Keyboard;
    private bool _busy, _edited, _disposed;
    private string _searched = "";
    private ScrollViewer? _emotesView, _emojisView;
    private Grid? _categoryLayout;

    // Toucher en cours
    private Border? _pressed;
    private Brush? _pressedBackground;
    private Point _down;
    private ScrollViewer? _scroll;
    private double _scrollStart;
    private bool _scrolling, _repeated;

    public DeckTextEntry(TextEntryRequest request, TwitchService twitch, Action changed, Action close)
    {
        _request = request;
        _twitch = twitch;
        _changed = changed;
        _close = close;
        _model = new TextEntryModel(request.MaxLength);

        Width = DeckScreen.ScreenWidth;
        Height = DeckScreen.ScreenHeight;
        Background = Frozen(Color.FromArgb(0xE6, 0x05, 0x06, 0x09));
        _textView.Foreground = TextColor;

        _submitButton = Button(request.Kind == TextEntryKind.Title ? "Appliquer" : "Envoyer", accent: true, Submit);
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button("Annuler", accent: false, _close));
        if (request.Kind != TextEntryKind.Category) buttons.Children.Add(_submitButton);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(new TextBlock
        {
            Text = request.Title, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = TextColor,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(header);

        var fieldGrid = new Grid();
        fieldGrid.Children.Add(_textView);
        fieldGrid.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Child = _counter, Margin = new Thickness(0, 0, 0, -4) });
        var field = new Border
        {
            Background = FieldBrush, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10, 16, 12),
            MinHeight = 74, Margin = new Thickness(0, 12, 0, 0), Child = fieldGrid
        };
        SetRow(field, 1);
        panel.Children.Add(field);
        SetRow(_error, 2);
        panel.Children.Add(_error);
        SetRow(_tabs, 3);
        panel.Children.Add(_tabs);
        SetRow(_content, 4);
        panel.Children.Add(_content);

        Children.Add(new Border
        {
            Background = PanelBrush, CornerRadius = new CornerRadius(18), Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(16), BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = panel
        });

        _repeatTimer.Tick += (_, _) => RepeatTick();
        _searchTimer.Tick += (_, _) => Search();
        BuildTabs();
        ShowTab(Tab.Keyboard);
        RefreshText();
        if (request.LoadInitial is { } load) LoadInitial(load);
    }

    // ---------- Construction ----------

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Border Button(string text, bool accent, Action action)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(26, 0, 26, 0), Height = 56, MinWidth = 140,
            Margin = new Thickness(10, 0, 0, 0), Tag = new Hit(action),
            Child = new TextBlock
            {
                Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (accent)
        {
            border.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            ((TextBlock)border.Child).Foreground = Brushes.White;
        }
        else
        {
            border.Background = SpecialKeyBrush;
            ((TextBlock)border.Child).Foreground = TextColor;
        }
        return border;
    }

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        var tabs = _request.Kind switch
        {
            TextEntryKind.Chat => new[] { (Tab.Keyboard, "Clavier"), (Tab.Emotes, "Émotes"), (Tab.Emojis, "Émojis") },
            TextEntryKind.Title => new[] { (Tab.Keyboard, "Clavier"), (Tab.Emojis, "Émojis") },
            _ => Array.Empty<(Tab, string)>()
        };
        _tabs.Visibility = tabs.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (tab, label) in tabs)
        {
            var selected = tab == _tab;
            var border = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(0, 0, 8, 0),
                Tag = new Hit(() => ShowTab(tab)),
                Child = new TextBlock { Text = label, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = selected ? Brushes.White : MutedColor }
            };
            if (selected) border.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else border.Background = FieldBrush;
            _tabs.Children.Add(border);
        }
    }

    private void ShowTab(Tab tab)
    {
        _tab = tab;
        BuildTabs();
        switch (tab)
        {
            case Tab.Emotes:
                _content.Child = _emotesView ??= BuildEmotes();
                break;
            case Tab.Emojis:
                _content.Child = _emojisView ??= BuildEmojis();
                break;
            default:
                BuildKeyboard();
                _content.Child = _request.Kind == TextEntryKind.Category ? _categoryLayout ??= BuildCategoryLayout() : _keyboard;
                break;
        }
        _changed();
    }

    /// <summary>Recherche de catégorie : résultats avec jaquettes au-dessus du clavier.</summary>
    private Grid BuildCategoryLayout()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(212) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var results = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        results.Children.Add(_resultsStatus);
        results.Children.Add(_results);
        grid.Children.Add(results);
        SetRow(_keyboard, 1);
        grid.Children.Add(_keyboard);
        _resultsStatus.Text = "Tape le nom d'un jeu ou d'une catégorie.";
        return grid;
    }

    private static double KeyWeight(string key) => key switch
    {
        TextEntryModel.ShiftKey or TextEntryModel.BackKey => 1.5,
        TextEntryModel.SymbolsKey or TextEntryModel.LettersKey or TextEntryModel.AccentsKey => 1.4,
        TextEntryModel.SpaceKey => 4.2,
        TextEntryModel.EnterKey => 2.2,
        _ => 1
    };

    /// <summary>Reconstruit les touches (couche ou majuscule changée) : une quarantaine de bordures, rien de coûteux.</summary>
    private void BuildKeyboard()
    {
        _keyboard.Children.Clear();
        _keyboard.RowDefinitions.Clear();
        var rows = _model.CurrentRows;
        for (var r = 0; r < rows.Count; r++)
        {
            _keyboard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var row = new Grid();
            foreach (var key in rows[r])
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyWeight(key), GridUnitType.Star) });
                var cell = Key(key);
                SetColumn(cell, row.ColumnDefinitions.Count - 1);
                row.Children.Add(cell);
            }
            SetRow(row, r);
            _keyboard.Children.Add(row);
        }
    }

    private Border Key(string key)
    {
        var special = TextEntryModel.IsSpecial(key);
        var label = _model.Label(key);
        if (key == TextEntryModel.EnterKey && _request.Kind == TextEntryKind.Category) label = "OK";
        else if (key == TextEntryModel.EnterKey && _request.Kind == TextEntryKind.Title) label = "Appliquer";
        var text = new TextBlock
        {
            Text = label, FontSize = special && label.Length > 2 ? 18 : 26, Foreground = TextColor,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(10), Margin = new Thickness(4), Child = text,
            Tag = new Hit(() => PressKey(key), Repeat: key == TextEntryModel.BackKey)
        };
        if (key == TextEntryModel.EnterKey || (key == TextEntryModel.ShiftKey && _model.Shift == ShiftMode.Locked))
        {
            border.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            text.Foreground = Brushes.White;
        }
        else if (key == TextEntryModel.ShiftKey && _model.Shift == ShiftMode.Once)
            border.Background = ShiftOnceBrush;
        else border.Background = special ? SpecialKeyBrush : KeyBrush;
        return border;
    }

    private ScrollViewer Scroller(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = content,
        Background = Brushes.Transparent
    };

    private ScrollViewer BuildEmojis()
    {
        var wrap = new WrapPanel();
        foreach (var emoji in Emojis)
        {
            var image = new Image { Width = 44, Height = 44 };
            Emoji.Wpf.Image.SetSource(image, emoji);
            var value = emoji;
            wrap.Children.Add(new Border
            {
                Width = 74, Height = 74, Margin = new Thickness(4), CornerRadius = new CornerRadius(10),
                Background = KeyBrush, Child = image, Tag = new Hit(() => { _model.Insert(value); OnTextChanged(); })
            });
        }
        return Scroller(wrap);
    }

    private ScrollViewer BuildEmotes()
    {
        var stack = new StackPanel();
        var view = Scroller(stack);
        if (!_twitch.HasScope(TwitchService.EmotesScope))
        {
            stack.Children.Add(Message("Reconnecte-toi à Twitch dans les Paramètres (Applications connectées) pour utiliser tes émotes."));
            return view;
        }
        stack.Children.Add(Message("Chargement de tes émotes…"));
        LoadEmotes(stack);
        return view;
    }

    private static TextBlock Message(string text) => new()
    {
        Text = text, FontSize = 18, Foreground = MutedColor, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 24, 8, 0),
        HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center
    };

    private static string SectionOf(string type) => type switch
    {
        "subscriptions" => "Abonnements",
        "follower" => "Follower",
        "bitstier" => "Bits",
        "globals" or "smilies" => "Globales",
        _ => "Autres"
    };

    private static readonly string[] SectionOrder = { "Abonnements", "Follower", "Bits", "Autres", "Globales" };

    private async void LoadEmotes(StackPanel stack)
    {
        var emotes = await _twitch.GetEmotesAsync();
        if (_disposed) return;
        stack.Children.Clear();
        if (emotes is null || emotes.Count == 0)
        {
            stack.Children.Add(Message(emotes is null ? "Impossible de lire tes émotes sur Twitch." : "Aucune émote disponible."));
            _changed();
            return;
        }
        foreach (var group in emotes.GroupBy(e => SectionOf(e.Type)).OrderBy(g => Array.IndexOf(SectionOrder, g.Key)))
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{group.Key} ({group.Count()})", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = MutedColor,
                Margin = new Thickness(6, 10, 0, 4)
            });
            var wrap = new WrapPanel();
            foreach (var emote in group)
            {
                var image = new Image { Width = 56, Height = 56 };
                LoadImage(image, emote.ImageUrl, 112);
                var name = emote.Name;
                wrap.Children.Add(new Border
                {
                    Width = 74, Height = 74, Margin = new Thickness(4), CornerRadius = new CornerRadius(10),
                    Background = KeyBrush, Child = image, Tag = new Hit(() => { _model.InsertWord(name); OnTextChanged(); })
                });
            }
            stack.Children.Add(wrap);
        }
        _changed();
    }

    // ---------- Images ----------

    /// <summary>Image distante (émote, jaquette) : cache mémoire borné, décodage hors thread interface, image figée.</summary>
    private async void LoadImage(Image target, string? url, int decodeWidth)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (ImageCache.TryGetValue(url, out var cached))
        {
            target.Source = cached;
            return;
        }
        var bytes = await _twitch.DownloadAsync(url);
        if (bytes is null || _disposed) return;
        var image = await Task.Run(() =>
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                return (ImageSource?)bitmap;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException)
            {
                return null;
            }
        });
        if (image is null || _disposed) return;
        if (!ImageCache.ContainsKey(url))
        {
            ImageCache[url] = image;
            ImageOrder.AddLast(url);
            while (ImageOrder.Count > ImageCacheSize)
            {
                ImageCache.Remove(ImageOrder.First!.Value);
                ImageOrder.RemoveFirst();
            }
        }
        target.Source = image;
        _changed();
    }

    // ---------- Saisie ----------

    private async void LoadInitial(Func<Task<string?>> load)
    {
        _textView.Text = "";
        _counter.Text = "Chargement…";
        var initial = await load();
        if (_disposed || _edited || string.IsNullOrEmpty(initial)) return;
        _model.Clear();
        _model.Insert(initial);
        if (_model.Shift == ShiftMode.Once) _model.Press(TextEntryModel.ShiftKey); // texte présent : pas de majuscule forcée
        BuildKeyboard();
        RefreshText();
        _changed();
    }

    private void PressKey(string key)
    {
        var shift = _model.Shift;
        var layer = _model.Layer;
        if (_model.Press(key))
        {
            if (_request.Kind == TextEntryKind.Category) Search();
            else Submit();
            return;
        }
        if (shift != _model.Shift || layer != _model.Layer) BuildKeyboard();
        if (!TextEntryModel.IsSpecial(key) || key is TextEntryModel.BackKey or TextEntryModel.SpaceKey) OnTextChanged();
        else _changed();
    }

    private void OnTextChanged()
    {
        _edited = true;
        _error.Text = "";
        RefreshText();
        if (_request.Kind == TextEntryKind.Category)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }
        _changed();
    }

    private void RefreshText()
    {
        var text = _model.Text;
        // Le champ montre la fin du texte : on écrit toujours à la fin.
        var shown = text.Length > 110 ? "…" + text[^109..] : text;
        if (text.Length == 0)
        {
            _textView.Foreground = MutedColor;
            _textView.Text = _request.Kind switch
            {
                TextEntryKind.Chat => "Écris ton message…",
                TextEntryKind.Category => "Rechercher une catégorie…",
                _ => "Nouveau titre…"
            };
        }
        else
        {
            _textView.Foreground = TextColor;
            _textView.Text = shown + "▏";
        }
        _counter.Text = $"{text.Length} / {_model.MaxLength}";
        _error.Visibility = string.IsNullOrEmpty(_error.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        RefreshText();
        _changed();
    }

    private async void Submit()
    {
        var text = _model.Text.Trim();
        if (_busy) return;
        if (text.Length == 0)
        {
            ShowError(_request.Kind == TextEntryKind.Title ? "Le titre ne peut pas être vide." : "Écris d'abord un message.");
            return;
        }
        await Run(text, null);
    }

    private async Task Run(string text, TwitchCategory? category)
    {
        _busy = true;
        SetSubmitText("Envoi…");
        try
        {
            var error = await _request.Submit(text, category);
            if (_disposed) return;
            if (error is null)
            {
                _close();
                return;
            }
            ShowError(error);
        }
        catch (Exception ex)
        {
            Log.Warn("Saisie Twitch sur la Switch en échec", ex);
            ShowError("L'envoi a échoué.");
        }
        finally
        {
            _busy = false;
            if (!_disposed) SetSubmitText(_request.Kind == TextEntryKind.Title ? "Appliquer" : "Envoyer");
        }
    }

    private void SetSubmitText(string text)
    {
        ((TextBlock)_submitButton.Child).Text = text;
        _changed();
    }

    private async void Search()
    {
        _searchTimer.Stop();
        var query = _model.Text.Trim();
        if (query == _searched && _results.Children.Count > 0) return;
        _searched = query;
        if (query.Length == 0)
        {
            _results.Children.Clear();
            _resultsStatus.Text = "Tape le nom d'un jeu ou d'une catégorie.";
            _changed();
            return;
        }
        _resultsStatus.Text = "Recherche…";
        _changed();
        var found = await _twitch.SearchCategoriesAsync(query);
        if (_disposed || query != _searched) return;
        _results.Children.Clear();
        _resultsStatus.Text = found.Count == 0 ? "Aucune catégorie trouvée." : "";
        foreach (var category in found.Take(8))
        {
            var art = new Image { Width = 108, Height = 144, Stretch = Stretch.UniformToFill };
            LoadImage(art, category.BoxArtUrl, 144);
            var card = new StackPanel();
            card.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = art, HorizontalAlignment = HorizontalAlignment.Center });
            card.Children.Add(new TextBlock
            {
                Text = category.Name, FontSize = 14, Foreground = TextColor, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 6, 4, 0)
            });
            var chosen = category;
            _results.Children.Add(new Border
            {
                Margin = new Thickness(5, 0, 5, 0), Padding = new Thickness(0, 6, 0, 6), CornerRadius = new CornerRadius(12),
                Background = KeyBrush, Child = card, Tag = new Hit(() => { if (!_busy) _ = Run(chosen.Name, chosen); })
            });
        }
        _changed();
    }

    // ---------- Tactile (routé par DeckController) ----------

    private T? FindAncestor<T>(Point point, Func<T, bool>? accept = null) where T : DependencyObject
    {
        for (var d = InputHitTest(point) as DependencyObject; d is not null && d != this; d = VisualTreeHelper.GetParent(d))
            if (d is T found && (accept is null || accept(found))) return found;
        return null;
    }

    private Border? FindHit(Point point) => FindAncestor<Border>(point, b => b.Tag is Hit);

    public void FingerDown(Point point)
    {
        Release();
        _down = point;
        _scrolling = _repeated = false;
        _scroll = FindAncestor<ScrollViewer>(point);
        _scrollStart = _scroll?.VerticalOffset ?? 0;
        if (FindHit(point) is not { } hit) return;
        _pressed = hit;
        _pressedBackground = hit.Background;
        hit.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        if (hit.Tag is Hit { Repeat: true })
        {
            _repeatTimer.Interval = TimeSpan.FromMilliseconds(450);
            _repeatTimer.Start();
        }
        _changed();
    }

    public void FingerMove(Point point)
    {
        var delta = point - _down;
        if (_scroll is not null && (_scrolling || Math.Abs(delta.Y) > 18))
        {
            _scrolling = true;
            Release();
            _scroll.ScrollToVerticalOffset(Math.Max(0, _scrollStart - delta.Y));
            _changed();
        }
        else if (_pressed is not null && delta.Length > 40)
        {
            Release();
            _changed();
        }
    }

    public void FingerUp(Point point)
    {
        var pressed = _pressed;
        var repeated = _repeated;
        Release();
        _changed();
        if (_scrolling || pressed is null || repeated || FindHit(point) != pressed) return;
        ((Hit)pressed.Tag).Action();
    }

    private void RepeatTick()
    {
        if (_pressed?.Tag is not Hit hit)
        {
            _repeatTimer.Stop();
            return;
        }
        _repeated = true;
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(80);
        hit.Action();
    }

    /// <summary>Relâche la zone enfoncée (couleur d'origine) et arrête la répétition.</summary>
    private void Release()
    {
        _repeatTimer.Stop();
        if (_pressed is null) return;
        _pressed.Background = _pressedBackground;
        _pressed = null;
        _pressedBackground = null;
    }

    public void Dispose()
    {
        _disposed = true;
        _repeatTimer.Stop();
        _searchTimer.Stop();
    }
}
