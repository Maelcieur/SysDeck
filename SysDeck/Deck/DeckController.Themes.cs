namespace SysDeck.Deck;

/// <summary>Thèmes de la Switch : atelier de création (en direct sur la Switch, puis retour à l'état d'avant) et application.</summary>
public sealed partial class DeckController
{
    /// <summary>Atelier en cours : la Switch montre le thème en création ; tout sera remis comme avant à la fin.</summary>
    public SwitchThemeStudio Studio { get; } = new();

    /// <summary>L'atelier a commencé ou s'est terminé.</summary>
    public event Action? StudioChanged;

    /// <summary>Ouvre l'atelier ; <paramref name="editing"/> : thème enregistré à modifier (affiché d'emblée).</summary>
    public void BeginStudio(string name, SwitchTheme? editing = null)
    {
        if (Studio.Active) return;
        Studio.Begin(_settings, name, editing);
        ReloadAll();
        StudioChanged?.Invoke();
    }

    /// <summary>Thème tel qu'il est dans l'atelier (page affichée).</summary>
    public SwitchTheme CaptureTheme(string name) => SwitchTheme.Capture(_settings, name);

    /// <summary>Ferme l'atelier : pages, positions et apparences d'avant reviennent sur la Switch.</summary>
    public void EndStudio()
    {
        if (!Studio.Active) return;
        Studio.End(_settings);
        ReloadAll();
        StudioChanged?.Invoke();
    }

    /// <summary>Applique un thème enregistré à la page affichée de la Switch.</summary>
    public void ApplyTheme(SwitchTheme theme)
    {
        theme.ApplyTo(_settings);
        ReloadAll();
    }

    /// <summary>Pages ou apparences remplacées d'un bloc : tout l'écran est remis en accord avec les réglages.</summary>
    private void ReloadAll()
    {
        var changed = Screen.Sync();
        Screen.ApplyBackground();
        Screen.ApplyPageLook();
        Screen.ApplyAllAppearances();
        RefreshShortcuts();
        WakeRender();
        if (changed) ContentsChanged?.Invoke();
        PageChanged?.Invoke();
    }
}
