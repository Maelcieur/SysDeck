using System.Globalization;

namespace SysDeck.Deck;

public enum KeyboardLayer { Letters, Symbols, Accents }

public enum ShiftMode { Off, Once, Locked }

/// <summary>
/// Logique du clavier tactile de la Switch, sans WPF (testée) : texte saisi, couches AZERTY / symboles / accents,
/// majuscule ponctuelle ou verrouillée par double appui, limite de longueur.
/// </summary>
public sealed class TextEntryModel
{
    public const string ShiftKey = "{shift}", BackKey = "{back}", SpaceKey = "{space}", EnterKey = "{enter}",
        LettersKey = "{abc}", SymbolsKey = "{123}", AccentsKey = "{accents}";

    public static readonly TimeSpan DoubleTap = TimeSpan.FromMilliseconds(450);

    private static readonly string[][] LetterRows =
    {
        new[] { "a", "z", "e", "r", "t", "y", "u", "i", "o", "p" },
        new[] { "q", "s", "d", "f", "g", "h", "j", "k", "l", "m" },
        new[] { ShiftKey, "w", "x", "c", "v", "b", "n", "'", BackKey },
        new[] { SymbolsKey, AccentsKey, SpaceKey, ",", ".", EnterKey }
    };

    private static readonly string[][] SymbolRows =
    {
        new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
        new[] { "@", "#", "&", "-", "_", "(", ")", "\"", ":", ";" },
        new[] { "%", "+", "=", "/", "?", "!", "*", "€", BackKey },
        new[] { LettersKey, AccentsKey, SpaceKey, ",", ".", EnterKey }
    };

    private static readonly string[][] AccentRows =
    {
        new[] { "é", "è", "ê", "ë", "à", "â", "ù", "û", "ü", "ç" },
        new[] { "ô", "ö", "î", "ï", "œ", "æ", "«", "»", "…", "°" },
        new[] { ShiftKey, "<", ">", "~", "|", "[", "]", "^", BackKey },
        new[] { LettersKey, SymbolsKey, SpaceKey, ",", ".", EnterKey }
    };

    private readonly Func<DateTime> _clock;
    private DateTime _lastShift = DateTime.MinValue;

    public TextEntryModel(int maxLength, string? initial = null, Func<DateTime>? clock = null)
    {
        MaxLength = Math.Max(1, maxLength);
        _clock = clock ?? (() => DateTime.UtcNow);
        Text = Truncate(initial ?? "");
        if (Text.Length == 0) Shift = ShiftMode.Once;
    }

    public int MaxLength { get; }

    public string Text { get; private set; }

    public ShiftMode Shift { get; private set; }

    public KeyboardLayer Layer { get; set; } = KeyboardLayer.Letters;

    public int Remaining => MaxLength - Text.Length;

    public static IReadOnlyList<string[]> Rows(KeyboardLayer layer) => layer switch
    {
        KeyboardLayer.Symbols => SymbolRows,
        KeyboardLayer.Accents => AccentRows,
        _ => LetterRows
    };

    public IReadOnlyList<string[]> CurrentRows => Rows(Layer);

    public static bool IsSpecial(string key) => key.Length > 2 && key[0] == '{' && key[^1] == '}';

    /// <summary>Texte affiché sur la touche (majuscule appliquée aux lettres).</summary>
    public string Label(string key) => key switch
    {
        ShiftKey => "⇧",
        BackKey => "⌫",
        SpaceKey => "espace",
        EnterKey => "Envoyer",
        LettersKey => "ABC",
        SymbolsKey => "123",
        AccentsKey => "éà",
        _ => Shift == ShiftMode.Off ? key : key.ToUpper(CultureInfo.CurrentCulture)
    };

    /// <summary>Appui sur une touche ; renvoie vrai pour Entrée (l'appelant envoie le texte).</summary>
    public bool Press(string key)
    {
        switch (key)
        {
            case EnterKey:
                return true;
            case BackKey:
                Backspace();
                break;
            case SpaceKey:
                Insert(" ");
                break;
            case ShiftKey:
                PressShift();
                break;
            case LettersKey:
                Layer = KeyboardLayer.Letters;
                break;
            case SymbolsKey:
                Layer = KeyboardLayer.Symbols;
                break;
            case AccentsKey:
                Layer = KeyboardLayer.Accents;
                break;
            default:
                Insert(Shift == ShiftMode.Off ? key : key.ToUpper(CultureInfo.CurrentCulture));
                if (Shift == ShiftMode.Once) Shift = ShiftMode.Off;
                break;
        }
        return false;
    }

    /// <summary>Un appui = majuscule pour la lettre suivante ; deux appuis rapprochés = verrouillage ; un appui verrouillé = retour.</summary>
    private void PressShift()
    {
        var now = _clock();
        var quick = now - _lastShift <= DoubleTap;
        _lastShift = now;
        Shift = Shift switch
        {
            ShiftMode.Locked => ShiftMode.Off,
            ShiftMode.Once when quick => ShiftMode.Locked,
            ShiftMode.Once => ShiftMode.Off,
            _ => ShiftMode.Once
        };
    }

    /// <summary>Ajoute du texte en fin de saisie, tronqué à la limite sans couper un émoji.</summary>
    public void Insert(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Text = Truncate(Text + value);
    }

    /// <summary>Ajoute un mot isolé (émote Twitch) : espace avant si besoin, espace après.</summary>
    public void InsertWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        var before = Text.Length > 0 && !char.IsWhiteSpace(Text[^1]) ? " " : "";
        var candidate = Text + before + word.Trim();
        if (candidate.Length > MaxLength) return; // une émote coupée ne s'afficherait pas
        Text = candidate.Length < MaxLength ? candidate + " " : candidate;
    }

    /// <summary>Efface le dernier caractère visible (un émoji compte pour un).</summary>
    public void Backspace()
    {
        if (Text.Length == 0) return;
        var starts = StringInfo.ParseCombiningCharacters(Text);
        Text = Text[..starts[^1]];
    }

    public void Clear() => Text = "";

    private string Truncate(string value)
    {
        if (value.Length <= MaxLength) return value;
        // Coupe au début du premier élément qui dépasserait la limite.
        var end = 0;
        foreach (var start in StringInfo.ParseCombiningCharacters(value))
        {
            if (start + StringInfo.GetNextTextElement(value, start).Length > MaxLength) break;
            end = start + StringInfo.GetNextTextElement(value, start).Length;
        }
        return value[..end];
    }
}
