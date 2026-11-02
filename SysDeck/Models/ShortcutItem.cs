using System.Text.Json.Serialization;

namespace SysDeck.Models;

public sealed class ShortcutItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Icône choisie manuellement (chemin d'image), prioritaire sur la détection automatique.</summary>
    public string? CustomIconPath { get; set; }

    /// <summary>
    /// Symbole (glyphe Segoe Fluent Icons) ou émoji choisi comme icône ; prioritaire sur
    /// <see cref="CustomIconPath"/>, lui-même prioritaire sur l'icône détectée. Null = aucun.
    /// </summary>
    public string? IconGlyph { get; set; }

    /// <summary>Couleur du symbole (ARGB hexadécimal) ; null = couleur du texte du thème. Sans effet sur un émoji ou une image.</summary>
    public string? IconColor { get; set; }

    /// <summary>Couleur de fond de la tuile sur la Switch (ARGB hexadécimal) ; null = fond du thème.</summary>
    public string? TileColor { get; set; }

    /// <summary>
    /// Combinaison de touches envoyée à la fenêtre au premier plan (forme de <c>HotkeyGesture.Serialize</c>) :
    /// la tuile est alors un raccourci clavier, sans programme (<see cref="Path"/> vide). Null = application normale.
    /// </summary>
    public string? Hotkey { get; set; }

    [JsonIgnore]
    public bool IsHotkey => !string.IsNullOrWhiteSpace(Hotkey);

    private TileKind? _kind;

    /// <summary>
    /// Type de tuile. Absent des anciens fichiers : déduit alors de <see cref="Hotkey"/>
    /// (raccourci clavier si renseigné, sinon application).
    /// </summary>
    public TileKind Kind
    {
        get => _kind ?? (IsHotkey ? TileKind.Hotkey : TileKind.App);
        set => _kind = value;
    }

    /// <summary>Ligne de commande d'une tuile <see cref="TileKind.Command"/> (exécutée via <c>cmd.exe /c</c>).</summary>
    public string? Command { get; set; }

    /// <summary>Commande exécutée avec élévation (demande UAC).</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>Demande un second appui dans les 3 s avant d'agir (évite les appuis accidentels).</summary>
    public bool ConfirmTap { get; set; }

    /// <summary>Identifiant du périphérique audio d'une tuile micro/sortie muet ; vide = défaut de Windows.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Colonne de la case dans la grille d'applications de la Switch (0 = première) ; null = pas encore placée.</summary>
    public int? GridCol { get; set; }

    /// <summary>Ligne de la case dans la grille d'applications de la Switch (0 = première) ; null = pas encore placée.</summary>
    public int? GridRow { get; set; }

    [JsonIgnore]
    public bool IsMuteTile => Kind is TileKind.MicMute or TileKind.OutputMute;

}

/// <summary>Type d'une tuile de la Switch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TileKind
{
    /// <summary>Application, fichier, dossier, lien ou protocole.</summary>
    App,
    /// <summary>Combinaison de touches envoyée au PC.</summary>
    Hotkey,
    /// <summary>Ligne de commande exécutée sans fenêtre.</summary>
    Command,
    /// <summary>Bascule muet d'un micro.</summary>
    MicMute,
    /// <summary>Bascule muet d'une sortie audio.</summary>
    OutputMute
}
