using System.Windows.Threading;
using SysDeck.Deck;
using SysDeck.Infrastructure;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

// Tuiles spéciales de la Switch : confirmation par second appui, micro / sortie muet, actions OBS, Twitch et Voicemod.
public sealed partial class WidgetManager
{
    private DispatcherTimer? _confirmTimer;
    private DispatcherTimer? _muteTimer;
    private bool _mutePolling;
    private string _integrationSignature = "";

    /// <summary>Démarre la relecture périodique de l'état muet (sans effet tant qu'aucune tuile muet n'existe).</summary>
    private void StartTileTimers()
    {
        _muteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _muteTimer.Tick += (_, _) =>
        {
            RefreshMuteStates();
            SyncObsWatch();
            SyncTwitchWatch();
            _plugins?.SyncTiles(); // tuiles de plugin ajoutées, retirées ou modifiées : willAppear / willDisappear
        };
        _muteTimer.Start();
        RefreshMuteStates();
    }

    private void StopTileTimers()
    {
        _muteTimer?.Stop();
        _confirmTimer?.Stop();
    }

    /// <summary>Toutes les tuiles : Switch (toutes pages) et barre.</summary>
    private IEnumerable<ShortcutItem> AllTiles()
        => _config.Deck.Pages.SelectMany(p => p.Apps).Concat(_shortcuts).Distinct();

    /// <summary>Tuiles muet de la Switch (toutes pages) et de la barre.</summary>
    private List<ShortcutItem> MuteTiles() => AllTiles().Where(t => t.IsMuteTile).ToList();

