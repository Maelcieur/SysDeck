using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace SysDeck.Services;

public sealed record NowPlayingInfo(bool HasSession, string Title, string Subtitle, BitmapImage? Thumbnail, bool IsPlaying, bool IsSpotify,
    string Album = "", string Source = "");

/// <summary>
/// Avancement du morceau : position mesurée à l'instant <see cref="At"/> (à faire avancer tant que ça joue), durée,
/// et commandes disponibles (aléatoire, répétition : 0 aucune, 1 morceau, 2 liste ; recherche dans le morceau).
/// </summary>
public sealed record MediaTimeline(TimeSpan Position, TimeSpan Duration, DateTime At, bool IsPlaying,
    bool Shuffle, int Repeat, bool CanShuffle, bool CanRepeat, bool CanSeek)
{
    public static readonly MediaTimeline Empty = new(TimeSpan.Zero, TimeSpan.Zero, DateTime.Now, false, false, 0, false, false, false);

    /// <summary>Position estimée maintenant (la source ne la renvoie que de temps en temps).</summary>
    public TimeSpan PositionAt(DateTime now)
    {
        var p = IsPlaying ? Position + (now - At) : Position;
        if (p < TimeSpan.Zero) p = TimeSpan.Zero;
        return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
    }
}

/// <summary>
/// Lecture en cours au niveau système (Spotify, YouTube/Netflix dans un navigateur, VLC, etc.) via
/// l'API WinRT Windows.Media.Control — la même que celle qui alimente les commandes multimédia du
/// clavier et le widget "média" de Windows. Aucune intégration par application n'est nécessaire :
/// toute appli qui déclare une session multimédia système est automatiquement détectée.
/// </summary>
public sealed class MediaSessionService
{
    public event Action<NowPlayingInfo>? NowPlayingChanged;

    /// <summary>Position, durée, aléatoire ou répétition changés (sans recharger la pochette).</summary>
    public event Action<MediaTimeline>? TimelineChanged;

    /// <summary>Dernier avancement connu.</summary>
    public MediaTimeline Timeline { get; private set; } = MediaTimeline.Empty;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
    private Task? _initialization;
    private int _refreshVersion;

    /// <summary>Dernier état connu, pour remplir un module (ré)affiché sans attendre le prochain changement.</summary>
    public NowPlayingInfo? Current { get; private set; }

