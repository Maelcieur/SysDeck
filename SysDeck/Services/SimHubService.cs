using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>Données de course lues dans SimHub.</summary>
public sealed record SimHubData(string Game, bool Running, string Gear, double Speed, double Rpm, double MaxRpm,
    TimeSpan? CurrentLap, TimeSpan? LastLap, TimeSpan? BestLap, int? Position, double? Fuel, double? FuelPercent);

/// <summary>
/// Client du plugin « SimHub Property Server » (TCP local, 18082 par défaut) : abonnement aux propriétés de jeu,
/// lignes « Property nom type valeur ». Reconnexion toutes les 5 s tant qu'un module SimHub est affiché.
/// Les lectures se font en arrière-plan ; Updated est levé sur le fil de l'interface, 10 fois par seconde au plus.
/// </summary>
public sealed class SimHubService : IDisposable
{
    public static readonly string[] Properties =
    {
        "dcp.GameName", "dcp.GameRunning", "dcp.gd.Gear", "dcp.gd.SpeedKmh", "dcp.gd.Rpms", "dcp.gd.MaxRpm",
        "dcp.gd.CurrentLapTime", "dcp.gd.LastLapTime", "dcp.gd.BestLapTime", "dcp.gd.Position", "dcp.gd.Fuel", "dcp.gd.FuelPercent"
    };

    private readonly ExtraWidgetSettings _settings;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _pushTimer;
    private readonly Dictionary<string, string?> _values = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private bool _dirty;

    public SimHubService(ExtraWidgetSettings settings)
    {
        _settings = settings;
        _pushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Push(), _dispatcher);
        _pushTimer.Stop();
    }

    public SimHubData? Current { get; private set; }

    /// <summary>État de la connexion en clair (null quand les données arrivent).</summary>
    public string? Status { get; private set; } = "Connexion à SimHub…";

    public event Action? Updated;

    public bool Enabled
    {
        get => _cts is not null;
        set
        {
            if (value == Enabled) return;
            if (value) Start();
            else Stop();
        }
    }

    public void Reconnect()
    {
        if (!Enabled) return;
        Stop();
        Start();
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var port = _settings.SimHubPort;
        _pushTimer.Start();
        _ = Task.Run(() => RunAsync(port, token));
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _pushTimer.Stop();
    }

    private async Task RunAsync(int port, CancellationToken token)
    {
        var warned = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await client.ConnectAsync("127.0.0.1", port, timeout.Token);
                }
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
                foreach (var p in Properties) await writer.WriteLineAsync(p.Insert(0, "subscribe "));
                SetStatus(null);
                warned = false;
                while (!token.IsCancellationRequested && await reader.ReadLineAsync(token) is { } line)
                {
                    if (ParseLine(line) is not { } prop) continue;
                    lock (_lock)
                    {
                        _values[prop.Name] = prop.Value;
                        _dirty = true;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!warned) Log.Info($"SimHub : Property Server injoignable sur le port {port} ({ex.Message})");
                warned = true;
            }
            SetStatus($"SimHub non détecté. Lance SimHub avec le plugin Property Server (port {port}).");
            try { await Task.Delay(5000, token); } catch (OperationCanceledException) { return; }
        }
    }

    private void SetStatus(string? status)
    {
        lock (_lock)
        {
            Status = status;
            _dirty = true;
        }
    }

    private void Push()
    {
        Dictionary<string, string?> values;
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            values = new Dictionary<string, string?>(_values);
        }
        Current = Status is null ? Build(values) : null;
        Updated?.Invoke();
    }

    /// <summary>« Property dcp.gd.Gear string 3 » → (nom, valeur) ; valeur null pour « (null) ».</summary>
    public static (string Name, string? Value)? ParseLine(string line)
    {
        if (!line.StartsWith("Property ", StringComparison.Ordinal)) return null;
        var parts = line.Split(' ', 4);
        if (parts.Length < 3) return null;
        var value = parts.Length == 4 ? parts[3].Trim() : "";
        return (parts[1], value == "(null)" ? null : value);
    }

    public static SimHubData Build(IReadOnlyDictionary<string, string?> v)
    {
        string? S(string k) => v.TryGetValue(k, out var s) ? s : null;
        double? D(string k) => double.TryParse(S(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
            : double.TryParse(S(k), NumberStyles.Float, CultureInfo.CurrentCulture, out d) ? d : null;
        int? I(string k) => D(k) is { } d ? (int)Math.Round(d) : null;
        return new SimHubData(S("dcp.GameName") ?? "", string.Equals(S("dcp.GameRunning"), "True", StringComparison.OrdinalIgnoreCase),
            S("dcp.gd.Gear") ?? "N", D("dcp.gd.SpeedKmh") ?? 0, D("dcp.gd.Rpms") ?? 0, D("dcp.gd.MaxRpm") ?? 0,
            Time(S("dcp.gd.CurrentLapTime")), Time(S("dcp.gd.LastLapTime")), Time(S("dcp.gd.BestLapTime")),
            I("dcp.gd.Position") is > 0 and var p ? p : null, D("dcp.gd.Fuel"), D("dcp.gd.FuelPercent"));
    }

    /// <summary>Durée envoyée par le plugin (« 00:01:23.4560000 », ou des secondes) ; null si vide ou nulle.</summary>
    public static TimeSpan? Time(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t) || TimeSpan.TryParse(s, CultureInfo.CurrentCulture, out t))
            return t > TimeSpan.Zero ? t : null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec > 0 ? TimeSpan.FromSeconds(sec) : null;
    }

    public static string FormatLap(TimeSpan? t) => t is { } v ? $"{(int)v.TotalMinutes}:{v.Seconds:00}.{v.Milliseconds:000}" : "-:--.---";

    public void Dispose() => Stop();
}
