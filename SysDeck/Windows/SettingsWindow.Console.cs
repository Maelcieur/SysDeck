using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SysDeck.ConsoleMode;
using SysDeck.Infrastructure;
using SysDeck.Interop;
using SysDeck.Services;
using SysDeck.Services.ConsoleMode;

namespace SysDeck;

// Page Mode console : TV et son, raccourci de secours, clé SteamGridDB, liste des jeux et des émulateurs.
public partial class SettingsWindow
{
    private const string SteamGridDbKeyPage = "https://www.steamgriddb.com/profile/preferences/api";

    private sealed record TvChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AudioChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ConsoleLibrary _consoleLibrary = new();
    private bool _consoleLoading;
    private bool _consoleBusyRefreshing;

    private ConsoleSettings ConsoleConfig => _widgetManager.Config.Console;

    private void InitConsolePage()
    {
        _consoleLoading = true;
        ConsoleDisableTvCheck.IsChecked = ConsoleConfig.DisableTvWhenIdle;
        ConsoleGridKeyBox.Password = ConsoleConfig.SteamGridDbKey;
        ConsoleHotkeyBox.Display(HotkeyGesture.Parse(ConsoleConfig.EmergencyHotkey));
        InitConsoleAppearance();
        InitConsoleTv();
        ConsoleHotkeyStatus.Text = _widgetManager.IsConsoleHotkeyActive
            ? "Actif : ce raccourci quitte le mode console, même si l'interface TV ne répond plus."
            : "Ce raccourci est déjà utilisé par une autre application : choisis-en un autre.";
        _consoleLoading = false;
        _widgetManager.ConsoleModeChanged += UpdateConsoleLaunch;
        UpdateConsoleLaunch();
        RefreshConsoleLists();
    }

    private void CloseConsolePage() => _widgetManager.ConsoleModeChanged -= UpdateConsoleLaunch;

    /// <summary>Page affichée : relit la bibliothèque (le mode console enregistre les temps de jeu), les écrans et les sorties audio.</summary>
    private void RefreshConsolePage()
    {
        _consoleLibrary.Load();
        RefreshConsoleLists();
        RefreshConsoleDevices();
    }

    private void UpdateConsoleLaunch()
    {
        var active = _widgetManager.IsConsoleModeActive;
        ConsoleLaunchButton.Content = active ? "Quitter le mode console" : "Lancer le mode console";
    }

    // ---------- Écrans et audio ----------

    private void ConsoleTvRefresh_Click(object sender, RoutedEventArgs e) => RefreshConsoleDevices();

