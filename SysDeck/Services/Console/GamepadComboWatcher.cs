using System.Runtime.InteropServices;
using SysDeck.Infrastructure;

namespace SysDeck.Services.ConsoleMode;

/// <summary>
/// Surveille en arrière-plan (20 fois par seconde, XInput) une combinaison de touches de la manette qui lance le mode
/// console depuis le PC. Déclenche une seule fois par appui (tous les boutons enfoncés ensemble), puis 3 s de pause.
/// </summary>
public sealed class GamepadComboWatcher : IDisposable
{
    private const ushort BtnStart = 0x0010, BtnBack = 0x0020, BtnLb = 0x0100, BtnRb = 0x0200;

    /// <summary>Combinaisons proposées dans les Paramètres (identifiant, libellé).</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> Combos = new[]
    {
        ("ViewMenu", "View ⧉ + Menu ≡ (les deux petits boutons du centre)"),
        ("LbRbMenu", "LB + RB + Menu ≡"),
        ("LbRbView", "LB + RB + View ⧉"),
        ("Off", "Désactivée"),
    };

    public static ushort MaskOf(string? id) => id switch
    {
        "ViewMenu" => BtnBack | BtnStart,
        "LbRbMenu" => BtnLb | BtnRb | BtnStart,
        "LbRbView" => BtnLb | BtnRb | BtnBack,
        _ => 0,
    };

    private readonly Func<string> _combo;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Combinaison faite (levé sur le thread de surveillance).</summary>
    public event Action? Triggered;

    /// <summary>View ⧉ + RT (+1) / View ⧉ + LT (-1) : volume, répété tant qu'on appuie (thread de surveillance).</summary>
    public event Action<int>? VolumeStep;

    /// <summary>Active la combinaison de volume (mode console seulement).</summary>
    public volatile bool VolumeEnabled;

    public GamepadComboWatcher(Func<string> combo) => _combo = combo;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SysDeck.ComboManette", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Loop()
    {
        var connected = new bool[4];
        var lastCheck = new long[4];
        var wasDown = false;
        long pauseUntil = 0, volNext = 0;
        var volPrev = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_running)
        {
            try
            {
                var mask = MaskOf(_combo());
                if (mask == 0 && !VolumeEnabled) { Thread.Sleep(1000); continue; }
                var now = sw.ElapsedMilliseconds;
                var down = false;
                var vol = 0;
                for (uint i = 0; i < 4; i++)
                {
                    if (!connected[i] && now - lastCheck[i] < 2000) continue;   // emplacement vide : rare
                    lastCheck[i] = now;
                    if (XInputGetState(i, out var st) != 0) { connected[i] = false; continue; }
                    connected[i] = true;
                    if (mask != 0 && (st.Buttons & mask) == mask) down = true;
                    if ((st.Buttons & BtnBack) != 0) vol = st.RT > 160 ? 1 : st.LT > 160 ? -1 : vol;
                }
                // Volume : 1er cran tout de suite, puis un cran toutes les 150 ms après 400 ms.
                if (!VolumeEnabled) vol = 0;
                if (vol != 0 && (vol != volPrev || now >= volNext))
                {
                    volNext = now + (vol != volPrev ? 400 : 150);
                    VolumeStep?.Invoke(vol);
                }
                volPrev = vol;
                if (down && !wasDown && now >= pauseUntil)
                {
                    pauseUntil = now + 3000;
                    Log.Info("Mode console : combinaison de la manette détectée.");
                    Triggered?.Invoke();
                }
                wasDown = down;
            }
            catch (Exception ex)
            {
                Log.WarnOnce("combo-watcher", "Combinaison de la manette : surveillance interrompue", ex);
                Thread.Sleep(2000);
            }
            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        _running = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Packet;
        public ushort Buttons;
        public byte LT, RT;
        public short LX, LY, RX, RY;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint user, out State state);
}
