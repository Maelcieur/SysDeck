using System.Text.Json.Serialization;
using SysDeck.Models;

namespace SysDeck.Deck;

/// <summary>Touches des Joy-Con : numéro de bit dans l'état envoyé par la Switch (jc_gamepad_rpt_t de la BDK).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JoyConButton
{
    Y = 0, X = 1, B = 2, A = 3, SrR = 4, SlR = 5, R = 6, ZR = 7,
    Minus = 8, Plus = 9, RStick = 10, LStick = 11, Home = 12, Capture = 13,
    Down = 16, Up = 17, Right = 18, Left = 19, SrL = 20, SlL = 21, L = 22, ZL = 23
}

/// <summary>Ce que fait une touche : une action de tuile, ou une commande de la Switch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JoyConCommand
{
    None, Tile, PagePrevious, PageNext, PageSelect, CursorUp, CursorDown, CursorLeft, CursorRight, CursorPress, VolumeUp, VolumeDown
}

/// <summary>Rôle d'un stick.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JoyConStickMode { None, Cursor, Volume, Mouse, Scroll }

/// <summary>Affectation d'une touche : commande, et pour <see cref="JoyConCommand.Tile"/> l'action (une tuile hors grille).</summary>
public sealed class JoyConBinding
{
    public JoyConCommand Command { get; set; }
    public ShortcutItem? Tile { get; set; }
    public string? TargetPageId { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Command == JoyConCommand.None
        || Command == JoyConCommand.Tile && Tile is null
        || Command == JoyConCommand.PageSelect && string.IsNullOrWhiteSpace(TargetPageId);
}

/// <summary>Affectations des touches et rôle des sticks : communes à toutes les pages, ou propres à une page (remplacements).</summary>
public sealed class JoyConSettings
{
    public Dictionary<JoyConButton, JoyConBinding> Buttons { get; set; } = new();
    public JoyConStickMode? LeftStick { get; set; }
    public JoyConStickMode? RightStick { get; set; }

    /// <summary>Réglages de départ : L/R changent de page, la croix déplace le curseur sur les tuiles, A appuie.</summary>
    public static JoyConSettings CreateDefault() => new()
    {
        Buttons = new()
        {
            [JoyConButton.L] = new() { Command = JoyConCommand.PagePrevious },
            [JoyConButton.R] = new() { Command = JoyConCommand.PageNext },
            [JoyConButton.Up] = new() { Command = JoyConCommand.CursorUp },
            [JoyConButton.Down] = new() { Command = JoyConCommand.CursorDown },
            [JoyConButton.Left] = new() { Command = JoyConCommand.CursorLeft },
            [JoyConButton.Right] = new() { Command = JoyConCommand.CursorRight },
            [JoyConButton.A] = new() { Command = JoyConCommand.CursorPress }
        },
        LeftStick = JoyConStickMode.Cursor,
        RightStick = JoyConStickMode.Volume
    };
}

/// <summary>Noms affichés, catégories et règles des touches (partie pure, testée).</summary>
public static class JoyConInfo
{
    public static string Label(JoyConButton button) => button switch
    {
        JoyConButton.Minus => "−",
        JoyConButton.Plus => "+",
        JoyConButton.LStick => "Clic du stick gauche",
        JoyConButton.RStick => "Clic du stick droit",
        JoyConButton.Home => "Home",
        JoyConButton.Capture => "Capture",
        JoyConButton.Up => "Croix haut",
        JoyConButton.Down => "Croix bas",
        JoyConButton.Left => "Croix gauche",
        JoyConButton.Right => "Croix droite",
        JoyConButton.SlL => "SL (gauche)",
        JoyConButton.SrL => "SR (gauche)",
        JoyConButton.SlR => "SL (droit)",
        JoyConButton.SrR => "SR (droit)",
        _ => button.ToString()
    };

