using System.Diagnostics;
using System.ComponentModel;
using SysDeck.Infrastructure;
using SysDeck.Models;

namespace SysDeck.Services;

/// <summary>
/// Confirmation par second appui : le premier appui arme la tuile, un second appui dans le délai exécute,
/// passé le délai le premier appui est oublié et l'appui suivant réarme. L'horloge est injectée (tests).
/// </summary>
public sealed class TapConfirmation
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    private readonly Func<DateTime> _clock;
    private readonly Dictionary<object, DateTime> _armedAt = new(ReferenceEqualityComparer.Instance);

    public TapConfirmation(Func<DateTime> clock) => _clock = clock;

    /// <summary>Vrai si l'action doit s'exécuter maintenant (second appui à temps) ; faux si la tuile vient d'être armée.</summary>
    public bool Press(object key)
    {
        var now = _clock();
        if (_armedAt.TryGetValue(key, out var armed) && now - armed <= Delay)
        {
            _armedAt.Remove(key);
            return true;
        }
        _armedAt[key] = now;
        return false;
    }

    /// <summary>Vrai tant que la tuile attend son second appui.</summary>
    public bool IsArmed(object key)
        => _armedAt.TryGetValue(key, out var armed) && _clock() - armed <= Delay;
}

/// <summary>Mise en valeur d'une tuile d'intégration : normale, active (accent : scène OBS, mode du tchat, voix chargée) ou en cours / coupée (rouge).</summary>
public enum IntegrationLook { Normal, Highlight, Alert }

/// <summary>État d'exécution partagé des tuiles (thread interface uniquement) : armement, états muet, état d'OBS, de Twitch et de Voicemod.</summary>
public static class TileRuntime
{
    public static readonly TapConfirmation Confirm = new(() => DateTime.UtcNow);

    /// <summary>Dernier état muet lu pour chaque tuile micro / sortie.</summary>
    public static readonly Dictionary<ShortcutItem, bool> Muted = new(ReferenceEqualityComparer.Instance);

    public static bool IsMuted(ShortcutItem item) => Muted.TryGetValue(item, out var muted) && muted;

    /// <summary>Service OBS (renseigné par <c>WidgetManager</c>) ; null dans les tests.</summary>
    public static ObsService? Obs { get; set; }

    /// <summary>Vrai pour une tuile OBS tant qu'OBS n'est pas connecté : elle s'affiche atténuée.</summary>
    public static bool ObsOffline(ShortcutItem item) => item.Kind == TileKind.Obs && Obs is not { IsConnected: true };

    /// <summary>Service Twitch (renseigné par <c>WidgetManager</c>) ; null dans les tests.</summary>
    public static TwitchService? Twitch { get; set; }

    /// <summary>Vrai pour une tuile Twitch tant que Twitch n'est pas connecté : elle s'affiche atténuée.</summary>
    public static bool TwitchOffline(ShortcutItem item) => item.Kind == TileKind.Twitch && Twitch is not { IsConnected: true };

    /// <summary>Service Voicemod (renseigné par <c>WidgetManager</c>) ; null dans les tests.</summary>
    public static VoicemodService? Voicemod { get; set; }

    public static bool VoicemodOffline(ShortcutItem item) => item.Kind == TileKind.Voicemod && Voicemod is not { IsConnected: true };

    private static bool? VoicemodOn(ShortcutItem item)
        => item.VoicemodAction is { } action ? Voicemod?.IsOn(action, item.VoicemodTarget) : null;

    /// <summary>Mise en valeur d'une tuile Voicemod : voix chargée, changeur et options actifs en accent ; micro ou sons coupés en rouge.</summary>
    public static IntegrationLook VoicemodLookOf(ShortcutItem item)
    {
        if (item.Kind != TileKind.Voicemod || VoicemodOn(item) != true) return IntegrationLook.Normal;
        return item.VoicemodAction is VoicemodAction.MuteMic or VoicemodAction.MuteSoundsForMe ? IntegrationLook.Alert : IntegrationLook.Highlight;
    }

