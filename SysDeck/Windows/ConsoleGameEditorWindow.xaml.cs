using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SysDeck.ConsoleMode;
using SysDeck.Infrastructure;

namespace SysDeck;

/// <summary>Chargement des images du cache console (sans verrouiller le fichier : il peut être remplacé par un nouveau téléchargement).</summary>
internal static class ConsoleImages
{
    public static BitmapImage? Load(string? path, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("console-image:" + path, "Image du mode console illisible", ex);
            return null;
        }
    }
}

/// <summary>Ajout ou modification d'un jeu du mode console : lancement (exécutable, lien, émulateur), textes et images.</summary>
public partial class ConsoleGameEditorWindow : Window
{
    private const string ImageFilter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Tous les fichiers (*.*)|*.*";

    private sealed record CandidateRow(ArtworkCandidate Candidate)
    {
        public string Label => $"{Candidate.Title}  ·  {(Candidate.Source == ArtworkSource.Steam ? "Steam" : "SteamGridDB")}";
    }

    private readonly ConsoleLibrary _library;
    private readonly ConsoleGame _target;
    private readonly bool _isNew;
    private readonly string _gridKey;
    private string _cover, _hero, _logo;
    private string? _steamAppId;
    private CancellationTokenSource? _cts;

    public ConsoleGameEditorWindow(Window owner, ConsoleLibrary library, ConsoleGame? game, string steamGridDbKey)
    {
        InitializeComponent();
        Owner = owner;
        _library = library;
        _isNew = game is null;
        _target = game ?? new ConsoleGame();
        _gridKey = steamGridDbKey;
        TitleText.Text = _isNew ? "Ajouter un jeu" : "Modifier le jeu";

        TitleBox.Text = _target.Title;
        ExeBox.Text = _target.ExePath;
        ArgsBox.Text = _target.Arguments;
        DirBox.Text = _target.WorkingDir;
        UriBox.Text = _target.LaunchUri;
        RomBox.Text = _target.RomPath;
        CoreBox.Text = _target.Core;
        PlatformBox.Text = _target.Platform;
        DescriptionBox.Text = _target.Description;
        _cover = _target.CoverPath;
        _hero = _target.HeroPath;
        _logo = _target.LogoPath;
        _steamAppId = _target.SteamAppId;

        EmulatorBox.ItemsSource = library.Emulators.ToList();
        EmulatorBox.SelectedItem = library.FindEmulator(_target.EmulatorId);
        TypeBox.SelectedIndex = _target.UsesEmulator ? 2 : _target.UsesUri ? 1 : 0;
        ShowImages();
        Closed += (_, _) => _cts?.Cancel();
    }

    // ---------- Type de lancement ----------

