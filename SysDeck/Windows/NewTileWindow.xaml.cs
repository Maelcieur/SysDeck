using System.Windows;
using System.Windows.Controls;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck;

/// <summary>Type de tuile à créer, choisi dans <see cref="NewTileWindow"/>.</summary>
public enum NewTileType { InstalledApps, Link, Command, Hotkey, MicMute, OutputMute, Obs, Twitch, Voicemod }

/// <summary>Fenêtre « Nouvelle tuile » : choix du type de tuile à ajouter à la page de la Switch.</summary>
public partial class NewTileWindow : Window
{
    private static readonly (NewTileType Type, string Title, string Description)[] Choices =
    {
        (NewTileType.InstalledApps, "Application installée", "Ajoute des .exe ou des raccourcis (.lnk) choisis dans l'Explorateur."),
        (NewTileType.Link, "Lien ou fichier", "Programme, fichier, dossier, adresse web ou protocole, avec arguments et icône au choix."),
        (NewTileType.Command, "Commande", "Ligne de commande exécutée sans fenêtre : éteindre, redémarrer, veille, verrouiller…"),
        (NewTileType.Hotkey, "Raccourci clavier", "Envoie une combinaison de touches au PC."),
        (NewTileType.MicMute, "Micro muet", "Coupe ou rétablit un micro ; la tuile devient rouge quand il est coupé."),
        (NewTileType.OutputMute, "Son muet", "Coupe ou rétablit le son d'un périphérique de sortie."),
        (NewTileType.Obs, "OBS Studio", "Change de scène, lance le stream ou l'enregistrement, coupe une entrée, masque une source…"),
        (NewTileType.Twitch, "Twitch", "Crée un clip, pose un marqueur, écrit dans le tchat, lance une pub, règle les modes du tchat, change le titre ou la catégorie…"),
        (NewTileType.Voicemod, "Voicemod", "Active le changeur de voix, charge une voix, joue un son du soundboard, coupe le micro ; la tuile s'allume selon l'état.")
    };

    private static readonly Dictionary<NewTileType, string> Icons = new()
    {
        [NewTileType.InstalledApps] = "",
        [NewTileType.Link] = "",
        [NewTileType.Command] = "",
        [NewTileType.Hotkey] = "",
        [NewTileType.MicMute] = "",
        [NewTileType.OutputMute] = "",
        [NewTileType.Twitch] = "",
        [NewTileType.Voicemod] = "",
        [NewTileType.Obs] = ""
    };

    /// <summary>Type choisi ; null si annulé.</summary>
    public NewTileType? Result { get; private set; }

    public NewTileWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        foreach (var (type, title, description) in Choices)
        {
            var text = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Left },
                    new TextBlock { Text = description, FontSize = 11.5, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, Margin = new Thickness(0, 2, 0, 0) }
                }
            };
            Grid.SetColumn(text, 1);
            var icon = new TextBlock
            {
                Text = Icons[type],
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 20,
                Width = 28,
                TextAlignment = TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            var button = new Button
            {
                Tag = type,
                Style = (Style)FindResource("OptionButton"),
                Margin = new Thickness(0, 0, 0, 6),
                Content = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(40) },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    },
                    Children = { icon, text }
                }
            };
            button.Click += Option_Click;
            Options.Children.Add(button);
        }
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NewTileType type }) return;
        Result = type;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
