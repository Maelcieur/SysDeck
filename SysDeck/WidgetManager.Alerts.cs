using System.Media;
using System.Windows;
using SysDeck.Infrastructure;
using SysDeck.Services;

namespace SysDeck;

// Alertes : température processeur / carte graphique, batterie de la souris, quota Claude.
public sealed partial class WidgetManager
{
    private const double ToastGap = -10; // les marges d'ombre se chevauchent un peu : cartes espacées d'environ 26 px

    private AlertMonitor? _alertMonitor;
    private readonly List<AlertToast> _toasts = new();

    private AlertMonitor Alerts => _alertMonitor ??= new AlertMonitor(_config.Alerts);

    /// <summary>Réglage d'alerte modifié : la souris et Claude peuvent devoir être lus même sans leur module.</summary>
    private void OnAlertSettingsChanged()
    {
        UpdateSensorDemand();
        if (_config.Alerts.MouseEnabled) RefreshMouseBattery();
    }

    private void ShowAlerts(IEnumerable<Alert> alerts)
    {
        foreach (var alert in alerts) ShowAlert(alert);
    }

    /// <summary>Affiche une alerte (remplace celle du même type encore affichée), avec un son si demandé.</summary>
    public void ShowAlert(Alert alert)
    {
        if (_disposed) return;
        try
        {
            foreach (var old in _toasts.Where(t => t.Kind == alert.Kind).ToList()) old.Dismiss();

            var toast = new AlertToast(alert, _config.Alerts);
            toast.Closed += (_, _) =>
            {
                _toasts.Remove(toast);
                LayoutToasts(animate: true);
            };
            _toasts.Add(toast);
            toast.Left = -10000; // placée après mesure de sa hauteur
            toast.Show();
            LayoutToasts(animate: true);

            if (_config.Alerts.SoundEnabled)
                (alert.Critical ? SystemSounds.Hand : SystemSounds.Exclamation).Play();
            Log.Info($"Alerte : {alert.Title} ({alert.Value})");
        }
        catch (Exception ex)
        {
            Log.Warn("Affichage d'une alerte impossible", ex);
        }
    }

    /// <summary>Empile les alertes dans le coin choisi de la zone de travail, la plus récente contre le bord.</summary>
    private void LayoutToasts(bool animate)
    {
        var area = SystemParameters.WorkArea;
        var position = _config.Alerts.Position;
        var fromTop = position is "TopRight" or "TopLeft";
        var fromLeft = position is "BottomLeft" or "TopLeft";
        var edge = fromTop ? area.Top : area.Bottom;
        for (var i = _toasts.Count - 1; i >= 0; i--)
        {
            var toast = _toasts[i];
            toast.UpdateLayout();
            var top = fromTop ? edge : edge - toast.StackHeight;
            var left = fromLeft ? area.Left : area.Right - toast.Width;
            // Une alerte qui vient d'apparaître est posée directement, les autres glissent vers leur place.
            toast.MoveTo(left, top, animate && toast.Left > -10000);
            edge = fromTop ? top + toast.StackHeight + ToastGap : top - ToastGap;
        }
    }

    /// <summary>Bouton « Tester » des Paramètres : montre l'alerte telle qu'elle apparaîtra.</summary>
    public void ShowSampleAlert(AlertKind kind) => ShowAlert(AlertMonitor.Sample(kind, _config.Alerts));

    private void CloseToasts()
    {
        foreach (var toast in _toasts.ToList())
        {
            try { toast.Close(); }
            catch (InvalidOperationException) { /* déjà fermée */ }
        }
        _toasts.Clear();
    }
}
