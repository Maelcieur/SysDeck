using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SysDeck.Infrastructure;

namespace SysDeck.Services.ConsoleMode;

/// <summary>Réveil réseau (Wake-on-LAN / WoWLAN) : « paquet magique » envoyé à l'adresse MAC de la TV.</summary>
public static class WakeOnLan
{
    /// <summary>Paquet magique : 6 octets 0xFF puis 16 fois l'adresse MAC.</summary>
    public static byte[] MagicPacket(string mac)
    {
        var bytes = ParseMac(mac) ?? throw new FormatException("Adresse MAC invalide.");
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Array.Copy(bytes, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    public static byte[]? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? Convert.FromHexString(hex) : null;
    }

    /// <summary>Envoie le paquet en diffusion (255.255.255.255 et réseau local x.x.x.255) et directement à l'IP, ports 9 et 7.</summary>
    public static void Send(string mac, string? host)
    {
        try
        {
            var packet = MagicPacket(mac);
            using var udp = new UdpClient { EnableBroadcast = true };
            var targets = new List<IPAddress> { IPAddress.Broadcast };
            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                targets.Add(new IPAddress(new byte[] { b[0], b[1], b[2], 255 }));
                targets.Add(ip);
            }
            foreach (var t in targets)
                foreach (var port in new[] { 9, 7 })
                    try { udp.Send(packet, packet.Length, new IPEndPoint(t, port)); } catch { }
        }
        catch (Exception ex)
        {
            Log.WarnOnce("wol", "Réveil réseau impossible", ex);
        }
    }

    /// <summary>Adresse MAC d'un appareil du réseau local (requête ARP), au format AA-BB-CC-DD-EE-FF.</summary>
    public static string? ResolveMac(string host)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var ip)) return null;
            var mac = new byte[6];
            var len = 6;
            if (SendARP(BitConverter.ToInt32(ip.GetAddressBytes(), 0), 0, mac, ref len) != 0 || len < 6) return null;
            return string.Join("-", mac.Take(6).Select(x => x.ToString("X2")));
        }
        catch (Exception ex)
        {
            Log.WarnOnce("arp", "Adresse MAC de la TV introuvable", ex);
            return null;
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(int destIp, int srcIp, byte[] mac, ref int len);
}
