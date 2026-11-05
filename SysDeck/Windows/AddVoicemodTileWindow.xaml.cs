using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

/// <summary>
/// Création ou modification d'une tuile Voicemod : action, voix ou son choisi dans les listes de Voicemod (il doit être
/// connecté pour les remplir), nom, confirmation et icône.
/// </summary>
public partial class AddVoicemodTileWindow : TileEditorWindow
{
    /// <summary>Actions proposées, avec leur libellé.</summary>
    public static readonly (VoicemodAction Action, string Label)[] Actions =
    {
        (VoicemodAction.VoiceChanger, "Changeur de voix (activer / couper)"),
        (VoicemodAction.LoadVoice, "Charger une voix"),
        (VoicemodAction.RandomVoice, "Voix au hasard"),
        (VoicemodAction.PlaySound, "Jouer un son du soundboard"),
        (VoicemodAction.StopSounds, "Arrêter tous les sons"),
        (VoicemodAction.MuteMic, "Couper / rétablir le micro"),
        (VoicemodAction.HearMyself, "M'entendre (activer / couper)"),
        (VoicemodAction.Background, "Effet d'ambiance (activer / couper)"),
        (VoicemodAction.MuteSoundsForMe, "Couper les sons pour moi")
    };

    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private readonly VoicemodService _voicemod;
    private readonly TileIconState _icon = new();
    private readonly string? _existingTarget, _existingTargetName;
    private bool _loading;
    private bool _nameManual; // le nom a été saisi à la main : il n'est plus proposé automatiquement
    private int _loadVersion, _iconVersion;
    private string? _autoIcon; // image de la voix ou du son lue dans Voicemod (fichier temporaire), copiée à la validation

    public AddVoicemodTileWindow(Window owner, Func<EmojiPickerWindow> createEmojiPicker, VoicemodService voicemod, ShortcutItem? existing = null)
        : base(owner)
    {
        InitializeComponent();
        _createEmojiPicker = createEmojiPicker;
        _voicemod = voicemod;

        _loading = true;
        foreach (var (_, label) in Actions) ActionBox.Items.Add(label);
        ActionBox.SelectedIndex = existing?.VoicemodAction is { } current ? Math.Max(0, Array.FindIndex(Actions, a => a.Action == current)) : 0;
        if (existing is not null)
        {
            Title = "SysDeck — Modifier la tuile Voicemod";
            TitleText.Text = "Modifier la tuile Voicemod";
            AddButton.Content = "Enregistrer";
            _existingTarget = existing.VoicemodTarget;
            _existingTargetName = existing.VoicemodTargetName;
            NameBox.Text = existing.Name;
            _nameManual = existing.Name != Suggest(Current, existing.VoicemodTargetName);
            ConfirmCheck.IsChecked = existing.ConfirmTap;
            _icon.Load(existing);
        }
        _loading = false;

        ShowStatus();
        UpdateTarget();
        ShowIcon();
    }

    private VoicemodAction Current => Actions[Math.Max(0, ActionBox.SelectedIndex)].Action;

    private static bool HasTarget(VoicemodAction action) => action is VoicemodAction.LoadVoice or VoicemodAction.PlaySound;

    /// <summary>Voix ou son proposé : nom affiché (favori, soundboard), nom simple, catégories où il apparaît, image officielle.</summary>
    private sealed record TargetItem(string Id, string Display, string Name, IReadOnlyList<string> Categories, string? ImageUrl);

    private const string AllCategory = "Tous";
    private List<TargetItem> _items = new();
    private List<string> _categories = new();
    private string _category = AllCategory;

    private TargetItem? SelectedItem => (TargetBox.SelectedItem as ComboBoxItem)?.Tag as TargetItem;

    /// <summary>Choix courant de la liste : (identifiant, nom lisible).</summary>
    private (string Id, string Name)? Selected => SelectedItem is { } item ? (item.Id, item.Name) : null;

