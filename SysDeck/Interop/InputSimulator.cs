using System.Runtime.InteropServices;
using System.Windows.Input;

namespace SysDeck.Interop;

internal static class InputSimulator
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;
    private const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    // The union must be sized for its largest native member (MOUSEINPUT) even though we only
    // ever populate KEYBDINPUT — otherwise the marshaled INPUT struct is smaller than what
    // SendInput expects for an array element, corrupting adjacent entries and making the whole
    // call silently fail (returns 0, no error, no exception).
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_WHEEL = 0x0800;

    /// <summary>Déplace la souris de (dx, dy) pixels (stick d'un Joy-Con).</summary>
    public static void MoveMouse(int dx, int dy) => SendMouse(new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE });

    /// <summary>Molette : <paramref name="delta"/> en crans de 120 (positif = vers le haut).</summary>
    public static void Wheel(int delta) => SendMouse(new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL });

    private static void SendMouse(MOUSEINPUT mouse)
    {
        var inputs = new[] { new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = mouse } } };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Tape <paramref name="text"/> dans l'application active comme s'il venait du clavier (caractères
    /// Unicode, émojis compris : chaque moitié d'une paire de substitution est envoyée tour à tour).
    /// Le presse-papiers n'est pas touché.
    /// </summary>
    public static void SendText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var unit in text)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = unit, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = unit, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count)
            throw new InvalidOperationException($"SendInput n'a envoyé que {sent}/{inputs.Count} évènements (erreur Win32 {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Étapes d'envoi d'une combinaison : modificateurs enfoncés, touche enfoncée puis relâchée, modificateurs
    /// relâchés en sens inverse. Séparé de l'envoi pour pouvoir être vérifié sans clavier.
    /// </summary>
    internal static IReadOnlyList<(ushort Vk, bool Up, bool Extended)> HotkeySequence(HotkeyGesture gesture)
    {
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (vk == 0) throw new InvalidOperationException($"Touche {gesture.Key} non envoyable.");

        var modifiers = new List<ushort>();
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers.Add(VK_CONTROL);
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers.Add(0x10);
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers.Add(0x12);
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers.Add(0x5B);

        var steps = new List<(ushort, bool, bool)>();
        steps.AddRange(modifiers.Select(m => (m, false, false)));
        steps.Add((vk, false, IsExtended(gesture.Key)));
        steps.Add((vk, true, IsExtended(gesture.Key)));
        steps.AddRange(Enumerable.Reverse(modifiers).Select(m => (m, true, false)));
        return steps;
    }

    // Sans ce drapeau, les touches de navigation sont lues comme celles du pavé numérique (Début = 7, Flèche = 8…).
    private static bool IsExtended(Key key) => key is Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp
        or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down or Key.NumLock or Key.Divide or Key.Snapshot;

    /// <summary>Envoie une combinaison (« Ctrl+Maj+M », touche multimédia seule…) à la fenêtre au premier plan.</summary>
    public static void SendHotkey(HotkeyGesture gesture)
    {
        var inputs = HotkeySequence(gesture).Select(s => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = s.Vk,
                    dwFlags = (s.Up ? KEYEVENTF_KEYUP : 0u) | (s.Extended ? KEYEVENTF_EXTENDEDKEY : 0u)
                }
            }
        }).ToArray();

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput n'a envoyé que {sent}/{inputs.Length} évènements (erreur Win32 {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Touche virtuelle 0xE8, non attribuée par Windows : voir <see cref="SendMaskKey"/>.</summary>
    internal const ushort VK_MASK = 0xE8;

    /// <summary>
    /// Appuie et relâche une touche sans effet pendant que Win est enfoncée : Windows croit alors à une combinaison
    /// et n'ouvre pas le menu Démarrer au relâchement de Win (même astuce qu'AutoHotkey). Sans gravité si l'envoi
    /// échoue : le menu Démarrer s'ouvre, rien de plus.
    /// </summary>
    public static void SendMaskKey()
        => SendInput(2, new[] { KeyDown(VK_MASK), KeyUp(VK_MASK) }, Marshal.SizeOf<INPUT>());

    public static void SendCtrlC() => SendCtrlCombo(VK_C);

    public static void SendCtrlV() => SendCtrlCombo(VK_V);

    private static void SendCtrlCombo(ushort vk)
    {
        var inputs = new[]
        {
            KeyDown(VK_CONTROL),
            KeyDown(vk),
            KeyUp(vk),
            KeyUp(VK_CONTROL),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SendInput n'a envoyé que {sent}/{inputs.Length} évènements (erreur Win32 {error}).");
        }
    }

    private static INPUT KeyDown(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0 } }
    };

    private static INPUT KeyUp(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
    };
}
