using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysDeck.Infrastructure;
using SysDeck.Services;

namespace SysDeck;

// Page « Profils » : galerie de thèmes prêts à l'emploi et profils enregistrés (appliquer, renommer,
// exporter, supprimer, importer). Les confirmations se font dans la page, sans boîte de dialogue bloquante.
public partial class SettingsWindow
{
    /// <summary>Vrai pendant que les contrôles sont remis à jour après un profil : leurs gestionnaires ne réécrivent rien.</summary>
    private bool _refreshingFromProfile;

    /// <summary>Instantané pris avant le dernier thème appliqué, pour le bouton « Annuler ».</summary>
    private Profile? _themeUndoSnapshot;

    /// <summary>Profil en attente de confirmation d'écrasement (null : aucune confirmation en cours).</summary>
    private string? _pendingOverwrite;

    private string? _renamingProfile;
    private string? _deletingProfile;

    private void InitProfilesPage()
    {
        BuildPresetGallery();
        RefreshProfilesList();
    }

    // ---------- Thèmes prêts à l'emploi ----------

    private void BuildPresetGallery()
    {
        var tileStyle = (Style)ProfilesContent.FindResource("PresetTile");
        foreach (var preset in ThemePresets.All)
        {
            var (start, end, panel, text) = preset.Swatch;
            var startColor = ThemeService.Parse(start, Colors.Cyan);
            var endColor = ThemeService.Parse(end, Colors.Violet);

            // Mini-aperçu : fond de carte, barre en dégradé et texte dans la couleur du thème.
            var sample = new StackPanel();
            sample.Children.Add(new Border
            {
                Height = 8,
                Width = 110,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Background = new LinearGradientBrush(startColor, endColor, 0)
            });
            sample.Children.Add(new TextBlock
            {
                Text = "Aa  42 %",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = new SolidColorBrush(ThemeService.Parse(text, Colors.White))
            });
            var previewBox = new Border
            {
                Height = 70,
                Padding = new Thickness(14, 14, 14, 0),
                Background = new SolidColorBrush(ThemeService.Parse(panel, Color.FromRgb(0x23, 0x25, 0x30))),
                Child = sample
            };

            var info = new StackPanel { Margin = new Thickness(12, 9, 12, 11) };
            info.Children.Add(new TextBlock { Text = preset.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = (Brush)FindResource("UiText") });
            info.Children.Add(new TextBlock
            {
                Text = preset.Description,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource("Muted")
            });

            var content = new StackPanel();
            content.Children.Add(previewBox);
            content.Children.Add(info);

            var tile = new Button
            {
                Style = tileStyle,
                Content = content,
                Tag = preset,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            tile.Click += PresetTile_Click;
            PresetGallery.Children.Add(tile);
        }
    }

    private void PresetTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ThemePreset preset }) return;
        ClearProfileMessages();
        try
        {
            FlushApply();
            var before = _widgetManager.CaptureProfile(); // photographie complète : l'annulation remet aussi les styles de modules
            _widgetManager.ApplyThemePreset(preset, PresetResetModulesCheck.IsChecked == true);
            _themeUndoSnapshot = before;
            PresetUndoText.Text = $"Thème « {preset.Name} » appliqué.";
            PresetUndoBar.Visibility = Visibility.Visible;
            RefreshStyleEditors();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex);
        }
    }

    private void PresetUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_themeUndoSnapshot is not { } snapshot) return;
        _themeUndoSnapshot = null;
        PresetUndoBar.Visibility = Visibility.Collapsed;
        try
        {
            FlushApply();
            _widgetManager.ApplyProfile(snapshot, includeLayout: false);
            RefreshAfterProfileApply(includeLayout: false);
            ShowProfileStatus("Thème précédent restauré.");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex);
        }
    }

    // ---------- Messages ----------

    private static bool IsRecoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException);

    private void ClearProfileMessages()
    {
        ProfilesStatus.Visibility = ProfilesError.Visibility = Visibility.Collapsed;
    }

    private void ShowProfileStatus(string message)
    {
        ProfilesError.Visibility = Visibility.Collapsed;
        ProfilesStatus.Text = message;
        ProfilesStatus.Visibility = Visibility.Visible;
    }

    /// <summary>Message de ProfileException tel quel (déjà en français) ; toute autre erreur est journalisée puis résumée.</summary>
    private void ShowProfileError(Exception ex)
    {
        if (ex is not ProfileException) Log.Warn("Erreur imprévue dans la page Profils", ex);
        ProfilesStatus.Visibility = Visibility.Collapsed;
        ProfilesError.Text = ex is ProfileException ? ex.Message : $"Opération impossible : {ex.Message}";
        ProfilesError.Visibility = Visibility.Visible;
    }

    // ---------- Enregistrer ----------

    private void ProfileNameBox_TextChanged(object sender, TextChangedEventArgs e) => CancelOverwrite();

    private void ProfileNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ProfileSave_Click(sender, e);
        e.Handled = true;
    }

    private void ProfileSave_Click(object sender, RoutedEventArgs e)
    {
        ClearProfileMessages();
        var name = ProfileNameBox.Text.Trim();
        try
        {
            if (_widgetManager.Profiles.Exists(name))
            {
                _pendingOverwrite = name;
                ProfileOverwriteText.Text = $"Un profil « {name} » existe déjà. Le remplacer par la configuration actuelle ?";
                ProfileOverwriteBar.Visibility = Visibility.Visible;
                return;
            }
            SaveProfile(name);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex);
        }
    }

    private void ProfileOverwriteConfirm_Click(object sender, RoutedEventArgs e)
    {
        var name = _pendingOverwrite;
        CancelOverwrite();
        if (name is null) return;
        try { SaveProfile(name); }
        catch (Exception ex) when (IsRecoverable(ex)) { ShowProfileError(ex); }
    }

    private void ProfileOverwriteCancel_Click(object sender, RoutedEventArgs e) => CancelOverwrite();

    private void CancelOverwrite()
    {
        _pendingOverwrite = null;
        if (ProfileOverwriteBar is not null) ProfileOverwriteBar.Visibility = Visibility.Collapsed;
    }

    private void SaveProfile(string name)
    {
        FlushApply(); // la configuration doit refléter le dernier réglage touché
        var saved = _widgetManager.Profiles.SaveCurrent(name);
        ProfileNameBox.Text = "";
        RefreshProfilesList();
        ShowProfileStatus($"Profil « {saved.Name} » enregistré.");
    }

    // ---------- Liste ----------

    private void RefreshProfilesList()
    {
        if (ProfilesListPanel is null) return;
        ProfilesListPanel.Children.Clear();
        IReadOnlyList<ProfileInfo> profiles;
        try { profiles = _widgetManager.Profiles.List(); }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex);
            profiles = Array.Empty<ProfileInfo>();
        }
        foreach (var info in profiles) ProfilesListPanel.Children.Add(BuildProfileRow(info));
        ProfilesEmptyText.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button RowButton(string text, RoutedEventHandler click, string name, string? style = null)
    {
        var button = new Button { Content = text, Tag = name, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(11, 4, 11, 4), MinHeight = 28 };
        if (style is not null) button.Style = (Style)FindResource(style);
        button.Click += click;
        return button;
    }

    private Border BuildProfileRow(ProfileInfo info)
    {
        var row = new Border { Style = (Style)FindResource("ListRow") };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Child = grid;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        FrameworkElement left;
        if (string.Equals(_renamingProfile, info.Name, StringComparison.Ordinal))
        {
            var box = new System.Windows.Controls.TextBox { Text = info.Name, MaxLength = 80, Tag = info.Name, VerticalContentAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { RenameProfile(info.Name, box.Text); e.Handled = true; }
                else if (e.Key == Key.Escape) { _renamingProfile = null; RefreshProfilesList(); e.Handled = true; }
            };
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            left = box;
            buttons.Children.Add(RowButton("Valider", (_, _) => RenameProfile(info.Name, box.Text), info.Name, "PrimaryButton"));
            buttons.Children.Add(RowButton("Annuler", (_, _) => { _renamingProfile = null; RefreshProfilesList(); }, info.Name));
        }
        else if (string.Equals(_deletingProfile, info.Name, StringComparison.Ordinal))
        {
            left = new TextBlock { Text = $"Supprimer le profil « {info.Name} » ?", Foreground = (Brush)FindResource("UiText"), VerticalAlignment = System.Windows.VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            buttons.Children.Add(RowButton("Supprimer", ProfileDeleteConfirm_Click, info.Name, "DangerButton"));
            buttons.Children.Add(RowButton("Annuler", (_, _) => { _deletingProfile = null; RefreshProfilesList(); }, info.Name));
        }
        else
        {
            var text = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = info.Name, Foreground = (Brush)FindResource("UiText"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = info.SavedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Muted")
            });
            left = text;
            buttons.Children.Add(RowButton("Appliquer", ProfileApply_Click, info.Name, "PrimaryButton"));
            buttons.Children.Add(RowButton("Renommer", (_, _) => { _renamingProfile = info.Name; _deletingProfile = null; RefreshProfilesList(); }, info.Name));
            buttons.Children.Add(RowButton("Exporter", ProfileExport_Click, info.Name));
            buttons.Children.Add(RowButton("Supprimer", (_, _) => { _deletingProfile = info.Name; _renamingProfile = null; RefreshProfilesList(); }, info.Name, "DangerButton"));
        }
        grid.Children.Add(left);
        return row;
    }

    // ---------- Actions sur un profil ----------

    private void ProfileApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        ClearProfileMessages();
        var includeLayout = ProfileLayoutCheck.IsChecked == true;
        try
        {
            FlushApply();
            _widgetManager.Profiles.Apply(name, includeLayout);
            _themeUndoSnapshot = null; // l'ancien instantané ne correspond plus à ce qui est affiché
            PresetUndoBar.Visibility = Visibility.Collapsed;
            RefreshAfterProfileApply(includeLayout);
            ShowProfileStatus($"Profil « {name} » appliqué.");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex);
        }
    }

    private void RenameProfile(string oldName, string newName)
    {
        ClearProfileMessages();
        try
        {
            if (!string.Equals(oldName, newName.Trim(), StringComparison.Ordinal))
                _widgetManager.Profiles.Rename(oldName, newName);
            _renamingProfile = null;
            RefreshProfilesList();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex); // la ligne reste en édition pour corriger le nom
        }
    }

    private void ProfileDeleteConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        ClearProfileMessages();
        _deletingProfile = null;
        try
        {
            _widgetManager.Profiles.Delete(name);
            ShowProfileStatus($"Profil « {name} » supprimé.");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex);
        }
        RefreshProfilesList();
    }

    private void ProfileExport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        ClearProfileMessages();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter le profil",
            Filter = $"Profil SysDeck (*{ProfileService.PackageExtension})|*{ProfileService.PackageExtension}",
            DefaultExt = ProfileService.PackageExtension,
            AddExtension = true,
            FileName = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray())
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _widgetManager.Profiles.Export(name, dialog.FileName);
            ShowProfileStatus($"Profil « {name} » exporté.");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex);
        }
    }

    private void ProfileImport_Click(object sender, RoutedEventArgs e)
    {
        ClearProfileMessages();
        var dialog = new OpenFileDialog
        {
            Title = "Importer un profil",
            Filter = $"Profil SysDeck (*{ProfileService.PackageExtension})|*{ProfileService.PackageExtension}|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var name = _widgetManager.Profiles.Import(dialog.FileName);
            RefreshProfilesList();
            ShowProfileStatus($"Profil « {name} » importé. Clique sur « Appliquer » pour l'utiliser.");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ShowProfileError(ex);
        }
    }

    // ---------- Mise à jour de la fenêtre après un profil ----------

    /// <summary>
    /// Les liaisons (thème global, sélecteur d'émojis, apparences) suivent d'elles-mêmes car le profil est copié
    /// dans les mêmes instances ; on remet à jour ce qui n'est PAS lié : barre de raccourcis, fond de la Switch,
    /// liste des modules affichés, module sélectionné (éditeurs, textes, éléments).
    /// </summary>
    private void RefreshAfterProfileApply(bool includeLayout)
    {
        _refreshingFromProfile = true;
        try
        {
            var config = _widgetManager.Config;

            // Chaque module doit garder des réglages de style prêts (voir le constructeur).
            foreach (var module in ModuleSelector.Items.Cast<ModuleRow>().Where(m => m.Appearance.Theme is null))
                module.Appearance.Theme = _widgetManager.Theme.Clone();

            DockPanelOpacitySlider.Value = config.DockPanelOpacity;
            DockBorderOpacitySlider.Value = config.DockBorderOpacity;
            (config.DockOrientation == "Vertical" ? OrientationVertical : OrientationHorizontal).IsChecked = true;
            (config.DockAnimationDirection switch
            {
                "Down" => DirDown,
                "Left" => DirLeft,
                "Right" => DirRight,
                _ => DirUp
            }).IsChecked = true;
            IconSizeSlider.Value = config.DockIconSize;
            IconSpacingSlider.Value = config.DockIconSpacing;

            var deck = Deck.Settings;
            DeckDimSlider.Value = deck.BackgroundDim;
            DeckClockOpacitySlider.Value = deck.ClockOpacity;
            DeckAppsOpacitySlider.Value = deck.AppsOpacity;
            DeckBackgroundSwatch.Hex = deck.BackgroundColor;
            DeckEditor.Rebuild();

            if (includeLayout)
            {
                WidgetsList.ItemsSource = WidgetManager.AllWidgets
                    .Select(w => new WidgetRow(w.Id, w.Label, Glyphs.GetValueOrDefault(w.Id, ""),
                        _widgetManager.IsWidgetShown(w.Id), w.Id != "shortcuts"))
                    .ToList();
            }

            // Module sélectionné : éditeurs de style, textes, éléments et apparence Switch (instances éventuellement recréées).
            ModuleSelector_SelectionChanged(ModuleSelector, null);
        }
        finally
        {
            _refreshingFromProfile = false;
        }
        ScheduleSave();
    }
}
