using SysDeck.Infrastructure;

namespace SysDeck.Services.ConsoleMode;

/// <summary>
/// La TV par le réseau pour le mode console : appairage, allumage à l'entrée, mise en veille à la sortie, volume.
/// Ne fait rien tant que la TV n'est pas appairée (adresse + certificat dans <see cref="ConsoleSettings"/>).
/// </summary>
public sealed class TvRemoteService : IDisposable
{
    private readonly Func<ConsoleSettings> _settings;
    private readonly Action _save;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AndroidTvRemote.Link? _link;
    private AndroidTvRemote.Pairing? _pairing;
    private byte[]? _pendingPfx;
    private string? _pendingHost;

    public TvRemoteService(Func<ConsoleSettings> settings, Action save)
    {
        _settings = settings;
        _save = save;
    }

    /// <summary>Volume de la TV reçu (niveau, maximum, muet).</summary>
    public event Action<int, int, bool>? VolumeChanged;

    public bool IsPaired => !string.IsNullOrWhiteSpace(_settings().TvRemoteHost) && Pfx() != null;

    private byte[]? Pfx()
    {
        var b64 = _settings().TvRemoteCertificate;
        if (string.IsNullOrWhiteSpace(b64)) return null;
        try { return Convert.FromBase64String(b64); } catch { return null; }
    }

    // ---------- Recherche ----------

    public sealed record FoundTv(string Host, string Name);

    /// <summary>TV Google TV du réseau local : appareils qui répondent sur le port de la télécommande (6466), avec leur nom Cast.</summary>
    public static async Task<List<FoundTv>> DiscoverAsync(CancellationToken ct = default)
    {
        var result = new List<FoundTv>();
        try
        {
            var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (local == null) return result;
            var b = local.GetAddressBytes();
            var probes = Enumerable.Range(1, 254).Select(async i =>
            {
                var ip = $"{b[0]}.{b[1]}.{b[2]}.{i}";
                using var tcp = new System.Net.Sockets.TcpClient();
                try
                {
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    t.CancelAfter(600);
                    await tcp.ConnectAsync(ip, AndroidTvRemote.RemotePort, t.Token);
                    return ip;
                }
                catch { return null; }
            });
            var hosts = (await Task.WhenAll(probes)).Where(h => h != null).Select(h => h!).ToList();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            foreach (var host in hosts)
            {
                var name = host;
                try
                {
                    var json = await http.GetStringAsync($"http://{host}:8008/setup/eureka_info?params=name", ct);
                    var m = System.Text.RegularExpressions.Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) name = $"{m.Groups[1].Value} ({host})";
                }
                catch { }
                result.Add(new FoundTv(host, name));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("TV réseau : recherche impossible", ex);
        }
        return result;
    }

    // ---------- Appairage ----------

    /// <summary>Étape 1 : la TV affiche un code à 6 caractères.</summary>
    public async Task StartPairingAsync(string host, CancellationToken ct = default)
    {
        CancelPairing();
        _pendingPfx = AndroidTvRemote.CreateClientCertificate();
        _pendingHost = host.Trim();
        _pairing = await AndroidTvRemote.Pairing.StartAsync(_pendingHost, _pendingPfx, ct);
        Log.Info($"TV réseau : appairage démarré avec {_pendingHost}.");
    }

    /// <summary>Étape 2 : code tapé par l'utilisateur. Vrai si la TV l'accepte (adresse et certificat enregistrés).</summary>
    public async Task<bool> FinishPairingAsync(string code, CancellationToken ct = default)
    {
        if (_pairing == null || _pendingPfx == null || _pendingHost == null) return false;
        try
        {
            if (!await _pairing.FinishAsync(code, ct)) return false;
            var s = _settings();
            s.TvRemoteHost = _pendingHost;
            s.TvRemoteCertificate = Convert.ToBase64String(_pendingPfx);
            s.TvRemoteMac = WakeOnLan.ResolveMac(_pendingHost) ?? s.TvRemoteMac;
            _save();
            Log.Info($"TV réseau : appairée ({_pendingHost}).");
            return true;
        }
        finally
        {
            CancelPairing();
        }
    }

    public void CancelPairing()
    {
        _pairing?.Dispose();
        _pairing = null;
    }

    public void Forget()
    {
        CloseLink();
        var s = _settings();
        s.TvRemoteHost = "";
        s.TvRemoteCertificate = "";
        _save();
    }

    // ---------- Commandes ----------

