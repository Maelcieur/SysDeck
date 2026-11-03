using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Models;
using SysDeck.Plugins;
using SysDeck.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace SysDeck;

/// <summary>
/// Création ou modification d'une tuile de plugin : plugin, action, réglages (formulaire généré d'après le manifest),
/// nom, confirmation et icône.
/// </summary>
public partial class AddPluginTileWindow : Window
{
    private readonly Func<EmojiPickerWindow> _createEmojiPicker;
    private readonly PluginHost _host;
    private readonly ShortcutItem? _existing;
    private readonly TileIconState _icon = new();
    private readonly List<PluginInfo> _plugins;
    private readonly List<(PluginSetting Setting, Func<JsonNode?> Read)> _fields = new();
    private bool _loading;
    private bool _nameManual;
    private readonly string? _preselectedAction;

    public ShortcutItem? Result { get; private set; }

    /// <param name="pluginId">Nouvelle tuile : plugin choisi d'avance (bibliothèque d'actions).</param>
    /// <param name="actionId">Nouvelle tuile : action choisie d'avance.</param>
    public AddPluginTileWindow(Window owner, Func<EmojiPickerWindow> createEmojiPicker, PluginHost host, ShortcutItem? existing = null,
        string? pluginId = null, string? actionId = null)
    {
        _preselectedAction = actionId;
        InitializeComponent();
        Owner = owner;
        _createEmojiPicker = createEmojiPicker;
        _host = host;
        _existing = existing;
        _plugins = host.Plugins.Where(p => p.Manifest is not null).ToList();

        _loading = true;
        foreach (var plugin in _plugins) PluginBox.Items.Add(plugin.Name);
        if (existing is not null)
        {
            Title = "SysDeck — Modifier la tuile de plugin";
            TitleText.Text = "Modifier la tuile de plugin";
            AddButton.Content = "Enregistrer";
            NameBox.Text = existing.Name;
            _nameManual = true;
            ConfirmCheck.IsChecked = existing.ConfirmTap;
            _icon.Load(existing);
        }
        PluginBox.SelectedIndex = Math.Max(0, _plugins.FindIndex(p => string.Equals(p.Id, existing?.PluginId ?? pluginId, StringComparison.OrdinalIgnoreCase)));
        _loading = false;

        FillActions();
        ShowStatus();
        ShowIcon();
    }

    /// <summary>Description courte d'une tuile de plugin (liste des tuiles des Paramètres).</summary>
    public static string Describe(ShortcutItem tile, PluginHost? host)
    {
        var manifest = host?.Find(tile.PluginId)?.Manifest;
        var action = manifest?.FindAction(tile.PluginAction);
        return manifest is null ? $"plugin absent ({tile.PluginId})" : $"{manifest.Name} · {action?.Name ?? tile.PluginAction}";
    }

    private PluginInfo? CurrentPlugin => PluginBox.SelectedIndex >= 0 && PluginBox.SelectedIndex < _plugins.Count ? _plugins[PluginBox.SelectedIndex] : null;

    private PluginAction? CurrentAction
        => CurrentPlugin?.Manifest is { } m && ActionBox.SelectedIndex >= 0 && ActionBox.SelectedIndex < m.Actions.Count ? m.Actions[ActionBox.SelectedIndex] : null;

