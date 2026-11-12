using System.IO;
using System.Windows;
using Microsoft.Win32;
using SysDeck.ConsoleMode;

namespace SysDeck;

/// <summary>
/// Modification d'un émulateur du mode console : réglages simples (nom, programme, console, plein écran, cœur)
/// et ligne de commande dans « Avancé ».
/// </summary>
public partial class ConsoleEmulatorEditorWindow : Window
{
    private readonly ConsoleEmulator _target;

    /// <param name="emulator">Émulateur modifié sur place après « Enregistrer ».</param>
    public ConsoleEmulatorEditorWindow(Window owner, ConsoleEmulator emulator)
    {
        InitializeComponent();
        Owner = owner;
        _target = emulator;
        EmulatorPresets.EnsureFullscreenFlag(emulator);
        NameBox.Text = emulator.Name;
        ExeBox.Text = emulator.ExePath;
        PlatformBox.Text = emulator.Platform;
        CoreBox.Text = emulator.Core;
        FlagBox.Text = emulator.FullscreenFlag ?? "";
        // Le modèle est affiché SANS l'option plein écran : c'est la case qui la gère.
        ArgsBox.Text = EmulatorCommandBuilder.WithFullscreen(new ConsoleEmulator
        {
            ArgumentsTemplate = emulator.ArgumentsTemplate, FullscreenFlag = emulator.FullscreenFlag, Fullscreen = false,
        });
        FullscreenCheck.IsChecked = emulator.Fullscreen;
        CorePanel.Visibility = emulator.Name.Contains("RetroArch", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(emulator.Core)
            ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(emulator.FullscreenHint))
        {
            HintText.Text = emulator.FullscreenHint;
            HintText.Visibility = Visibility.Visible;
        }
        FlagBox.TextChanged += (_, _) => UpdateFullscreenInfo();
        UpdateFullscreenInfo();
    }

    private void UpdateFullscreenInfo()
    {
        var known = !string.IsNullOrWhiteSpace(FlagBox.Text);
        FullscreenCheck.IsEnabled = known;
        FullscreenInfo.Text = known
            ? "Le jeu s'ouvre directement en plein écran sur la TV."
            : "SysDeck ne sait pas demander le plein écran à cet émulateur : active-le dans ses propres options (souvent Graphismes ou Affichage).";
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir le programme de l'émulateur",
            Filter = "Programmes (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        ExeBox.Text = dialog.FileName;
        // Dérivé d'Eden / yuzu reconnu : option plein écran connue.
        if (string.IsNullOrWhiteSpace(FlagBox.Text) && EmulatorPresets.IsYuzuFamily(dialog.FileName)) FlagBox.Text = "-f";
    }

    private void BrowseCore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choisir le cœur RetroArch", Filter = "Cœurs (*.dll)|*.dll|Tous les fichiers (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) CoreBox.Text = dialog.FileName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { Fail("Le nom est obligatoire."); return; }
        if (string.IsNullOrWhiteSpace(ExeBox.Text)) { Fail("Choisis le programme de l'émulateur."); return; }
        if (string.IsNullOrWhiteSpace(ArgsBox.Text)) { Fail("La ligne de commande (Avancé) est vide : mets au moins {rom}."); return; }

        var previousDir = Path.GetDirectoryName(_target.ExePath) ?? "";
        _target.Name = NameBox.Text.Trim();
        _target.ExePath = ExeBox.Text.Trim();
        _target.ArgumentsTemplate = ArgsBox.Text.Trim();
        _target.FullscreenFlag = FlagBox.Text.Trim();
        _target.Fullscreen = FullscreenCheck.IsChecked == true;
        _target.Platform = PlatformBox.Text.Trim();
        _target.Core = CoreBox.Text.Trim();
        // Dossier de travail : suit le programme tant qu'il n'a pas été personnalisé.
        if (string.IsNullOrWhiteSpace(_target.WorkingDir) || string.Equals(_target.WorkingDir, previousDir, StringComparison.OrdinalIgnoreCase))
            _target.WorkingDir = Path.GetDirectoryName(_target.ExePath) ?? "";
        DialogResult = true;
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
