using System.Windows;
using System.Windows.Controls;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

/// <summary>Création ou modification d'une tuile Commande : préréglage, nom, ligne de commande, options et icône.</summary>
public partial class AddCommandTileWindow : TileEditorWindow
{
    private const string CustomPreset = "Personnalisée";

    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private readonly TileIconState _icon = new();
    private string _defaultGlyph = TileRuntime.CommandGlyph;
    private bool _loading;

    public AddCommandTileWindow(Window owner, Func<EmojiPickerWindow> createEmojiPicker, ShortcutItem? existing = null)
        : base(owner)
    {
        InitializeComponent();
        _createEmojiPicker = createEmojiPicker;

        _loading = true;
        PresetBox.Items.Add(CustomPreset);
        foreach (var preset in CommandRunner.Presets) PresetBox.Items.Add(preset.Name);
        PresetBox.SelectedIndex = 0;
        _loading = false;

        if (existing is not null)
        {
            Title = "SysDeck — Modifier la commande";
            TitleText.Text = "Modifier la commande";
            AddButton.Content = "Enregistrer";
            NameBox.Text = existing.Name;
            CommandBox.Text = existing.Command ?? "";
            ConfirmCheck.IsChecked = existing.ConfirmTap;
            AdminCheck.IsChecked = existing.RunAsAdmin;
            _icon.Load(existing);
        }
        ShowIcon();
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var preset = CommandRunner.Presets.FirstOrDefault(p => p.Name == PresetBox.SelectedItem as string);
        if (preset is null)
        {
            PresetHint.Text = "Remplit le nom, la commande et l'icône ; tout reste modifiable.";
            return;
        }
        NameBox.Text = preset.Name;
        CommandBox.Text = preset.Command;
        ConfirmCheck.IsChecked = preset.Confirm;
        PresetHint.Text = preset.Description;
        _defaultGlyph = preset.Glyph;
        _icon.Glyph = _icon.ImagePath = null; // l'icône du préréglage remplace un choix précédent
        ShowIcon();
    }

    private void ShowIcon() => IconHost.Content = ShortcutIcons.Create(_icon.Preview(_defaultGlyph), 28);

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Commande" : NameBox.Text.Trim();
        if (ChooseIconInline(_icon.CreateChooser(DialogOwner, name, _createEmojiPicker, _defaultGlyph), choice =>
            {
                _icon.ApplyChoice(choice);
                ShowIcon();
            })) return;
        if (_icon.Choose(DialogOwner, name, _createEmojiPicker, _defaultGlyph)) ShowIcon();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(false);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var command = CommandBox.Text.Trim();
        if (command.Length == 0)
        {
            MessageBox.Show(DialogOwner, "La commande ne peut pas être vide.", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = new ShortcutItem
        {
            Kind = TileKind.Command,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? command : NameBox.Text.Trim(),
            Command = command,
            ConfirmTap = ConfirmCheck.IsChecked == true,
            RunAsAdmin = AdminCheck.IsChecked == true
        };
        if (!_icon.ApplyTo(item, DialogOwner, _defaultGlyph)) return;

        Result = item;
        Complete(true);
    }
}