    /// <summary>
    /// Relit l'état muet des périphériques hors thread interface ; les tuiles ne sont reconstruites que si un état a changé
    /// (un changement fait ailleurs dans Windows est ainsi suivi).
    /// </summary>
    private void RefreshMuteStates()
    {
        if (_mutePolling) return;
        var tiles = MuteTiles();
        if (tiles.Count == 0)
        {
            TileRuntime.Muted.Clear();
            return;
        }

        _mutePolling = true;
        Task.Run(() => tiles
                .Select(t => (Tile: t, Muted: AudioMuteService.IsMuted(t.DeviceId, AudioMuteService.FlowOf(t.Kind == TileKind.OutputMute))))
                .ToList())
            .ContinueWith(task =>
            {
                _mutePolling = false;
                if (task.IsFaulted) return;
                var changed = false;
                foreach (var (tile, muted) in task.Result)
                {
                    if (muted is null) continue;
                    if (TileRuntime.IsMuted(tile) == muted.Value && TileRuntime.Muted.ContainsKey(tile)) continue;
                    TileRuntime.Muted[tile] = muted.Value;
                    changed = true;
                }
                if (changed) RefreshTiles();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ToggleMute(ShortcutItem item)
    {
        var muted = AudioMuteService.Toggle(item.DeviceId, AudioMuteService.FlowOf(item.Kind == TileKind.OutputMute));
        if (muted is null)
        {
            ShowWarning($"Périphérique audio introuvable pour « {item.Name} ».");
            return;
        }
        TileRuntime.Muted[item] = muted.Value;
        RefreshTiles();
    }

    private void RefreshTiles()
    {
        _integrationSignature = IntegrationSignature();
        _deck.RefreshShortcuts();
    }

    // ---------- OBS ----------

    /// <summary>Applique les réglages OBS de la configuration (connexion, reconnexion ou arrêt).</summary>
    public void ApplyObsSettings()
    {
        var settings = _config.Obs;
        _obs.Configure(settings.Enabled, settings.Host, settings.Port, settings.Password);
        SyncObsWatch();
    }

    /// <summary>Indique à OBS les entrées et sources dont les tuiles affichent l'état (relu à la connexion et aux changements de tuiles).</summary>
    private void SyncObsWatch()
    {
        var tiles = AllTiles().Where(t => t.Kind == TileKind.Obs).ToList();
        _obs.Watch(
            tiles.Where(t => t.ObsAction == ObsAction.InputMute && !string.IsNullOrEmpty(t.ObsTarget)).Select(t => t.ObsTarget!),
            tiles.Where(t => t.ObsAction == ObsAction.SourceVisibility && !string.IsNullOrEmpty(t.ObsTarget) && !string.IsNullOrEmpty(t.ObsScene))
                .Select(t => (t.ObsScene!, t.ObsTarget!)));
    }

    /// <summary>Résumé de ce qui s'affiche sur les tuiles OBS, Twitch et Voicemod (atténuation, mise en valeur, symbole) : sert à ne rafraîchir que sur un vrai changement.</summary>
    private string IntegrationSignature()
        => string.Join("|", AllTiles().Where(t => t.Kind is TileKind.Obs or TileKind.Twitch or TileKind.Voicemod)
            .Select(t => $"{TileRuntime.IntegrationOffline(t)}{TileRuntime.IntegrationLookOf(t)}{TileRuntime.IntegrationGlyph(t)}"));

    private void OnObsStateChanged()
    {
        if (_disposed) return;
        SyncObsWatch();
        if (IntegrationSignature() != _integrationSignature) RefreshTiles();
    }

    private async void RunObsTile(ShortcutItem item)
    {
        try
        {
            if (item.ObsAction is not { } action)
            {
                ShowWarning($"Aucune action OBS n'est définie pour « {item.Name} ».");
                return;
            }
            if (!_obs.IsConnected)
            {
                ShowWarning(_config.Obs.Enabled
                    ? "OBS n'est pas connecté."
                    : "L'intégration OBS est désactivée : active-la dans Paramètres › Applications connectées.");
                return;
            }
            if (await _obs.ExecuteAsync(action, item.ObsTarget, item.ObsScene) is { } error)
                ShowWarning($"« {item.Name} » : {error}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Action OBS « {item.Name} » en échec", ex);
        }
    }

    // ---------- Twitch ----------

    /// <summary>Applique le compte Twitch de la configuration (connexion d'arrière-plan si un compte est connecté).</summary>
    public void ApplyTwitchSettings()
    {
        _twitch.Configure(_config.Twitch, SaveConfig);
        SyncTwitchWatch();
    }

    /// <summary>Indique à Twitch si des tuiles existent (état relu toutes les 30 s) et si l'une d'elles utilise le mode bouclier.</summary>
    private void SyncTwitchWatch()
    {
        var tiles = AllTiles().Where(t => t.Kind == TileKind.Twitch).ToList();
        _twitch.Watch(tiles.Count > 0, tiles.Any(t => t.TwitchAction == TwitchAction.ShieldMode));
    }

    private void OnTwitchStateChanged()
    {
        if (_disposed) return;
        SyncTwitchWatch();
        if (IntegrationSignature() != _integrationSignature) RefreshTiles();
    }

    private async void RunTwitchTile(ShortcutItem item, bool fromDesktop)
    {
        try
        {
            if (item.TwitchAction is not { } action)
            {
                ShowWarning($"Aucune action Twitch n'est définie pour « {item.Name} ».");
                return;
            }
            if (!_twitch.IsConnected)
            {
                ShowWarning(_config.Twitch.Enabled
                    ? "Twitch n'est pas connecté."
                    : "Aucun compte Twitch n'est connecté : connecte-toi dans Paramètres › Applications connectées.");
                return;
            }
            if (item.AsksTwitchText)
            {
                AskTwitchText(item, action, fromDesktop);
                return;
            }
            var result = await _twitch.ExecuteAsync(action, item.TwitchText, item.TwitchId);
            if (result.Error is not null)
            {
                ShowWarning($"« {item.Name} » : {result.Error}");
                return;
            }
            if (result.Clipboard is { } text && TrySetClipboard(text))
                TrayIconService.ShowNotice($"{result.Notice} : lien d'édition copié dans le presse-papiers.");
            else if (result.Notice is not null)
                TrayIconService.ShowNotice(result.Notice);
        }
        catch (Exception ex)
        {
            Log.Warn($"Action Twitch « {item.Name} » en échec", ex);
        }
    }

    /// <summary>
    /// Saisie au moment de l'appui : clavier sur la Switch si l'appui vient d'elle, petite fenêtre sur le PC sinon.
    /// L'action part avec le texte saisi (ou la catégorie choisie) ; l'erreur éventuelle reste affichée dans la saisie.
    /// </summary>
    private void AskTwitchText(ShortcutItem item, TwitchAction action, bool fromDesktop)
    {
        var kind = action switch
        {
            TwitchAction.SetTitle => TextEntryKind.Title,
            TwitchAction.SetCategory => TextEntryKind.Category,
            _ => TextEntryKind.Chat
        };
        var request = new TextEntryRequest(kind,
            kind switch { TextEntryKind.Title => "Titre du live", TextEntryKind.Category => "Catégorie", _ => "Message dans le chat" },
            kind == TextEntryKind.Title ? 140 : 500,
            async (text, category) =>
            {
                var result = await _twitch.ExecuteAsync(action, category?.Name ?? text, category?.Id);
                if (result.Error is null && kind != TextEntryKind.Chat)
                    TrayIconService.ShowNotice(kind == TextEntryKind.Title ? "Titre du live modifié" : $"Catégorie : {category?.Name}");
                return result.Error;
            },
            kind == TextEntryKind.Title ? _twitch.GetTitleAsync : null);

        if (!fromDesktop && _deck.IsConnected) _deck.ShowTextEntry(request, _twitch);
        else TwitchInputWindow.Open(request, _twitch);
    }

    public void ApplyVoicemodSettings() => _voicemod.Configure(_config.Voicemod.Enabled);

    // ---------- Voicemod ----------

    private void OnVoicemodStateChanged()
    {
        if (_disposed) return;
        if (IntegrationSignature() != _integrationSignature) RefreshTiles();
    }

    private async void RunVoicemodTile(ShortcutItem item)
    {
        try
        {
            if (item.VoicemodAction is not { } action)
            {
                ShowWarning($"Aucune action Voicemod n'est définie pour « {item.Name} ».");
                return;
            }
            if (!_voicemod.IsConnected)
            {
                ShowWarning(_config.Voicemod.Enabled
                    ? "Voicemod n'est pas connecté : vérifie qu'il est lancé."
                    : "Voicemod n'est pas activé : ajoute-le dans Paramètres › Applications connectées.");
                return;
            }
            if (await _voicemod.ExecuteAsync(action, item.VoicemodTarget) is { } error)
                ShowWarning($"« {item.Name} » : {error}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Action Voicemod « {item.Name} » en échec", ex);
        }
    }

    private static bool TrySetClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("twitch-clipboard", "Presse-papiers indisponible", ex);
            return false;
        }
    }

    /// <summary>Tuile armée : elle s'affiche en rouge 3 s sur la Switch, puis revient à la normale si aucun second appui n'est venu.</summary>
    private void OnTileArmed(ShortcutItem item, bool fromDesktop)
    {
        RefreshTiles();
        if (fromDesktop) TrayIconService.ShowNotice($"Clique encore pour confirmer : {item.Name}");

        _confirmTimer ??= new DispatcherTimer();
        _confirmTimer.Stop();
        _confirmTimer.Interval = TapConfirmation.Delay + TimeSpan.FromMilliseconds(150);
        _confirmTimer.Tick -= OnConfirmTimeout;
        _confirmTimer.Tick += OnConfirmTimeout;
        _confirmTimer.Start();
    }

    private void OnConfirmTimeout(object? sender, EventArgs e)
    {
        _confirmTimer?.Stop();
        RefreshTiles();
    }
}
