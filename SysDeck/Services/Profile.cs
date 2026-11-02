namespace SysDeck.Services;

/// <summary>Erreur de profil dont le message, en français, est affichable tel quel à l'utilisateur.</summary>
public sealed class ProfileException : Exception
{
    public ProfileException(string message) : base(message) { }
    public ProfileException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Ligne de la liste des profils enregistrés.</summary>
public sealed record ProfileInfo(string Name, DateTime SavedAtUtc);

/// <summary>
/// Ce que ProfileService attend de l'application : lire la personnalisation courante et l'appliquer.
/// WidgetManager l'implémente ; les tests utilisent un faux hôte, sans interface.
/// </summary>
public interface IProfileHost
{
    Profile CaptureProfile();
    void ApplyProfile(Profile profile, bool includeLayout);
}

/// <summary>Réglages de la barre de raccourcis qui font partie d'un profil.</summary>
public sealed class ProfileDock
{
    public double PanelOpacity { get; set; } = 0.88;
    public double BorderOpacity { get; set; } = 0.25;
    public string Orientation { get; set; } = "Horizontal";
    public string AnimationDirection { get; set; } = "Up";
    public double IconSize { get; set; } = 50;
    public double IconSpacing { get; set; } = 6;
}

/// <summary>Style de l'écran de la Switch (fond et horloge) qui fait partie d'un profil ; l'image est une référence comme celles des modules.</summary>
public sealed class ProfileDeckScreen
{
    public string BackgroundColor { get; set; } = "#0B0C10";
    public string? BackgroundImage { get; set; }
    public double BackgroundDim { get; set; } = 0.35;
    public double ClockOpacity { get; set; } = 1;
    public double AppsOpacity { get; set; } = 1;
}

/// <summary>Accès à une image de fond référencée par un profil (module du bureau, de la Switch ou fond de l'écran Switch).</summary>
internal sealed class ImageSlot
{
    private readonly Func<string?> _get;
    private readonly Action<string?> _set;
    public ImageSlot(Func<string?> get, Action<string?> set) { _get = get; _set = set; }
    public string? Value { get => _get(); set => _set(value); }
}

/// <summary>
/// Instantané nommé de la personnalisation : thème global, apparences des modules (bureau et Switch),
/// barre de raccourcis, sélecteur d'émojis et disposition. Ne contient jamais la clé Gemini, le mot de passe OBS, les jetons Twitch, les
/// raccourcis clavier, l'historique d'énergie ni le prix de l'électricité.
/// </summary>
public sealed class Profile
{
    public const int CurrentFormat = 1;

    public int FormatVersion { get; set; } = CurrentFormat;
    public string Name { get; set; } = "";
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    public ThemeSettings Theme { get; set; } = new();

    /// <summary>Apparences des modules du bureau, par identifiant.</summary>
    public Dictionary<string, ModuleAppearance> Modules { get; set; } = new();

    /// <summary>Apparences propres à la Switch (Deck.Modules), par identifiant.</summary>
    public Dictionary<string, ModuleAppearance> DeckModules { get; set; } = new();

    /// <summary>Fond et horloge de l'écran de la Switch ; null dans un profil ancien : l'écran n'est alors pas modifié à l'application.</summary>
    public ProfileDeckScreen? DeckScreen { get; set; } = new();

    public ProfileDock Dock { get; set; } = new();
    public EmojiPickerSettings EmojiPicker { get; set; } = new();

    /// <summary>Modules affichés et positions ; toujours enregistrée, appliquée seulement à la demande.</summary>
    public List<WidgetPosition> Layout { get; set; } = new();

    /// <summary>Photographie la configuration courante (copie profonde : le profil ne partage aucune instance).</summary>
    public static Profile Capture(AppConfig config) => new()
    {
        SavedAtUtc = DateTime.UtcNow,
        Theme = SettingsCopier.Clone(config.Theme ?? new ThemeSettings()),
        Modules = SettingsCopier.Clone(config.Modules),
        DeckModules = SettingsCopier.Clone(config.Deck.Modules),
        DeckScreen = new ProfileDeckScreen
        {
            BackgroundColor = config.Deck.BackgroundColor,
            BackgroundImage = config.Deck.BackgroundImage,
            BackgroundDim = config.Deck.BackgroundDim,
            ClockOpacity = config.Deck.ClockOpacity,
            AppsOpacity = config.Deck.AppsOpacity
        },
        Dock = new ProfileDock
        {
            PanelOpacity = config.DockPanelOpacity,
            BorderOpacity = config.DockBorderOpacity,
            Orientation = config.DockOrientation,
            AnimationDirection = config.DockAnimationDirection,
            IconSize = config.DockIconSize,
            IconSpacing = config.DockIconSpacing
        },
        EmojiPicker = SettingsCopier.Clone(config.EmojiPicker),
        Layout = SettingsCopier.Clone(config.Layout)
    };

