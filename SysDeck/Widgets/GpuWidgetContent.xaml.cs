using System.Globalization;
using SysDeck.Controls;

namespace SysDeck.Widgets;

public partial class GpuWidgetContent : UserControl
{
    private readonly GaugeReadout _load, _temp;

    public GpuWidgetContent()
    {
        InitializeComponent();
        // Les chiffres défilent en même temps que l'arc se remplit.
        _load = new GaugeReadout(LoadGauge, LoadValueRun);
        _temp = new GaugeReadout(TempGauge, TempValueRun);
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>FPS du jeu au premier plan ; la bulle d'aide explique l'absence de valeur.</summary>
    public void SetFps(double? fps, Services.FpsState state)
    {
        FpsText.Text = fps is { } value ? Math.Round(value).ToString("0", Fr) : "--";
        FpsTile.ToolTip = state switch
        {
            Services.FpsState.NoPermission => "Mesure des FPS à autoriser : Paramètres › Système › Général.",
            Services.FpsState.Unavailable => "Mesure des FPS indisponible (voir le journal de SysDeck).",
            _ when fps is null => "Aucun jeu au premier plan.",
            _ => "Images par seconde du jeu au premier plan."
        };
    }

    public void Update(string name, float? load, float? temp, float? coreClockMhz, float? memoryClockMhz, float? memoryUsedGb)
    {
        GpuClockText.Text = coreClockMhz is { } core ? core.ToString("N0", Fr) : "--";
        MemClockText.Text = memoryClockMhz is { } mem ? mem.ToString("N0", Fr) : "--";
        VramText.Text = memoryUsedGb is { } used ? used.ToString("0.0", Fr) : "--";

        GpuNameText.Text = name;

        _load.Set(load);

        _temp.Set(temp);
    }
}
