using System.Windows;
using System.Windows.Controls;
using SysDeck.Controls;
using SysDeck.Models;

namespace SysDeck;

/// <summary>
/// Lien ou application personnalisée : nom, lien (exécutable, fichier, dossier, URL, protocole), arguments et icône.
/// Depuis la barre du bureau : icône PNG facultative. Depuis les Paramètres › Switch (sélecteur d'émoji fourni) :
/// choix d'icône complet, et modification d'une tuile existante.
/// </summary>
public partial class AddCustomShortcutWindow : Window
{
    private readonly Func<EmojiPickerWindow>? _createEmojiPicker;
    private readonly TileIconState _icon = new();

    public ShortcutItem? Result { get; private set; }

    /// <param name="owner">Fenêtre parente (null = centrée sur l'écran).</param>
    /// <param name="createEmojiPicker">Fournit le sélecteur d'émoji ; active le choix d'icône complet.</param>
    /// <param name="existing">Tuile à modifier (champs pré-remplis) ; null pour en créer une.</param>
    public AddCustomShortcutWindow(Window? owner = null, Func<EmojiPickerWindow>? createEmojiPicker = null, ShortcutItem? existing = null)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        _createEmojiPicker = createEmojiPicker;
        if (createEmojiPicker is not null)
        {
            LegacyIconPanel.Visibility = Visibility.Collapsed;
            ChooserIconPanel.Visibility = Visibility.Visible;
        }
        if (existing is not null)
        {
            Title = "SysDeck — Modifier la tuile";
            TitleText.Text = "Modifier la tuile";
            AddButton.Content = "Enregistrer";
            NameBox.Text = existing.Name;
            LinkBox.Text = existing.Path;
            ArgumentsBox.Text = existing.Arguments;
            _icon.Load(existing);
        }
        ShowIcon();
    }

    private void ShowIcon()
    {
        if (_createEmojiPicker is null) return;
        IconHost.Content = ShortcutIcons.Create(_icon.Preview(null, LinkBox.Text.Trim()), 28);
    }

    private void LinkBox_LostFocus(object sender, RoutedEventArgs e) => ShowIcon();

    private void BrowseLink_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir un exécutable ou un fichier",
            Filter = "Exécutables et raccourcis (*.exe;*.lnk)|*.exe;*.lnk|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        LinkBox.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        ShowIcon();
    }

    private void BrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir une icône",
            Filter = "Images (*.png;*.ico;*.jpg;*.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.bmp|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        IconBox.Text = dialog.FileName;
    }

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        if (_createEmojiPicker is null) return;
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Tuile" : NameBox.Text.Trim();
        if (_icon.Choose(this, name, _createEmojiPicker, null, LinkBox.Text.Trim())) ShowIcon();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var link = LinkBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(link))
        {
            MessageBox.Show(this, "Le lien ne peut pas être vide.", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
            name = System.IO.Path.GetFileNameWithoutExtension(link.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) name = link;

        var item = new ShortcutItem
        {
            Name = name,
            Path = link,
            Arguments = ArgumentsBox.Text.Trim()
        };
        if (_createEmojiPicker is null)
            item.CustomIconPath = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text.Trim();
        else if (!_icon.ApplyTo(item, this, defaultGlyph: null))
            return;

        Result = item;
        DialogResult = true;
        Close();
    }
}
