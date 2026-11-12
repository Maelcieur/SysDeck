using System.Runtime.InteropServices;
using System.Windows.Threading;
using HidSharp;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Sondage des manettes (~60 Hz, thread de fond) : Xbox via XInput, Switch Pro / Joy-Con via HID (HidSharp).
/// Les évènements sont envoyés sur le Dispatcher fourni. Ne plante jamais si une manette est prise par Steam.
/// </summary>
public sealed class GamepadService : IDisposable
{
    private const int RepeatDelayMs = 400, RepeatIntervalMs = 120, GuideLongMs = 1000;
    private const PadButtons Directions = PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right;

    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, SwitchPad> _switchPads = new();
    private IReadOnlyList<ControllerInfo> _controllers = Array.Empty<ControllerInfo>();
    private Thread? _thread;
    private volatile bool _running;

    public GamepadService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Navigation : direction (avec répétition), A, B, X, Y, Menu, Guide (relâché court), onglets.</summary>
    public event Action<PadButtons>? ButtonPressed;
    /// <summary>Appui long (1 s) sur Guide / Home.</summary>
    public event Action? GuideLongPressed;
    /// <summary>La liste des manettes ou une batterie a changé.</summary>
    public event Action? ControllersChanged;

    public IReadOnlyList<ControllerInfo> Controllers { get { lock (_gate) return _controllers; } }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SysDeck.Gamepad", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        lock (_gate)
        {
            foreach (var p in _switchPads.Values) p.Close();
            _switchPads.Clear();
        }
    }

    public void Dispose() => Stop();

    // ---------- Vibration ----------

    private volatile int _activeSlot = -1;
    private int _rumbleGen;

    /// <summary>
    /// Vibration de la manette Xbox utilisée : moteurs gauche (grave) et droit (aigu) de 0 à 1, pendant <paramref name="ms"/>.
    /// Sans effet sur les manettes Switch.
    /// </summary>
    public void Rumble(double low, double high, int ms)
    {
        var slot = _activeSlot;
        if (slot < 0 || !XInput.Available) return;
        var gen = Interlocked.Increment(ref _rumbleGen);
        XInput.SetVibration((uint)slot, low, high);
        _ = Task.Delay(ms).ContinueWith(_ =>
        {
            if (gen == Volatile.Read(ref _rumbleGen)) XInput.SetVibration((uint)slot, 0, 0);
        });
    }

    // ---------- Boucle ----------

    private void Loop()
    {
        var xinput = new bool[4];
        var xinputCheck = new long[4];
        var xinputBattery = new (int Percent, bool Wired)[4];
        var xinputBatteryAt = new long[4];
        var prev = PadButtons.None;
        long dirSince = 0, nextRepeat = 0, guideSince = 0, volRepeat = 0;
        var guideLongDone = false;
        long lastScan = -10000, lastBt = -60000;
        var btXbox = new List<int>();          // batteries Bluetooth des manettes Xbox (ordre d'énumération)
        string? activeId = null;
        var xinputOk = XInput.Available;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (_running)
        {
            try
            {
                var now = sw.ElapsedMilliseconds;
                var combined = PadButtons.None;
                var list = new List<ControllerInfo>();

                if (xinputOk && now - lastBt >= 30000)
                {
                    lastBt = now;
                    btXbox = BluetoothBattery.Read().Where(e => BluetoothBattery.IsXboxName(e.Name)).Select(e => e.Percent).ToList();
                }

                if (xinputOk)
                {
                    var xboxIndex = 0;
                    for (uint i = 0; i < 4; i++)
                    {
                        if (!xinput[i] && now - xinputCheck[i] < 2000) continue;   // les emplacements vides coûtent cher
                        xinputCheck[i] = now;
                        if (!XInput.TryGetState(i, out var st))
                        {
                            xinput[i] = false;
                            continue;
                        }
                        if (!xinput[i]) Log.Info($"Mode console : manette Xbox détectée (emplacement {i + 1}, Guide {(XInput.HasGuide ? "lu" : "non lu")}).");
                        xinput[i] = true;
                        var pad =GamepadDecoder.FromXInput(st.Buttons, st.LX, st.LY, st.LT, st.RT);
                        combined |= pad;
                        if (pad != PadButtons.None) activeId = $"xinput{i}";
                        if (now - xinputBatteryAt[i] > 5000 || xinputBatteryAt[i] == 0)
                        {
                            xinputBatteryAt[i] = Math.Max(now, 1);
                            xinputBattery[i] = XInput.ReadBattery(i);
                        }
                        var (pct, wired) = xinputBattery[i];
                        // Bluetooth : XInput ne connaît pas la batterie, Windows si (même valeur que les Paramètres).
                        if (pct < 0 && !wired && xboxIndex < btXbox.Count) pct = btXbox[xboxIndex];
                        xboxIndex++;
                        list.Add(new ControllerInfo($"xinput{i}", "Manette Xbox", ControllerKind.Xbox, pct, false, wired));
                    }
                }

                if (now - lastScan >= 3000) { lastScan = now; ScanSwitch(); }
                lock (_gate)
                {
                    foreach (var p in _switchPads.Values.ToList())
                    {
                        if (p.Dead) { p.Close(); _switchPads.Remove(p.Path); continue; }
                        var inp = p.Latest;
                        if (inp == null) continue;
                        combined |= inp.Buttons;
                        if (inp.Buttons != PadButtons.None) activeId = p.Path;
                        list.Add(new ControllerInfo(p.Path, p.Name, p.Kind, inp.BatteryPercent, inp.Charging, inp.Wired));
                    }
                }
                // Manette active : la dernière qui a servi, sinon la première connectée.
                if (activeId == null || !list.Any(c => c.Id == activeId)) activeId = list.FirstOrDefault()?.Id;
                _activeSlot = activeId != null && activeId.StartsWith("xinput") && int.TryParse(activeId[6..], out var slotNo) ? slotNo
                    : list.FirstOrDefault(c => c.Id.StartsWith("xinput")) is { } firstPad && int.TryParse(firstPad.Id[6..], out var s2) ? s2 : -1;
                for (var k = 0; k < list.Count; k++)
                    if (list[k].Id == activeId) list[k] = list[k] with { Active = true };
                UpdateControllers(list);

                // Guide : court = évènement au relâchement, long = évènement à 1 s.
                var guideDown = (combined & PadButtons.Guide) != 0;
                if (guideDown && (prev & PadButtons.Guide) == 0) { guideSince = now; guideLongDone = false; }
                if (guideDown && !guideLongDone && now - guideSince >= GuideLongMs)
                {
                    guideLongDone = true;
                    Post(() => GuideLongPressed?.Invoke());
                }
                if (!guideDown && (prev & PadButtons.Guide) != 0 && !guideLongDone)
                    Post(() => ButtonPressed?.Invoke(PadButtons.Guide));

                var pressed = combined & ~prev & ~PadButtons.Guide;
                foreach (var b in new[] { PadButtons.A, PadButtons.B, PadButtons.X, PadButtons.Y, PadButtons.Menu, PadButtons.PrevTab, PadButtons.NextTab })
                    if ((pressed & b) != 0) { var bb = b; Post(() => ButtonPressed?.Invoke(bb)); }

                // Directions avec répétition (une seule direction à la fois : priorité haut, bas, gauche, droite).
                var dir = PadButtons.None;
                foreach (var d in new[] { PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right })
                    if ((combined & d) != 0) { dir = d; break; }
                var prevDir = PadButtons.None;
                foreach (var d in new[] { PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right })
                    if ((prev & d) != 0) { prevDir = d; break; }
                if (dir != PadButtons.None)
                {
                    if (dir != prevDir) { dirSince = now; nextRepeat = now + RepeatDelayMs; var dd = dir; Post(() => ButtonPressed?.Invoke(dd)); }
                    else if (now >= nextRepeat) { nextRepeat = now + RepeatIntervalMs; var dd = dir; Post(() => ButtonPressed?.Invoke(dd)); }
                }
                _ = dirSince;
                // Volume (gâchettes) : répété tant qu'on appuie.
                var vol = (combined & PadButtons.VolumeUp) != 0 ? PadButtons.VolumeUp : (combined & PadButtons.VolumeDown) != 0 ? PadButtons.VolumeDown : PadButtons.None;
                var prevVol = prev & (PadButtons.VolumeUp | PadButtons.VolumeDown);
                if (vol != PadButtons.None)
                {
                    if (vol != prevVol) { volRepeat = now + RepeatDelayMs; var vv = vol; Post(() => ButtonPressed?.Invoke(vv)); }
                    else if (now >= volRepeat) { volRepeat = now + 150; var vv = vol; Post(() => ButtonPressed?.Invoke(vv)); }
                }
                prev = combined & (Directions | PadButtons.Guide | PadButtons.A | PadButtons.B | PadButtons.X | PadButtons.Y
                    | PadButtons.Menu | PadButtons.PrevTab | PadButtons.NextTab | PadButtons.VolumeUp | PadButtons.VolumeDown);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("gamepad-loop", "Erreur du sondage des manettes", ex);
            }
            Thread.Sleep(16);
        }
    }

    private void Post(Action a)
    {
        try { _dispatcher.BeginInvoke(a); } catch { }
    }

    private void UpdateControllers(List<ControllerInfo> list)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_controllers.SequenceEqual(list);
            if (changed) _controllers = list;
        }
        if (changed) Post(() => ControllersChanged?.Invoke());
    }

    // ---------- Switch (HID) ----------

    private static readonly (int Pid, ControllerKind Kind, string Name)[] SwitchModels =
    {
        (0x2009, ControllerKind.SwitchPro, "Manette Switch Pro"),
        (0x2006, ControllerKind.JoyConLeft, "Joy-Con (G)"),
        (0x2007, ControllerKind.JoyConRight, "Joy-Con (D)"),
    };

    private void ScanSwitch()
    {
        try
        {
            foreach (var (pid, kind, name) in SwitchModels)
            {
                foreach (var dev in DeviceList.Local.GetHidDevices(0x057E, pid))
                {
                    var path = dev.DevicePath;
                    lock (_gate) if (_switchPads.ContainsKey(path)) continue;
                    if (!dev.TryOpen(out HidStream stream))
                    {
                        Log.WarnOnce("gamepad-hid-" + path, $"{name} : ouverture impossible (utilisée par Steam ?)");
                        continue;
                    }
                    var pad = new SwitchPad(path, name, kind, stream, dev.GetMaxOutputReportLength());
                    lock (_gate) _switchPads[path] = pad;
                    pad.Start();
                }
            }
        }
        catch (Exception ex)
        {
            Log.WarnOnce("gamepad-scan", "Recherche des manettes Switch impossible", ex);
        }
    }

    private sealed class SwitchPad
    {
        private readonly HidStream _stream;
        private readonly int _outLen;
        private byte _packet;
        private volatile bool _closed;

        public string Path { get; }
        public string Name { get; }
        public ControllerKind Kind { get; }
        public volatile GamepadDecoder.SwitchInput? Latest;
        public volatile bool Dead;

        public SwitchPad(string path, string name, ControllerKind kind, HidStream stream, int outLen)
        {
            Path = path; Name = name; Kind = kind; _stream = stream; _outLen = Math.Max(outLen, 49);
            _stream.ReadTimeout = 500;
        }

        public void Start() => new Thread(Read) { IsBackground = true, Name = "SysDeck.SwitchPad" }.Start();

        public void Close()
        {
            _closed = true;
            try { _stream.Dispose(); } catch { }
        }

        private void Read()
        {
            var buf = new byte[Math.Max(_stream.Device.GetMaxInputReportLength(), 64)];
            long lastSet;
            try
            {
                SetFullMode();
                lastSet = Environment.TickCount64;
                while (!_closed)
                {
                    int n;
                    try { n = _stream.Read(buf, 0, buf.Length); }
                    catch (TimeoutException) { continue; }
                    if (n <= 0) continue;
                    if (buf[0] == 0x30)
                    {
                        var inp = GamepadDecoder.ParseSwitchReport(buf, n, Kind);
                        if (inp != null) Latest = inp;
                    }
                    else if (buf[0] == 0x3F && Environment.TickCount64 - lastSet > 1000)
                    {
                        lastSet = Environment.TickCount64;
                        SetFullMode();     // mode simple : on demande le mode complet 0x30
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_closed) Log.WarnOnce("gamepad-read-" + Path, $"{Name} : lecture interrompue", ex);
            }
            Dead = true;
        }

        /// <summary>Sous-commande 0x03 (mode du rapport d'entrée) avec l'argument 0x30.</summary>
        private void SetFullMode()
        {
            try
            {
                var o = new byte[_outLen];
                o[0] = 0x01;
                o[1] = (byte)(_packet++ & 0x0F);
                o[2] = 0x00; o[3] = 0x01; o[4] = 0x40; o[5] = 0x40;   // vibration neutre
                o[6] = 0x00; o[7] = 0x01; o[8] = 0x40; o[9] = 0x40;
                o[10] = 0x03; o[11] = 0x30;
                _stream.Write(o);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("gamepad-write-" + Path, $"{Name} : changement de mode impossible", ex);
            }
        }
    }

    // ---------- XInput ----------

    private static class XInput
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct State
        {
            public uint Packet;
            public ushort Buttons;
            public byte LT, RT;
            public short LX, LY, RX, RY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Battery { public byte Type; public byte Level; }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState(uint user, out State state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern uint GetBattery(uint user, byte devType, out Battery info);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string name);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetStateExDelegate(uint user, out State state);

        private static readonly GetStateExDelegate? GetStateEx = LoadEx();
        public static bool Available { get; private set; } = Probe();
        public static bool HasGuide => GetStateEx != null;

        private static GetStateExDelegate? LoadEx()
        {
            try
            {
                var h = LoadLibrary("xinput1_4.dll");
                if (h == IntPtr.Zero) return null;
                var p = GetProcAddress(h, (IntPtr)100);
                return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<GetStateExDelegate>(p);
            }
            catch { return null; }
        }

        private static bool Probe()
        {
            try { GetState(0, out _); return true; }
            catch { return false; }
        }

        public static bool TryGetState(uint user, out State state)
        {
            state = default;
            try
            {
                var rc = GetStateEx != null ? GetStateEx(user, out state) : GetState(user, out state);
                return rc == 0;
            }
            catch { Available = false; return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Vibration { public ushort Left, Right; }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        private static extern uint SetState(uint user, ref Vibration vibration);

        public static void SetVibration(uint user, double low, double high)
        {
            try
            {
                var v = new Vibration { Left = (ushort)(Math.Clamp(low, 0, 1) * 65535), Right = (ushort)(Math.Clamp(high, 0, 1) * 65535) };
                SetState(user, ref v);
            }
            catch { }
        }

        public static (int Percent, bool Wired) ReadBattery(uint user)
        {
            try
            {
                if (GetBattery(user, 0, out var b) != 0) return (-1, false);
                return GamepadDecoder.FromXInputBattery(b.Type, b.Level);
            }
            catch { return (-1, false); }
        }
    }
}
