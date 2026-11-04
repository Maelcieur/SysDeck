using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SysDeck.Infrastructure;

namespace SysDeck.Deck;

/// <summary>État de la liaison avec la Switch, publié sur le thread d'interface.</summary>
public sealed record DeckStatus(bool Connected, int Battery, bool Charging);

/// <summary>
/// Liaison USB avec la Switch sur un thread dédié : recherche et reconnexion automatiques, envoi des
/// seules zones modifiées de l'image, interrogation régulière pour le tactile, BYE à l'arrêt.
/// Les événements (état, tactile) sont publiés sur le thread d'interface.
/// </summary>
public sealed class DeckLink : IDisposable
{
    private const int W = DeckProtocol.ScreenWidth;
    private const int H = DeckProtocol.ScreenHeight;
    private const int Stride = W * 4;
    public const int FrameBytes = W * H * 4;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stopping;
    private volatile bool _enabled = true;
    private volatile int _brightness = 60;
    private bool _resync; // la Switch a démarré une nouvelle session : écran et luminosité à renvoyer (thread USB seulement)
    private int _brightnessSent = -1; // luminosité connue de la Switch, -1 = à renvoyer (thread USB seulement)

    /// <summary>Dernière image proposée, pas encore envoyée (la plus récente remplace la précédente).</summary>
    private byte[]? _pending;

    /// <summary>Tampons libres pour les images suivantes (évite d'allouer 3,6 Mo à chaque image).</summary>
    private readonly Stack<byte[]> _free = new();

    /// <summary>Ce qui est actuellement affiché sur la Switch ; null = inconnu (tout renvoyer).</summary>
    private byte[]? _shown;
    private byte[] _rectBuffer = Array.Empty<byte>();
    private volatile int _unchangedFrames; // images reçues identiques à l'écran, d'affilée (écrit par le thread USB)
    private volatile DeckStatus _status = new(false, -1, false);

    // ---------- Mode nuit ----------
    private static readonly TimeSpan NightResync = TimeSpan.FromMinutes(10);

    /// <summary>Dernier kit nuit proposé (null = aucun : celui de la Switch est effacé).</summary>
    private volatile byte[]? _nightKit;

    /// <summary>Kit actuellement dans la Switch (null = aucun ou inconnu) et heure de son dernier envoi ou recalage.</summary>
    private byte[]? _nightSent;
    private bool _nightStored;
    private bool _nightUnsupported;
    private DateTime _nightTimeSent;

    /// <summary>
    /// Luminosité de veille voulue (-1 = aucune) et dernière envoyée. Une valeur différente part seule (512 octets)
    /// au lieu du kit complet ; un ancien payload qui ne connaît pas la commande est détecté (BadCmd).
    /// </summary>
    private volatile int _nightBrightness = -1;
    private int _nightBrightnessSent = -1;
    private volatile bool _nightBrightnessUnsupported;

    private volatile int _flushRequest; // 0 = rien, 1 = envoi final demandé
    private readonly ManualResetEventSlim _flushDone = new(false);

    public event Action<DeckStatus>? StatusChanged;
    public event Action<DeckProtocol.TouchEvent>? Touch;

    /// <summary>
    /// Pas de volume demandé par les boutons VOL+ / VOL- de la Switch : +1 ou -1. Levé sur le thread USB.
    /// </summary>
    public event Action<int>? VolumeStep;

    /// <summary>(v6) État des Joy-Con à chaque réponse où ils sont branchés, ou au moment où ils se débranchent (thread interface).</summary>
    public event Action<DeckProtocol.JoyConState>? JoyCon;

    /// <summary>Version du protocole du payload connecté (0 tant qu'aucune réponse) : 5 = payload sans Joy-Con.</summary>
    public int PayloadVersion { get; private set; }

    private bool _joyConSeen;
    private byte _joyConFlags;
    private (byte Left, byte Right) _joyConDebug;

