using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Infrastructure;
using SysDeck.Interop;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

/// <summary>Création d'un raccourci clavier pour la page Switch : nom, combinaison et icône.</summary>
public partial class AddHotkeyShortcutWindow : TileEditorWindow
{
    private const string KeyboardGlyph = "\uE765";

    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private HotkeyGesture? _gesture; // combinaison validée par « Terminer » ; null tant qu'elle ne l'est pas
    private Key _key = Key.None;      // touche principale en cours de composition
    private string? _glyph;
    private string? _imagePath;
    private string? _iconColor, _tileColor; // couleurs de la tuile ; null = thème

    private string? _existingImage; // icône déjà copiée par SysDeck (modification) : réutilisée telle quelle
    private double? _iconScale; // taille d'icône et nom sur la Switch : gardés tels quels (réglés par le bouton Icône de la liste)
    private bool _hideName;
    private string? _imageTint;
    private ShortcutItem? _existingItem; // apparence active gardée telle quelle (réglée par le bouton Icône de la liste)

    /// <summary>Tuile créée (Path vide, Hotkey rempli) ; null si annulé. En modification, nouvelle version de la tuile.</summary>
    /// <param name="existing">Tuile à modifier (champs pré-remplis) ; null pour en créer une.</param>
    public AddHotkeyShortcutWindow(Window owner, Func<EmojiPickerWindow> createEmojiPicker, ShortcutItem? existing = null)
        : base(owner)
    {
        InitializeComponent();
        _createEmojiPicker = createEmojiPicker;
        if (existing is not null) LoadExisting(existing);
        ShowCombo();
        ShowIcon();
    }

    /// <summary>Pré-remplit la fenêtre avec une tuile existante, combinaison déjà validée.</summary>
    private void LoadExisting(ShortcutItem item)
    {
        Title = "SysDeck — Modifier le raccourci clavier";
        TitleText.Text = "Modifier le raccourci clavier";
        AddButton.Content = "Enregistrer";
        NameBox.Text = item.Name;
        _glyph = item.IconGlyph;
        _imagePath = _existingImage = item.CustomIconPath;
        _iconScale = item.IconScale;
        _hideName = item.HideName;
        _imageTint = item.ImageTint;
        _existingItem = item;
        _iconColor = item.IconColor;
        _tileColor = item.TileColor;

        if (HotkeyGesture.Parse(item.Hotkey, requireModifier: false) is not { } gesture) return;
        CtrlToggle.IsChecked = gesture.Modifiers.HasFlag(ModifierKeys.Control);
        AltToggle.IsChecked = gesture.Modifiers.HasFlag(ModifierKeys.Alt);
        ShiftToggle.IsChecked = gesture.Modifiers.HasFlag(ModifierKeys.Shift);
        WinToggle.IsChecked = gesture.Modifiers.HasFlag(ModifierKeys.Windows);
        _key = gesture.Key;
        _gesture = gesture;
        AddButton.IsEnabled = true;
        ComboHint.Text = "Combinaison actuelle. Modifie-la si besoin, puis « Terminer ».";
    }

    // ---------- Composition touche par touche ----------
    // Taper Win+G ou Alt+Tab d'un coup est intercepté par Windows avant d'arriver ici : chaque touche est donc
    // ajoutée l'une après l'autre (un modificateur reste acquis une fois relâché), et « Terminer » valide.

    private ModifierKeys Modifiers =>
        (CtrlToggle.IsChecked == true ? ModifierKeys.Control : 0)
        | (AltToggle.IsChecked == true ? ModifierKeys.Alt : 0)
        | (ShiftToggle.IsChecked == true ? ModifierKeys.Shift : 0)
        | (WinToggle.IsChecked == true ? ModifierKeys.Windows : 0);

    private void KeyCapture_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => KeyCapture.Text = "Appuie sur les touches une par une…";

    private void KeyCapture_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => KeyCapture.Text = "Clique ici pour saisir les touches";

