using System.Windows.Media;
using SysDeck.Infrastructure;
using SysDeck.Services;

namespace SysDeck;

// Noms courts et icônes des sorties audio, logos choisis des applications du mixeur de volume.
public sealed partial class WidgetManager
{
    /// <summary>Application vue par le mixeur pendant cette session (liste « Applications » des Paramètres).</summary>
    public sealed record SeenApp(string AppKey, string DisplayName, ImageSource? Icon);

    private readonly Dictionary<string, SeenApp> _seenApps = new();

    /// <summary>Joint à chaque sortie audio son nom court et son icône (réglages de Personnaliser › Sortie audio).</summary>
    private IEnumerable<AudioDeviceInfo> WithCustomNames(IEnumerable<AudioDeviceInfo> devices)
        => devices.Select(d => _config.AudioDevices.TryGetValue(d.Id, out var custom)
            ? d with { ShortName = custom.Name, Icon = custom.Icon, IconColor = custom.IconColor }
            : d);

    /// <summary>Joint à chaque session l'icône choisie pour son application, qui remplace le logo automatique.</summary>
    private IEnumerable<AudioSessionInfo> WithCustomIcons(IEnumerable<AudioSessionInfo> sessions)
        => sessions.Select(s => _config.AppIcons.TryGetValue(s.AppKey, out var custom) && !string.IsNullOrEmpty(custom.Icon)
            ? s with { CustomIcon = custom.Icon, CustomIconColor = custom.IconColor }
            : s);

    private void NoteSeenApps(IEnumerable<AudioSessionInfo> sessions)
    {
        foreach (var s in sessions)
        {
            // Garde le dernier logo trouvé : une application qui se ferme reste dans la liste des Paramètres.
            var icon = s.Icon ?? (_seenApps.TryGetValue(s.AppKey, out var known) ? known.Icon : null);
            _seenApps[s.AppKey] = new SeenApp(s.AppKey, s.DisplayName, icon);
        }
    }

    /// <summary>Sorties audio actives (même celles masquées du widget), avec leur nom d'origine : liste des Paramètres.</summary>
    public IReadOnlyList<AudioDeviceInfo> GetAudioDevices()
    {
        try
        {
            return _audio.GetPlaybackDevices();
        }
        catch (Exception ex)
        {
            Log.WarnOnce("settings:audio", "Liste des sorties audio indisponible", ex);
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    /// <summary>Applications vues par le mixeur depuis le lancement (liste « Applications » des Paramètres).</summary>
    public IReadOnlyList<SeenApp> GetSeenApps() => _seenApps.Values.ToList();

    /// <summary>
    /// Enregistre le nom court et l'icône d'une sortie audio (vides = d'origine) et met à jour les widgets
    /// du bureau et de la Switch. <paramref name="originalName"/> sert à afficher la ligne d'un périphérique débranché.
    /// </summary>
    public void SetAudioDeviceCustom(string deviceId, string originalName, string? name, string? icon, string? iconColor)
    {
        var previousIcon = _config.AudioDevices.GetValueOrDefault(deviceId)?.Icon;
        var custom = new AudioDeviceCustom
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Icon = string.IsNullOrEmpty(icon) ? null : icon,
            IconColor = string.IsNullOrEmpty(icon) ? null : iconColor,
            OriginalName = originalName
        };
        if (custom.IsEmpty) _config.AudioDevices.Remove(deviceId);
        else _config.AudioDevices[deviceId] = custom;
        SaveConfigSoon(); // le nom se tape lettre par lettre : sauvegarde différée
        IconPicker.Release(previousIcon, custom.Icon);
        RefreshAudioDevices();
    }

    /// <summary>Enregistre l'icône choisie pour une application du mixeur (vide = logo automatique) et met à jour les widgets.</summary>
    public void SetAppIcon(string appKey, string displayName, string? icon, string? iconColor)
    {
        var previousIcon = _config.AppIcons.GetValueOrDefault(appKey)?.Icon;
        UpdateConfig(c =>
        {
            if (string.IsNullOrEmpty(icon)) c.AppIcons.Remove(appKey);
            else c.AppIcons[appKey] = new AppIconCustom { Icon = icon, IconColor = iconColor, DisplayName = displayName };
        });
        IconPicker.Release(previousIcon, icon);
        RefreshVolumeMixer(force: true);
    }

    /// <summary>Sélecteur d'émojis du choix d'icône (même fenêtre que la page Switch).</summary>
    public EmojiPickerWindow CreateEmojiPickerForChoice()
        => new(() => _config.RecentEmojis, _config.EmojiPicker);

    /// <summary>Clic droit sur une application du mixeur › « Changer l'icône… ».</summary>
    private void ChooseAppIcon(AudioSessionInfo info)
    {
        var current = _config.AppIcons.GetValueOrDefault(info.AppKey);
        // Propriétaire invisible (un widget rattaché au bureau ne peut pas posséder de fenêtre) : centré sur l'écran.
        var pick = IconPicker.Choose(_hiddenOwner, info.DisplayName, current?.Icon, current?.IconColor, CreateEmojiPickerForChoice, centerOnScreen: true);
        if (pick is not null) SetAppIcon(info.AppKey, info.DisplayName, pick.Value, pick.Color);
    }
}
