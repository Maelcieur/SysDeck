using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SysDeck.Controls;
using SysDeck.Deck;
using SysDeck.Infrastructure;
using SysDeck.Interop;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

/// <summary>Page « Switch » des Paramètres : disposition de l'écran, éléments affichés, luminosité, fond.</summary>
public partial class SettingsWindow
{
    private sealed record DeckElementRow(string Id, string Label, bool IsVisible);

    private sealed record DeckAppRow(ShortcutItem Item, FrameworkElement? Icon, string Name, string Detail, string Warning);

    private bool _deckPageReady;

    private DeckController Deck => _widgetManager.Deck;

    private void InitDeckPage()
    {
        var settings = Deck.Settings;
        DeckEditor.Attach(Deck);
        DeckEditor.LayoutChanged += () => { ScheduleSave(); RefreshDeckApps(); };
        DeckEditor.VisibilityChanged += RefreshDeckElementsList;
        DeckEditor.ElementLayoutChanged += _ => ScheduleSave();
        Closed += (_, _) =>
        {
            foreach (var (appearance, handler) in _switchSubscriptions.Values)
                appearance.PropertyChanged -= handler;
            _switchSubscriptions.Clear();
        };
        RefreshDeckElementsList();
        RefreshDeckApps();

        DeckEnabledCheck.IsChecked = settings.Enabled;
        DeckBrightnessSlider.Value = settings.Brightness;
        DeckFpsSlider.Value = settings.Fps;
        DeckTileSlider.Value = settings.TileSize;
        DeckClockOpacitySlider.Value = settings.ClockOpacity;
        DeckAppsOpacitySlider.Value = settings.AppsOpacity;
        DeckNightClockCheck.IsChecked = settings.NightClock;
        DeckNightBrightnessSlider.Value = settings.NightBrightness;
        DeckDimSlider.Value = settings.BackgroundDim;
        DeckBackgroundSwatch.Hex = settings.BackgroundColor;
        DependencyPropertyDescriptor.FromProperty(ColorSwatch.HexProperty, typeof(ColorSwatch))
            .AddValueChanged(DeckBackgroundSwatch, (_, _) =>
            {
                settings.BackgroundColor = DeckBackgroundSwatch.Hex;
                Deck.ApplyBackground();
                ScheduleSave();
            });

        ShowDeckStatus(Deck.Status);
        Deck.StatusChanged += ShowDeckStatus;
        RefreshDeckPages();
        Deck.PageChanged += OnDeckPageChanged;
        Closed += (_, _) => Deck.PageChanged -= OnDeckPageChanged;
        _deckPageReady = true;
    }

    private void RefreshDeckElementsList()
        => DeckElementsList.ItemsSource = DeckController.AllElements
            .Select(e => new DeckElementRow(e.Id, e.Label, Deck.IsShown(e.Id)))
            .ToList();

    private void ShowDeckStatus(DeckStatus status)
    {
        DeckStatusText.Text = !Deck.Settings.Enabled ? "Désactivée"
            : !status.Connected ? "En attente de la Switch…"
            : status.Battery < 0 ? "● Connectée"
            : $"● Connectée · batterie {status.Battery} %{(status.Charging ? " (en charge)" : "")}";
    }

