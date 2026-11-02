using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck.Widgets;

public partial class ClaudeWidgetContent : UserControl
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public ClaudeWidgetContent()
    {
        InitializeComponent();
    }

    public void Update(ClaudeUsageSnapshot s)
    {
        PlanText.Text = string.IsNullOrEmpty(s.Plan) ? "" : char.ToUpper(s.Plan[0], Fr) + s.Plan[1..];
        OfflineText.Visibility = s.NetworkError ? Visibility.Visible : Visibility.Collapsed;
        OfflineText.ToolTip = s.UpdatedAt is { } at
            ? $"Réseau indisponible : valeurs de {at.ToLocalTime():HH:mm}."
            : "Réseau indisponible.";

        // Valeurs anciennes (plus de 15 min sans réussite) : on le dit, plutôt que d'afficher un quota périmé sans prévenir.
        var stale = s.NetworkError && s.UpdatedAt is { } last && DateTimeOffset.Now - last > TimeSpan.FromMinutes(15);
        var message = s.State switch
        {
            ClaudeUsageState.NotConnected => "Non connecté à Claude Code",
            ClaudeUsageState.Expired => "Jeton expiré — ouvre Claude Code",
            ClaudeUsageState.Unavailable => "Quota indisponible (service Claude modifié)",
            _ when stale => $"Dernière mise à jour à {s.UpdatedAt!.Value.ToLocalTime():HH:mm}",
            _ => null
        };
        StatusText.Text = message ?? "";
        StatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;

        SetWindow(SessionGauge, SessionPercentText, SessionResetText, s.SessionPercent, s.SessionResetsAt);
        SetWindow(WeekGauge, WeekPercentText, WeekResetText, s.WeekPercent, s.WeekResetsAt);
    }

    private static void SetWindow(LinearGauge gauge, TextBlock percentText, TextBlock resetText,
        double? percent, DateTimeOffset? resetsAt)
    {
        gauge.Value = Math.Clamp(percent ?? 0, 0, 100);
        percentText.Text = percent is { } p ? $"{p:0} %" : "--";
        resetText.Text = percent is null || resetsAt is null ? "" : ResetText(resetsAt.Value);

        // Couleur selon le niveau ; au niveau normal, les couleurs du thème reprennent la main.
        if (percent > 90) SetColors(gauge, Color.FromRgb(0xFF, 0x4D, 0x4D), Color.FromRgb(0xFF, 0x2D, 0x55));
        else if (percent > 75) SetColors(gauge, Color.FromRgb(0xFF, 0xB0, 0x20), Color.FromRgb(0xFF, 0x8A, 0x00));
        else
        {
            gauge.ClearValue(LinearGauge.StartColorProperty);
            gauge.ClearValue(LinearGauge.EndColorProperty);
        }
    }

    private static void SetColors(LinearGauge gauge, Color start, Color end)
    {
        gauge.StartColor = start;
        gauge.EndColor = end;
    }

    /// <summary>« Réinit. dans 2 h 15 » si c'est proche, sinon « Réinit. jeu. 18:00 » (heure locale).</summary>
    private static string ResetText(DateTimeOffset resetsAt)
    {
        var remaining = resetsAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "Réinit. imminente";
        if (remaining < TimeSpan.FromHours(24))
            return remaining.TotalHours >= 1
                ? $"Réinit. dans {(int)remaining.TotalHours} h {remaining.Minutes:00}"
                : $"Réinit. dans {Math.Max(1, remaining.Minutes)} min";

        return $"Réinit. {resetsAt.ToLocalTime().ToString("ddd HH:mm", Fr)}";
    }
}
