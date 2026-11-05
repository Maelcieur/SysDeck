using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SysDeck.Services;

namespace SysDeck;

// Page Thème, apparence des modules (application groupée) et réinitialisation.
public partial class SettingsWindow
{
    // ---------- Application groupée ----------
    // Un champ de couleur lié en UpdateSourceTrigger=PropertyChanged, un curseur ou CopyTheme (une
    // cinquantaine de propriétés d'un coup) déclenchent une rafale de PropertyChanged. Appliquer le
    // thème à chaque notification reconstruirait tous les modules pour rien : on regroupe en une seule
    // application, ~60 ms après la dernière notification (imperceptible pour l'aperçu en direct).

    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _applyTimerWired;
    private bool _globalThemePending;
    private readonly HashSet<string> _desktopPending = new();
    private readonly HashSet<string> _switchPending = new();

    private void QueueApply()
    {
        if (!_applyTimerWired)
        {
            _applyTimer.Tick += (_, _) => FlushApply();
            _applyTimerWired = true;
        }
        _applyTimer.Stop(); // redémarre le délai : seule la dernière notification de la rafale compte
        _applyTimer.Start();
    }

    /// <summary>Applique tout de suite ce qui est en attente (utilisé aussi à la fermeture de la fenêtre).</summary>
    private void FlushApply()
    {
        _applyTimer.Stop();

        if (_globalThemePending)
        {
            _globalThemePending = false;
            _widgetManager.ApplyGlobalTheme();
        }

        var desktop = _desktopPending.ToList();
        var deck = _switchPending.ToList();
        _desktopPending.Clear();
        _switchPending.Clear();
        foreach (var id in desktop) _widgetManager.ApplyModuleAppearance(id);
        foreach (var id in deck) _widgetManager.ApplyDeckAppearance(id);

        var previewId = SelectedModule?.Id;
        if (previewId is not null
            && (_customizeSwitch ? deck.Contains(previewId) : desktop.Contains(previewId)))
            RefreshPreviewElements();
    }

    private void OnGlobalThemeChanged(object? sender, PropertyChangedEventArgs e)
    {
        _globalThemePending = true;
        QueueApply();
        ScheduleSave();
    }

    private void OnModuleChanged(string id, ModuleAppearance appearance, string? property)
    {
        var onSwitch = IsSwitchAppearance(id, appearance);
        switch (property)
        {
            // Premier groupe personnalisé : on part d'une copie du thème global.
            case nameof(ModuleAppearance.UseCustomTheme) or nameof(ModuleAppearance.CustomCard)
                or nameof(ModuleAppearance.CustomGauges) or nameof(ModuleAppearance.CustomAnimations)
                when appearance.Theme is null:
                appearance.Theme = _widgetManager.Theme.Clone();
                break;

            // Passage en taille manuelle : on part de la taille actuelle de la carte, pas d'une valeur arbitraire.
            case nameof(ModuleAppearance.AutoSize) when !appearance.AutoSize && !onSwitch:
                if (_widgetManager.GetModuleCardSize(id) is { Width: > 0 } size)
                {
                    appearance.Width = Math.Round(size.Width);
                    appearance.Height = Math.Round(size.Height);
                }
                break;
        }

        // Un interrupteur de groupe a changé : l'éditeur passe du thème global (grisé) au style du module, ou l'inverse.
        if (property is nameof(ModuleAppearance.UseCustomTheme) or nameof(ModuleAppearance.CustomCard)
            or nameof(ModuleAppearance.CustomGauges) or nameof(ModuleAppearance.CustomAnimations)
            or nameof(ModuleAppearance.Theme))
            RefreshStyleEditors();

        // Disques : anneaux ou lignes, les réglages de jauge utiles changent.
        if (property == nameof(ModuleAppearance.DiskStyle) && ReferenceEquals(appearance, CurrentAppearance))
            UpdateCustomizeOptions();

        // Taille automatique ou manuelle : « Adaptation du contenu » apparaît ou disparaît (bureau).
        if (property == nameof(ModuleAppearance.AutoSize)) UpdateFitCard();

        (onSwitch ? _switchPending : _desktopPending).Add(id);
        QueueApply();
        ScheduleSave();
    }

    // ---------- Sous-onglets du thème global ----------

    private void GlobalThemeTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || GlobalAnimationsEditor is null) return;

        static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        GlobalThemeEditor.Visibility = Vis(sender == GlobalTabColorsButton);
        GlobalCardEditor.Visibility = Vis(sender == GlobalTabCardButton);
        GlobalGaugesEditor.Visibility = Vis(sender == GlobalTabGaugesButton);
        GlobalAnimationsEditor.Visibility = Vis(sender == GlobalTabAnimationsButton);
    }

    /// <summary>Lien « Modifier le thème global » de l'onglet Style : ouvre la page Thème (onglet Couleurs et polices, ou celui du Tag).</summary>
    private void EditGlobalTheme_Click(object sender, RoutedEventArgs e)
    {
        var section = (sender as FrameworkElement)?.Tag as string;

        NavTheme.IsChecked = true;
        (section switch
        {
            "Card" => GlobalTabCardButton,
            "Gauges" => GlobalTabGaugesButton,
            "Animations" => GlobalTabAnimationsButton,
            _ => GlobalTabColorsButton
        }).IsChecked = true;
    }

    private void ResetModuleTheme_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAppearance is not { } appearance) return;

        // Copie du thème global, puis les 4 interrupteurs « Propre à ce module » éteints : le module suit le thème global.
        appearance.Theme = _widgetManager.Theme.Clone();
        appearance.UseCustomTheme = appearance.CustomCard = appearance.CustomGauges = appearance.CustomAnimations = false;
        RefreshStyleEditors();
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        // Confirmation en deux clics, sans boîte de dialogue.
        if (!_confirmReset)
        {
            _confirmReset = true;
            ResetAppearanceButton.Content = "Cliquer à nouveau pour confirmer";
            return;
        }

        _confirmReset = false;
        ResetAppearanceButton.Content = "Réinitialiser toute l'apparence";

        CopyTheme(new ThemeSettings(), _widgetManager.Theme);
        foreach (var row in ModuleSelector.Items.Cast<ModuleRow>())
        {
            var a = row.Appearance;
            var defaults = new ModuleAppearance();
            var oldImage = a.BackgroundImage;
            a.BackgroundImage = null;
            DeleteBackgroundIfUnused(oldImage); // peut encore servir à la Switch
            a.UseCustomTheme = a.CustomCard = a.CustomGauges = a.CustomAnimations = false;
            a.Theme = null;
            a.AutoSize = true;
            a.Width = defaults.Width;
            a.Height = defaults.Height;
            a.StretchMode = defaults.StretchMode;
            a.BackgroundImage = null;
            a.BackgroundImageOpacity = defaults.BackgroundImageOpacity;
            a.BackgroundDim = defaults.BackgroundDim;
            a.ResetTexts();
            ResetButtonsOf(a);
            a.FontScale = 1;
            a.ResetLayout();
        }
        BuildTextRows();
        BuildButtonRows();
    }

    /// <summary>Copie propriété par propriété, pour garder les liaisons et abonnements sur l'objet cible.</summary>
    private static void CopyTheme(ThemeSettings from, ThemeSettings to)
    {
        foreach (var property in typeof(ThemeSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            property.SetValue(to, property.GetValue(from));
    }

}