    /// <summary>Symbole par défaut d'une tuile Voicemod selon son action (micro barré quand coupé).</summary>
    public static string VoicemodGlyph(ShortcutItem item) => item.VoicemodAction switch
    {
        VoicemodAction.VoiceChanger => "",
        VoicemodAction.LoadVoice => "",
        VoicemodAction.RandomVoice => "",
        VoicemodAction.PlaySound => "",
        VoicemodAction.StopSounds => "",
        VoicemodAction.MuteMic => VoicemodOn(item) == true ? MicOffGlyph : MicGlyph,
        VoicemodAction.HearMyself => "",
        VoicemodAction.Background => "",
        VoicemodAction.MuteSoundsForMe => VoicemodOn(item) == true ? SpeakerOffGlyph : SpeakerGlyph,
        _ => ""
    };

    /// <summary>Vrai pour une tuile d'intégration (OBS, Twitch, Voicemod) dont le service n'est pas connecté : elle s'affiche atténuée.</summary>
    public static bool IntegrationOffline(ShortcutItem item) => ObsOffline(item) || TwitchOffline(item) || VoicemodOffline(item);

    private static bool? TwitchOn(ShortcutItem item)
        => item.TwitchAction is { } action ? Twitch?.IsOn(action) : null;

    /// <summary>Mise en valeur d'une tuile Twitch : mode bouclier actif en rouge, autres modes du tchat actifs en accent.</summary>
    public static IntegrationLook TwitchLookOf(ShortcutItem item)
    {
        if (item.Kind != TileKind.Twitch || TwitchOn(item) != true) return IntegrationLook.Normal;
        return item.TwitchAction == TwitchAction.ShieldMode ? IntegrationLook.Alert : IntegrationLook.Highlight;
    }

    /// <summary>Mise en valeur d'une tuile d'intégration (OBS, Twitch ou Voicemod).</summary>
    public static IntegrationLook IntegrationLookOf(ShortcutItem item) => item.Kind switch
    {
        TileKind.Twitch => TwitchLookOf(item),
        TileKind.Voicemod => VoicemodLookOf(item),
        _ => ObsLookOf(item)
    };

    /// <summary>Symbole par défaut d'une tuile Twitch selon son action.</summary>
    public static string TwitchGlyph(ShortcutItem item) => item.TwitchAction switch
    {
        TwitchAction.Clip => "",
        TwitchAction.Marker => "",
        TwitchAction.ChatMessage => "",
        TwitchAction.Commercial => "",
        TwitchAction.SlowMode => "",
        TwitchAction.EmoteOnly => "",
        TwitchAction.FollowersOnly => "",
        TwitchAction.SubscribersOnly => "",
        TwitchAction.ShieldMode => "",
        TwitchAction.SetTitle => "",
        TwitchAction.SetCategory => "",
        _ => ""
    };

    /// <summary>Symbole par défaut d'une tuile d'intégration (OBS, Twitch ou Voicemod).</summary>
    public static string IntegrationGlyph(ShortcutItem item) => item.Kind switch
    {
        TileKind.Twitch => TwitchGlyph(item),
        TileKind.Voicemod => VoicemodGlyph(item),
        _ => ObsGlyph(item)
    };

    private static bool? ObsOn(ShortcutItem item)
        => item.ObsAction is { } action ? Obs?.IsOn(action, item.ObsTarget, item.ObsScene) : null;

    /// <summary>Mise en valeur d'une tuile OBS : scène active en accent ; sortie qui tourne, pause ou entrée coupée en rouge.</summary>
    public static IntegrationLook ObsLookOf(ShortcutItem item)
    {
        if (item.Kind != TileKind.Obs || ObsOn(item) != true) return IntegrationLook.Normal;
        return item.ObsAction switch
        {
            ObsAction.Scene => IntegrationLook.Highlight,
            ObsAction.Stream or ObsAction.Record or ObsAction.RecordPause or ObsAction.VirtualCam
                or ObsAction.ReplayBuffer or ObsAction.InputMute => IntegrationLook.Alert,
            _ => IntegrationLook.Normal
        };
    }

    /// <summary>Symbole par défaut d'une tuile OBS selon son action et l'état courant (micro barré, œil barré).</summary>
    public static string ObsGlyph(ShortcutItem item) => item.ObsAction switch
    {
        ObsAction.Stream => "",
        ObsAction.Record => "",
        ObsAction.RecordPause => "",
        ObsAction.VirtualCam => "",
        ObsAction.ReplayBuffer => "",
        ObsAction.SaveReplay => "",
        ObsAction.InputMute => ObsOn(item) == true ? MicOffGlyph : MicGlyph,
        ObsAction.SourceVisibility => ObsOn(item) == false ? "" : "",
        ObsAction.StudioTransition => "",
        _ => ""
    };

