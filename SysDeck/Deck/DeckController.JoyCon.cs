using SysDeck.Interop;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck.Deck;

// Joy-Con fixés sur la Switch (ou commandes de la Switch Lite) : chaque touche déclenche son affectation (celle de la
// page affichée si elle en a une, sinon la commune), les sticks déplacent le curseur, règlent le volume ou pilotent la
// souris. Le curseur entoure une tuile de la page ; A (par défaut) l'appuie.
public sealed partial class DeckController
{
    private const int RepeatDelayMs = 400, RepeatMs = 130, StickRepeatDelayMs = 350, StickRepeatMs = 160;
    private const int CursorHideMs = 8000;

    private readonly JoyConStick _leftStick = new(), _rightStick = new();
    private readonly Dictionary<JoyConButton, (long Since, long Last)> _held = new();
    private readonly (int Dx, int Dy, long Since, long Last)[] _stickStep = new (int, int, long, long)[2];
    private long _wheelLast;
    private ShortcutItem? _cursor;
    private long _cursorShownAt;

    /// <summary>État des Joy-Con à chaque réponse de la Switch (thread interface) : Paramètres › Switch › Manettes.</summary>
    public event Action<DeckProtocol.JoyConState>? JoyConChanged;

    public DeckProtocol.JoyConState JoyCon { get; private set; }

    /// <summary>Version du protocole du payload connecté : avant 6 = sans Joy-Con, avant 7 = sans actions système, 0 = pas encore de réponse.</summary>
    public int PayloadVersion => _link.PayloadVersion;

    private void OnJoyCon(DeckProtocol.JoyConState state)
    {
        if (!state.LeftConnected) _leftStick.Reset();
        if (!state.RightConnected) _rightStick.Reset();
        JoyCon = state;
        JoyConChanged?.Invoke(state);

        var now = Environment.TickCount64;
        var global = _settings.JoyCon;
        var page = _settings.Page.JoyCon;
        foreach (var button in Enum.GetValues<JoyConButton>())
        {
            var binding = JoyConInfo.Resolve(button, global, page);
            var mappedNavigation = _settings.Page.FindMappedTileNavigation(button);
            if (mappedNavigation is not null)
            {
                if (state.WasPressed(button) && mappedNavigation.PageNavigation is { } direction)
                    NavigateAppsSubpage(direction == TilePageNavigation.Next ? 1 : -1);
                if (!state.IsDown(button)) _held.Remove(button);
                continue;
            }
            if (state.WasPressed(button))
            {
                _held[button] = (now, now);
                Execute(binding);
            }
            else if (state.IsDown(button) && binding is { } held && JoyConInfo.Repeats(held.Command)
                     && _held.TryGetValue(button, out var t) && now - t.Since >= RepeatDelayMs && now - t.Last >= RepeatMs)
            {
                _held[button] = (t.Since, now);
                Execute(held);
            }
            if (!state.IsDown(button)) _held.Remove(button);
        }

        Stick(0, _leftStick.Read(state.LeftX, state.LeftY, state.Lite), JoyConInfo.ResolveStick(true, global, page), now);
        Stick(1, _rightStick.Read(state.RightX, state.RightY, state.Lite), JoyConInfo.ResolveStick(false, global, page), now);

        if (_cursor is not null && now - _cursorShownAt > CursorHideMs) SetCursor(null);
    }

    private void Execute(JoyConBinding? binding)
    {
        if (binding is null || binding.IsEmpty) return;
        switch (binding.Command)
        {
            case JoyConCommand.Tile:
                _launch(binding.Tile!);
                break;
            case JoyConCommand.PagePrevious:
            case JoyConCommand.PageNext:
                var count = _settings.Pages.Count;
                if (count > 1)
                {
                    SetCursor(null);
                    SetPage((_settings.CurrentPage + (binding.Command == JoyConCommand.PageNext ? 1 : -1) + count) % count);
                }
                break;
            case JoyConCommand.PageSelect:
                var target = _settings.Pages.FindIndex(page => page.Id == binding.TargetPageId);
                if (target >= 0)
                {
                    SetCursor(null);
                    SetPage(target);
                }
                break;
            case JoyConCommand.CursorUp: MoveCursor(0, -1); break;
            case JoyConCommand.CursorDown: MoveCursor(0, 1); break;
            case JoyConCommand.CursorLeft: MoveCursor(-1, 0); break;
            case JoyConCommand.CursorRight: MoveCursor(1, 0); break;
            case JoyConCommand.CursorPress: PressCursor(); break;
            case JoyConCommand.VolumeUp: MasterVolume.Change(2); break;
            case JoyConCommand.VolumeDown: MasterVolume.Change(-2); break;
        }
    }

