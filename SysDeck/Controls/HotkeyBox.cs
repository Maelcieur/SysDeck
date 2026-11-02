using System.Windows.Input;
using SysDeck.Interop;

namespace SysDeck.Controls;

/// <summary>
/// Zone de saisie d'un raccourci : un clic puis la combinaison voulue (au moins un modificateur :
/// Ctrl, Maj, Alt ou Win). Échap annule. Les touches ne sont jamais tapées dans la zone.
/// </summary>
public sealed class HotkeyBox : System.Windows.Controls.TextBox
{
    /// <summary>Combinaison complète saisie.</summary>
    public event Action<HotkeyGesture>? GestureCaptured;

    /// <summary>Début (vrai) et fin (faux) de la saisie : les raccourcis globaux doivent être suspendus entre les deux.</summary>
    public event Action<bool>? CaptureStateChanged;

    private string _shown = "";

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = System.Windows.Input.Cursors.Hand;
        GotKeyboardFocus += (_, _) =>
        {
            Text = "Appuie sur la combinaison…";
            CaptureStateChanged?.Invoke(true);
        };
        LostKeyboardFocus += (_, _) =>
        {
            Text = _shown;
            CaptureStateChanged?.Invoke(false);
        };
    }

    /// <summary>Combinaison affichée au repos (null = aucune).</summary>
    public void Display(HotkeyGesture? gesture)
    {
        _shown = gesture?.ToString() ?? "Aucun raccourci";
        if (!IsKeyboardFocused) Text = _shown;
    }

    // Impr. écran ne produit qu'un évènement de relâchement : on le traite comme une pression.
    protected override void OnPreviewKeyUp(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Snapshot && IsKeyboardFocused) Capture(e, e.Key);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        => Capture(e, e.Key == Key.System ? e.SystemKey : e.Key);

    private void Capture(System.Windows.Input.KeyEventArgs e, Key key)
    {

        // Tab (ou Maj+Tab) seul reste à la navigation : sinon la zone serait un piège clavier dont on
        // ne sortirait qu'à la souris ou avec Échap.
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
            return;

        e.Handled = true;

        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }

        // Modificateur seul : on attend la touche principale, en montrant ce qui est déjà enfoncé.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            var partial = new HotkeyGesture(CurrentModifiers(), Key.None).ToString();
            Text = partial.EndsWith("+None") ? partial[..^5] + "+…" : "Appuie sur la combinaison…";
            return;
        }

        var modifiers = CurrentModifiers();
        if (modifiers == ModifierKeys.None)
        {
            Text = "Ajoute Ctrl, Maj, Alt ou Win à la touche";
            return;
        }

        var gesture = new HotkeyGesture(modifiers, key);
        Keyboard.ClearFocus(); // fin de la saisie : les raccourcis sont réactivés avant d'enregistrer le nouveau
        GestureCaptured?.Invoke(gesture);
    }

    private static ModifierKeys CurrentModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }
}