    /// <summary>Idempotent : la session système n'est ouverte qu'une fois, même si le module est réaffiché.</summary>
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            // BeginInvoke, pas Invoke : les événements WinRT arrivent sur un autre thread, et un appel
            // synchrone vers l'interface peut se bloquer pendant la fermeture de l'application.
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(AttachToCurrentSession);
            AttachToCurrentSession();
        }
        catch (Exception ex)
        {
            SysDeck.Infrastructure.Log.Warn("Lecture en cours indisponible (API Windows.Media.Control)", ex);
            // API indisponible (build de Windows trop ancien) : le widget reste en état "aucune lecture"
            Publish(new NowPlayingInfo(false, "", "", null, false, false));
        }
    }

    private void Publish(NowPlayingInfo info)
    {
        Current = info;
        NowPlayingChanged?.Invoke(info);
    }

    private void AttachToCurrentSession()
    {
        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged -= OnSessionEvent;
            _currentSession.PlaybackInfoChanged -= OnSessionEvent;
            _currentSession.TimelinePropertiesChanged -= OnTimelineEvent;
        }

        // La barre de jeu Xbox (Game Bar) déclare parfois une session multimédia factice, sans
        // musique réelle derrière — on l'ignore et on cherche une vraie session parmi les autres,
        // plutôt que d'afficher "Xbox Game Bar" à la place de ce qui joue vraiment (ex. Spotify).
        // De même, Windows garde parfois une session Spotify "fantôme" quelques instants après
        // que le processus a été tué (bouton Stop) : on la rejette aussi tant que Spotify.exe n'est
        // pas réellement relancé, sinon le widget réaffiche cette session morte juste après le Stop.
        var current = _manager?.GetCurrentSession();
        _currentSession = IsValidSession(current) ? current : _manager?.GetSessions().FirstOrDefault(IsValidSession);

        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged += OnSessionEvent;
            _currentSession.PlaybackInfoChanged += OnSessionEvent;
            _currentSession.TimelinePropertiesChanged += OnTimelineEvent;
        }

        _ = RefreshAsync();
    }

    private void OnSessionEvent(GlobalSystemMediaTransportControlsSession sender, object args)
        => _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private void OnTimelineEvent(GlobalSystemMediaTransportControlsSession sender, object args)
        => _dispatcher.BeginInvoke(PublishTimeline);

    /// <summary>Lit l'avancement de la session courante et le publie.</summary>
    private void PublishTimeline()
    {
        var session = _currentSession;
        var timeline = MediaTimeline.Empty;
        if (session is not null)
        {
            try
            {
                var t = session.GetTimelineProperties();
                var pb = session.GetPlaybackInfo();
                var playing = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var duration = t.EndTime - t.StartTime;
                // Date de la mesure : certaines applis ne la remplissent pas (année 1601) ; on prend alors maintenant.
                var at = t.LastUpdatedTime.Year > 2000 ? t.LastUpdatedTime.LocalDateTime : DateTime.Now;
                if (at > DateTime.Now) at = DateTime.Now;
                var repeat = pb?.AutoRepeatMode switch
                {
                    Windows.Media.MediaPlaybackAutoRepeatMode.Track => 1,
                    Windows.Media.MediaPlaybackAutoRepeatMode.List => 2,
                    _ => 0
                };
                timeline = new MediaTimeline(t.Position - t.StartTime, duration > TimeSpan.Zero ? duration : TimeSpan.Zero, at, playing,
                    pb?.IsShuffleActive == true, repeat, pb?.Controls.IsShuffleEnabled == true, pb?.Controls.IsRepeatEnabled == true,
                    pb?.Controls.IsPlaybackPositionEnabled == true);
            }
            catch
            {
                // Session fermée entre-temps : avancement vide.
            }
        }
        Timeline = timeline;
        TimelineChanged?.Invoke(timeline);
    }

    public async Task ToggleShuffleAsync()
    {
        try { if (_currentSession is not null) await _currentSession.TryChangeShuffleActiveAsync(!Timeline.Shuffle); } catch { }
    }

    /// <summary>Répétition : aucune → liste → morceau → aucune.</summary>
    public async Task CycleRepeatAsync()
    {
        var next = Timeline.Repeat switch
        {
            0 => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            2 => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None
        };
        try { if (_currentSession is not null) await _currentSession.TryChangeAutoRepeatModeAsync(next); } catch { }
    }

    /// <summary>Va à une position du morceau.</summary>
    public async Task SeekAsync(TimeSpan position)
    {
        try { if (_currentSession is not null) await _currentSession.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }

    private async Task RefreshAsync()
    {
        // Plusieurs rafraîchissements peuvent se chevaucher (changement de morceau rapide) : seul le
        // plus récent publie son résultat, sinon une pochette en retard écraserait la nouvelle.
        var version = ++_refreshVersion;
        var session = _currentSession;
        if (session is null)
        {
            Publish(new NowPlayingInfo(false, "", "", null, false, false));
            PublishTimeline();
            return;
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try { props = await session.TryGetMediaPropertiesAsync(); } catch { }

        var isPlaying = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        BitmapImage? thumbnail = null;
        if (props?.Thumbnail is { } thumbRef)
        {
            try
            {
                using var stream = await thumbRef.OpenReadAsync();
                var emptyBuffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
                Windows.Storage.Streams.IBuffer filledBuffer = await stream.ReadAsync(emptyBuffer, (uint)stream.Size, Windows.Storage.Streams.InputStreamOptions.None);
                using var netStream = filledBuffer.AsStream();
                thumbnail = new BitmapImage();
                thumbnail.BeginInit();
                thumbnail.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                thumbnail.StreamSource = netStream;
                thumbnail.EndInit();
                thumbnail.Freeze();
            }
            catch
            {
                thumbnail = null;
            }
        }

        var subtitle = !string.IsNullOrWhiteSpace(props?.Artist) ? props!.Artist : FriendlySourceName(session.SourceAppUserModelId);
        var isSpotify = session.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

        if (version != _refreshVersion) return;
        Publish(new NowPlayingInfo(
            HasSession: true,
            Title: string.IsNullOrWhiteSpace(props?.Title) ? subtitle : props!.Title,
            Subtitle: subtitle,
            Thumbnail: thumbnail,
            IsPlaying: isPlaying,
            IsSpotify: isSpotify,
            Album: props?.AlbumTitle ?? "",
            Source: FriendlySourceName(session.SourceAppUserModelId)));
        PublishTimeline();
    }

    private static bool IsValidSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null) return false;

        var id = session.SourceAppUserModelId;
        if (id.Contains("XboxGamingOverlay", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Contains("spotify", StringComparison.OrdinalIgnoreCase) && !IsSpotifyRunning()) return false;

        return true;
    }

    private static bool IsSpotifyRunning()
    {
        var processes = Process.GetProcessesByName("Spotify");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    private static string FriendlySourceName(string appUserModelId)
    {
        var name = appUserModelId.Split('!')[0];
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        name = name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

        return name.ToLowerInvariant() switch
        {
            "spotify" => "Spotify",
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            "vlc" => "VLC",
            _ => name
        };
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession is not null)
        {
            try { await _currentSession.TryTogglePlayPauseAsync(); } catch { }
            return;
        }

        // Aucune session "courante" au sens de Windows, mais Spotify tourne peut-être déjà en
        // arrière-plan (réduit dans la zone de notification) — dans ce cas il a toujours une
        // session, juste pas celle mise en avant. On la cherche explicitement avant de considérer
        // que Spotify n'est pas lancé du tout.
        var spotifySession = FindSpotifySession();
        if (spotifySession is not null)
        {
            try { await spotifySession.TryPlayAsync(); } catch { }
            return;
        }

        await LaunchAndPlaySpotifyAsync();
    }

    public async Task NextAsync()
    {
        try { if (_currentSession is not null) await _currentSession.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        try { if (_currentSession is not null) await _currentSession.TrySkipPreviousAsync(); } catch { }
    }

    private GlobalSystemMediaTransportControlsSession? FindSpotifySession()
        => _manager?.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase));

    private async Task LaunchAndPlaySpotifyAsync()
    {
        try
        {
            // Spotify est installé via le Microsoft Store sur cette machine (pas l'installeur
            // classique) : ni %AppData%\Spotify\Spotify.exe ni l'argument --minimized (propre à
            // l'ancien installeur) ne s'appliquent. L'alias créé par Windows pour les apps Store
            // permet de la lancer comme un exe normal.
            var aliasPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "Spotify.exe");

            Process.Start(new ProcessStartInfo(File.Exists(aliasPath) ? aliasPath : "spotify:") { UseShellExecute = true });
        }
        catch
        {
            return;
        }

        // Spotify met un instant à démarrer et à déclarer sa session multimédia auprès de Windows :
        // un simple lancement ne suffit pas à faire jouer de la musique, il faut attendre que la
        // session existe puis envoyer explicitement la commande lecture (sinon l'appli s'ouvre sans
        // rien jouer et il faut réappuyer sur play une seconde fois).
        for (var i = 0; i < 16; i++)
        {
            await Task.Delay(500);
            var session = FindSpotifySession();
            if (session is null) continue;

            try { await session.TryPlayAsync(); } catch { }
            await Task.Delay(800); // laisse la fenêtre apparaître avant de tenter de la réduire
            MinimizeSpotifyWindow();
            return;
        }
    }

    /// <summary>
    /// Ferme Spotify. L'attente de sortie (jusqu'à 1,5 s) se fait hors du thread de l'interface pour ne pas
    /// la figer ; la mise à jour de l'état, elle, reprend sur le thread appelant (celui de l'interface).
    /// </summary>
    public async Task StopSpotifyAsync()
    {
        await Task.Run(() =>
        {
            foreach (var process in Process.GetProcessesByName("Spotify"))
            {
                using (process)
                {
                    try
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(1500))
                            process.Kill();
                    }
                    catch { /* processus déjà terminé ou accès refusé */ }
                }
            }
        });

        // Windows ne signale pas toujours CurrentSessionChanged immédiatement après un Kill() forcé :
        // sans ça, _currentSession garde une référence à la session Spotify désormais morte, et le
        // prochain appui sur lecture échoue silencieusement (TryTogglePlayPauseAsync sur une session
        // morte) au lieu de relancer Spotify. On force donc l'état "aucune lecture" tout de suite.
        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged -= OnSessionEvent;
            _currentSession.PlaybackInfoChanged -= OnSessionEvent;
            _currentSession = null;
        }
        Publish(new NowPlayingInfo(false, "", "", null, false, false));
    }

    private static void MinimizeSpotifyWindow()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("Spotify"))
            {
                using (process)
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        NativeMethods.ShowWindow(process.MainWindowHandle, NativeMethods.SW_MINIMIZE);
                }
            }
        }
        catch { }
    }

    private static class NativeMethods
    {
        public const int SW_MINIMIZE = 6;

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