    /// <summary>
    /// Voix : Voicemod ne transmet pas ses catégories (Horreur, FPS…) ; on classe selon ce qu'il fournit (favorites, nouvelles, perso).
    /// Sons : une catégorie par soundboard, les soundboards créés par l'utilisateur en tête et regroupés dans « Mes sons ».
    /// </summary>
    private static (List<TargetItem> Items, List<string> Categories) BuildVoices(List<VoicemodVoice> voices)
    {
        var items = voices.Select(v =>
        {
            var categories = new List<string>();
            if (v.Favorite) categories.Add("Favorites");
            if (v.IsNew) categories.Add("Nouvelles");
            if (v.IsCustom) categories.Add("Perso");
            return new TargetItem(v.Id, v.Favorite ? $"★ {v.Name}" : v.Name, v.Name, categories, v.ImageUrl);
        }).ToList();
        var order = new[] { "Favorites", "Nouvelles", "Perso" }.Where(c => items.Any(i => i.Categories.Contains(c)));
        return (items, new[] { AllCategory }.Concat(order).ToList());
    }

    private static (List<TargetItem> Items, List<string> Categories) BuildSounds(List<VoicemodSound> sounds)
    {
        var boards = sounds.Select(s => (s.Profile, s.Custom)).Distinct()
            .OrderByDescending(b => b.Custom).Select(b => b.Profile).Where(p => p.Length > 0).ToList();
        var hasCustom = sounds.Any(s => s.Custom);
        // Un son présent dans plusieurs soundboards n'apparaît qu'une fois, avec toutes ses catégories.
        var items = sounds.GroupBy(s => s.FileName).Select(g =>
        {
            var first = g.First();
            var categories = g.Select(s => s.Profile).Where(p => p.Length > 0).Distinct().ToList();
            if (g.Any(s => s.Custom)) categories.Add("Mes sons");
            var board = g.FirstOrDefault(s => s.Custom)?.Profile ?? first.Profile;
            return new TargetItem(g.Key, string.IsNullOrEmpty(board) ? first.Name : $"{first.Name} ({board})", first.Name, categories,
                g.Select(s => s.ImageUrl).FirstOrDefault(u => u is not null));
        }).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var categories = new List<string> { AllCategory };
        if (hasCustom) categories.Add("Mes sons");
        categories.AddRange(boards);
        return (items, categories);
    }