    private int SelectedType => Math.Max(0, TypeBox.SelectedIndex);

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ExePanel is null) return;
        ExePanel.Visibility = SelectedType == 0 ? Visibility.Visible : Visibility.Collapsed;
        UriPanel.Visibility = SelectedType == 1 ? Visibility.Visible : Visibility.Collapsed;
        EmuPanel.Visibility = SelectedType == 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmulatorHint();
    }

    private void Emulator_Changed(object sender, SelectionChangedEventArgs e) => UpdateEmulatorHint();

    private void UpdateEmulatorHint()
    {
        if (EmulatorHint is null) return;
        if (_library.Emulators.Count == 0)
        {
            EmulatorHint.Text = "Aucun émulateur n'est configuré : ajoute-en un dans la section « Émulateurs » des Paramètres.";
            return;
        }
        var emulator = EmulatorBox.SelectedItem as ConsoleEmulator;
        EmulatorHint.Text = emulator is null ? ""
            : string.Join(" ", new[] { string.IsNullOrWhiteSpace(emulator.Platform) ? "" : $"Plateforme : {emulator.Platform}.", emulator.FullscreenHint }
                .Where(s => s.Length > 0));
    }

    private static string? PickFile(Window owner, string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        if (PickFile(this, "Choisir le programme du jeu", "Programmes (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|Tous les fichiers (*.*)|*.*") is not { } path) return;
        ExeBox.Text = path;
        if (string.IsNullOrWhiteSpace(DirBox.Text)) DirBox.Text = Path.GetDirectoryName(path) ?? "";
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) TitleBox.Text = Path.GetFileNameWithoutExtension(path);
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choisir le dossier de travail" };
        if (dialog.ShowDialog(this) == true) DirBox.Text = dialog.FolderName;
    }

    private void BrowseRom_Click(object sender, RoutedEventArgs e)
    {
        if (PickFile(this, "Choisir le fichier du jeu", "Tous les fichiers (*.*)|*.*") is not { } path) return;
        RomBox.Text = path;
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) TitleBox.Text = Path.GetFileNameWithoutExtension(path);
    }

    // ---------- Images ----------

    private void ShowImages()
    {
        CoverImage.Source = ConsoleImages.Load(_cover, 120);
        HeroImage.Source = ConsoleImages.Load(_hero, 160);
        LogoImage.Source = ConsoleImages.Load(_logo, 160);
        CoverPathText.Text = _cover;
        HeroPathText.Text = _hero;
        LogoPathText.Text = _logo;
    }

    private string SlotName(string slot) => _target.Id + "-" + slot;

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string slot }) return;
        if (PickFile(this, "Choisir une image", ImageFilter) is not { } source) return;
        try
        {
            // Copie dans le cache : l'image reste disponible même si le fichier d'origine est déplacé.
            Directory.CreateDirectory(ConsoleLibrary.ArtDir);
            var name = SlotName(slot);
            var destination = Path.Combine(ConsoleLibrary.ArtDir, name + Path.GetExtension(source).ToLowerInvariant());
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var old in Directory.GetFiles(ConsoleLibrary.ArtDir, name + ".*"))
                {
                    try { File.Delete(old); } catch (IOException) { }
                }
                File.Copy(source, destination, true);
            }
            SetImage(slot, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Copie de l'image du jeu impossible", ex);
            ShowError($"Impossible d'utiliser cette image :\n{ex.Message}");
        }
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string slot }) SetImage(slot, "");
    }

    private void SetImage(string slot, string path)
    {
        switch (slot)
        {
            case "cover": _cover = path; break;
            case "hero": _hero = path; break;
            default: _logo = path; break;
        }
        ShowImages();
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ShowError("Saisis d'abord le titre du jeu.");
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        var emulator = SelectedType == 2;
        var art = new ArtworkService(_gridKey);
        if (emulator && !art.HasSteamGridDb)
        {
            SearchStatus.Text = "Les jeux d'émulateur se cherchent sur SteamGridDB : saisis une clé dans les Paramètres (section Jaquettes).";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SearchButton.IsEnabled = false;
        SearchStatus.Text = "Recherche en cours…";
        try
        {
            var results = await art.SearchAsync(title, includeSteamGridDb: art.HasSteamGridDb, gridOnly: emulator, _cts.Token);
            CandidatesList.ItemsSource = results.Select(c => new CandidateRow(c)).ToList();
            CandidatesPanel.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (results.Count > 0) CandidatesList.SelectedIndex = 0;
            SearchStatus.Text = results.Count > 0
                ? "Choisis le bon résultat (double-clic pour télécharger)."
                : "Aucun résultat (ou réseau indisponible). Essaie un autre titre, ou choisis les images à la main.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private void Candidates_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CandidatesList.SelectedItem is not null) UseCandidate_Click(sender, e);
    }

    private async void UseCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesList.SelectedItem is not CandidateRow row) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        UseCandidateButton.IsEnabled = SearchButton.IsEnabled = false;
        SearchStatus.Text = "Téléchargement des images…";
        try
        {
            var result = await new ArtworkService(_gridKey).DownloadAsync(row.Candidate, _target.Id, token);
            if (!string.IsNullOrEmpty(result.CoverPath)) _cover = result.CoverPath;
            if (!string.IsNullOrEmpty(result.HeroPath)) _hero = result.HeroPath;
            if (!string.IsNullOrEmpty(result.LogoPath)) _logo = result.LogoPath;
            if (!string.IsNullOrEmpty(result.SteamAppId)) _steamAppId = result.SteamAppId;
            if (string.IsNullOrWhiteSpace(DescriptionBox.Text) && !string.IsNullOrWhiteSpace(result.Description))
                DescriptionBox.Text = result.Description;
            ShowImages();
            var got = new[] { result.CoverPath, result.HeroPath, result.LogoPath }.Count(p => !string.IsNullOrEmpty(p));
            SearchStatus.Text = got == 0
                ? "Aucune image n'a pu être téléchargée pour ce résultat."
                : $"{got} image(s) récupérée(s){(string.IsNullOrWhiteSpace(result.Description) ? "" : ", description ajoutée si elle était vide")}.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            UseCandidateButton.IsEnabled = SearchButton.IsEnabled = true;
        }
    }

    // ---------- Enregistrement ----------

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { ShowError("Le titre est obligatoire."); return; }

        var type = SelectedType;
        var emulator = EmulatorBox.SelectedItem as ConsoleEmulator;
        if (type == 0 && string.IsNullOrWhiteSpace(ExeBox.Text)) { ShowError("Choisis le programme à lancer."); return; }
        if (type == 1 && string.IsNullOrWhiteSpace(UriBox.Text)) { ShowError("Saisis l'adresse à ouvrir (par exemple steam://rungameid/620)."); return; }
        if (type == 1 && !Uri.TryCreate(UriBox.Text.Trim(), UriKind.Absolute, out _)) { ShowError("Cette adresse n'est pas valide."); return; }
        if (type == 2 && emulator is null) { ShowError("Choisis un émulateur (ajoute-le d'abord dans la section « Émulateurs » si la liste est vide)."); return; }
        if (type == 2 && string.IsNullOrWhiteSpace(RomBox.Text)) { ShowError("Choisis le fichier du jeu."); return; }

        _target.Title = title;
        _target.Description = DescriptionBox.Text.Trim();
        _target.Platform = PlatformBox.Text.Trim();
        _target.ExePath = type == 0 ? ExeBox.Text.Trim() : "";
        _target.Arguments = type == 0 ? ArgsBox.Text.Trim() : "";
        _target.WorkingDir = type == 0 ? DirBox.Text.Trim() : "";
        _target.LaunchUri = type == 1 ? UriBox.Text.Trim() : "";
        _target.EmulatorId = type == 2 ? emulator!.Id : null;
        _target.RomPath = type == 2 ? RomBox.Text.Trim() : "";
        _target.Core = type == 2 ? CoreBox.Text.Trim() : "";
        _target.CoverPath = _cover;
        _target.HeroPath = _hero;
        _target.LogoPath = _logo;
        _target.SteamAppId = _steamAppId;
        DialogResult = true;
    }

    /// <summary>Le jeu édité (nouveau, ou l'objet de la bibliothèque modifié sur place) ; valide après « Enregistrer ».</summary>
    public ConsoleGame Game => _target;

    public bool IsNew => _isNew;
}
