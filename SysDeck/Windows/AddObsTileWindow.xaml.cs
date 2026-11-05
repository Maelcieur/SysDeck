using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace SysDeck;

/// <summary>
/// Création ou modification d'une tuile OBS Studio : action, cible (scène, entrée audio ou source) choisie dans les listes
/// d'OBS quand il est connecté (sinon saisie à la main), nom, confirmation et icône.
/// </summary>
public partial class AddObsTileWindow : TileEditorWindow
{
    /// <summary>Actions proposées, avec leur libellé.</summary>
    public static readonly (ObsAction Action, string Label)[] Actions =
    {
        (ObsAction.Scene, "Changer de scène"),
        (ObsAction.Stream, "Démarrer / arrêter le stream"),
        (ObsAction.Record, "Démarrer / arrêter l'enregistrement"),
        (ObsAction.RecordPause, "Pause de l'enregistrement"),
        (ObsAction.VirtualCam, "Caméra virtuelle"),
        (ObsAction.ReplayBuffer, "Tampon de relecture (activer / couper)"),
        (ObsAction.SaveReplay, "Enregistrer le replay"),
        (ObsAction.InputMute, "Couper / rétablir une entrée audio"),
        (ObsAction.SourceVisibility, "Afficher / masquer une source"),
        (ObsAction.StudioTransition, "Transition du mode Studio")
    };

    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private readonly ObsService _obs;
    private readonly TileIconState _icon = new();
    private bool _loading;
    private bool _nameManual; // le nom a été saisi à la main : il n'est plus proposé automatiquement
    private string? _sourcesScene; // scène dont la liste des sources est affichée

    public AddObsTileWindow(Window owner, Func<EmojiPickerWindow> createEmojiPicker, ObsService obs, ShortcutItem? existing = null)
        : base(owner)
    {
        InitializeComponent();
        _createEmojiPicker = createEmojiPicker;
        _obs = obs;

        _loading = true;
        foreach (var (_, label) in Actions) ActionBox.Items.Add(label);
        ActionBox.SelectedIndex = existing?.ObsAction is { } current ? Math.Max(0, Array.FindIndex(Actions, a => a.Action == current)) : 0;
        if (existing is not null)
        {
            Title = "SysDeck — Modifier la tuile OBS";
            TitleText.Text = "Modifier la tuile OBS";
            AddButton.Content = "Enregistrer";
            if (existing.ObsAction == ObsAction.Scene) SceneText.Text = existing.ObsTarget ?? "";
            else if (existing.ObsAction == ObsAction.InputMute) InputText.Text = existing.ObsTarget ?? "";
            else if (existing.ObsAction == ObsAction.SourceVisibility)
            {
                SceneText.Text = existing.ObsScene ?? "";
                SourceText.Text = existing.ObsTarget ?? "";
            }
            NameBox.Text = existing.Name;
            _nameManual = existing.Name != Suggest(Current, Target());
            ConfirmCheck.IsChecked = existing.ConfirmTap;
            _icon.Load(existing);
        }
        _loading = false;

        UpdateRows();
        ShowStatus();
        UpdateSuggestion();
        ShowIcon();
    }

    protected override void OnAttached() => _ = LoadFromObsAsync();

    private ObsAction Current => Actions[Math.Max(0, ActionBox.SelectedIndex)].Action;

    /// <summary>Cible saisie pour l'action courante (scène, entrée ou source) ; vide si l'action n'en a pas.</summary>
    private string Target() => Current switch
    {
        ObsAction.Scene => SceneText.Text.Trim(),
        ObsAction.InputMute => InputText.Text.Trim(),
        ObsAction.SourceVisibility => SourceText.Text.Trim(),
        _ => ""
    };

    /// <summary>Nom proposé pour une tuile selon son action et sa cible.</summary>
    public static string Suggest(ObsAction action, string target) => action switch
    {
        ObsAction.Scene => target.Length > 0 ? target : "Scène",
        ObsAction.Stream => "Stream",
        ObsAction.Record => "Enregistrer",
        ObsAction.RecordPause => "Pause",
        ObsAction.VirtualCam => "Caméra virtuelle",
        ObsAction.ReplayBuffer => "Replay",
        ObsAction.SaveReplay => "Sauver le replay",
        ObsAction.InputMute => target.Length > 0 ? target : "Muet",
        ObsAction.SourceVisibility => target.Length > 0 ? target : "Source",
        _ => "Transition"
    };

    /// <summary>Précision affichée dans la liste des tuiles d'une page.</summary>
    public static string Describe(ShortcutItem tile)
    {
        var detail = tile.ObsAction switch
        {
            ObsAction.Scene => $"scène « {tile.ObsTarget} »",
            ObsAction.InputMute => $"muet « {tile.ObsTarget} »",
            ObsAction.SourceVisibility => $"« {tile.ObsTarget} » dans « {tile.ObsScene} »",
            { } action => Actions.First(a => a.Action == action).Label.ToLowerInvariant(),
            _ => "action non définie"
        };
        return "OBS · " + detail + (tile.ConfirmTap ? " · confirmation" : "");
    }