    public static string Label(JoyConCommand command) => command switch
    {
        JoyConCommand.PagePrevious => "Page précédente",
        JoyConCommand.PageNext => "Page suivante",
        JoyConCommand.PageSelect => "Aller à une page",
        JoyConCommand.CursorUp => "Curseur vers le haut",
        JoyConCommand.CursorDown => "Curseur vers le bas",
        JoyConCommand.CursorLeft => "Curseur vers la gauche",
        JoyConCommand.CursorRight => "Curseur vers la droite",
        JoyConCommand.CursorPress => "Appuyer sur la tuile du curseur",
        JoyConCommand.VolumeUp => "Volume +",
        JoyConCommand.VolumeDown => "Volume −",
        JoyConCommand.Tile => "Action",
        _ => "Aucune"
    };

    public static string Label(JoyConStickMode mode) => mode switch
    {
        JoyConStickMode.Cursor => "Curseur sur les tuiles",
        JoyConStickMode.Volume => "Volume (haut / bas)",
        JoyConStickMode.Mouse => "Souris du PC",
        JoyConStickMode.Scroll => "Défilement (molette)",
        _ => "Aucun"
    };

    /// <summary>Texte d'une affectation : nom de l'action ou de la commande.</summary>
    public static string Describe(JoyConBinding? binding, IReadOnlyList<DeckPage>? pages = null)
    {
        if (binding is null || binding.IsEmpty) return "Aucune";
        if (binding.Command == JoyConCommand.Tile) return binding.Tile!.Name;
        if (binding.Command == JoyConCommand.PageSelect)
            return pages?.FirstOrDefault(page => page.Id == binding.TargetPageId)?.Name ?? "Page introuvable";
        return Label(binding.Command);
    }

    /// <summary>Commandes qui se répètent tant que la touche est tenue (curseur, volume).</summary>
    public static bool Repeats(JoyConCommand command)
        => command is JoyConCommand.CursorUp or JoyConCommand.CursorDown or JoyConCommand.CursorLeft or JoyConCommand.CursorRight
            or JoyConCommand.VolumeUp or JoyConCommand.VolumeDown;

    /// <summary>Affectation effective : celle de la page si elle en a une, sinon la commune.</summary>
    public static JoyConBinding? Resolve(JoyConButton button, JoyConSettings global, JoyConSettings? page)
        => page?.Buttons.TryGetValue(button, out var own) == true ? own : global.Buttons.GetValueOrDefault(button);

    public static JoyConStickMode ResolveStick(bool left, JoyConSettings global, JoyConSettings? page)
        => (left ? page?.LeftStick ?? global.LeftStick : page?.RightStick ?? global.RightStick) ?? JoyConStickMode.None;

    /// <summary>Touches présentes sur chaque Joy-Con.</summary>
    public static bool IsLeft(JoyConButton button) => (int)button >= 16 || button is JoyConButton.Minus or JoyConButton.LStick or JoyConButton.Capture;
}

/// <summary>
/// Calibrage d'un stick : la position au repos (prise à la connexion) sert de centre ; au-delà d'une zone morte, la
/// déviation est ramenée entre -1 et 1. Les valeurs brutes vont de 0 à 4095 (12 bits).
/// </summary>
public sealed class JoyConStick
{
    private const double DeadZone = 380, Range = 1250;
    private int? _cx, _cy;

    public void Reset() => _cx = _cy = null;

    /// <summary>Position (-1..1, Y positif vers le haut) ; <paramref name="invertY"/> pour la Switch Lite.</summary>
    public (double X, double Y) Read(int rawX, int rawY, bool invertY)
    {
        if (rawX == 0 && rawY == 0) return (0, 0); // pas de stick (Joy-Con absent)
        _cx ??= rawX;
        _cy ??= rawY;
        var y = Axis(rawY - _cy.Value);
        return (Axis(rawX - _cx.Value), invertY ? -y : y);
    }

    private static double Axis(int delta)
    {
        var magnitude = Math.Abs(delta) - DeadZone;
        return magnitude <= 0 ? 0 : Math.Sign(delta) * Math.Min(1, magnitude / Range);
    }
}
