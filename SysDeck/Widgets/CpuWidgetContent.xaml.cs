using System.Globalization;
using SysDeck.Controls;

namespace SysDeck.Widgets;

public partial class CpuWidgetContent : UserControl
{
    private readonly GaugeReadout _load, _temp;

    public CpuWidgetContent()
    {
        InitializeComponent();
        // Les chiffres défilent en même temps que l'arc se remplit.
        _load = new GaugeReadout(LoadGauge, LoadValueRun);
        _temp = new GaugeReadout(TempGauge, TempValueRun);
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public void Update(string name, float? load, float? temp, float? clockMhz)
    {
        ClockText.Text = clockMhz is { } clock ? clock.ToString("N0", Fr) : "--";

        CpuNameText.Text = name;

        _load.Set(load);

        _temp.Set(temp);
    }
}