    private void UpdateRows()
    {
        var action = Current;
        SceneRow.Visibility = action is ObsAction.Scene or ObsAction.SourceVisibility ? Visibility.Visible : Visibility.Collapsed;
        InputRow.Visibility = action == ObsAction.InputMute ? Visibility.Visible : Visibility.Collapsed;
        SourceRow.Visibility = action == ObsAction.SourceVisibility ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus()
    {
        if (_obs.IsConnected)
        {
            StatusText.Text = "Connecté à OBS : les listes ci-dessous viennent d'OBS.";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
            return;
        }
        StatusText.Text = _obs.IsEnabled
            ? "OBS n'est pas connecté : saisis les noms à la main (ils doivent correspondre exactement à ceux d'OBS)."
            : "L'intégration OBS est désactivée (Paramètres › Applications connectées) : saisis les noms à la main, ils doivent correspondre exactement à ceux d'OBS.";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xA0, 0x3E));
    }

    private void UpdateSuggestion()
    {
        if (_nameManual) return;
        _loading = true;
        NameBox.Text = Suggest(Current, Target());
        _loading = false;
    }

    // ---------- Listes d'OBS ----------

    private async Task LoadFromObsAsync()
    {
        if (!_obs.IsConnected) return;
        var scenes = await _obs.GetScenesAsync();
        var inputs = await _obs.GetAudioInputsAsync();
        Fill(SceneBox, scenes, SceneText.Text.Trim());
        Fill(InputBox, inputs.Select(i => i.Name).ToList(), InputText.Text.Trim());
        await LoadSourcesAsync();
    }

    private async Task LoadSourcesAsync()
    {
        var scene = SceneText.Text.Trim();
        _sourcesScene = scene;
        if (!_obs.IsConnected || scene.Length == 0)
        {
            SourceBox.Visibility = Visibility.Collapsed;
            return;
        }
        var items = await _obs.GetSceneItemsAsync(scene);
        Fill(SourceBox, items.Select(i => i.SourceName).Distinct().ToList(), SourceText.Text.Trim());
    }

    /// <summary>Remplit une liste déroulante (masquée si elle est vide : il reste la saisie à la main).</summary>
    private void Fill(ComboBox box, List<string> names, string current)
    {
        var wasLoading = _loading;
        _loading = true;
        box.Items.Clear();
        foreach (var name in names) box.Items.Add(name);
        box.SelectedIndex = names.IndexOf(current);
        box.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _loading = wasLoading;
    }

    // ---------- Évènements ----------

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        UpdateRows();
        UpdateSuggestion();
        ShowIcon();
        if (Current == ObsAction.SourceVisibility && _sourcesScene != SceneText.Text.Trim()) _ = LoadSourcesAsync();
    }

    private void SceneBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SceneBox.SelectedItem is not string scene) return;
        SceneText.Text = scene;
        if (Current == ObsAction.SourceVisibility)
        {
            SourceText.Text = "";
            _ = LoadSourcesAsync();
        }
    }

    private void InputBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && InputBox.SelectedItem is string input) InputText.Text = input;
    }

    private void SourceBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SourceBox.SelectedItem is string source) SourceText.Text = source;
    }

    private void SceneText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Current == ObsAction.SourceVisibility && _sourcesScene != SceneText.Text.Trim()) _ = LoadSourcesAsync();
    }

    private void Target_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        UpdateSuggestion();
        ShowIcon();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        _nameManual = NameBox.Text.Trim().Length > 0;
    }

    // ---------- Icône et validation ----------

    private ShortcutItem PreviewTile() => new()
    {
        Kind = TileKind.Obs,
        ObsAction = Current,
        ObsTarget = Target(),
        ObsScene = Current == ObsAction.SourceVisibility ? SceneText.Text.Trim() : null
    };

    private string DefaultGlyph => TileRuntime.ObsGlyph(PreviewTile());

    private void ShowIcon() => IconHost.Content = ShortcutIcons.Create(_icon.Preview(DefaultGlyph), 28);

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "OBS" : NameBox.Text.Trim();
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
        var action = Current;
        var target = Target();
        var scene = SceneText.Text.Trim();
        var missing = action switch
        {
            ObsAction.Scene when target.Length == 0 => "Choisis ou saisis le nom de la scène.",
            ObsAction.InputMute when target.Length == 0 => "Choisis ou saisis le nom de l'entrée audio.",
            ObsAction.SourceVisibility when scene.Length == 0 => "Choisis ou saisis le nom de la scène qui contient la source.",
            ObsAction.SourceVisibility when target.Length == 0 => "Choisis ou saisis le nom de la source.",
            _ => null
        };
        if (missing is not null)
        {
            MessageBox.Show(DialogOwner, missing, "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = new ShortcutItem
        {
            Kind = TileKind.Obs,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? Suggest(action, target) : NameBox.Text.Trim(),
            ObsAction = action,
            ObsTarget = target.Length > 0 ? target : null,
            ObsScene = action == ObsAction.SourceVisibility ? scene : null,
            ConfirmTap = ConfirmCheck.IsChecked == true
        };
        // Icône par défaut laissée vide : elle suit l'action et l'état, voir TileRuntime.ForDisplay.
        if (!_icon.ApplyTo(item, DialogOwner, defaultGlyph: null)) return;

        Result = item;
        Complete(true);
    }
}
