using NAudio.CoreAudioApi;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>Volume principal de la sortie audio par défaut de Windows (boutons VOL± de la Switch).</summary>
public static class MasterVolume
{
    /// <summary>Ajoute <paramref name="deltaPercent"/> points au volume principal (borné à 0-100 %).</summary>
    public static void Change(int deltaPercent) => ChangeAndGet(deltaPercent);

    /// <summary>Comme <see cref="Change"/>, et renvoie le nouveau volume en % (-1 si inaccessible).</summary>
    public static int ChangeAndGet(int deltaPercent)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;
            volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + deltaPercent / 100f, 0f, 1f);
            return (int)Math.Round(volume.MasterVolumeLevelScalar * 100);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("deck:mastervolume", "Volume principal inaccessible (boutons VOL± de la Switch)", ex);
            return -1;
        }
    }
}