    /// <summary>Stick : direction du curseur (avec répétition), volume, souris ou molette selon son rôle.</summary>
    private void Stick(int index, (double X, double Y) position, JoyConStickMode mode, long now)
    {
        var (x, y) = position;
        switch (mode)
        {
            case JoyConStickMode.Mouse:
                // Courbe quadratique : précis près du centre, rapide en bout de course (~60 lectures par seconde).
                if (x != 0 || y != 0) InputSimulator.MoveMouse((int)Math.Round(x * Math.Abs(x) * 22), (int)Math.Round(-y * Math.Abs(y) * 22));
                return;
            case JoyConStickMode.Scroll:
                if (Math.Abs(y) > 0.5 && now - _wheelLast >= (Math.Abs(y) > 0.9 ? 60 : 140))
                {
                    _wheelLast = now;
                    InputSimulator.Wheel(Math.Sign(y) * 120);
                }
                return;
            case JoyConStickMode.Cursor:
            case JoyConStickMode.Volume:
                var (dx, dy) = Direction(x, y, mode == JoyConStickMode.Volume);
                ref var step = ref _stickStep[index];
                if (dx == 0 && dy == 0)
                {
                    step = default;
                    return;
                }
                var fresh = dx != step.Dx || dy != step.Dy;
                if (!fresh && (now - step.Since < StickRepeatDelayMs || now - step.Last < StickRepeatMs)) return;
                step = (dx, dy, fresh ? now : step.Since, now);
                if (mode == JoyConStickMode.Volume) MasterVolume.Change(dy < 0 ? 2 : -2); // haut = plus fort
                else MoveCursor(dx, dy);
                return;
        }
    }

    /// <summary>Direction franche du stick (axe dominant, au-delà de 60 %) ; en mode volume, seulement haut / bas. Y écran vers le bas.</summary>
    internal static (int Dx, int Dy) Direction(double x, double y, bool verticalOnly)
    {
        const double threshold = 0.6;
        if (verticalOnly) return Math.Abs(y) >= threshold ? (0, y > 0 ? -1 : 1) : (0, 0);
        if (Math.Max(Math.Abs(x), Math.Abs(y)) < threshold) return (0, 0);
        return Math.Abs(x) >= Math.Abs(y) ? (Math.Sign(x), 0) : (0, y > 0 ? -1 : 1);
    }

    // ---------- Curseur sur les tuiles ----------

    private void MoveCursor(int dx, int dy)
    {
        var (columns, rows) = AppsGrid();
        var cells = TileGrid.Arrange(CurrentAppsSubpage(), columns, rows).Cells;
        // Premier mouvement (ou tuile disparue) : le curseur apparaît sur la première tuile, sans bouger.
        var next = _cursor is not null && cells.ContainsKey(_cursor) ? TileGrid.Neighbor(cells, _cursor, dx, dy) ?? _cursor : TileGrid.First(cells);
        SetCursor(next);
    }

    private void PressCursor()
    {
        var (columns, rows) = AppsGrid();
        var cells = TileGrid.Arrange(CurrentAppsSubpage(), columns, rows).Cells;
        if (_cursor is null || !cells.ContainsKey(_cursor))
        {
            SetCursor(TileGrid.First(cells)); // A sans curseur : il apparaît d'abord
            return;
        }
        var tile = _cursor;
        SetCursor(tile);
        foreach (var panel in Screen.TilePanels) panel.FlashPressed(tile);
        _launch(tile);
    }

    private void SetCursor(ShortcutItem? tile)
    {
        _cursor = tile;
        _cursorShownAt = Environment.TickCount64;
        foreach (var panel in Screen.TilePanels) panel.SetCursor(tile);
    }
}