    private void KeyCapture_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Toutes les touches sont saisies, Tab et Échap compris (Ctrl+Maj+Échap…) : on quitte la zone à la souris.
        e.Handled = true;
        if (e.IsRepeat) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LWin or Key.RWin)
            InputSimulator.SendMaskKey(); // sinon le menu Démarrer s'ouvrirait au relâchement de Win
        AddKey(key);
    }

    // Impr. écran ne produit qu'un évènement de relâchement : on le traite comme une pression.
    private void KeyCapture_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Snapshot) AddKey(Key.Snapshot);
    }

    private void AddKey(Key key)
    {
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl: CtrlToggle.IsChecked = true; break;
            case Key.LeftAlt or Key.RightAlt: AltToggle.IsChecked = true; break;
            case Key.LeftShift or Key.RightShift: ShiftToggle.IsChecked = true; break;
            case Key.LWin or Key.RWin: WinToggle.IsChecked = true; break;
            // Touche masque renvoyée par SendMaskKey, et évènements sans touche réelle (méthode de saisie, accent mort).
            case Key.None or Key.ImeProcessed or Key.DeadCharProcessed: return;
            default:
                if (KeyInterop.VirtualKeyFromKey(key) is 0 or InputSimulator.VK_MASK) return;
                _key = key; // une autre touche remplace la précédente : une combinaison n'a qu'une touche principale
                break;
        }
        OnComboChanged();
    }

    private void Modifier_Click(object sender, RoutedEventArgs e) => OnComboChanged();

    private void ClearKeys_Click(object sender, RoutedEventArgs e)
    {
        CtrlToggle.IsChecked = AltToggle.IsChecked = ShiftToggle.IsChecked = WinToggle.IsChecked = false;
        _key = Key.None;
        OnComboChanged();
    }

    /// <summary>Toute modification annule la validation : « Ajouter » attend un nouveau « Terminer ».</summary>
    private void OnComboChanged()
    {
        _gesture = null;
        AddButton.IsEnabled = false;
        ComboHint.ClearValue(TextBlock.ForegroundProperty);
        ComboHint.Text = "Clique sur « Terminer » quand la combinaison est complète.";
        ShowCombo();
    }

    private void ShowCombo()
    {
        ValidatedMark.Visibility = _gesture is null ? Visibility.Collapsed : Visibility.Visible;
        if (_key != Key.None)
            ComboText.Text = new HotkeyGesture(Modifiers, _key).ToString();
        else if (Modifiers != ModifierKeys.None)
            ComboText.Text = new HotkeyGesture(Modifiers, Key.A).ToString()[..^1] + "…"; // « Win+… » : touche attendue
        else
            ComboText.Text = "Aucune touche";
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_key == Key.None)
        {
            ComboHint.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x70, 0x6B));
            ComboHint.Text = "Ajoute une touche principale (lettre, chiffre, F1…) en plus des touches de modification.";
            return;
        }

        _gesture = new HotkeyGesture(Modifiers, _key);
        AddButton.IsEnabled = true;
        ComboHint.ClearValue(TextBlock.ForegroundProperty);
        ComboHint.Text = "Combinaison validée. Tu peux encore la modifier, puis « Terminer » à nouveau.";
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = _gesture.ToString();
        ShowCombo();
        Keyboard.ClearFocus();
    }

    /// <summary>Aperçu de l'icône choisie ; par défaut, un symbole de clavier.</summary>
    private void ShowIcon()
        => IconHost.Content = ShortcutIcons.Create(
            new ShortcutItem { IconGlyph = _glyph ?? (_imagePath is null ? KeyboardGlyph : null), CustomIconPath = _imagePath, IconColor = _iconColor }, 28);

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        var current = new ShortcutItem
        {
            IconGlyph = _glyph ?? (_imagePath is null ? KeyboardGlyph : null),
            CustomIconPath = _imagePath,
            IconColor = _iconColor,
            TileColor = _tileColor,
            ImageTint = _imageTint
        };
        var chooser = new IconChooserWindow(DialogOwner, string.IsNullOrWhiteSpace(NameBox.Text) ? "Raccourci clavier" : NameBox.Text.Trim(), _createEmojiPicker, current);
        if (ChooseIconInline(chooser, ApplyIconChoice)) return;
        if (chooser.ShowDialog() != true || chooser.Result is not { } choice) return;

        ApplyIconChoice(choice);
    }

    private void ApplyIconChoice(IconChoice choice)
    {
        _iconColor = choice.IconColor;
        _imageTint = choice.ImageTint;
        _tileColor = choice.TileColor;
        switch (choice.Kind)
        {
            case IconChoiceKind.Keep: break;
            case IconChoiceKind.Image: _imagePath = choice.Value; _glyph = null; break;
            case IconChoiceKind.Glyph: _glyph = choice.Value; _imagePath = null; break;
            default: _glyph = _imagePath = null; break;
        }
        ShowIcon();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(false);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_gesture is null) return;

        string? imageCopy = _imagePath == _existingImage ? _existingImage : null;
        if (_imagePath is not null && imageCopy is null)
        {
            try
            {
                imageCopy = CustomIconStore.SaveCopy(_imagePath);
            }
            catch (Exception ex)
            {
                Log.Warn("Copie de l'icône impossible", ex);
                MessageBox.Show(DialogOwner, $"Impossible d'utiliser cette image :\n{ex.Message}", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Result = new ShortcutItem
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _gesture.ToString() : NameBox.Text.Trim(),
            Hotkey = _gesture.Serialize(),
            IconGlyph = _glyph ?? (imageCopy is null ? KeyboardGlyph : null), // sans choix : symbole de clavier
            IconColor = _iconColor,
            TileColor = _tileColor,
            CustomIconPath = imageCopy,
            IconScale = _iconScale,
            HideName = _hideName,
            ImageTint = imageCopy is null ? null : _imageTint
        };
        if (_existingItem is not null) Result.CopyActiveFrom(_existingItem);
        Complete(true);
    }
}
