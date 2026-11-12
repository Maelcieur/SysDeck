using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SysDeck.Infrastructure;

namespace SysDeck.Services.ConsoleMode;

/// <summary>
/// Protocole « télécommande Android TV v2 » (Google TV) utilisé EN ARRIÈRE-PLAN par le mode console :
/// allumer / éteindre la TV et régler son volume. Pas d'interface de télécommande.
/// Appairage (port 6467) : la TV affiche un code à 6 caractères ; commandes (port 6466) : TLS avec le certificat
/// client créé par SysDeck et mémorisé (chiffré DPAPI) après l'appairage.
/// </summary>
public static class AndroidTvRemote
{
    public const int PairingPort = 6467, RemotePort = 6466;

    // Touches Android.
    public const int KeyVolumeUp = 24, KeyVolumeDown = 25, KeyPower = 26, KeyMute = 164, KeySleep = 223, KeyWakeUp = 224;

    /// <summary>Touche « entrée HDMI n » (KEYCODE_TV_INPUT_HDMI_1 = 243 ... HDMI_4 = 246).</summary>
    public static int KeyHdmi(int n) => 242 + Math.Clamp(n, 1, 4);

    /// <summary>Certificat client autosigné (RSA 2048) pour cette installation, au format PFX.</summary>
    public static byte[] CreateClientCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=SysDeck", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return cert.Export(X509ContentType.Pfx);
    }

    public static X509Certificate2 LoadCertificate(byte[] pfx) =>
        new(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

    // ---------- Appairage ----------

    /// <summary>Appairage en deux temps : <see cref="StartAsync"/> fait afficher le code sur la TV, <see cref="FinishAsync"/> l'envoie.</summary>
    public sealed class Pairing : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly SslStream _ssl;
        private readonly X509Certificate2 _client;
        private X509Certificate2? _server;

        private Pairing(TcpClient tcp, SslStream ssl, X509Certificate2 client) { _tcp = tcp; _ssl = ssl; _client = client; }

        public static async Task<Pairing> StartAsync(string host, byte[] pfx, CancellationToken ct)
        {
            var client = LoadCertificate(pfx);
            var (tcp, ssl, server) = await ConnectAsync(host, PairingPort, client, ct);
            var p = new Pairing(tcp, ssl, client) { _server = server };
            // 1. Demande d'appairage.
            await p.SendAsync(Proto.Msg(w => { Head(w); w.Message(10, r => { r.String(1, "atvremote"); r.String(2, "SysDeck"); }); }), ct);
            await p.ExpectOkAsync(ct);
            // 2. Options : code hexadécimal à 6 caractères, nous sommes la « saisie ».
            await p.SendAsync(Proto.Msg(w => { Head(w); w.Message(20, o => { o.Message(1, e => { e.Varint(1, 3); e.Varint(2, 6); }); o.Varint(3, 1); }); }), ct);
            await p.ExpectOkAsync(ct);
            // 3. Configuration : la TV affiche alors le code.
            await p.SendAsync(Proto.Msg(w => { Head(w); w.Message(30, c => { c.Message(1, e => { e.Varint(1, 3); e.Varint(2, 6); }); c.Varint(2, 1); }); }), ct);
            await p.ExpectOkAsync(ct);
            return p;
        }

        /// <summary>Envoie le code affiché sur la TV ; faux si le code est faux.</summary>
        public async Task<bool> FinishAsync(string code, CancellationToken ct)
        {
            code = code.Trim().ToUpperInvariant();
            if (code.Length != 6 || !code.All(Uri.IsHexDigit)) return false;
            var secret = ComputeSecret(_client, _server!, code);
            if (secret[0] != Convert.ToByte(code[..2], 16)) return false;   // contrôle local du code
            await SendAsync(Proto.Msg(w => { Head(w); w.Message(40, s => s.Bytes(1, secret)); }), ct);
            await ExpectOkAsync(ct);
            return true;
        }

        private static void Head(Proto.Writer w) { w.Varint(1, 2); w.Varint(2, 200); }

        private Task SendAsync(byte[] msg, CancellationToken ct) => WriteFramedAsync(_ssl, msg, ct);

        private async Task ExpectOkAsync(CancellationToken ct)
        {
            var reply = await ReadFramedAsync(_ssl, ct);
            var status = Proto.Read(reply).FirstOrDefault(f => f.Field == 2)?.Varint ?? 0;
            if (status != 200) throw new IOException($"La TV a refusé l'appairage (statut {status}).");
        }

        public void Dispose()
        {
            try { _ssl.Dispose(); } catch { }
            _tcp.Dispose();
            _client.Dispose();
            _server?.Dispose();
        }
    }

    /// <summary>SHA-256(module client, exposant client, module TV, exposant TV, octets 3 à 6 du code).</summary>
    public static byte[] ComputeSecret(X509Certificate2 client, X509Certificate2 server, string code)
    {
        var c = client.GetRSAPublicKey()!.ExportParameters(false);
        var s = server.GetRSAPublicKey()!.ExportParameters(false);
        using var sha = SHA256.Create();
        var data = new List<byte>();
        data.AddRange(TrimZeros(c.Modulus!)); data.AddRange(TrimZeros(c.Exponent!));
        data.AddRange(TrimZeros(s.Modulus!)); data.AddRange(TrimZeros(s.Exponent!));
        data.AddRange(Convert.FromHexString(code[2..]));
        return sha.ComputeHash(data.ToArray());
    }

    private static byte[] TrimZeros(byte[] b)
    {
        var i = 0;
        while (i < b.Length - 1 && b[i] == 0) i++;
        return b[i..];
    }

    // ---------- Commandes ----------

    /// <summary>État reçu de la TV : allumée ou non, volume.</summary>
    public sealed record TvState(bool? IsOn, int Volume, int VolumeMax, bool Muted);

    /// <summary>
    /// Connexion de commande (port 6466) : fait la poignée de main, répond aux « ping » de la TV, garde l'état
    /// (allumée, volume) et envoie des touches.
    /// </summary>
    public sealed class Link : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly SslStream _ssl;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool? _isOn;
        private int _volume, _volumeMax = 100;
        private bool _muted;

        public event Action<TvState>? StateChanged;
        public TvState State => new(_isOn, _volume, _volumeMax, _muted);
        public bool Closed { get; private set; }

        private Link(TcpClient tcp, SslStream ssl) { _tcp = tcp; _ssl = ssl; }

        /// <summary>Se connecte et attend que la TV soit prête (état allumée/éteinte connu), 5 s au plus.</summary>
        public static async Task<Link> OpenAsync(string host, byte[] pfx, CancellationToken ct)
        {
            using var client = LoadCertificate(pfx);
            var (tcp, ssl, server) = await ConnectAsync(host, RemotePort, client, ct);
            server.Dispose();
            var link = new Link(tcp, ssl);
            _ = link.ReadLoopAsync();
            await Task.WhenAny(link._ready.Task, Task.Delay(5000, ct));
            if (link.Closed) throw new IOException("La TV a fermé la connexion (appairage perdu ?).");
            return link;
        }

        public bool? IsOn => _isOn;

        /// <summary>Ouvre un lien sur la TV (application, ou entrée HDMI : content://android.media.tv/passthrough/...).</summary>
        public Task AppLinkAsync(string uri) =>
            SendAsync(Proto.Msg(w => w.Message(90, a => a.String(1, uri))));

        public Task KeyAsync(int keyCode) =>
            SendAsync(Proto.Msg(w => w.Message(10, k => { k.Varint(1, keyCode); k.Varint(2, 3 /* appui court */); })));

        private async Task SendAsync(byte[] msg)
        {
            await _write.WaitAsync();
            try { await WriteFramedAsync(_ssl, msg, _cts.Token); }
            finally { _write.Release(); }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var msg = await ReadFramedAsync(_ssl, _cts.Token);
                    foreach (var f in Proto.Read(msg))
                    {
                        switch (f.Field)
                        {
                            case 1: // remote_configure -> on répond avec nos infos
                                await SendAsync(Proto.Msg(w => w.Message(1, c =>
                                {
                                    c.Varint(1, 622);
                                    c.Message(2, d => { d.String(1, "SysDeck"); d.String(2, "SysDeck"); d.Varint(3, 1); d.String(4, "1"); d.String(5, "atvremote"); d.String(6, "1.0.0"); });
                                })));
                                break;
                            case 2: // remote_set_active
                                await SendAsync(Proto.Msg(w => w.Message(2, a => a.Varint(1, 622))));
                                break;
                            case 8: // ping -> pong
                                var val = Proto.Read(f.Bytes!).FirstOrDefault(x => x.Field == 1)?.Varint ?? 0;
                                await SendAsync(Proto.Msg(w => w.Message(9, p => p.Varint(1, (long)val))));
                                break;
                            case 40: // remote_start : TV allumée ou en veille
                                _isOn = Proto.Read(f.Bytes!).FirstOrDefault(x => x.Field == 1)?.Varint == 1;
                                Log.Info($"TV réseau : la TV indique {(_isOn == true ? "allumée" : "en veille")}.");
                                _ready.TrySetResult();
                                StateChanged?.Invoke(State);
                                break;
                            case 50: // volume
                                var v = Proto.Read(f.Bytes!);
                                _volumeMax = (int)(v.FirstOrDefault(x => x.Field == 6)?.Varint ?? (ulong)_volumeMax);
                                _volume = (int)(v.FirstOrDefault(x => x.Field == 7)?.Varint ?? 0);
                                _muted = v.FirstOrDefault(x => x.Field == 8)?.Varint == 1;
                                StateChanged?.Invoke(State);
                                break;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
            }
            catch (Exception ex)
            {
                Log.WarnOnce("atv-read", "TV réseau : lecture interrompue", ex);
            }
            Closed = true;
            _ready.TrySetResult();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _ssl.Dispose(); } catch { }
            _tcp.Dispose();
        }
    }

    // ---------- Transport ----------

    private static async Task<(TcpClient, SslStream, X509Certificate2)> ConnectAsync(string host, int port, X509Certificate2 client, CancellationToken ct)
    {
        var tcp = new TcpClient();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(4000);
            await tcp.ConnectAsync(host, port, timeout.Token);
        }
        X509Certificate2? server = null;
        var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert != null) server = new X509Certificate2(cert);
            return true;   // certificat autosigné de la TV
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ClientCertificates = new X509CertificateCollection { client },
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, ct);
        return (tcp, ssl, server ?? throw new IOException("Certificat de la TV absent."));
    }

    private static async Task WriteFramedAsync(Stream s, byte[] msg, CancellationToken ct)
    {
        var len = Proto.VarintBytes((ulong)msg.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(msg, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadFramedAsync(Stream s, CancellationToken ct)
    {
        ulong len = 0;
        var shift = 0;
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) throw new IOException("Connexion fermée par la TV.");
            len |= (ulong)(one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
            shift += 7;
        }
        if (len > 1_000_000) throw new IOException("Message invalide.");
        var buf = new byte[len];
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new IOException("Connexion fermée par la TV.");
            read += n;
        }
        return buf;
    }

    // ---------- Protobuf minimal ----------

    public static class Proto
    {
        public sealed record Entry(int Field, ulong? Varint, byte[]? Bytes);

        public sealed class Writer
        {
            private readonly MemoryStream _ms = new();
            public byte[] ToArray() => _ms.ToArray();
            private void Key(int field, int wire) => _ms.Write(VarintBytes((ulong)((field << 3) | wire)));
            public void Varint(int field, long value) { Key(field, 0); _ms.Write(VarintBytes((ulong)value)); }
            public void Bytes(int field, byte[] value) { Key(field, 2); _ms.Write(VarintBytes((ulong)value.Length)); _ms.Write(value); }
            public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
            public void Message(int field, Action<Writer> build) { var w = new Writer(); build(w); Bytes(field, w.ToArray()); }
        }

        public static byte[] Msg(Action<Writer> build) { var w = new Writer(); build(w); return w.ToArray(); }

        public static byte[] VarintBytes(ulong v)
        {
            var list = new List<byte>();
            do { var b = (byte)(v & 0x7F); v >>= 7; if (v != 0) b |= 0x80; list.Add(b); } while (v != 0);
            return list.ToArray();
        }

        /// <summary>Champs de premier niveau d'un message (varint et longueur délimitée ; les autres types sont sautés).</summary>
        public static List<Entry> Read(byte[] data)
        {
            var list = new List<Entry>();
            var i = 0;
            while (i < data.Length)
            {
                var key = ReadVarint(data, ref i);
                var field = (int)(key >> 3);
                switch ((int)(key & 7))
                {
                    case 0: list.Add(new Entry(field, ReadVarint(data, ref i), null)); break;
                    case 2:
                        var len = (int)ReadVarint(data, ref i);
                        list.Add(new Entry(field, null, data[i..(i + len)]));
                        i += len;
                        break;
                    case 1: i += 8; break;
                    case 5: i += 4; break;
                    default: return list;
                }
            }
            return list;
        }

        private static ulong ReadVarint(byte[] d, ref int i)
        {
            ulong v = 0;
            var shift = 0;
            while (i < d.Length)
            {
                var b = d[i++];
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return v;
        }
    }
}
