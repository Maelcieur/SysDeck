using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Deck;
using SysDeck.Models;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck;

// Menu de gauche masquable, et page Switch façon Stream Deck : bibliothèque d'actions et réglages à droite,
// inspecteur de la tuile sélectionnée sous l'aperçu.
public partial class SettingsWindow
{
    // ---------- Menu de gauche ----------

    private bool _navCollapsed;

    private void NavToggle_Click(object sender, RoutedEventArgs e) => SetNavCollapsed(!_navCollapsed, save: true);

    /// <summary>Masque ou réaffiche le menu de gauche (le choix est retenu d'une ouverture à l'autre).</summary>
    private void SetNavCollapsed(bool collapsed, bool save)
    {
        _navCollapsed = collapsed;
        NavColumn.Width = new GridLength(collapsed ? 0 : 240);
        Sidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedTitle.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        NavToggleButton.ToolTip = collapsed ? "Afficher le menu (Ctrl+B)" : "Masquer le menu (Ctrl+B)";
        if (!save || _widgetManager.Config.SettingsNavCollapsed == collapsed) return;
        _widgetManager.Config.SettingsNavCollapsed = collapsed;
        ScheduleSave();
    }

    // ---------- Panneau de droite de la page Switch ----------

    private void SwitchPanelTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || SwitchActionsPanel is null || SwitchSettingsPanel is null) return;
        var actions = SwitchTabActions.IsChecked == true;
        SwitchActionsPanel.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        SwitchSettingsPanel.Visibility = actions ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InitSwitchLayout()
    {
        DeckEditor.TileSelected += InspectTile;
        DeckEditor.TileActivated += EditDeckTile;
        DeckEditor.ActionDropped += (data, cell) =>
        {
            if (data is DeckAction action) RunDeckAction(action, cell);
        };
        var host = _widgetManager.PluginHost;
        void OnPluginsChanged()
        {
            RefreshDeckActions();
            // Plugin démarré ou arrêté : la page de réglages de la tuile inspectée suit.
            if (_inspected is { Kind: TileKind.Plugin } tile && (_pluginLaunch is null) == host.IsRunning(tile.PluginId)) ShowPluginPage(tile);
        }
        host.StateChanged += OnPluginsChanged;
        Closed += (_, _) =>
        {
            host.StateChanged -= OnPluginsChanged;
            ClosePluginPage();
            _pluginPage?.Dispose();
        };
        RefreshDeckActions();
        InspectTile(null);
        InitJoyConView();
    }

    // ---------- Inspecteur de la tuile sélectionnée ----------

    private ShortcutItem? _inspected;
    private bool _inspectorLoading;

    /// <summary>Affiche les réglages d'une tuile de la page (null : invite à en choisir une).</summary>
    private void InspectTile(ShortcutItem? item)
    {
        if (item is not null && !Deck.Settings.Page.Apps.Contains(item)) item = null; // retirée, ou sur une autre page
        _inspected = item;
        if (!ReferenceEquals(DeckEditor.SelectedTile, item)) DeckEditor.SelectedTile = item;
        DeckInspector.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        DeckInspectorEmpty.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        ShowPluginPage(item); // plugin Stream Deck : sa page de réglages sous la tuile
        if (item is null) return;

        _inspectorLoading = true;
        if (DeckInspectorName.Text != item.Name) DeckInspectorName.Text = item.Name;
        DeckInspectorConfirm.IsChecked = item.ConfirmTap;
        _inspectorLoading = false;
        DeckInspectorKind.Text = KindLabel(item).ToUpperInvariant();
        var detail = item.Kind == TileKind.App ? string.Join(" ", new[] { item.Path, item.Arguments }.Where(s => !string.IsNullOrWhiteSpace(s))) : TileDetail(item);
        DeckInspectorDetail.Text = detail;
        DeckInspectorDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { DeckInspectorEdit, DeckInspectorIconButton, DeckInspectorLeft, DeckInspectorRight, DeckInspectorRemove })
            button.Tag = item;

        DeckInspectorIcon.Content = ShortcutIcons.Create(TileRuntime.ForDisplay(item), 40);
        DeckInspectorTile.Background = string.IsNullOrWhiteSpace(item.TileColor) || DeckShortcutsPanel.IsTransparent(item.TileColor)
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x30))
            : new SolidColorBrush(ThemeService.Parse(item.TileColor, Colors.Black));
    }

    private static string KindLabel(ShortcutItem item) => item.Kind switch
    {
        TileKind.Hotkey => "Raccourci clavier",
        TileKind.Command => "Commande",
        TileKind.MicMute => "Micro muet",
        TileKind.OutputMute => "Son muet",
        TileKind.Obs => "OBS Studio",
        TileKind.Twitch => "Twitch",
        TileKind.Voicemod => "Voicemod",
        TileKind.Plugin => "Plugin",
        _ => Uri.TryCreate(item.Path, UriKind.Absolute, out var uri) && !uri.IsFile ? "Lien" : "Application ou fichier"
    };

    private void DeckInspectorName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_inspectorLoading || _inspected is null) return;
        _inspected.Name = DeckInspectorName.Text;
        Deck.RefreshShortcuts();
        ScheduleSave();
    }

    private void DeckInspectorConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_inspected is null) return;
        _inspected.ConfirmTap = DeckInspectorConfirm.IsChecked == true;
        ScheduleSave();
    }

    /// <summary>Tuiles hors de la grille (non affichées sur la Switch) : listées sous l'aperçu pour pouvoir les régler ou les retirer.</summary>
    private void RefreshHiddenApps()
    {
        var hidden = Deck.HiddenApps();
        DeckHiddenAppsPanel.Visibility = hidden.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DeckHiddenAppsList.Children.Clear();
        if (hidden.Count == 0) return;
        DeckHiddenAppsText.Text = $"{hidden.Count} tuile(s) hors de la grille, non affichée(s) sur la Switch : agrandis le bloc « Tuiles de la page » " +
                                  "dans l'aperçu (coin en bas à droite) ou réduis la taille des tuiles.";
        foreach (var app in hidden)
        {
            var button = new Button
            {
                Margin = new Thickness(3),
                Padding = new Thickness(8, 2, 10, 2),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        ShortcutIcons.Create(TileRuntime.ForDisplay(app), 16) ?? new Border { Width = 16 },
                        new TextBlock { Text = app.Name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            };
            button.Click += (_, _) => InspectTile(app);
            DeckHiddenAppsList.Children.Add(button);
        }
    }

    // ---------- Bibliothèque d'actions ----------

    /// <summary>Action de la bibliothèque : catégorie, nom, icône (symbole, « brand:… » ou image), aide, et création des tuiles.</summary>
    /// <param name="Command">Commande de manette (vue Manettes) : rien à créer, la touche reçoit cette commande.</param>
    /// <param name="Plugin">Action d'un plugin (pas proposée aux touches des Joy-Con).</param>
    private sealed record DeckAction(string Category, string CategoryIcon, string Name, string Icon, string Description,
        Func<IReadOnlyList<ShortcutItem>?> Create, string? IconColor = null, JoyConCommand? Command = null, bool Plugin = false);

    private readonly HashSet<string> _collapsedActionCategories = new();
    private Point _actionDragStart;
    private bool _actionDragged;

    private static IReadOnlyList<ShortcutItem>? One(ShortcutItem? item) => item is null ? null : new[] { item };

    private IEnumerable<DeckAction> DeckActions()
    {
        const string apps = "Applications", system = "Système", keyboard = "Clavier", audio = "Audio";
        const string appsIcon = "", systemIcon = "", keyboardIcon = "", audioIcon = "";

        yield return new(apps, appsIcon, "Application installée", "", "Un ou plusieurs .exe ou raccourcis (.lnk).", PickInstalledApps);
        yield return new(apps, appsIcon, "Lien ou fichier", "", "Programme, fichier, dossier, adresse web ou protocole.",
            () => One(EditTile(new AddCustomShortcutWindow(this, CreateEmojiPickerForChoice), w => w.Result)));

        yield return new(system, systemIcon, "Commande", TileRuntime.CommandGlyph, "Ligne de commande exécutée sans fenêtre.",
            () => One(EditTile(new AddCommandTileWindow(this, CreateEmojiPickerForChoice), w => w.Result)));
        foreach (var preset in CommandRunner.Presets)
            yield return new(system, systemIcon, preset.Name, preset.Glyph, preset.Description, () => One(new ShortcutItem
            {
                Kind = TileKind.Command,
                Name = preset.Name,
                Command = preset.Command,
                ConfirmTap = preset.Confirm,
                IconGlyph = preset.Glyph
            }));

        yield return new(keyboard, keyboardIcon, "Raccourci clavier", "", "Envoie une combinaison de touches au PC.",
            () => One(EditTile(new AddHotkeyShortcutWindow(this, CreateEmojiPickerForChoice), w => w.Result)));

        // Micro et son : le périphérique par défaut tout de suite ; « Configurer l'action » permet d'en choisir un autre.
        yield return new(audio, audioIcon, "Micro muet", TileRuntime.MicGlyph, "Coupe ou rétablit le micro par défaut ; la tuile s'allume quand il est coupé.",
            () => One(new ShortcutItem { Kind = TileKind.MicMute, Name = "Micro" }));
        yield return new(audio, audioIcon, "Son muet", TileRuntime.SpeakerGlyph, "Coupe ou rétablit la sortie audio par défaut.",
            () => One(new ShortcutItem { Kind = TileKind.OutputMute, Name = "Son" }));

        yield return new("OBS Studio", "brand:obsstudio", "Action OBS", "brand:obsstudio",
            "Scène, stream, enregistrement, caméra virtuelle, relecture, muet d'une entrée, source…",
            () => One(EditTile(new AddObsTileWindow(this, CreateEmojiPickerForChoice, _widgetManager.Obs), w => w.Result)), "#FFE7E8EB");
        yield return new("Twitch", "brand:twitch", "Action Twitch", "brand:twitch",
            "Clip, marqueur, message, pub, modes du tchat, bouclier, titre, catégorie…",
            () => One(EditTile(new AddTwitchTileWindow(this, CreateEmojiPickerForChoice, _widgetManager.Twitch), w => w.Result)));
        yield return new("Voicemod", "", "Action Voicemod", "",
            "Changeur de voix, voix, son du soundboard, micro, m'entendre…",
            () => One(EditTile(new AddVoicemodTileWindow(this, CreateEmojiPickerForChoice, _widgetManager.Voicemod), w => w.Result)), "#FF2EE6B6");

        // Une catégorie par plugin installé, une entrée par action (la fenêtre s'ouvre sur cette action).
        foreach (var plugin in _widgetManager.PluginHost.Plugins.Where(p => p.Manifest is not null))
            foreach (var action in plugin.Manifest!.Actions)
                yield return new(plugin.Name, plugin.Manifest.IconPath ?? TileRuntime.PluginGlyph, action.Name, action.IconPath ?? TileRuntime.PluginGlyph,
                    action.Tooltip ?? plugin.Manifest.Description ?? "",
                    () => One(EditTile(new AddPluginTileWindow(this, CreateEmojiPickerForChoice, _widgetManager.PluginHost,
                        pluginId: plugin.Id, actionId: action.Id), w => w.Result)), Plugin: true);
    }

    private void DeckActionSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshDeckActions();

    /// <summary>Liste des actions par catégorie (repliables), filtrée par la recherche (nom, aide, catégorie, sans accents).</summary>
    private void RefreshDeckActions()
    {
        if (DeckActionsList is null) return;
        DeckActionsList.Children.Clear();
        var needle = BrandIcons.Fold(DeckActionSearch.Text);
        // Vue Manettes : commandes de manette en tête, pas de plugins.
        var actions = (_joyConView ? JoyConActions() : DeckActions()).Where(a => needle.Length == 0
            || BrandIcons.Fold(a.Name).Contains(needle, StringComparison.Ordinal)
            || BrandIcons.Fold(a.Description).Contains(needle, StringComparison.Ordinal)
            || BrandIcons.Fold(a.Category).Contains(needle, StringComparison.Ordinal)).ToList();

        foreach (var group in actions.GroupBy(a => a.Category))
        {
            var category = group.Key;
            var collapsed = needle.Length == 0 && _collapsedActionCategories.Contains(category);
            var header = new Button { Style = (Style)FindResource("SearchResultButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            header.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = collapsed ? "" : "",
                        FontFamily = new FontFamily(ShortcutIcons.SymbolFont),
                        FontSize = 10,
                        Foreground = (Brush)FindResource("Muted"),
                        Width = 18,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    ActionIcon(group.First().CategoryIcon, 18, group.First().IconColor),
                    new TextBlock { Text = category, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                }
            };
            header.Click += (_, _) =>
            {
                if (!_collapsedActionCategories.Remove(category)) _collapsedActionCategories.Add(category);
                RefreshDeckActions();
            };
            DeckActionsList.Children.Add(header);
            if (collapsed) continue;

            foreach (var action in group)
                DeckActionsList.Children.Add(BuildActionButton(action));
        }
        if (actions.Count == 0)
            DeckActionsList.Children.Add(new TextBlock { Text = "Aucune action ne correspond.", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(12, 8, 0, 0) });
    }

    private FrameworkElement ActionIcon(string icon, double size, string? color)
    {
        var element = ShortcutIcons.CreateCustom(icon, size, color) ?? ShortcutIcons.CreateGlyph(TileRuntime.PluginGlyph, size);
        element.Width = element.Height = size;
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }

    private Button BuildActionButton(DeckAction action)
    {
        var button = new Button
        {
            Style = (Style)FindResource("SearchResultButton"),
            Margin = new Thickness(18, 0, 0, 2),
            ToolTip = string.IsNullOrWhiteSpace(action.Description) ? action.Name : $"{action.Name}\n{action.Description}",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Border
                    {
                        Width = 30,
                        Height = 30,
                        CornerRadius = new CornerRadius(7),
                        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x33)),
                        Child = ActionIcon(action.Icon, 18, action.IconColor)
                    },
                    new TextBlock { Text = action.Name, FontSize = 12.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };

        // Glisser vers une case de l'aperçu, ou clic pour ajouter à la première case libre.
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _actionDragStart = e.GetPosition(this);
            _actionDragged = false;
        };
        button.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _actionDragged) return;
            if ((e.GetPosition(this) - _actionDragStart).Length < 8) return;
            _actionDragged = true;
            DragDrop.DoDragDrop(button, new System.Windows.DataObject(DeckLayoutEditor.ActionDataFormat, action), DragDropEffects.Copy);
        };
        button.Click += (_, _) =>
        {
            if (_actionDragged)
            {
                _actionDragged = false;
                return;
            }
            RunDeckAction(action, null);
        };
        return button;
    }

    /// <summary>Crée les tuiles de l'action (fenêtre de réglage si besoin) et les place : case visée, sinon première case libre.</summary>
    private void RunDeckAction(DeckAction action, (int Col, int Row)? cell)
    {
        if (_joyConView)
        {
            AssignJoyCon(action); // vue Manettes : l'action va à la touche choisie
            return;
        }
        if (!Deck.AppsGridHasRoom())
        {
            WarnGridFull(1);
            return;
        }
        var items = action.Create();
        if (items is null || items.Count == 0) return;

        var wasShown = Deck.IsShown("apps");
        ShortcutItem? last = null;
        var notAdded = 0;
        foreach (var item in items)
        {
            if (!Deck.AddApp(item))
            {
                notAdded++;
                continue;
            }
            if (last is null && cell is { } target) Deck.MoveAppToCell(item, target.Col, target.Row);
            last = item;
        }
        if (notAdded > 0) WarnGridFull(notAdded);
        if (!wasShown && Deck.IsShown("apps"))
        {
            DeckEditor.Rebuild();
            RefreshDeckElementsList();
        }
        if (last is not null) InspectTile(last);
        RefreshDeckApps();
        ScheduleSave();
    }

    private IReadOnlyList<ShortcutItem>? PickInstalledApps()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ajouter des applications à la page",
            Filter = "Exécutables et raccourcis (*.exe;*.lnk)|*.exe;*.lnk|Tous les fichiers (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return null;
        var existing = Deck.Settings.Page.Apps;
        return dialog.FileNames
            .Where(p => !existing.Any(a => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new ShortcutItem { Name = System.IO.Path.GetFileNameWithoutExtension(p), Path = p })
            .ToList();
    }
}