    public DeckLink(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "SwitchDeck USB" };
    }

    public void Start() => _thread.Start();

    public bool IsConnected => _status.Connected;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; _wake.Set(); }
    }

    public int Brightness
    {
        get => _brightness;
        set { _brightness = Math.Clamp(value, 0, 100); _wake.Set(); }
    }

    /// <summary>Propose le kit nuit à envoyer (dès que connecté, puis à chaque changement) ; null efface celui de la Switch.</summary>
    public void SetNightKit(byte[]? kit)
    {
        _nightKit = kit;
        _wake.Set();
    }

    /// <summary>Faux une fois qu'une Switch a refusé la commande de luminosité seule (ancien payload).</summary>
    public bool NightBrightnessSupported => !_nightBrightnessUnsupported;

    /// <summary>
    /// Change la luminosité de veille sans renvoyer le kit. L'en-tête du kit gardé ici est mis à jour aussi : un
    /// renvoi (reconnexion) porte la bonne valeur. Écriture de 2 octets pendant un éventuel envoi : au pire cet
    /// envoi porte l'ancienne ou la nouvelle valeur, et la commande qui suit remet la bonne.
    /// </summary>
    public void SetNightBrightness(int percent)
    {
        var kit = _nightKit;
        if (kit is not null) DeckProtocol.PatchNightBrightness(kit, percent);
        _nightBrightness = Math.Clamp(percent, 0, 100);
        _wake.Set();
    }

    /// <summary>
    /// Envoi final avant l'extinction ou la veille du PC : kit s'il a changé, heure, puis BYE. Attend la fin
    /// (au plus <paramref name="timeoutMs"/>) ; sans effet si la Switch n'est pas connectée.
    /// </summary>
    public void FlushAndBye(int timeoutMs)
    {
        if (!IsConnected)
        {
            // Liaison absente : ne rien laisser en attente, sinon un BYE partirait à la reconnexion.
            _flushRequest = 0;
            return;
        }
        _flushDone.Reset();
        _flushRequest = 1;
        _wake.Set();
        _flushDone.Wait(timeoutMs);
    }

    /// <summary>Tampon d'image à remplir (Pbgra32 = XRGB8888 en mémoire), à rendre via <see cref="Submit"/>.</summary>
    public byte[] RentFrame()
    {
        lock (_free)
            return _free.Count > 0 ? _free.Pop() : new byte[FrameBytes];
    }

    /// <summary>Nombre d'images proposées d'affilée sans aucune différence avec l'écran (l'écran est au repos).</summary>
    public int UnchangedFrames => _unchangedFrames;

    /// <summary>Propose une image complète 1280x720 ; seules ses différences avec l'écran partiront.</summary>
    public void Submit(byte[] frame)
    {
        var previous = Interlocked.Exchange(ref _pending, frame);
        if (previous is not null) Recycle(previous);
        _wake.Set();
    }

    private void Recycle(byte[] frame)
    {
        lock (_free)
            if (_free.Count < 2) _free.Push(frame);
    }

    // ---------- Thread USB ----------

    private void Run()
    {
        while (!_stopping)
        {
            if (!_enabled)
            {
                _wake.WaitOne(1000);
                continue;
            }

            DeckUsb? usb = null;
            try
            {
                usb = DeckUsb.TryOpen();
            }
            catch (Exception ex)
            {
                Log.WarnOnce("deck:open", "Ouverture de la Switch impossible", ex);
            }

            if (usb is null)
            {
                _wake.WaitOne(1500);
                continue;
            }

            using (usb)
            {
                try
                {
                    Serve(usb);
                }
                catch (Exception ex) when (IsLinkError(ex))
                {
                    LogLinkLoss("Switch déconnectée", ex);
                }
                catch (Exception ex)
                {
                    Log.Error("Erreur de la liaison avec la Switch", ex);
                }
            }

            // Liaison tombée pendant un envoi final : plus personne ne l'exécutera, on libère l'appelant
            // et on évite qu'un BYE périmé parte à la reconnexion.
            if (_flushRequest != 0)
            {
                _flushRequest = 0;
                _flushDone.Set();
            }

            _shown = null;
            Publish(_status with { Connected = false });
            if (!_stopping) _wake.WaitOne(1000);
        }
    }

    private void Serve(DeckUsb usb)
    {
        Handle(usb.Ping(), connected: true);
        Log.Info("Switch connectée");
        _shown = null; // écran inconnu : la première image part en entier
        _nightSent = null; // kit de la Switch inconnu : renvoyé
        _nightStored = false;
        _nightUnsupported = false;
        _nightBrightnessUnsupported = false;

        _brightnessSent = -1;
        while (!_stopping && _enabled)
        {
            if (_flushRequest != 0)
            {
                FlushAndStop(usb);
                return;
            }

            if (_resync)
            {
                _resync = false;
                _shown = null;
                _brightnessSent = -1;
            }

            if (_brightnessSent != _brightness)
            {
                _brightnessSent = _brightness;
                Handle(usb.SetBrightness(_brightnessSent), what: "luminosité");
            }

            SyncNight(usb);

            var frame = Interlocked.Exchange(ref _pending, null);
            if (frame is not null)
            {
                try
                {
                    SendChanges(usb, frame);
                }
                finally
                {
                    Recycle(frame); // même si l'envoi lève : le tampon n'est plus utilisé
                }
            }
            else
            {
                Handle(usb.Ping());
                // Réveillé tout de suite par une nouvelle image ; sinon ~60 interrogations/s pour le tactile.
                _wake.WaitOne(16);
            }
        }

        if (_stopping || !_enabled)
        {
            try
            {
                SyncNight(usb, final: true);
                usb.Bye();
            }
            catch (Exception ex) when (IsLinkError(ex)) { } // liaison déjà perdue : rien à signaler
        }
    }

    private static bool IsLinkError(Exception ex) => ex is IOException or InvalidDataException or Win32Exception;

    private static void LogLinkLoss(string context, Exception ex) => Log.Info($"{context} : {ex.Message}");

    /// <summary>
    /// Envoi final demandé par <see cref="FlushAndBye"/> : kit à jour, heure, BYE. La session se termine ensuite
    /// (le PC s'éteint ou se met en veille) ; <see cref="Run"/> reprendra la recherche au réveil.
    /// </summary>
    private void FlushAndStop(DeckUsb usb)
    {
        try
        {
            SyncNight(usb, final: true);
            usb.Bye();
        }
        catch (Exception ex) when (IsLinkError(ex))
        {
            LogLinkLoss("Envoi final à la Switch impossible", ex);
        }
        finally
        {
            _flushRequest = 0;
            _flushDone.Set();
        }
    }

    /// <summary>
    /// Tient le kit nuit de la Switch à jour : envoi d'un nouveau kit, effacement si retiré, puis recalage de
    /// l'heure toutes les ~10 min (ou tout de suite si <paramref name="final"/>). Une Switch qui refuse le kit
    /// (ancien payload : commande inconnue) n'est plus sollicitée jusqu'à la reconnexion.
    /// </summary>
    private void SyncNight(DeckUsb usb, bool final = false)
    {
        if (_nightUnsupported) return;

        var kit = _nightKit;
        var now = DateTime.Now;
        if (!ReferenceEquals(kit, _nightSent))
        {
            if (kit is null)
            {
                if (_nightStored) Handle(usb.ClearNight(), checkStatus: false);
                _nightStored = false;
            }
            else
            {
                DeckProtocol.SetNightTime(kit, DeckProtocol.LocalEpoch(now));
                var reply = usb.SendNight(kit);
                Handle(reply, checkStatus: false); // refus géré ci-dessous (ancien payload : BadCmd)
                _nightStored = reply.Status == DeckProtocol.Status.Ok;
                _nightUnsupported = reply.Status == DeckProtocol.Status.BadCmd;
                if (!_nightStored) Log.Info($"Kit nuit refusé par la Switch (état {reply.Status})");
            }
            _nightSent = kit;
            _nightTimeSent = now;
            _nightBrightnessSent = _nightBrightness; // le kit porte déjà la luminosité voulue
        }
        else if (_nightStored)
        {
            var wanted = _nightBrightness;
            if (wanted >= 0 && wanted != _nightBrightnessSent && !_nightBrightnessUnsupported)
            {
                _nightBrightnessSent = wanted;
                var reply = usb.SendNightBrightness(wanted);
                Handle(reply, checkStatus: false);
                if (reply.Status == DeckProtocol.Status.BadCmd)
                {
                    _nightBrightnessUnsupported = true; // le contrôleur renverra le kit complet
                    Log.Info("Payload sans commande de luminosité de nuit : kit complet renvoyé à chaque changement");
                }
                else if (reply.Status != DeckProtocol.Status.Ok)
                {
                    _nightSent = null; // kit perdu côté Switch : renvoyé
                }
            }

            if (final || now - _nightTimeSent >= NightResync)
            {
                Handle(usb.SendNightTime(DeckProtocol.LocalEpoch(now)), checkStatus: false);
                _nightTimeSent = now;
            }
        }
    }

    /// <summary>
    /// Compare l'image à ce qu'affiche la Switch et n'envoie que les bandes de lignes modifiées
    /// (bornées à gauche et à droite) ; la dernière demande l'affichage.
    /// </summary>
    private void SendChanges(DeckUsb usb, byte[] frame)
    {
        var bands = new List<(int Y0, int Y1, int X0, int X1)>();
        var shown = _shown;
        if (shown is null)
        {
            bands.Add((0, H, 0, W));
            _shown = shown = new byte[FrameBytes];
        }
        else
        {
            FindChangedBands(frame, shown, bands);
            if (bands.Count == 0)
            {
                _unchangedFrames++;
                return;
            }
        }
        _unchangedFrames = 0;

        for (var i = 0; i < bands.Count; i++)
        {
            var (y0, y1, x0, x1) = bands[i];
            int w = x1 - x0, h = y1 - y0;
            var length = DeckProtocol.PaddedLength(w * h * 4);
            if (_rectBuffer.Length < length) _rectBuffer = new byte[length];

            for (var y = y0; y < y1; y++)
            {
                var src = frame.AsSpan(y * Stride + x0 * 4, w * 4);
                src.CopyTo(_rectBuffer.AsSpan((y - y0) * w * 4));
                src.CopyTo(shown.AsSpan(y * Stride + x0 * 4));
            }
            Handle(usb.SendRect(x0, y0, w, h, _rectBuffer, present: i == bands.Count - 1), what: "rectangle");
            if (_shown is null) return; // rejet : Handle a invalidé l'écran, le reste partira au prochain cycle en entier
        }
    }

    internal static void FindChangedBands(byte[] frame, byte[] shown, List<(int, int, int, int)> bands)
    {
        const int MergeGap = 8; // lignes inchangées tolérées dans une bande (moins de commandes)
        int start = -1, last = -1, x0 = W, x1 = 0;

        for (var y = 0; y < H; y++)
        {
            var a = MemoryMarshal.Cast<byte, uint>(frame.AsSpan(y * Stride, Stride));
            var b = MemoryMarshal.Cast<byte, uint>(shown.AsSpan(y * Stride, Stride));
            var first = a.CommonPrefixLength(b);
            if (first == W) continue;

            var end = W - 1;
            while (a[end] == b[end]) end--;

            if (start >= 0 && y - last > MergeGap)
            {
                bands.Add(Widen(start, last + 1, x0, x1));
                start = -1;
            }
            if (start < 0) { start = y; x0 = W; x1 = 0; }
            last = y;
            x0 = Math.Min(x0, first);
            x1 = Math.Max(x1, end + 1);
        }
        if (start >= 0) bands.Add(Widen(start, last + 1, x0, x1));

        // Trop de petites bandes : une seule zone englobante coûte moins de commandes.
        if (bands.Count > 6)
        {
            var merged = (bands.Min(b => b.Item1), bands.Max(b => b.Item2), bands.Min(b => b.Item3), bands.Max(b => b.Item4));
            bands.Clear();
            bands.Add(Widen(merged.Item1, merged.Item2, merged.Item3, merged.Item4));
        }
    }

    /// <summary>Une bande presque pleine largeur part en pleine largeur : la Switch la reçoit sans copie.</summary>
    private static (int, int, int, int) Widen(int y0, int y1, int x0, int x1)
        => x1 - x0 > W * 3 / 4 ? (y0, y1, 0, W) : (y0, y1, x0, x1);

    // ---------- Boutons de volume de la Switch ----------
    // Un appui seul, tenu 150 ms, donne un pas (le double appui VOL+ VOL- sert à redémarrer la Switch : pas de pas
    // parasite) ; au-delà de 500 ms, un pas toutes les 150 ms. Les réponses arrivent au moins toutes les 100 ms.
    private const int VolumeHoldMs = 150, VolumeRepeatAfterMs = 500, VolumeRepeatMs = 150;
    private byte _volButtons;
    private long _volPressedAt, _volLastStep;

    private void TrackVolumeButtons(byte buttons)
    {
        var vol = (byte)(buttons & (DeckProtocol.ButtonVolUp | DeckProtocol.ButtonVolDown));
        if (vol != DeckProtocol.ButtonVolUp && vol != DeckProtocol.ButtonVolDown)
        {
            _volButtons = 0; // rien, ou les deux
            return;
        }

        var now = Environment.TickCount64;
        if (vol != _volButtons)
        {
            _volButtons = vol;
            _volPressedAt = now;
            _volLastStep = 0;
            return;
        }

        var held = now - _volPressedAt;
        if (held < VolumeHoldMs) return;
        if (_volLastStep != 0 && (held < VolumeRepeatAfterMs || now - _volLastStep < VolumeRepeatMs)) return;

        _volLastStep = now;
        VolumeStep?.Invoke(vol == DeckProtocol.ButtonVolUp ? 1 : -1);
    }

    private void Handle(DeckProtocol.Reply reply, bool connected = true, bool checkStatus = true, string what = "commande")
    {
        if (checkStatus && reply.Status != DeckProtocol.Status.Ok)
        {
            // La Switch a refusé la commande : son écran ne correspond plus à ce que le PC croit y avoir envoyé.
            Log.WarnOnce($"deck:status:{what}:{reply.Status}", $"La Switch a refusé la {what} (état {reply.Status})");
            _shown = null;
        }

        // La Switch vient d'être réveillée (ou s'était remise en attente après un silence) : son écran et sa
        // luminosité sont ceux de l'écran de connexion, tout est à renvoyer.
        if (reply.NewSession) _resync = true;

        if (reply.Battery != _status.Battery || reply.Charging != _status.Charging || !_status.Connected)
            Publish(new DeckStatus(connected, reply.Battery, reply.Charging));

        TrackVolumeButtons(reply.Buttons);

        if (reply.Version != PayloadVersion)
            Log.Info(reply.Version >= 6
                ? $"Switch : payload protocole v{reply.Version} (Joy-Con pris en charge)"
                : $"Switch : payload protocole v{reply.Version}, trop ancien pour les Joy-Con (copier le nouveau switchdeck.bin)");
        PayloadVersion = reply.Version;
        var joyCon = reply.JoyCon;
        if (joyCon.DebugLeft != _joyConDebug.Left || joyCon.DebugRight != _joyConDebug.Right)
        {
            Log.Info($"Joy-Con (diagnostic) : gauche {DeckProtocol.JoyConState.DescribeDebug(joyCon.DebugLeft)}, " +
                     $"droit {DeckProtocol.JoyConState.DescribeDebug(joyCon.DebugRight)}");
            _joyConDebug = (joyCon.DebugLeft, joyCon.DebugRight);
        }
        if (joyCon.Flags != _joyConFlags)
        {
            Log.Info($"Joy-Con : gauche {(joyCon.LeftConnected ? "connecté" : "absent")}, droit {(joyCon.RightConnected ? "connecté" : "absent")}" +
                     (joyCon.Lite ? " (Switch Lite)" : ""));
            _joyConFlags = joyCon.Flags;
        }
        if (joyCon.AnyConnected || _joyConSeen)
        {
            _joyConSeen = joyCon.AnyConnected;
            _dispatcher.BeginInvoke(() => JoyCon?.Invoke(joyCon));
        }

        if (reply.Events.Length == 0) return;
        var events = reply.Events;
        _dispatcher.BeginInvoke(() =>
        {
            foreach (var e in events) Touch?.Invoke(e);
        });
    }

    private void Publish(DeckStatus status)
    {
        _status = status;
        _dispatcher.BeginInvoke(() => StatusChanged?.Invoke(status));
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        // Laisse le temps d'envoyer BYE (la Switch revient à son écran d'attente).
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }
}
