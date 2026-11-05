using System.Windows;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

/// <summary>
/// Création ou modification d'une tuile « muet » : micro (entrée) ou son de sortie, périphérique choisi
/// (ou celui par défaut de Windows), nom et icône. L'état muet est reflété sur la tuile.
/// </summary>
public partial class AddMuteTileWindow : TileEditorWindow
{
    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private readonly TileIconState _icon = new();
    private readonly bool _output;
    private readonly List<string> _ids = new(); // identifiant par ligne de la liste ; "" = défaut de Windows

    /// <param name="output">Vrai : son de sortie ; faux : micro.</param>
    public AddMuteTileWindow(Window owner, Func<EmojiPickerWindow> createEmojiPicker, bool output, ShortcutItem? existing = null)
        : base(owner)
    {
        InitializeComponent();
        _createEmojiPicker = createEmojiPicker;
        _output = output;

        var what = output ? "son de sortie" : "micro";
        TitleText.Text = existing is null ? (output ? "Ajouter un son muet" : "Ajouter un micro muet") : $"Modifier la tuile {what}";
        Title = "SysDeck — " + (output ? "Son muet" : "Micro muet");
        IntroText.Text = output
            ? "Toucher la tuile coupe ou rétablit le son du périphérique de sortie choisi. La tuile devient rouge quand il est coupé."
            : "Toucher la tuile coupe ou rétablit le micro choisi. La tuile devient rouge quand il est coupé.";
        DeviceLabel.Text = output ? "Périphérique de sortie" : "Micro";
        IconHint.Text = "Sans choix, l'icône change toute seule (" + (output ? "haut-parleur" : "micro") + " / barré).";
        if (existing is not null)
        {
            AddButton.Content = "Enregistrer";
            NameBox.Text = existing.Name;
            _icon.Load(existing);
        }

        _ids.Add("");
        DeviceBox.Items.Add(output ? "Sortie par défaut de Windows" : "Micro par défaut de Windows");
        foreach (var device in AudioMuteService.GetDevices(AudioMuteService.FlowOf(output)))
        {
            _ids.Add(device.Id);
            DeviceBox.Items.Add(device.Name);
        }
        var index = existing?.DeviceId is { Length: > 0 } id ? _ids.IndexOf(id) : 0;
        if (index < 0)
        {
            // Périphérique débranché : on le garde pour ne pas perdre le réglage.
            _ids.Add(existing!.DeviceId!);
            DeviceBox.Items.Add("Périphérique absent");
            index = _ids.Count - 1;
        }
        DeviceBox.SelectedIndex = index;
        ShowIcon();
    }

    private string DefaultGlyph => _output ? TileRuntime.SpeakerGlyph : TileRuntime.MicGlyph;

    private void ShowIcon() => IconHost.Content = ShortcutIcons.Create(_icon.Preview(DefaultGlyph), 28);

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? (_output ? "Son muet" : "Micro muet") : NameBox.Text.Trim();
        if (ChooseIconInline(_icon.CreateChooser(DialogOwner, name, _createEmojiPicker, DefaultGlyph), choice =>
            {
                _icon.ApplyChoice(choice);
                ShowIcon();
            })) return;
        if (_icon.Choose(DialogOwner, name, _createEmojiPicker, DefaultGlyph)) ShowIcon();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(false);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var item = new ShortcutItem
        {
            Kind = _output ? TileKind.OutputMute : TileKind.MicMute,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? (_output ? "Son muet" : "Micro muet") : NameBox.Text.Trim(),
            DeviceId = DeviceBox.SelectedIndex > 0 ? _ids[DeviceBox.SelectedIndex] : null
        };
        // Icône par défaut laissée vide : elle suit l'état (barré quand coupé), voir TileRuntime.ForDisplay.
        if (!_icon.ApplyTo(item, DialogOwner, defaultGlyph: null)) return;

        Result = item;
        Complete(true);
    }
}