    /// <summary>
    /// Remplace les valeurs de <paramref name="config"/> par celles du profil, SUR PLACE : les instances
    /// de ThemeSettings / ModuleAppearance / EmojiPickerSettings existantes sont conservées (leurs
    /// abonnés, fenêtres et Paramètres, sont ainsi prévenus). N'affiche rien : c'est WidgetManager qui réapplique.
    /// </summary>
    public void ApplyTo(AppConfig config, bool includeLayout)
    {
        Normalize();

        config.Theme ??= new ThemeSettings();
        SettingsCopier.CopyInto(Theme, config.Theme);

        // Bureau : un module absent du profil n'avait pas de réglage propre, il revient aux valeurs par défaut
        // (sur l'instance existante : la supprimer couperait ses abonnés).
        foreach (var (id, existing) in config.Modules)
            if (!Modules.ContainsKey(id))
                SettingsCopier.CopyInto(new ModuleAppearance(), existing);
        MergeModules(Modules, config.Modules);

        // Switch : une apparence absente est recréée à la demande à partir de celle du bureau, on la retire.
        foreach (var id in config.Deck.Modules.Keys.Where(k => !DeckModules.ContainsKey(k)).ToList())
            config.Deck.Modules.Remove(id);
        MergeModules(DeckModules, config.Deck.Modules);

        if (DeckScreen is { } screen)
        {
            config.Deck.BackgroundColor = string.IsNullOrWhiteSpace(screen.BackgroundColor) ? "#0B0C10" : screen.BackgroundColor;
            config.Deck.BackgroundImage = screen.BackgroundImage;
            config.Deck.BackgroundDim = Math.Clamp(screen.BackgroundDim, 0, 1);
            config.Deck.ClockOpacity = Math.Clamp(screen.ClockOpacity, 0, 1);
            config.Deck.AppsOpacity = double.IsNaN(screen.AppsOpacity) ? 1 : Math.Clamp(screen.AppsOpacity, 0, 1);
        }

        // Valeurs bornées : un profil importé peut avoir été modifié à la main.
        config.DockPanelOpacity = Math.Clamp(Dock.PanelOpacity, 0, 1);
        config.DockBorderOpacity = Math.Clamp(Dock.BorderOpacity, 0, 1);
        config.DockOrientation = Dock.Orientation is "Vertical" ? "Vertical" : "Horizontal";
        config.DockAnimationDirection = Dock.AnimationDirection is "Up" or "Down" or "Left" or "Right" ? Dock.AnimationDirection : "Up";
        config.DockIconSize = Math.Clamp(Dock.IconSize, 16, 160);
        config.DockIconSpacing = Math.Clamp(Dock.IconSpacing, 0, 60);

        SettingsCopier.CopyInto(EmojiPicker, config.EmojiPicker);

        if (includeLayout)
        {
            config.Layout.Clear();
            config.Layout.AddRange(SettingsCopier.Clone(Layout));
        }
    }

    private static void MergeModules(Dictionary<string, ModuleAppearance> source, Dictionary<string, ModuleAppearance> target)
    {
        foreach (var (id, appearance) in source)
        {
            if (target.TryGetValue(id, out var existing))
                SettingsCopier.CopyInto(appearance, existing);
            else
                target[id] = SettingsCopier.Clone(appearance);
        }
    }

    /// <summary>Remplace les valeurs absentes d'un fichier édité à la main ou d'une version plus ancienne.</summary>
    public void Normalize()
    {
        Theme ??= new ThemeSettings();
        Modules ??= new();
        DeckModules ??= new();
        Dock ??= new ProfileDock();
        EmojiPicker ??= new EmojiPickerSettings();
        Layout ??= new();
        Name ??= "";
        foreach (var key in Modules.Where(m => m.Value is null).Select(m => m.Key).ToList()) Modules.Remove(key);
        foreach (var key in DeckModules.Where(m => m.Value is null).Select(m => m.Key).ToList()) DeckModules.Remove(key);
        Layout.RemoveAll(w => w is null);
        foreach (var appearance in AllModuleAppearances())
        {
            appearance.Texts ??= new();
            appearance.Elements ??= new();
            appearance.Buttons ??= new();
        }
    }

    /// <summary>Toutes les apparences de module (bureau et Switch) du profil.</summary>
    internal IEnumerable<ModuleAppearance> AllModuleAppearances() => Modules.Values.Concat(DeckModules.Values);

    /// <summary>Toutes les images de fond du profil : modules (bureau et Switch) et fond de l'écran de la Switch.</summary>
    internal IEnumerable<ImageSlot> AllImageSlots()
    {
        foreach (var appearance in AllModuleAppearances())
        {
            var a = appearance;
            yield return new ImageSlot(() => a.BackgroundImage, v => a.BackgroundImage = v);
        }
        if (DeckScreen is { } screen)
            yield return new ImageSlot(() => screen.BackgroundImage, v => screen.BackgroundImage = v);
    }
}