    private async Task<AndroidTvRemote.Link?> LinkAsync(bool quiet = false)
    {
        if (!IsPaired) return null;
        await _gate.WaitAsync();
        try
        {
            if (_link is { Closed: false }) return _link;
            _link?.Dispose();
            _link = await AndroidTvRemote.Link.OpenAsync(_settings().TvRemoteHost, Pfx()!, CancellationToken.None);
            _link.StateChanged += st => VolumeChanged?.Invoke(st.Volume, st.VolumeMax, st.Muted);
            // Adresse MAC apprise dès que la TV répond (réveil réseau quand elle sera en veille profonde).
            var settings = _settings();
            if (string.IsNullOrWhiteSpace(settings.TvRemoteMac) && WakeOnLan.ResolveMac(settings.TvRemoteHost) is { } mac)
            {
                settings.TvRemoteMac = mac;
                _save();
                Log.Info($"TV réseau : adresse MAC mémorisée ({mac}).");
            }
            return _link;
        }
        catch (Exception ex)
        {
            if (!quiet) Log.WarnOnce("tv-link", "TV réseau : connexion impossible (TV débranchée du réseau, ou appairage perdu ?)", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Allume la TV (comportement mesuré sur la Philips Android TV le 10/11) :
    /// - TV joignable en veille : seule la touche « marche/arrêt » l'allume, en 6 à 10 s (le paquet de réveil et la
    ///   touche « réveil » sont ignorés). Elle BASCULE : envoyée une seule fois, et seulement si la TV dit « en veille ».
    /// - TV injoignable (veille profonde) : paquet Wake-on-LAN / WoWLAN, qui l'allume directement ; si elle revient
    ///   sur le réseau sans s'allumer, une touche « marche/arrêt ».
    /// Vrai si la TV se dit allumée.
    /// </summary>
    public async Task<bool> WakeAsync()
    {
        var host = _settings().TvRemoteHost;
        var mac = _settings().TvRemoteMac;
        var link = await LinkAsync(quiet: true);

        if (link == null)
        {
            if (string.IsNullOrWhiteSpace(mac))
            {
                Log.Warn("TV réseau : TV injoignable et adresse MAC inconnue : réveil impossible.");
                return false;
            }
            Log.Info($"TV réseau : TV injoignable, paquet de réveil envoyé à {mac}.");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastSend = -10_000;
            while (sw.Elapsed < TimeSpan.FromSeconds(25))
            {
                if (sw.ElapsedMilliseconds - lastSend >= 3000) { WakeOnLan.Send(mac, host); lastSend = sw.ElapsedMilliseconds; }
                await Task.Delay(1000);
                if (!await PortOpenAsync(host, AndroidTvRemote.RemotePort)) continue;
                link = await LinkAsync(quiet: true);
                if (link != null) break;
            }
            if (link == null)
            {
                Log.Warn("TV réseau : la TV ne répond pas au réveil. Sur la TV, active « Wake on Wireless LAN ».");
                return false;
            }
            Log.Info($"TV réseau : TV revenue sur le réseau après {sw.Elapsed.TotalSeconds:0.0} s.");
            // Le réveil réseau allume en général l'écran : on lui laisse 8 s avant d'insister.
            if (await WaitOnAsync(8)) { Log.Info("TV réseau : TV allumée par le paquet de réveil."); return true; }
        }

        link = _link;
        if (link == null) return false;
        Log.Info($"TV réseau : connectée, allumée = {link.IsOn?.ToString() ?? "inconnu"}.");
        if (link.IsOn == true) return true;
        if (link.IsOn != false)
        {
            // État inconnu : on attend qu'il arrive plutôt que de risquer d'éteindre la TV avec « marche/arrêt ».
            if (await WaitOnAsync(3)) return true;
            if (_link?.IsOn != false) { Log.Warn("TV réseau : état de la TV inconnu, allumage annulé."); return false; }
        }

        Log.Info("TV réseau : touche marche/arrêt envoyée (une seule fois).");
        await _link!.KeyAsync(AndroidTvRemote.KeyPower);
        if (await WaitOnAsync(15)) { Log.Info("TV réseau : TV allumée."); return true; }
        Log.Warn($"TV réseau : la TV ne s'est pas déclarée allumée (état reçu : {_link?.IsOn?.ToString() ?? "aucun"}).");
        return false;
    }

    /// <summary>Attend (au plus <paramref name="seconds"/> s) que la TV se dise allumée ; se reconnecte si la TV coupe la connexion en s'allumant.</summary>
    private async Task<bool> WaitOnAsync(int seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var link = _link;
            if (link?.IsOn == true) return true;
            if (link == null || link.Closed)
            {
                await Task.Delay(1000);
                link = await LinkAsync(quiet: true);
                if (link?.IsOn == true) return true;
                continue;
            }
            await Task.Delay(250);
        }
        return _link?.IsOn == true;
    }

    private static async Task<bool> PortOpenAsync(string host, int port)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(800);
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Met la TV en veille si elle est allumée.</summary>
    public async Task SleepAsync()
    {
        var link = await LinkAsync();
        if (link == null || link.IsOn == false) { CloseLink(); return; }
        Log.Info("TV réseau : mise en veille.");
        await link.KeyAsync(AndroidTvRemote.KeySleep);
        for (var i = 0; i < 12 && link.IsOn != false && !link.Closed; i++) await Task.Delay(250);
        if (link.IsOn == true) await link.KeyAsync(AndroidTvRemote.KeyPower);
        await Task.Delay(500);
        CloseLink();
    }

    /// <summary>
    /// Passe la TV sur l'entrée HDMI du PC : méthode trouvée par l'assistant (TvInputCommand), sinon touche
    /// « HDMI n » puis adresse Philips/MediaTek. Faux si non réglé ou TV injoignable.
    /// </summary>
    public async Task<bool> SelectPcInputAsync(int? input = null)
    {
        var s = _settings();
        var n = input ?? s.TvHdmiInput;
        if (n is < 1 or > 4) return false;
        var link = await LinkAsync();
        if (link == null) return false;
        if (input == null && !string.IsNullOrWhiteSpace(s.TvInputCommand))
        {
            Log.Info($"TV réseau : entrée du PC ({s.TvInputCommand}).");
            return await RunInputCommandAsync(s.TvInputCommand);
        }
        Log.Info($"TV réseau : entrée HDMI {n} demandée (méthode par défaut).");
        await link.KeyAsync(AndroidTvRemote.KeyHdmi(n));          // la plupart des Google TV / Android TV
        await Task.Delay(300);
        await link.AppLinkAsync(HdmiInputUri(n));                // Philips Android TV (touches HDMI ignorées)
        return true;
    }

    /// <summary>
    /// Adresse Android TV de l'entrée HDMI n sur les Philips Android TV à puce MediaTek (mesuré le 10/11 :
    /// HDMI 1 = HW5, HDMI 2 = HW6, HDMI 3 = HW7, HDMI 4 = HW8, soit HW4 + n).
    /// </summary>
    public static string HdmiInputUri(int n) => Passthrough("com.mediatek.tvinput%2F.hdmi.HDMIInputService", 4 + Math.Clamp(n, 1, 4));

    private static string Passthrough(string service, int hw) => $"content://android.media.tv/passthrough/{service}%2FHW{hw}";

    /// <summary>
    /// Méthodes connues pour passer sur l'entrée HDMI n, à essayer une par une (assistant des Paramètres) :
    /// touche HDMI standard, puis adresses des principaux fabricants. Une méthode fausse ne fait rien.
    /// </summary>
    public static List<(string Label, string Command)> InputCandidates(int n)
    {
        n = Math.Clamp(n, 1, 4);
        var list = new List<(string, string)>
        {
            ("Touche « HDMI » standard (Sony, TCL, Xiaomi...)", $"key:{AndroidTvRemote.KeyHdmi(n)}"),
            ("Philips / MediaTek (HW+4)", "link:" + HdmiInputUri(n)),
            ("MediaTek (HW+1)", "link:" + Passthrough("com.mediatek.tvinput%2F.hdmi.HDMIInputService", n + 1)),
            ("MediaTek (HW)", "link:" + Passthrough("com.mediatek.tvinput%2F.hdmi.HDMIInputService", n)),
            ("Sony Bravia", "link:" + Passthrough("com.sony.dtv.tvinput.external%2F.ExternalTvInputService", n + 1)),
            ("Realtek (TCL, Hisense...)", "link:" + Passthrough("com.realtek.tv.passthrough%2F.hdmiinput.HDMITvInputService", n)),
        };
        return list;
    }

    /// <summary>Exécute une méthode d'entrée (« key:243 » ou « link:content://... »).</summary>
    public async Task<bool> RunInputCommandAsync(string command)
    {
        var link = await LinkAsync();
        if (link == null) return false;
        if (command.StartsWith("key:") && int.TryParse(command[4..], out var key)) await link.KeyAsync(key);
        else if (command.StartsWith("link:")) await link.AppLinkAsync(command[5..]);
        else return false;
        return true;
    }

    /// <summary>Écran d'accueil de la TV (pour qu'un changement d'entrée soit bien visible pendant l'assistant).</summary>
    public async Task<bool> HomeAsync()
    {
        var link = await LinkAsync();
        if (link == null) return false;
        await link.KeyAsync(3);   // KEYCODE_HOME
        return true;
    }

    /// <summary>Volume de la TV : +1 / -1 cran. Faux si la TV n'est pas joignable (l'appelant règle alors Windows).</summary>
    public async Task<bool> VolumeStepAsync(int direction)
    {
        var link = await LinkAsync();
        if (link == null) return false;
        await link.KeyAsync(direction > 0 ? AndroidTvRemote.KeyVolumeUp : AndroidTvRemote.KeyVolumeDown);
        return true;
    }

    public void CloseLink()
    {
        _link?.Dispose();
        _link = null;
    }

    public void Dispose()
    {
        CancelPairing();
        CloseLink();
    }
}
