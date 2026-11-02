using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SysDeck.Interop;

/// <summary>
/// Combinaison de touches. Deux formes texte : <see cref="Serialize"/> pour la config (nom exact de la
/// touche, sans ambiguïté) et <see cref="ToString"/> pour l'affichage (« Ctrl+Maj+5 », « Ctrl+< »).
/// </summary>
public sealed record HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public static readonly HotkeyGesture CtrlSpace = new(ModifierKeys.Control, Key.Space);

    /// <summary>Texte enregistré : modificateurs + nom de la touche WPF (« Ctrl+Maj+D5 »).</summary>
    public string Serialize() => string.Join("+", ModifierNames().Append(Key.ToString()));

    /// <summary>Texte affiché : « Ctrl+Maj+E », « Win+Alt+Espace », « Ctrl+< »…</summary>
    public override string ToString() => string.Join("+", ModifierNames().Append(KeyName(Key)));

    private IEnumerable<string> ModifierNames()
    {
        if (Modifiers.HasFlag(ModifierKeys.Control)) yield return "Ctrl";
        if (Modifiers.HasFlag(ModifierKeys.Shift)) yield return "Maj";
        if (Modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win";
    }

    /// <summary>
    /// Lit la forme enregistrée (et, par compatibilité, l'ancienne forme affichée). <paramref name="requireModifier"/> faux :
    /// une touche seule est acceptée (F5, touches multimédia…), pour les raccourcis envoyés plutôt qu'enregistrés.
    /// </summary>
    public static HotkeyGesture? Parse(string? text, bool requireModifier = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "maj" or "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                case "espace": key = Key.Space; break;
                default:
                    // « 5 » est la touche 5 (D5), pas la valeur numérique 5 de l'énumération (qui serait Key.Clear).
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (raw.StartsWith("Pavé", StringComparison.OrdinalIgnoreCase) && raw.Length == 5 && char.IsDigit(raw[4])) key = Key.NumPad0 + (raw[4] - '0');
                    else if (!raw.All(char.IsDigit) && Enum.TryParse<Key>(raw, ignoreCase: true, out var parsed)) key = parsed;
                    break;
            }
        }
        return key is { } k && k != Key.None && (modifiers != ModifierKeys.None || !requireModifier) ? new HotkeyGesture(modifiers, k) : null;
    }

    private static string KeyName(Key key)
    {
        switch (key)
        {
            case Key.Space: return "Espace";
            case >= Key.D0 and <= Key.D9: return ((int)(key - Key.D0)).ToString();
            case >= Key.NumPad0 and <= Key.NumPad9: return "Pavé" + (int)(key - Key.NumPad0);
            case >= Key.A and <= Key.Z: return key.ToString();
            case >= Key.F1 and <= Key.F24: return key.ToString();
            case Key.MediaPlayPause: return "Lecture/Pause";
            case Key.MediaNextTrack: return "Piste suivante";
            case Key.MediaPreviousTrack: return "Piste précédente";
            case Key.MediaStop: return "Stop média";
            case Key.VolumeUp: return "Volume +";
            case Key.VolumeDown: return "Volume -";
            case Key.VolumeMute: return "Muet";
            case Key.Snapshot: return "Impr. écran";
        }
        // Touches de ponctuation (Oem…) : le caractère réellement imprimé sur le clavier (« < », « ! »…).
        var character = MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(key), MAPVK_VK_TO_CHAR) & 0x7FFF;
        return character > 32 ? ((char)character).ToString().ToUpperInvariant() : key.ToString();
    }

    private const uint MAPVK_VK_TO_CHAR = 2;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
/// <summary>Raccourci clavier global (actif dans toutes les applications), reçu par une fenêtre de SysDeck.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000; // touches maintenues : un seul déclenchement

    private readonly int _id;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>Vrai si la combinaison est actuellement enregistrée auprès de Windows.</summary>
    public bool IsRegistered => _registered;

    public GlobalHotkey(Window window, int id)
    {
        _id = id;
        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("La fenêtre doit être initialisée avant d'enregistrer un raccourci global.");
        _source.AddHook(WndProc);
    }

    /// <summary>Enregistre la combinaison ; faux si une autre application l'utilise déjà.</summary>
    public bool Register(HotkeyGesture gesture)
    {
        Unregister();
        uint modifiers = MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= MOD_ALT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= MOD_WIN;

        _registered = RegisterHotKey(_source.Handle, _id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key));
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, _id);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