    private async void RefreshConsoleDevices()
    {
        if (_consoleBusyRefreshing) return;
        _consoleBusyRefreshing = true;
        ConsoleTvRefreshButton.IsEnabled = false;
        ConsoleTvStatus.Text = "Lecture des écrans…";
        try
        {
            var (targets, audio) = await Task.Run(() =>
            {
                var tvs = new List<DisplayTarget>();
                var outputs = new List<AudioDeviceInfo>();
                try { tvs = _widgetManager.ConsoleService.Display.ListTargets(); }
                catch (Exception ex) { Log.Warn("Mode console : liste des écrans impossible", ex); }
                try { outputs = new AudioService().GetPlaybackDevices(); }
                catch (Exception ex) { Log.Warn("Mode console : liste des sorties audio impossible", ex); }
                return (tvs, outputs);
            });

            _consoleLoading = true;
            var savedTv = ConsoleConfig.TvTargetId;
            var tvItems = new List<TvChoice> { new("", "(aucune)") };
            tvItems.AddRange(targets.Select(t => new TvChoice(t.Id,
                $"{(string.IsNullOrWhiteSpace(t.Name) ? "Écran sans nom" : t.Name)} — {(t.IsActive ? "actif" : "inactif")}")));
            var selected = targets.FirstOrDefault(t => DisplayTargetId.Matches(savedTv, t.AdapterLuid, t.TargetId, t.DevicePath));
            if (!string.IsNullOrWhiteSpace(savedTv) && selected is null)
                tvItems.Add(new TvChoice(savedTv, "TV enregistrée (non branchée)"));
            ConsoleTvCombo.ItemsSource = tvItems;
            ConsoleTvCombo.SelectedItem = selected is not null ? tvItems.First(i => i.Id == selected.Id)
                : string.IsNullOrWhiteSpace(savedTv) ? tvItems[0] : tvItems[^1];

            var savedAudio = ConsoleConfig.TvAudioDeviceId;
            var audioItems = new List<AudioChoice> { new("", "(ne pas changer le son)") };
            audioItems.AddRange(audio.Select(d => new AudioChoice(d.Id, d.DisplayName)));
            if (!string.IsNullOrWhiteSpace(savedAudio) && audioItems.All(a => a.Id != savedAudio))
                audioItems.Add(new AudioChoice(savedAudio, "Sortie enregistrée (introuvable)"));
            ConsoleAudioCombo.ItemsSource = audioItems;
            ConsoleAudioCombo.SelectedItem = audioItems.FirstOrDefault(a => a.Id == savedAudio) ?? audioItems[0];
            _consoleLoading = false;

            ConsoleTvStatus.Text = targets.Count == 0
                ? "Aucun écran détecté. Vérifie que la TV est allumée et branchée, puis clique sur Actualiser."
                : selected is null && string.IsNullOrWhiteSpace(savedTv)
                    ? $"{targets.Count} écran(s) détecté(s). Choisis ta TV."
                    : selected is null
                        ? "La TV enregistrée n'est pas branchée ou pas allumée."
                        : selected.IsActive
                            ? "TV détectée et active dans Windows."
                            : "TV détectée mais inactive : le mode console l'activera.";
        }
        catch (Exception ex)
        {
            _consoleLoading = false;
            Log.Warn("Mode console : actualisation des écrans en échec", ex);
            ConsoleTvStatus.Text = "Lecture des écrans impossible : " + ex.Message;
        }
        finally
        {
            ConsoleTvRefreshButton.IsEnabled = true;
            _consoleBusyRefreshing = false;
        }
    }

