using NAudio.CoreAudioApi;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>Périphérique audio proposé pour une tuile « muet ».</summary>
public sealed record AudioEndpointInfo(string Id, string Name);

/// <summary>
/// Muet d'un micro (entrée) ou d'une sortie audio via le volume du point de terminaison (NAudio / Core Audio).
/// Identifiant vide = périphérique par défaut de Windows (suivi à chaque appel).
/// </summary>
/// <remarks>
/// Chaque objet COM (énumérateur, périphérique) est libéré aussitôt après usage, comme dans
/// <see cref="VolumeMixerService"/> : sinon ils s'accumulent jusqu'au ramasse-miettes.
/// </remarks>
public static class AudioMuteService
{
    public static DataFlow FlowOf(bool output) => output ? DataFlow.Render : DataFlow.Capture;

    /// <summary>Périphériques actifs du sens demandé.</summary>
    public static List<AudioEndpointInfo> GetDevices(DataFlow flow)
    {
        var result = new List<AudioEndpointInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList();
            foreach (var device in devices)
            {
                try { result.Add(new AudioEndpointInfo(device.ID, device.FriendlyName)); }
                catch { /* débranché pendant l'énumération */ }
                finally { device.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Liste des périphériques audio illisible", ex);
        }
        return result;
    }

    private static MMDevice? Open(MMDeviceEnumerator enumerator, string? id, DataFlow flow)
    {
        try
        {
            return string.IsNullOrWhiteSpace(id)
                ? enumerator.GetDefaultAudioEndpoint(flow, Role.Console)
                : enumerator.GetDevice(id);
        }
        catch
        {
            return null; // périphérique absent ou débranché
        }
    }

    /// <summary>État muet du périphérique ; null s'il est introuvable.</summary>
    public static bool? IsMuted(string? id, DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = Open(enumerator, id, flow);
            return device?.AudioEndpointVolume.Mute;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("audio-mute-read", "Lecture de l'état muet impossible", ex);
            return null;
        }
    }

    /// <summary>Bascule le muet et renvoie le nouvel état ; null si le périphérique est introuvable.</summary>
    public static bool? Toggle(string? id, DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = Open(enumerator, id, flow);
            if (device is null) return null;
            var volume = device.AudioEndpointVolume;
            volume.Mute = !volume.Mute;
            var muted = volume.Mute;
            Log.Info($"{(flow == DataFlow.Capture ? "Micro" : "Sortie")} « {device.FriendlyName} » : {(muted ? "coupé" : "rétabli")}");
            return muted;
        }
        catch (Exception ex)
        {
            Log.Warn("Bascule du muet impossible", ex);
            return null;
        }
    }
}
