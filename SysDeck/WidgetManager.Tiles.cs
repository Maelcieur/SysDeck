using System.Windows.Threading;
using SysDeck.Infrastructure;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

// Tuiles spéciales de la Switch : confirmation par second appui, micro / sortie muet.
public sealed partial class WidgetManager
{
    private DispatcherTimer? _confirmTimer;
    private DispatcherTimer? _muteTimer;
    private bool _mutePolling;

    /// <summary>Démarre la relecture périodique de l'état muet (sans effet tant qu'aucune tuile muet n'existe).</summary>
    private void StartTileTimers()
    {
        _muteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _muteTimer.Tick += (_, _) => RefreshMuteStates();
        _muteTimer.Start();
        RefreshMuteStates();
    }

    private void StopTileTimers()
    {
        _muteTimer?.Stop();
        _confirmTimer?.Stop();
    }

    /// <summary>Tuiles muet de la Switch (toutes pages) et de la barre.</summary>
    private List<ShortcutItem> MuteTiles()
        => _config.Deck.Pages.SelectMany(p => p.Apps).Concat(_shortcuts).Where(t => t.IsMuteTile).Distinct().ToList();

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

    private void RefreshTiles() => _deck.RefreshShortcuts();

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
