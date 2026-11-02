using System.Net.NetworkInformation;

namespace SysDeck.Services;

public sealed class NetworkSpeedSnapshot
{
    public double DownloadBytesPerSecond { get; init; }
    public double UploadBytesPerSecond { get; init; }
}

/// <summary>
/// Calcule le débit réseau instantané en comparant les compteurs cumulés d'octets entre deux
/// appels. Additionne toutes les interfaces actives (hors loopback/tunnel) plutôt que de deviner
/// laquelle est "la bonne" — suffisant pour une machine perso avec une seule connexion active.
/// </summary>
public sealed class NetworkMonitorService
{
    private long _lastReceived;
    private long _lastSent;
    private DateTime _lastSampleUtc;
    private bool _hasSample;

    public NetworkSpeedSnapshot Read()
    {
        long received = 0, sent = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var stats = nic.GetIPv4Statistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        var now = DateTime.UtcNow;
        if (!_hasSample)
        {
            _lastReceived = received;
            _lastSent = sent;
            _lastSampleUtc = now;
            _hasSample = true;
            return new NetworkSpeedSnapshot { DownloadBytesPerSecond = 0, UploadBytesPerSecond = 0 };
        }

        var elapsed = (now - _lastSampleUtc).TotalSeconds;
        var down = elapsed > 0 ? Math.Max(0, received - _lastReceived) / elapsed : 0;
        var up = elapsed > 0 ? Math.Max(0, sent - _lastSent) / elapsed : 0;

        _lastReceived = received;
        _lastSent = sent;
        _lastSampleUtc = now;

        return new NetworkSpeedSnapshot { DownloadBytesPerSecond = down, UploadBytesPerSecond = up };
    }
}