    private void ConsoleTv_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_consoleLoading || ConsoleTvCombo.SelectedItem is not TvChoice choice) return;
        ConsoleConfig.TvTargetId = choice.Id;
        ScheduleSave();
    }

    private void ConsoleAudio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_consoleLoading || ConsoleAudioCombo.SelectedItem is not AudioChoice choice) return;
        ConsoleConfig.TvAudioDeviceId = choice.Id;
        ScheduleSave();
    }

    private void ConsoleDisableTv_Changed(object sender, RoutedEventArgs e)
    {
        if (_consoleLoading) return;
        var enabled = ConsoleDisableTvCheck.IsChecked == true;
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(ConsoleConfig.TvTargetId))
            {
                MessageBox.Show(this, "Choisis d'abord l'écran de ta TV.", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                SetConsoleDisableCheck(false);
                return;
            }
            var answer = MessageBox.Show(this,
                "La TV sera désactivée dans Windows maintenant, et à chaque démarrage de SysDeck quand le mode console n'est pas actif.\n\n" +
                "Vérifie que l'écran choisi est bien la TV (et pas ton écran principal). Continuer ?",
                "SysDeck", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                SetConsoleDisableCheck(false);
                return;
            }
        }

        ConsoleConfig.DisableTvWhenIdle = enabled;
        ScheduleSave();
        if (enabled)
        {
            _widgetManager.EnsureTvDisabledIfConfigured();
            // La désactivation se fait en arrière-plan : la liste est relue un peu après.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (IsLoaded) RefreshConsoleDevices();
            };
            timer.Start();
        }
    }

    private void SetConsoleDisableCheck(bool value)
    {
        _consoleLoading = true;
        ConsoleDisableTvCheck.IsChecked = value;
        _consoleLoading = false;
    }

    // ---------- Lancement, raccourci, clé ----------

    private void ConsoleLaunch_Click(object sender, RoutedEventArgs e)
    {
        _widgetManager.SaveConfig(); // les choix de TV et de son tout juste faits
        _ = _widgetManager.ToggleConsoleModeAsync();
    }

    private void ConsoleHotkeyBox_GestureCaptured(HotkeyGesture gesture)
    {
        if (_widgetManager.SetConsoleEmergencyHotkey(gesture))
        {
            ConsoleHotkeyBox.Display(gesture);
            ConsoleHotkeyStatus.Text = $"Actif : appuie sur {gesture} pour quitter le mode console.";
        }
        else
        {
            ConsoleHotkeyBox.Display(HotkeyGesture.Parse(ConsoleConfig.EmergencyHotkey));
            ConsoleHotkeyStatus.Text = $"{gesture} est déjà utilisé par Windows ou une autre application : choisis une autre combinaison.";
        }
    }

    private void ConsoleHotkeyBox_CaptureStateChanged(bool capturing) => _widgetManager.SuspendHotkeys(capturing);

    private void ConsoleHotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        var gesture = HotkeyGesture.Parse(ConsoleSettings.DefaultEmergencyHotkey);
        if (gesture is not null) ConsoleHotkeyBox_GestureCaptured(gesture);
    }

    private void ConsoleGridKey_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_consoleLoading) return;
        var key = ConsoleGridKeyBox.Password.Trim();
        if (key == ConsoleConfig.SteamGridDbKey) return;
        ConsoleConfig.SteamGridDbKey = key;
        ScheduleSave();
    }

    private void ConsoleGridKeyLink_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SteamGridDbKeyPage) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn("Ouverture de la page SteamGridDB impossible", ex); }
    }

    // ---------- Listes ----------

    private Brush ConsoleBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void RefreshConsoleLists()
    {
        BuildConsoleGameRows();
        BuildConsoleEmulatorRows();
    }

    private static TextBlock ConsoleLine(string text, double size, Brush brush, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private Border ConsoleRow(UIElement leading, string title, string subtitle, Action edit, Action remove)
    {
        var text = new Brush[] { ConsoleBrush("UiText", Brushes.White), ConsoleBrush("Muted", Brushes.Gray) };
        var edit_ = new Button { Content = "Modifier", Margin = new Thickness(0, 0, 8, 0) };
        edit_.Click += (_, _) => edit();
        var remove_ = new Button { Content = "Supprimer", Style = (Style)FindResource("DangerButton") };
        remove_.Click += (_, _) => remove();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { edit_, remove_ } };
        DockPanel.SetDock(buttons, Dock.Right);

        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            Children = { ConsoleLine(title, 14, text[0], FontWeights.SemiBold), ConsoleLine(subtitle, 11.5, text[1]) }
        };
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(buttons);
        if (leading is FrameworkElement lead)
        {
            DockPanel.SetDock(lead, Dock.Left);
            dock.Children.Add(lead);
        }
        dock.Children.Add(texts);
        return new Border { Style = (Style)FindResource("RowBorder"), Child = dock };
    }

    private void BuildConsoleGameRows()
    {
        ConsoleGamesList.Children.Clear();
        var games = _consoleLibrary.Games.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        ConsoleGamesEmpty.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var game in games)
        {
            var platform = _consoleLibrary.PlatformOf(game);
            var cover = new Border
            {
                Width = 40,
                Height = 60,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x28, 0x31)),
                ClipToBounds = true,
                Child = new System.Windows.Controls.Image { Source = ConsoleImages.Load(game.CoverPath, 80), Stretch = Stretch.UniformToFill }
            };
            var id = game.Id;
            ConsoleGamesList.Children.Add(ConsoleRow(cover, game.Title, string.IsNullOrWhiteSpace(platform) ? "PC" : platform,
                () => EditConsoleGame(id), () => RemoveConsoleGame(id)));
        }
    }

    private void BuildConsoleEmulatorRows()
    {
        ConsoleEmulatorsList.Children.Clear();
        var emulators = _consoleLibrary.Emulators.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ConsoleEmulatorsEmpty.Visibility = emulators.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var emulator in emulators)
        {
            var icon = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 22,
                Width = 40,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ConsoleBrush("Muted", Brushes.Gray)
            };
            var subtitle = string.Join(" · ", new[] { emulator.Platform, emulator.ExePath }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var id = emulator.Id;
            ConsoleEmulatorsList.Children.Add(ConsoleRow(icon, emulator.Name, subtitle, () => EditConsoleEmulator(id), () => RemoveConsoleEmulator(id)));
        }
    }

    // ---------- Jeux ----------

    private void ConsoleGameAdd_Click(object sender, RoutedEventArgs e) => EditConsoleGame(null);

    /// <summary>Ajoute (id null) ou modifie un jeu. La bibliothèque est relue juste avant : l'interface TV y enregistre les temps de jeu.</summary>
    private void EditConsoleGame(string? id)
    {
        _consoleLibrary.Load();
        var game = id is null ? null : _consoleLibrary.Games.FirstOrDefault(g => g.Id == id);
        if (id is not null && game is null) { RefreshConsoleLists(); return; }

        var editor = new ConsoleGameEditorWindow(this, _consoleLibrary, game, ConsoleConfig.SteamGridDbKey);
        if (editor.ShowDialog() != true)
        {
            _consoleLibrary.Load(); // annule les modifications faites sur place
            RefreshConsoleLists();
            return;
        }
        if (editor.IsNew) _consoleLibrary.Add(editor.Game);
        _consoleLibrary.Save();
        RefreshConsoleLists();
    }

    private void RemoveConsoleGame(string id)
    {
        _consoleLibrary.Load();
        var game = _consoleLibrary.Games.FirstOrDefault(g => g.Id == id);
        if (game is null) { RefreshConsoleLists(); return; }
        if (MessageBox.Show(this, $"Retirer « {game.Title} » de la bibliothèque ?\n\nLe jeu lui-même n'est pas supprimé.", "SysDeck",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var images = new[] { game.CoverPath, game.HeroPath, game.LogoPath };
        _consoleLibrary.Remove(id);
        _consoleLibrary.Save();
        foreach (var path in images)
        {
            // Seulement les copies gérées par SysDeck : jamais un fichier en dehors du cache.
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(ConsoleLibrary.ArtDir), StringComparison.OrdinalIgnoreCase))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Suppression d'une image du mode console impossible", ex);
            }
        }
        RefreshConsoleLists();
    }

    // ---------- Émulateurs ----------

    /// <summary>Menu des préréglages ; le choix ouvre la sélection du programme puis crée l'émulateur.</summary>
    private void ConsoleEmulatorAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var preset in EmulatorPresets.All)
        {
            var item = new MenuItem { Header = string.IsNullOrWhiteSpace(preset.Platform) ? preset.Name : $"{preset.Name}  ({preset.Platform})" };
            item.Click += (_, _) => AddConsoleEmulator(preset);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AddConsoleEmulator(EmulatorPreset preset)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choisir le programme de {preset.Name}",
            Filter = "Programmes (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        _consoleLibrary.Load();
        var emulator = EmulatorPresets.Create(preset, dialog.FileName);
        _consoleLibrary.AddEmulator(emulator);
        _consoleLibrary.Save();
        RefreshConsoleLists();
        // RetroArch : le cœur doit être choisi ; les autres préréglages sont prêts.
        if (preset.NeedsCore || preset.Name == "Personnalisé") EditConsoleEmulator(emulator.Id);
        else if (!string.IsNullOrWhiteSpace(preset.Hint))
            MessageBox.Show(this, preset.Hint, "SysDeck", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EditConsoleEmulator(string id)
    {
        _consoleLibrary.Load();
        var emulator = _consoleLibrary.FindEmulator(id);
        if (emulator is null) { RefreshConsoleLists(); return; }
        var editor = new ConsoleEmulatorEditorWindow(this, emulator);
        if (editor.ShowDialog() == true) _consoleLibrary.Save();
        else _consoleLibrary.Load();
        RefreshConsoleLists();
    }

    private void RemoveConsoleEmulator(string id)
    {
        _consoleLibrary.Load();
        var emulator = _consoleLibrary.FindEmulator(id);
        if (emulator is null) { RefreshConsoleLists(); return; }

        var users = _consoleLibrary.Games.Count(g => g.EmulatorId == id);
        if (users > 0)
        {
            MessageBox.Show(this,
                $"« {emulator.Name} » est utilisé par {users} jeu(x). Modifie ou retire ces jeux avant de supprimer l'émulateur.",
                "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, $"Supprimer l'émulateur « {emulator.Name} » ?", "SysDeck",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _consoleLibrary.RemoveEmulator(id);
        _consoleLibrary.Save();
        RefreshConsoleLists();
    }
}