    /// <summary>Liste déroulante de catégories ; la sélection filtre la liste des voix ou des sons.</summary>
    private void BuildCategorySelector()
    {
        _loading = true;
        CategoryBox.Items.Clear();
        CategoryBox.Visibility = _categories.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var category in _categories)
            CategoryBox.Items.Add(new ComboBoxItem { Content = category, Tag = category });
        CategoryBox.SelectedItem = CategoryBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _category));
        _loading = false;
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CategoryBox.SelectedItem is not ComboBoxItem { Tag: string category } || _category == category) return;
        _category = category;
        FillTargets(keepSelection: true);
    }

    /// <summary>Remplit la liste selon la catégorie et la recherche ; garde le choix courant s'il reste visible.</summary>
    private void FillTargets(bool keepSelection)
    {
        var keep = keepSelection ? SelectedItem?.Id ?? _existingTarget : _existingTarget;
        var search = SearchBox.Text.Trim();
        var visible = _items
            .Where(i => _category == AllCategory || i.Categories.Contains(_category))
            .Where(i => search.Length == 0 || i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        _loading = true;
        TargetBox.Items.Clear();
        foreach (var item in visible) TargetBox.Items.Add(new ComboBoxItem { Content = item.Display, Tag = item });
        // Cible enregistrée absente de Voicemod (fermé, son supprimé) : on la garde en tête de « Tous ».
        if (_existingTarget is { } target && _items.All(i => i.Id != target) && _category == AllCategory && search.Length == 0)
            TargetBox.Items.Insert(0, new ComboBoxItem
            {
                Content = _existingTargetName ?? target,
                Tag = new TargetItem(target, _existingTargetName ?? target, _existingTargetName ?? target, Array.Empty<string>(), null)
            });
        var index = keep is null ? -1 : TargetBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (i.Tag as TargetItem)?.Id == keep);
        TargetBox.SelectedIndex = index >= 0 ? index : TargetBox.Items.Count > 0 ? 0 : -1;
        _loading = false;

        TargetHint.Text = TargetBox.Items.Count > 0 ? $"{visible.Count} résultat{(visible.Count > 1 ? "s" : "")}."
            : _items.Count > 0 ? "Rien ne correspond dans cette catégorie."
            : _voicemod.IsConnected ? (Current == VoicemodAction.LoadVoice ? "Aucune voix disponible." : "Aucun son dans le soundboard.")
            : "Connecte Voicemod pour choisir dans la liste.";
        UpdateSuggestion();
        RefreshVoicemodIcon();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        FillTargets(keepSelection: true);
    }

    /// <summary>Nom proposé pour une tuile selon son action et la voix ou le son choisi.</summary>
    public static string Suggest(VoicemodAction action, string? targetName) => action switch
    {
        VoicemodAction.VoiceChanger => "Voix",
        VoicemodAction.LoadVoice => string.IsNullOrWhiteSpace(targetName) ? "Voix" : targetName,
        VoicemodAction.RandomVoice => "Voix au hasard",
        VoicemodAction.PlaySound => string.IsNullOrWhiteSpace(targetName) ? "Son" : targetName,
        VoicemodAction.StopSounds => "Stop sons",
        VoicemodAction.MuteMic => "Micro",
        VoicemodAction.HearMyself => "M'entendre",
        VoicemodAction.Background => "Ambiance",
        VoicemodAction.MuteSoundsForMe => "Sons pour moi",
        _ => "Voicemod"
    };

    /// <summary>Précision affichée dans la liste des tuiles d'une page.</summary>
    public static string Describe(ShortcutItem tile)
    {
        var detail = tile.VoicemodAction switch
        {
            VoicemodAction.LoadVoice => $"voix « {tile.VoicemodTargetName ?? tile.VoicemodTarget} »",
            VoicemodAction.PlaySound => $"son « {tile.VoicemodTargetName ?? tile.VoicemodTarget} »",
            { } action => Actions.First(a => a.Action == action).Label.ToLowerInvariant(),
            _ => "action non définie"
        };
        return "Voicemod · " + detail + (tile.ConfirmTap ? " · confirmation" : "");
    }

    private void ShowStatus()
    {
        if (_voicemod.IsConnected)
        {
            StatusText.Text = "Connecté à Voicemod.";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
            return;
        }
        StatusText.Text = string.IsNullOrEmpty(_voicemod.ClientKey)
            ? "Cette version de SysDeck n'a pas de clé Voicemod : les tuiles ne fonctionneront pas."
            : "Voicemod n'est pas connecté (Paramètres › Applications connectées, et Voicemod lancé) : les listes de voix et de sons sont vides. Tu peux créer les autres tuiles quand même.";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xA0, 0x3E));
    }

    /// <summary>Affiche la liste des voix ou des sons selon l'action, remplie depuis Voicemod.</summary>
    private async void UpdateTarget()
    {
        var action = Current;
        TargetRow.Visibility = HasTarget(action) ? Visibility.Visible : Visibility.Collapsed;
        UpdateSuggestion();
        ShowIcon();
        if (!HasTarget(action))
        {
            RefreshVoicemodIcon();
            return;
        }

        var version = ++_loadVersion;
        TargetLabel.Text = action == VoicemodAction.LoadVoice ? "Voix" : "Son";
        TargetHint.Text = _voicemod.IsConnected ? "Chargement…" : "";
        _loading = true;
        TargetBox.Items.Clear();
        SearchBox.Text = "";
        _loading = false;
        _items = new();
        _categories = new();
        CategoryBox.Items.Clear();
        CategoryBox.Visibility = Visibility.Collapsed;

        (_items, _categories) = action == VoicemodAction.LoadVoice
            ? BuildVoices(await _voicemod.GetVoicesAsync())
            : BuildSounds(await _voicemod.GetSoundsAsync());
        if (version != _loadVersion) return;

        // Tuile existante : on ouvre sur « Tous » pour retrouver son choix.
        _category = AllCategory;
        BuildCategorySelector();
        FillTargets(keepSelection: false);
    }

    /// <summary>Vrai tant que l'utilisateur n'a pas choisi d'icône lui-même : l'image de Voicemod tient alors lieu d'icône.</summary>
    private bool IconFollowsVoicemod => _icon.Glyph is null && (_icon.ImagePath is null || _icon.ImagePath == _autoIcon);

    /// <summary>Lit dans Voicemod l'image de la voix ou du son choisi et la met en icône (sauf icône choisie à la main).</summary>
    private async void RefreshVoicemodIcon()
    {
        var version = ++_iconVersion;
        if (!IconFollowsVoicemod) return;
        if (!HasTarget(Current) || Selected is not { } selected || !_voicemod.IsConnected)
        {
            if (_autoIcon is not null && _icon.ImagePath == _autoIcon) _icon.ImagePath = null;
            ShowIcon();
            return;
        }

        var bytes = await _voicemod.GetBitmapAsync(Current, selected.Id, SelectedItem?.ImageUrl);
        if (version != _iconVersion || !IconFollowsVoicemod) return;
        if (bytes is null)
        {
            if (_icon.ImagePath == _autoIcon) _icon.ImagePath = null;
            ShowIcon();
            return;
        }
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sysdeck-voicemod-{Guid.NewGuid():N}{VoicemodService.ImageExtension(bytes)}");
            await System.IO.File.WriteAllBytesAsync(path, bytes);
            if (version != _iconVersion) return;
            TryDeleteAutoIcon();
            _autoIcon = _icon.ImagePath = path;
            ShowIcon();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Infrastructure.Log.Warn("Image Voicemod impossible à enregistrer", ex);
        }
    }

    private void TryDeleteAutoIcon()
    {
        if (_autoIcon is null) return;
        try { System.IO.File.Delete(_autoIcon); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        _autoIcon = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        TryDeleteAutoIcon(); // la tuile validée en a déjà sa propre copie
        base.OnClosed(e);
    }

    private void UpdateSuggestion()
    {
        if (_nameManual) return;
        _loading = true;
        NameBox.Text = Suggest(Current, Selected?.Name);
        _loading = false;
    }

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        UpdateTarget();
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        UpdateSuggestion();
        RefreshVoicemodIcon();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        _nameManual = NameBox.Text.Trim().Length > 0;
    }

    private string DefaultGlyph => TileRuntime.VoicemodGlyph(new ShortcutItem { Kind = TileKind.Voicemod, VoicemodAction = Current });

    private void ShowIcon() => IconHost.Content = ShortcutIcons.Create(_icon.Preview(DefaultGlyph), 28);

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Voicemod" : NameBox.Text.Trim();
        if (ChooseIconInline(_icon.CreateChooser(DialogOwner, name, _createEmojiPicker, DefaultGlyph), choice =>
            {
                _icon.ApplyChoice(choice);
                ShowIcon();
            })) return;
        if (!_icon.Choose(DialogOwner, name, _createEmojiPicker, DefaultGlyph)) return;
        ShowIcon();
        RefreshVoicemodIcon(); // retour à l'icône par défaut : celle de Voicemod revient
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(false);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var action = Current;
        var selected = HasTarget(action) ? Selected : null;
        if (HasTarget(action) && selected is null)
        {
            MessageBox.Show(DialogOwner, action == VoicemodAction.LoadVoice ? "Choisis une voix (Voicemod doit être connecté)." : "Choisis un son (Voicemod doit être connecté).",
                "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = new ShortcutItem
        {
            Kind = TileKind.Voicemod,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? Suggest(action, selected?.Name) : NameBox.Text.Trim(),
            VoicemodAction = action,
            VoicemodTarget = selected?.Id,
            VoicemodTargetName = selected?.Name,
            ConfirmTap = ConfirmCheck.IsChecked == true
        };
        // Icône par défaut laissée vide : elle suit l'action, voir TileRuntime.ForDisplay.
        if (!_icon.ApplyTo(item, DialogOwner, defaultGlyph: null)) return;

        Result = item;
        Complete(true);
    }
}