    public const string MicGlyph = "", MicOffGlyph = "", SpeakerGlyph = "", SpeakerOffGlyph = "", CommandGlyph = "";

    /// <summary>
    /// Copie de la tuile avec l'icône à afficher : micro / sortie barré quand coupé, symbole par défaut selon le type
    /// si aucune icône n'a été choisie. Renvoie la tuile telle quelle dans les autres cas.
    /// </summary>
    public static ShortcutItem ForDisplay(ShortcutItem item)
    {
        if (!string.IsNullOrEmpty(item.IconGlyph) || !string.IsNullOrWhiteSpace(item.CustomIconPath)) return item;
        var glyph = item.Kind switch
        {
            TileKind.MicMute => IsMuted(item) ? MicOffGlyph : MicGlyph,
            TileKind.OutputMute => IsMuted(item) ? SpeakerOffGlyph : SpeakerGlyph,
            TileKind.Command => CommandGlyph,
            TileKind.Obs => ObsGlyph(item),
            TileKind.Twitch => TwitchGlyph(item),
            TileKind.Voicemod => VoicemodGlyph(item),
            _ => null
        };
        return glyph is null ? item : new ShortcutItem { Name = item.Name, IconGlyph = glyph, IconColor = item.IconColor, TileColor = item.TileColor };
    }
}

/// <summary>Préréglage de tuile Commande.</summary>
public sealed record CommandPreset(string Name, string Command, string Glyph, bool Confirm, string Description);

/// <summary>Exécution des tuiles Commande, sans fenêtre.</summary>
public static class CommandRunner
{
    public static readonly IReadOnlyList<CommandPreset> Presets = new[]
    {
        new CommandPreset("Éteindre", "shutdown /s /t 0", "", true, "Éteint le PC immédiatement."),
        new CommandPreset("Redémarrer", "shutdown /r /t 0", "", true, "Redémarre le PC immédiatement."),
        new CommandPreset("Veille", "rundll32.exe powrprof.dll,SetSuspendState 0,1,0", "", true,
            "Met le PC en veille. Attention : si la mise en veille prolongée est activée dans Windows, le PC hiberne à la place."),
        new CommandPreset("Verrouiller", "rundll32.exe user32.dll,LockWorkStation", "", false, "Verrouille la session Windows."),
        new CommandPreset("Fermer la session", "shutdown /l", "", true, "Ferme la session Windows en cours.")
    };

    /// <summary>
    /// Démarrage de <c>cmd.exe /c &lt;commande&gt;</c> : caché et sans redirection en mode normal ; en administrateur,
    /// passe par le shell (verbe <c>runas</c>, demande UAC), fenêtre cachée.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string command, bool runAsAdmin)
    {
        var info = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = runAsAdmin
        };
        if (runAsAdmin) info.Verb = "runas";
        return info;
    }

    /// <summary>Lance la commande de la tuile ; renvoie un message d'erreur lisible, ou null si elle est partie.</summary>
    public static string? Run(ShortcutItem item)
    {
        var command = item.Command?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            Log.Warn($"Tuile commande « {item.Name} » sans commande : ignorée.");
            return "Aucune commande n'est définie pour cette tuile.";
        }

        try
        {
            Log.Info($"Commande « {item.Name} » : {command}{(item.RunAsAdmin ? " (administrateur)" : "")}");
            var process = Process.Start(BuildStartInfo(command, item.RunAsAdmin));
            if (process is null) return null;
            if (!item.RunAsAdmin)
            {
                // Code de sortie journalisé sans bloquer ; le processus est libéré à sa fin.
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    try
                    {
                        if (process.ExitCode != 0) Log.Warn($"Commande « {item.Name} » terminée avec le code {process.ExitCode}.");
                    }
                    catch { /* processus déjà libéré */ }
                    finally { process.Dispose(); }
                };
            }
            else process.Dispose();
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn($"Commande « {item.Name} » : élévation refusée.", ex);
            return "L'exécution en administrateur a été refusée.";
        }
        catch (Exception ex)
        {
            Log.Warn($"Commande « {item.Name} » impossible", ex);
            return ex.Message;
        }
    }
}
