using NAudio.CoreAudioApi;
using SysDeck.Interop;

namespace SysDeck.Services;

/// <param name="ShortName">Nom court choisi par l'utilisateur ; vide = nom d'origine.</param>
/// <param name="Icon">Icône choisie (symbole, émoji ou chemin d'image) ; vide = aucune.</param>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, string? ShortName = null, string? Icon = null, string? IconColor = null)
{
    /// <summary>Nom affiché dans le widget : nom court s'il existe, sinon nom complet.</summary>
    public string DisplayName => AudioDeviceCustom.ResolveName(Name, new AudioDeviceCustom { Name = ShortName });

    public bool HasIcon => !string.IsNullOrEmpty(Icon);
}

public sealed class AudioService
{
    public List<AudioDeviceInfo> GetPlaybackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultDeviceId(enumerator);

        var devices = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            // Chaque MMDevice est un objet COM : libéré tout de suite plutôt qu'au ramasse-miettes.
            using (device)
                devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
        }
        return devices;
    }

    public void SetDefaultDevice(string deviceId)
    {
        var client = new PolicyConfigClient();
        client.SetDefaultEndpoint(deviceId, ERole.eConsole);
        client.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
        client.SetDefaultEndpoint(deviceId, ERole.eCommunications);
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null; // aucun périphérique de sortie
        }
    }
}
