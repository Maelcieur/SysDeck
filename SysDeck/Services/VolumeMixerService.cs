using System.Diagnostics;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SysDeck.Services;

/// <param name="Icon">Logo de l'application (image figée) ; null si introuvable, l'interface affiche alors un glyphe.</param>
/// <param name="CustomIcon">Icône choisie par l'utilisateur (symbole, émoji, chemin d'image) ; remplace le logo automatique.</param>
public sealed record AudioSessionInfo(int ProcessId, string ProcessName, string DisplayName, string? ExePath, int VolumePercent, bool Muted, string DeviceId, ImageSource? Icon = null,
    string? CustomIcon = null, string? CustomIconColor = null)
{
    public bool HasCustomIcon => !string.IsNullOrEmpty(CustomIcon);

    /// <summary>Logo automatique affiché (trouvé, et non remplacé par un choix).</summary>
    public bool ShowAutoLogo => Icon is not null && !HasCustomIcon;

    /// <summary>Glyphe générique affiché (ni logo trouvé, ni icône choisie).</summary>
    public bool ShowFallbackGlyph => Icon is null && !HasCustomIcon;

    /// <summary>Clé stable de l'application (nom d'exécutable en minuscules), pour lui associer une icône choisie.</summary>
    public string AppKey => ProcessName.ToLowerInvariant();
}

/// <summary>
/// Volume par application (comme le mixeur de volume de Windows), via l'API Core Audio de Windows
/// (WASAPI) exposée par NAudio — chaque application qui joue du son a sa propre "session audio" sur
/// le périphérique de sortie où elle joue, avec un volume/mute indépendant du volume général.
/// Tous les périphériques actifs sont parcourus : une application peut avoir sa propre sortie.
/// </summary>
/// <remarks>
/// Appelé toutes les 2 secondes : chaque objet COM (périphérique, session) est libéré aussitôt lu,
/// sinon ils s'accumulent jusqu'au passage du ramasse-miettes et la mémoire grimpe sur la durée.
/// </remarks>
public sealed class VolumeMixerService
{
    // Nom et chemin de l'exécutable par processus : lire MainModule à chaque rafraîchissement coûte cher.
    private readonly Dictionary<int, (string Name, string? ExePath, ImageSource? Icon)> _processCache = new();
    private readonly Dictionary<int, DateTime> _iconRetryAt = new();

    public List<AudioSessionInfo> GetSessions()
    {
        var result = new List<AudioSessionInfo>();
        var seen = new HashSet<int>();

        ForEachSession((device, session) =>
        {
            if (session.State == AudioSessionState.AudioSessionStateExpired) return;

            var pid = (int)session.GetProcessID;
            if (pid == 0 || !seen.Add(pid)) return; // 0 = sons système ; une appli peut ouvrir plusieurs sessions

            if (GetProcessInfo(pid) is not { } process) return;

            var displayName = string.IsNullOrWhiteSpace(session.DisplayName)
                ? FriendlyName(process.Name)
                : session.DisplayName;

            result.Add(new AudioSessionInfo(
                pid, process.Name, displayName, process.ExePath,
                VolumePercent: (int)Math.Round(session.SimpleAudioVolume.Volume * 100),
                Muted: session.SimpleAudioVolume.Mute,
                DeviceId: device.ID,
                Icon: process.Icon));
        });

        // Oublie les processus terminés (un PID peut être réutilisé par un autre programme).
        foreach (var pid in _processCache.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            _processCache.Remove(pid);
            _iconRetryAt.Remove(pid);
        }

        return result;
    }

    private (string Name, string? ExePath, ImageSource? Icon)? GetProcessInfo(int pid)
    {
        if (_processCache.TryGetValue(pid, out var cached))
        {
            // Sans icône (fenêtre pas encore créée au lancement du jeu, par exemple) : nouvel essai toutes les 30 s.
            if (cached.Icon is not null || DateTime.UtcNow < _iconRetryAt.GetValueOrDefault(pid)) return cached;
            _iconRetryAt[pid] = DateTime.UtcNow.AddSeconds(30);
            var retried = IconExtractor.GetProcessIcon(pid, cached.ExePath, ProcessNative.GetPackageInstallPath(pid));
            return retried is null ? cached : _processCache[pid] = cached with { Icon = retried };
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            // OpenProcess en lecture limitée marche sur la plupart des processus protégés (jeux avec
            // anti-triche, applications du Store) là où MainModule est refusé ; MainModule reste le secours.
            var exePath = ProcessNative.QueryImagePath(pid);
            if (string.IsNullOrWhiteSpace(exePath))
            {
                try { exePath = process.MainModule?.FileName; } catch { /* accès refusé (processus système/protégé) */ }
            }
            // Lu une seule fois par processus (cache ci-dessus), ici sur le thread de fond.
            var icon = IconExtractor.GetProcessIcon(pid, exePath, ProcessNative.GetPackageInstallPath(pid));
            if (icon is null) _iconRetryAt[pid] = DateTime.UtcNow.AddSeconds(30);
            return _processCache[pid] = (process.ProcessName, exePath, icon);
        }
        catch
        {
            return null; // le processus s'est terminé entre l'énumération et la lecture de ses infos
        }
    }

    public void SetVolume(int processId, int volumePercent)
        => ForEachSession((_, session) =>
        {
            if ((int)session.GetProcessID == processId)
                session.SimpleAudioVolume.Volume = Math.Clamp(volumePercent, 0, 100) / 100f;
        });

    public void SetMuted(int processId, bool muted)
        => ForEachSession((_, session) =>
        {
            if ((int)session.GetProcessID == processId)
                session.SimpleAudioVolume.Mute = muted;
        });

    /// <summary>
    /// Sessions audio de tous les périphériques de sortie actifs, celui par défaut en premier. Les
    /// objets COM sont libérés après l'appel de <paramref name="action"/> : ne pas les conserver.
    /// </summary>
    private static void ForEachSession(Action<MMDevice, AudioSessionControl> action)
    {
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch { /* aucun périphérique de sortie */ }

        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        try
        {
            foreach (var device in devices.OrderByDescending(d => d.ID == defaultId))
            {
                SessionCollection sessions;
                try { sessions = device.AudioSessionManager.Sessions; }
                catch { continue; } // périphérique débranché pendant l'énumération

                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    try { action(device, session); }
                    catch { /* session fermée entre-temps */ }
                }
            }
        }
        finally
        {
            foreach (var device in devices)
                device.Dispose();
        }
    }

    private static string FriendlyName(string processName) => processName.ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "firefox" => "Firefox",
        "spotify" => "Spotify",
        "discord" => "Discord",
        "steam" => "Steam",
        "steamwebhelper" => "Steam (overlay)",
        "vlc" => "VLC",
        _ => processName
    };
}
