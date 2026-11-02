using System.Globalization;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck.Widgets;

public partial class PowerWidgetContent : UserControl
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private readonly GaugeReadout _watts;

    public PowerWidgetContent()
    {
        InitializeComponent();
        // Les chiffres défilent en même temps que l'arc se remplit.
        _watts = new GaugeReadout(WattsGauge, WattsValueRun);
    }

    public void Update(PowerUsageSummary summary, double gaugeMaxWatts)
    {
        var reading = summary.Reading;
        WattsGauge.Maximum = Math.Max(50, gaugeMaxWatts);
        _watts.Set(reading.TotalWatts);

        SessionCostText.Text = Euros(summary.SessionCost);
        SessionLabelText.Text = $"depuis l'allumage · {Duration(summary.Uptime)}" + (summary.OffPeakNow ? " · HC" : "");
        TodayText.Text = Euros(summary.TodayCost);
        MonthText.Text = Euros(summary.MonthCost);

        ProjectionText.Text = summary.ProjectedMonthCost is { } projected
            ? $"≈ {Euros(projected)}/mois · {summary.MonthKwh.ToString("0.0", Fr)} kWh ce mois"
            : $"{summary.MonthKwh.ToString("0.00", Fr)} kWh ce mois";

        UsageTodayText.Text = $"Aujourd'hui · jour {Duration(TimeSpan.FromHours(summary.TodayDayHours))} · soir {Duration(TimeSpan.FromHours(summary.TodayNightHours))}";

        if (summary.Lifetime is { } life)
        {
            // « ≈ » : les heures d'avant SysDeck sont reconstituées puis estimées, pas mesurées.
            LifetimeText.Text = $"≈ {Euros(life.Cost)}";
            LifetimeLabelText.Text = $"Depuis le {life.Since.ToString("d MMM yyyy", Fr)} · {life.Hours.ToString("N0", Fr)} h · {life.Kwh.ToString("N0", Fr)} kWh";
            UsageLifetimeText.Text = $"Jour {life.DayHours.ToString("N0", Fr)} h · soir {life.NightHours.ToString("N0", Fr)} h";
        }

        var cpu = $"CPU {(reading.CpuEstimated ? "~" : "")}{reading.CpuWatts:0} W";
        var gpu = reading.GpuWatts is { } g ? $" · GPU {g:0} W" : "";
        DetailsText.Text = $"{cpu}{gpu} · autres ~{reading.OtherWatts:0} W";
    }

    /// <summary>Plus de décimales pour les petites sommes, pour voir le compteur avancer.</summary>
    private static string Euros(double value) => value switch
    {
        < 1 => value.ToString("0.000", Fr),
        < 100 => value.ToString("0.00", Fr),
        _ => value.ToString("0", Fr)
    } + " €";

    private static string Duration(TimeSpan t) => t.TotalDays >= 1
        ? $"{(int)t.TotalDays} j {t.Hours} h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00}" : $"{t.Minutes} min";
}
