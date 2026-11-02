using System.Windows;
using System.Windows.Controls;
using SysDeck.Services;

namespace SysDeck.Controls;

/// <summary>
/// Choix d'une police installée ou importée ; la clé choisie est écrite dans <see cref="FontKey"/>.
/// La liste complète (plusieurs centaines de polices) n'est chargée qu'à l'ouverture du menu : tant
/// qu'il est fermé, seule la police choisie est affichée, ce qui garde les pages de réglages rapides.
/// </summary>
public partial class FontPicker : UserControl
{
    public static readonly DependencyProperty FontKeyProperty = DependencyProperty.Register(
        nameof(FontKey), typeof(string), typeof(FontPicker),
        new FrameworkPropertyMetadata("Segoe UI", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((FontPicker)d).SelectCurrent()));

    public string FontKey { get => (string)GetValue(FontKeyProperty); set => SetValue(FontKeyProperty, value); }

    /// <summary>Ajoute en tête « Police du thème » (clé vide) : le texte garde la police définie par le thème.</summary>
    public static readonly DependencyProperty AllowThemeDefaultProperty = DependencyProperty.Register(
        nameof(AllowThemeDefault), typeof(bool), typeof(FontPicker), new PropertyMetadata(false));

    public bool AllowThemeDefault { get => (bool)GetValue(AllowThemeDefaultProperty); set => SetValue(AllowThemeDefaultProperty, value); }

    /// <summary>Affiche le bouton « Importer… » (masqué dans les listes compactes).</summary>
    public static readonly DependencyProperty ShowImportProperty = DependencyProperty.Register(
        nameof(ShowImport), typeof(bool), typeof(FontPicker),
        new PropertyMetadata(true, (d, e) => ((FontPicker)d).ImportButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public bool ShowImport { get => (bool)GetValue(ShowImportProperty); set => SetValue(ShowImportProperty, value); }

    private static readonly FontChoice ThemeDefault = new("", "Police du thème", new FontFamily("Segoe UI"), false);

    private List<FontChoice> _choices = new();
    private bool _fullList;
    private bool _syncing;

    public FontPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => SelectCurrent();
        Combo.DropDownOpened += (_, _) => LoadFullList();
    }

    private void LoadFullList()
    {
        if (_fullList) return;
        _fullList = true;
        _choices = FontService.GetChoices().ToList();
        if (AllowThemeDefault) _choices.Insert(0, ThemeDefault);
        SelectCurrent();
    }

    private FontChoice? FindCurrent()
    {
        if (AllowThemeDefault && string.IsNullOrEmpty(FontKey)) return ThemeDefault;
        if (string.IsNullOrWhiteSpace(FontKey)) return null;
        return _choices.FirstOrDefault(c => string.Equals(c.Key, FontKey, StringComparison.OrdinalIgnoreCase))
               // Police absente de la liste (pas encore chargée, ou style nommé d'une police variable).
               ?? new FontChoice(FontKey, FontKey.StartsWith('@') ? FontKey[(FontKey.IndexOf('#') + 1)..] : FontKey, FontService.Resolve(FontKey), false);
    }

    private void SelectCurrent()
    {
        if (!IsLoaded) return;
        var current = FindCurrent();

        _syncing = true;
        if (!_fullList)
        {
            Combo.ItemsSource = current is null ? Array.Empty<FontChoice>() : new[] { current };
        }
        else
        {
            if (current is not null && !_choices.Contains(current))
                _choices.Insert(0, current);
            if (!ReferenceEquals(Combo.ItemsSource, _choices))
                Combo.ItemsSource = _choices;
        }
        Combo.SelectedItem = current;
        _syncing = false;
    }

    private void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Combo.SelectedItem is not FontChoice choice) return;
        FontKey = choice.Key;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importer une police",
            Filter = "Polices (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        string? key;
        try
        {
            key = FontService.Import(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"Impossible d'importer la police :\n{ex.Message}", "SysDeck");
            return;
        }

        if (key is null)
        {
            MessageBox.Show(Window.GetWindow(this)!, "Ce fichier n'est pas une police lisible.", "SysDeck");
            return;
        }

        _fullList = false; // la liste partagée a changé : rechargée à la prochaine ouverture
        FontKey = key;
    }
}