    private void DeckElementLabel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border { Child: StackPanel { Children: [CheckBox check, ..] } })
            check.IsChecked = check.IsChecked != true;
    }

    private void DeckElementCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_deckPageReady || sender is not CheckBox { Tag: string id } check) return;
        Deck.SetVisible(id, check.IsChecked == true);
        DeckEditor.Rebuild();
        ScheduleSave();
    }

    private void DeckEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_deckPageReady) return;
        Deck.SetEnabled(DeckEnabledCheck.IsChecked == true);
        ShowDeckStatus(Deck.Status);
        ScheduleSave();
    }

    private void DeckNightClock_Changed(object sender, RoutedEventArgs e)
    {
        if (!_deckPageReady) return;
        Deck.SetNightClock(DeckNightClockCheck.IsChecked == true);
        ScheduleSave();
    }

    private void DeckSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_deckPageReady) return;
        var settings = Deck.Settings;
        if (sender == DeckBrightnessSlider) Deck.SetBrightness((int)Math.Round(e.NewValue));
        else if (sender == DeckFpsSlider) Deck.SetFps((int)Math.Round(e.NewValue));
        else if (sender == DeckTileSlider)
        {
            settings.TileSize = Math.Round(e.NewValue);
            Deck.RefreshShortcuts();
            RefreshDeckApps();
            DeckEditor.RefreshGrid();
        }
        else if (sender == DeckNightBrightnessSlider) Deck.SetNightBrightness((int)Math.Round(e.NewValue));
        else if (sender == DeckClockOpacitySlider)
        {
            settings.ClockOpacity = e.NewValue;
            Deck.ApplyClockOpacity();
        }
        else if (sender == DeckAppsOpacitySlider)
        {
            settings.AppsOpacity = e.NewValue;
            Deck.ApplyAppsOpacity();
        }
        else if (sender == DeckDimSlider)
        {
            settings.BackgroundDim = e.NewValue;
            Deck.ApplyBackground();
        }
        ScheduleSave();
    }

    // ---------- Pages ----------

    private bool _syncingPages;

    private void RefreshDeckPages()
    {
        _syncingPages = true;
        var settings = Deck.Settings;
        DeckPagesList.ItemsSource = null;
        DeckPagesList.ItemsSource = settings.Pages;
        DeckPagesList.SelectedIndex = settings.CurrentPage;
        DeckPageNameBox.Text = settings.Page.Name;
        DeckPageRemoveButton.IsEnabled = settings.Pages.Count > 1;
        _syncingPages = false;
    }

    /// <summary>Page changée (ici ou par un glissement sur la Switch) : liste, aperçu et éléments suivent.</summary>
    private void OnDeckPageChanged()
    {
        RefreshDeckPages();
        DeckEditor.Rebuild();
        RefreshDeckElementsList();
        RefreshDeckApps();
        ScheduleSave();
    }

    private void DeckPages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPages || !_deckPageReady || DeckPagesList.SelectedIndex < 0) return;
        Deck.SetPage(DeckPagesList.SelectedIndex);
    }

    private void DeckPageAdd_Click(object sender, RoutedEventArgs e) => Deck.AddPage();

    private void DeckPageLeft_Click(object sender, RoutedEventArgs e) => Deck.MovePage(-1);

    private void DeckPageRight_Click(object sender, RoutedEventArgs e) => Deck.MovePage(1);

    private void DeckPageRemove_Click(object sender, RoutedEventArgs e)
    {
        var page = Deck.Settings.Page;
        if (page.Elements.Any(el => el.Visible)
            && MessageBox.Show(this, $"Supprimer la page « {page.Name} » et ses éléments ?", "SysDeck",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Deck.RemovePage();
    }

    private void DeckPageName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingPages || !_deckPageReady) return;
        Deck.Settings.Page.Name = DeckPageNameBox.Text;
        ScheduleSave();
    }

    // ---------- Applications de la page ----------

    private void RefreshDeckApps()
    {
        var apps = Deck.Settings.Page.Apps;
        var hidden = Deck.HiddenApps();
        DeckAppsList.ItemsSource = apps
            .Select(a => new DeckAppRow(a, ShortcutIcons.Create(TileRuntime.ForDisplay(a), 22), a.Name, TileDetail(a),
                hidden.Contains(a) ? "hors de la grille — agrandis le bloc" : ""))
            .ToList();
        DeckAppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Précision affichée à côté du nom d'une tuile : combinaison, commande, périphérique, arguments.</summary>
    private static string TileDetail(ShortcutItem tile) => tile.Kind switch
    {
        TileKind.Hotkey => HotkeyGesture.Parse(tile.Hotkey, requireModifier: false)?.ToString() ?? tile.Hotkey ?? "",
        TileKind.Command => (tile.Command ?? "") + (tile.RunAsAdmin ? " · admin" : "") + (tile.ConfirmTap ? " · confirmation" : ""),
        TileKind.MicMute => string.IsNullOrEmpty(tile.DeviceId) ? "micro par défaut" : "micro choisi",
        TileKind.OutputMute => string.IsNullOrEmpty(tile.DeviceId) ? "sortie par défaut" : "sortie choisie",
        _ => tile.Arguments
    };

    private void WarnGridFull(int count)
    {
        var (columns, rows) = Deck.AppsGrid();
        MessageBox.Show(this,
            $"La grille des applications est pleine ({columns} colonne(s) × {rows} ligne(s)) : {(count > 1 ? $"{count} tuiles n'ont pas été ajoutées" : "la tuile n'a pas été ajoutée")}.\n\n" +
            "Agrandis le bloc « Applications de la page » dans l'aperçu (coin en bas à droite), réduis la taille des tuiles ou retire une tuile.",
            "SysDeck", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Ajoute les applications à la page ; l'élément apparaît sur la Switch s'il n'y était pas.</summary>
    private void AddDeckApps(IEnumerable<string> paths)
    {
        var wasShown = Deck.IsShown("apps");
        var added = Deck.AddApps(paths, out var notAdded);
        if (notAdded > 0) WarnGridFull(notAdded);
        if (added == 0) return;
        if (!wasShown)
        {
            DeckEditor.Rebuild();
            RefreshDeckElementsList();
        }
        RefreshDeckApps();
        ScheduleSave();
    }

    /// <summary>« Ajouter une tuile… » : choix du type, puis fenêtre de création correspondante.</summary>
    private void DeckTileAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!Deck.AppsGridHasRoom())
        {
            WarnGridFull(1);
            return;
        }
        var chooser = new NewTileWindow(this);
        if (chooser.ShowDialog() != true || chooser.Result is not { } type) return;

        if (type == NewTileType.InstalledApps)
        {
            AddInstalledApps();
            return;
        }
        var item = type switch
        {
            NewTileType.Link => EditTile(new AddCustomShortcutWindow(this, CreateEmojiPickerForChoice), w => w.Result),
            NewTileType.Command => EditTile(new AddCommandTileWindow(this, CreateEmojiPickerForChoice), w => w.Result),
            NewTileType.Hotkey => EditTile(new AddHotkeyShortcutWindow(this, CreateEmojiPickerForChoice), w => w.Result),
            NewTileType.MicMute => EditTile(new AddMuteTileWindow(this, CreateEmojiPickerForChoice, output: false), w => w.Result),
            _ => EditTile(new AddMuteTileWindow(this, CreateEmojiPickerForChoice, output: true), w => w.Result)
        };
        if (item is not null) AddDeckTile(item);
    }

    private static ShortcutItem? EditTile<TWindow>(TWindow window, Func<TWindow, ShortcutItem?> result) where TWindow : Window
        => window.ShowDialog() == true ? result(window) : null;

    private void AddInstalledApps()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ajouter des applications à la page",
            Filter = "Exécutables et raccourcis (*.exe;*.lnk)|*.exe;*.lnk|Tous les fichiers (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true) AddDeckApps(dialog.FileNames);
    }

    private EmojiPickerWindow CreateEmojiPickerForChoice()
        => new(() => _widgetManager.Config.RecentEmojis, _widgetManager.Config.EmojiPicker);

    /// <summary>Ajoute une tuile toute faite à la page (l'élément « apps » apparaît sur la Switch s'il n'y était pas).</summary>
    private void AddDeckTile(ShortcutItem item)
    {
        var wasShown = Deck.IsShown("apps");
        if (!Deck.AddApp(item))
        {
            WarnGridFull(1);
            return;
        }
        if (!wasShown)
        {
            DeckEditor.Rebuild();
            RefreshDeckElementsList();
        }
        RefreshDeckApps();
        ScheduleSave();
    }

    /// <summary>Modifie une tuile sur place (même position sur la page) avec la fenêtre de son type.</summary>
    private void DeckTileEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutItem app }) return;
        var edited = app.Kind switch
        {
            TileKind.Hotkey => EditTile(new AddHotkeyShortcutWindow(this, CreateEmojiPickerForChoice, app), w => w.Result),
            TileKind.Command => EditTile(new AddCommandTileWindow(this, CreateEmojiPickerForChoice, app), w => w.Result),
            TileKind.MicMute => EditTile(new AddMuteTileWindow(this, CreateEmojiPickerForChoice, output: false, app), w => w.Result),
            TileKind.OutputMute => EditTile(new AddMuteTileWindow(this, CreateEmojiPickerForChoice, output: true, app), w => w.Result),
            _ => EditTile(new AddCustomShortcutWindow(this, CreateEmojiPickerForChoice, app), w => w.Result)
        };
        if (edited is null) return;

        var previousImage = app.CustomIconPath;
        app.Name = edited.Name;
        app.Kind = edited.Kind;
        app.Path = edited.Path;
        app.Arguments = edited.Arguments;
        app.Hotkey = edited.Hotkey;
        app.Command = edited.Command;
        app.RunAsAdmin = edited.RunAsAdmin;
        app.ConfirmTap = edited.ConfirmTap;
        app.DeviceId = edited.DeviceId;
        app.IconGlyph = edited.IconGlyph;
        app.CustomIconPath = edited.CustomIconPath;
        app.IconColor = edited.IconColor;
        app.TileColor = edited.TileColor;
        TileRuntime.Muted.Remove(app); // le périphérique a pu changer : relu au prochain passage
        if (previousImage != edited.CustomIconPath) CustomIconStore.TryDelete(previousImage);

        RefreshDeckApps();
        Deck.RefreshShortcuts();
        ScheduleSave();
    }

    private void DeckAppIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutItem app }) return;
        var chooser = new IconChooserWindow(this, app.Name, CreateEmojiPickerForChoice, app);
        if (chooser.ShowDialog() != true || chooser.Result is not { } choice) return;

        app.IconColor = choice.IconColor;
        app.TileColor = choice.TileColor;
        if (choice.Kind == IconChoiceKind.Keep)
        {
            // Seules les couleurs ont changé.
            RefreshDeckApps();
            Deck.RefreshShortcuts();
            ScheduleSave();
            return;
        }

        var previousImage = app.CustomIconPath;
        string? newImage = null;
        if (choice.Kind == IconChoiceKind.Image)
        {
            try
            {
                newImage = CustomIconStore.SaveCopy(choice.Value!);
            }
            catch (Exception ex)
            {
                Log.Warn("Copie de l'icône impossible", ex);
                MessageBox.Show(this, $"Impossible d'utiliser cette image :\n{ex.Message}", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // Un seul choix à la fois : le symbole ou l'émoji masquerait l'image, autant ne pas garder une copie inutile.
        app.CustomIconPath = newImage;
        app.IconGlyph = choice.Kind == IconChoiceKind.Glyph ? choice.Value : null;
        CustomIconStore.TryDelete(previousImage);

        RefreshDeckApps();
        Deck.RefreshShortcuts();
        ScheduleSave();
    }

    private void DeckApps_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DeckApps_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddDeckApps(files.Where(System.IO.File.Exists));
    }

    private void DeckAppRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShortcutItem app }) return;
        Deck.RemoveApp(app);
        CustomIconStore.TryDelete(app.CustomIconPath); // copie d'icône gérée par SysDeck, devenue inutile
        RefreshDeckApps();
        ScheduleSave();
    }

    private void DeckAppLeft_Click(object sender, RoutedEventArgs e) => MoveDeckApp(sender, -1);

    private void DeckAppRight_Click(object sender, RoutedEventArgs e) => MoveDeckApp(sender, 1);

    private void MoveDeckApp(object sender, int delta)
    {
        if (sender is not FrameworkElement { Tag: ShortcutItem app }) return;
        Deck.MoveApp(app, delta);
        RefreshDeckApps();
        ScheduleSave();
    }

    // ---------- Personnaliser : apparence propre à la Switch ----------

    /// <summary>Vrai : la page Personnaliser modifie l'apparence des modules sur la Switch, pas sur le bureau.</summary>
    private bool _customizeSwitch;

    private readonly Dictionary<string, (ModuleAppearance Appearance, PropertyChangedEventHandler Handler)> _switchSubscriptions = new();

    /// <summary>Apparence Switch du module, suivie pour appliquer chaque changement en direct.</summary>
    private ModuleAppearance SwitchAppearance(string id)
    {
        var appearance = Deck.GetAppearance(id);
        if (_switchSubscriptions.TryGetValue(id, out var current) && ReferenceEquals(current.Appearance, appearance))
            return appearance;

        if (current.Appearance is not null) current.Appearance.PropertyChanged -= current.Handler;
        appearance.Theme ??= _widgetManager.Theme.Clone();
        PropertyChangedEventHandler handler = (_, e) => OnModuleChanged(id, appearance, e.PropertyName);
        appearance.PropertyChanged += handler;
        _switchSubscriptions[id] = (appearance, handler);
        return appearance;
    }

    private bool IsSwitchAppearance(string id, ModuleAppearance appearance)
        => Deck.Settings.Modules.TryGetValue(id, out var onSwitch) && ReferenceEquals(onSwitch, appearance);

    private void CustomizeTarget_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || TargetSwitch is null || SizeCard is null) return;
        _customizeSwitch = TargetSwitch.IsChecked == true;

        SizeCard.Visibility = _customizeSwitch ? Visibility.Collapsed : Visibility.Visible;
        SwitchSizeNote.Visibility = _customizeSwitch ? Visibility.Visible : Visibility.Collapsed;
        CopyFromDesktopButton.Visibility = _customizeSwitch ? Visibility.Visible : Visibility.Collapsed;
        EditOnSwitchRow.Visibility = _customizeSwitch ? Visibility.Visible : Visibility.Collapsed;
        LayoutEditRow.Visibility = _customizeSwitch ? Visibility.Collapsed : Visibility.Visible;
        ModuleSelector_SelectionChanged(ModuleSelector, null);
    }

    private void CopyFromDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedModule is not { } module) return;
        Deck.CopyAppearanceFromDesktop(module.Id);
        ModuleSelector_SelectionChanged(ModuleSelector, null); // nouvel objet : réabonnement et réaffichage
        ScheduleSave();
    }

    private void EditOnSwitch_Click(object sender, RoutedEventArgs e)
    {
        NavSwitch.IsChecked = true;
    }

    /// <summary>
    /// Bureau et Switch peuvent partager la même copie d'image (apparence Switch copiée du bureau) :
    /// elle n'est supprimée que si plus aucune apparence ne l'utilise.
    /// </summary>
    private void DeleteBackgroundIfUnused(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var config = _widgetManager.Config;
        var used = config.Modules.Values.Concat(config.Deck.Modules.Values)
            .Any(a => string.Equals(a.BackgroundImage, path, StringComparison.OrdinalIgnoreCase));
        if (!used && !string.Equals(config.Deck.BackgroundImage, path, StringComparison.OrdinalIgnoreCase))
            BackgroundImageStore.TryDelete(path);
    }

    private void DeckBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir une image de fond pour la Switch",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        var settings = Deck.Settings;
        var previous = settings.BackgroundImage;
        try
        {
            settings.BackgroundImage = BackgroundImageStore.SaveCopy(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Warn("Copie de l'image de fond de la Switch impossible", ex);
            MessageBox.Show(this, $"Impossible d'utiliser cette image :\n{ex.Message}", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Deck.ApplyBackground();
        SaveThenDelete(previous);
    }

    /// <summary>
    /// Enregistre la configuration tout de suite, puis seulement supprime l'ancienne image : avec la sauvegarde
    /// différée, un arrêt entre-temps laissait la configuration pointer sur un fichier déjà effacé.
    /// Si l'enregistrement échoue, l'ancienne image est conservée.
    /// </summary>
    private void SaveThenDelete(string? previous)
    {
        _saveTimer.Stop();
        try
        {
            _widgetManager.SaveConfig();
        }
        catch (Exception ex)
        {
            Log.Warn("Enregistrement de la configuration impossible : ancienne image de fond conservée", ex);
            ScheduleSave();
            return;
        }
        DeleteBackgroundIfUnused(previous);
    }

    private void DeckBackgroundImageClear_Click(object sender, RoutedEventArgs e)
    {
        var settings = Deck.Settings;
        var previous = settings.BackgroundImage;
        settings.BackgroundImage = null;
        Deck.ApplyBackground();
        SaveThenDelete(previous);
    }
}
