using System.Windows;
using System.Windows.Controls;
using SysDeck.Deck;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck;

/// <summary>Page Switch, onglet Thèmes : atelier de création (en direct, puis retour à l'état d'avant), thèmes enregistrés.</summary>
public partial class SettingsWindow
{
    private List<SwitchTheme> _switchThemes = new();

    private void InitSwitchThemes()
    {
        _switchThemes = SwitchThemeStore.Load();
        Deck.StudioChanged += OnStudioChanged;
        Closed += (_, _) => Deck.StudioChanged -= OnStudioChanged;
        OnStudioChanged();
    }

    private void OnStudioChanged()
    {
        var active = Deck.Studio.Active;
        StudioBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StudioNewButton.IsEnabled = !active;
        if (active) StudioNameBox.Text = Deck.Studio.Name;
        // Pages et apparences remplacées : les réglages affichés suivent.
        ModuleSelector_SelectionChanged(ModuleSelector, null);
        RefreshSwitchThemes();
    }

    private void RefreshSwitchThemes()
    {
        SwitchThemesList.Children.Clear();
        SwitchThemesEmpty.Visibility = _switchThemes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var theme in _switchThemes)
        {
            var apply = new Button { Content = "Appliquer", Style = (Style)FindResource("PrimaryButton"), IsEnabled = !Deck.Studio.Active,
                ToolTip = "Applique ce thème à la page affichée de la Switch (ses tuiles sont gardées)" };
            apply.Click += (_, _) => ApplySwitchTheme(theme);
            var edit = new Button { Content = "Modifier", Margin = new Thickness(8, 0, 0, 0), IsEnabled = !Deck.Studio.Active,
                ToolTip = "Ouvre ce thème dans l'atelier" };
            edit.Click += (_, _) => StartStudio(theme.Name, theme);
            var delete = new Button { Content = "Supprimer", Margin = new Thickness(8, 0, 0, 0) };
            delete.Click += (_, _) => DeleteSwitchTheme(theme);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { apply, edit, delete } };
            DockPanel.SetDock(buttons, Dock.Right);
            var widgets = theme.Elements.Count(e => e.Visible);
            var info = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = theme.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("UiText") },
                    new TextBlock
                    {
                        Text = $"{widgets} élément{(widgets > 1 ? "s" : "")} · créé le {theme.Created:dd/MM/yyyy à HH:mm}",
                        FontSize = 11.5, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0)
                    }
                }
            };
            var swatch = new Border
            {
                Width = 44, Height = 26, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(Services.ThemeService.Parse(theme.Look.BackgroundColor, System.Windows.Media.Colors.Black)),
                BorderBrush = (System.Windows.Media.Brush)FindResource("Muted"), BorderThickness = new Thickness(1)
            };
            DockPanel.SetDock(swatch, Dock.Left);
            SwitchThemesList.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8),
                Child = new DockPanel { Children = { buttons, swatch, info } }
            });
        }
    }

    private void StudioNew_Click(object sender, RoutedEventArgs e)
        => StartStudio($"Thème {_switchThemes.Count + 1}", null);

    private void StartStudio(string name, SwitchTheme? editing)
    {
        Deck.BeginStudio(name, editing);
        ScheduleSave();
        SwitchViewScreen.IsChecked = true; // on place les widgets dans l'aperçu
    }

    private void StudioSave_Click(object sender, RoutedEventArgs e)
    {
        var name = StudioNameBox.Text.Trim();
        if (name.Length == 0) name = Deck.Studio.Name;
        var theme = Deck.CaptureTheme(name);
        if (Deck.Studio.Editing is { } editing && _switchThemes.IndexOf(editing) is >= 0 and var index)
        {
            theme.Created = editing.Created;
            _switchThemes[index] = theme;
        }
        else
        {
            // Même nom qu'un thème existant : il est remplacé.
            var same = _switchThemes.FindIndex(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (same >= 0) _switchThemes[same] = theme;
            else _switchThemes.Add(theme);
        }
        if (!SaveSwitchThemes()) return;
        Deck.EndStudio();
        ScheduleSave();
        SwitchViewThemes.IsChecked = true;
    }

    private void StudioCancel_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Quitter l'atelier sans enregistrer ? La Switch retrouve son thème d'avant.", "Atelier de thème",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Deck.EndStudio();
        ScheduleSave();
        SwitchViewThemes.IsChecked = true;
    }

    /// <summary>Ouvre Personnaliser (cible Switch) sur le widget sélectionné dans l'aperçu.</summary>
    private void StudioCustomize_Click(object sender, RoutedEventArgs e)
    {
        var id = DeckEditor.SelectedId;
        var row = ModuleSelector.Items.OfType<ModuleRow>().FirstOrDefault(m => m.Id == id);
        if (row is null)
        {
            MessageBox.Show(this, "Clique d'abord sur un widget dans l'aperçu de l'onglet Écran.", "Atelier de thème",
                MessageBoxButton.OK, MessageBoxImage.Information);
            SwitchViewScreen.IsChecked = true;
            return;
        }
        TargetSwitch.IsChecked = true;
        ModuleSelector.SelectedItem = row;
        NavigateTo(NavModules, customize: true);
    }

    private void ApplySwitchTheme(SwitchTheme theme)
    {
        if (MessageBox.Show(this, $"Appliquer « {theme.Name} » à la page « {Deck.Settings.Page.Name} » ? La position des widgets, le fond et leurs couleurs sont remplacés (les tuiles sont gardées).",
                "Thèmes Switch", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Deck.ApplyTheme(theme);
        ModuleSelector_SelectionChanged(ModuleSelector, null);
        ScheduleSave();
    }

    private void DeleteSwitchTheme(SwitchTheme theme)
    {
        if (MessageBox.Show(this, $"Supprimer le thème « {theme.Name} » ?", "Thèmes Switch",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _switchThemes.Remove(theme);
        SaveSwitchThemes();
        RefreshSwitchThemes();
    }

    private bool SaveSwitchThemes()
    {
        try
        {
            SwitchThemeStore.Save(_switchThemes);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Enregistrement des thèmes impossible : {ex.Message}", "Thèmes Switch", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