    private void ShowStatus()
    {
        if (_plugins.Count == 0)
        {
            StatusText.Text = "Aucun plugin n'est installé : installe-en un dans Paramètres › Plugins.";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xA0, 0x3E));
            AddButton.IsEnabled = false;
            return;
        }
        var plugin = CurrentPlugin!;
        var running = _host.IsRunning(plugin.Id);
        StatusText.Text = running
            ? $"{plugin.Name} {plugin.Manifest!.Version}{(plugin.Manifest.Author is { } a ? $" · {a}" : "")} : en marche."
            : $"{plugin.Name} ne tourne pas ({StatusLabel(plugin.Status)}) : la tuile restera atténuée tant qu'il n'est pas lancé.";
        StatusText.Foreground = new SolidColorBrush(running ? Color.FromRgb(0x5F, 0xD0, 0x8A) : Color.FromRgb(0xE5, 0xA0, 0x3E));
        AddButton.IsEnabled = true;
    }

    internal static string StatusLabel(PluginStatus status) => status switch
    {
        PluginStatus.Running => "en marche",
        PluginStatus.Starting => "démarrage",
        PluginStatus.Crashed => "en erreur",
        PluginStatus.Disabled => "désactivé",
        PluginStatus.Invalid => "invalide",
        _ => "arrêté"
    };

    private void Plugin_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        FillActions();
        ShowStatus();
    }

    private void FillActions()
    {
        _loading = true;
        ActionBox.Items.Clear();
        var actions = CurrentPlugin?.Manifest?.Actions ?? Array.Empty<PluginAction>();
        foreach (var action in actions) ActionBox.Items.Add(action.Name);
        var index = _existing is not null && string.Equals(_existing.PluginId, CurrentPlugin?.Id, StringComparison.OrdinalIgnoreCase)
            ? actions.ToList().FindIndex(a => string.Equals(a.Id, _existing.PluginAction, StringComparison.OrdinalIgnoreCase))
            : actions.ToList().FindIndex(a => string.Equals(a.Id, _preselectedAction, StringComparison.OrdinalIgnoreCase));
        ActionBox.SelectedIndex = actions.Count == 0 ? -1 : Math.Max(0, index);
        _loading = false;
        BuildSettings();
    }

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        BuildSettings();
    }

    /// <summary>Formulaire des réglages de l'action : un champ par réglage déclaré, prérempli (tuile modifiée, sinon valeur par défaut).</summary>
    private void BuildSettings()
    {
        SettingsPanel.Children.Clear();
        _fields.Clear();
        var action = CurrentAction;
        ActionHint.Text = action?.Tooltip ?? "";
        ActionHint.Visibility = string.IsNullOrEmpty(ActionHint.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (!_nameManual) NameBox.Text = action?.Name ?? "";
        ShowIcon();
        if (action is null) return;

        var sameAction = _existing is not null && string.Equals(_existing.PluginAction, action.Id, StringComparison.OrdinalIgnoreCase);
        var values = sameAction ? _existing!.PluginSettings ?? new JsonObject() : PluginManifest.DefaultSettings(action);
        foreach (var setting in action.Settings)
        {
            SettingsPanel.Children.Add(new TextBlock { Text = setting.Label, Style = (Style)FindResource("FieldLabel") });
            var (control, read) = BuildField(setting, values[setting.Key]);
            control.Margin = new Thickness(0, 0, 0, setting.Description is null ? 14 : 4);
            SettingsPanel.Children.Add(control);
            if (setting.Description is not null)
                SettingsPanel.Children.Add(new TextBlock
                {
                    Text = setting.Description,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("Muted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 14)
                });
            _fields.Add((setting, read));
        }
    }

    private (FrameworkElement Control, Func<JsonNode?> Read) BuildField(PluginSetting setting, JsonNode? value)
    {
        var text = value is JsonValue v ? v.ToString() : null;
        switch (setting.Type)
        {
            case PluginSettingType.Checkbox:
            {
                var check = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = value is JsonValue b && b.TryGetValue(out bool on) && on };
                return (check, () => JsonValue.Create(check.IsChecked == true));
            }
            case PluginSettingType.Select:
            {
                var combo = new ComboBox();
                foreach (var option in setting.Options) combo.Items.Add(option.Label);
                combo.SelectedIndex = Math.Max(0, setting.Options.ToList().FindIndex(o => o.Value == text));
                return (combo, () => combo.SelectedIndex >= 0 ? JsonValue.Create(setting.Options[combo.SelectedIndex].Value) : null);
            }
            case PluginSettingType.Number:
            {
                var box = new TextBox { Text = text ?? "" };
                return (box, () => double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    ? n == Math.Floor(n) && Math.Abs(n) < 1e15 ? JsonValue.Create((long)n) : JsonValue.Create(n)
                    : null);
            }
            case PluginSettingType.Color:
            {
                var swatch = new ColorSwatch { Hex = string.IsNullOrWhiteSpace(text) ? "#FFFFFFFF" : text, Content = "Choisir…", HorizontalAlignment = HorizontalAlignment.Left };
                return (swatch, () => JsonValue.Create(swatch.Hex));
            }
            case PluginSettingType.File:
            {
                var box = new TextBox { Text = text ?? "" };
                var browse = new Button { Content = "Parcourir…", Margin = new Thickness(8, 0, 0, 0) };
                browse.Click += (_, _) =>
                {
                    var dialog = new OpenFileDialog { Title = setting.Label, Filter = "Tous les fichiers (*.*)|*.*" };
                    if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
                };
                DockPanel.SetDock(browse, Dock.Right);
                return (new DockPanel { Children = { browse, box } }, () => JsonValue.Create(box.Text.Trim()));
            }
            default:
            {
                var box = new TextBox { Text = text ?? "", ToolTip = setting.Placeholder };
                if (setting.Type == PluginSettingType.TextArea)
                {
                    box.AcceptsReturn = true;
                    box.TextWrapping = TextWrapping.Wrap;
                    box.Height = 72;
                    box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }
                return (box, () => JsonValue.Create(box.Text));
            }
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        _nameManual = NameBox.Text.Trim().Length > 0;
    }

    private void ShowIcon()
    {
        var preview = _icon.Preview(TileRuntime.PluginGlyph);
        if (_icon.Glyph is null && _icon.ImagePath is null && CurrentAction?.IconPath is { } actionIcon)
        {
            preview.IconGlyph = null;
            preview.CustomIconPath = actionIcon;
        }
        IconHost.Content = ShortcutIcons.Create(preview, 28);
    }

    private void Icon_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Plugin" : NameBox.Text.Trim();
        if (!_icon.Choose(this, name, _createEmojiPicker, TileRuntime.PluginGlyph)) return;
        ShowIcon();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPlugin is not { } plugin || CurrentAction is not { } action)
        {
            MessageBox.Show(this, "Choisis un plugin et une action.", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Réglages déjà enregistrés par le plugin (setSettings) gardés ; ceux du formulaire remplacés.
        var sameAction = _existing is not null && string.Equals(_existing.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(_existing.PluginAction, action.Id, StringComparison.OrdinalIgnoreCase);
        var settings = sameAction ? (JsonObject)(_existing!.PluginSettings?.DeepClone() ?? new JsonObject()) : new JsonObject();
        foreach (var (setting, read) in _fields)
        {
            var value = read();
            if (value is null) settings.Remove(setting.Key);
            else settings[setting.Key] = value;
        }

        var item = new ShortcutItem
        {
            Kind = TileKind.Plugin,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? action.Name : NameBox.Text.Trim(),
            PluginId = plugin.Id,
            PluginAction = action.Id,
            PluginSettings = settings,
            // Même tuile pour le plugin tant que l'action ne change pas : il garde ce qu'il sait d'elle.
            PluginContext = sameAction ? _existing!.PluginContext : null,
            ConfirmTap = ConfirmCheck.IsChecked == true
        };
        // Icône par défaut laissée vide : celle de l'action du manifest, voir TileRuntime.ForDisplay.
        if (!_icon.ApplyTo(item, this, defaultGlyph: null)) return;

        Result = item;
        DialogResult = true;
    }
}
