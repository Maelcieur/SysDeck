using SysDeck.Controls;


namespace SysDeck.Widgets;

public partial class RamWidgetContent : UserControl
{
    private readonly GaugeReadout _load;

    public RamWidgetContent()
    {
        InitializeComponent();
        _load = new GaugeReadout(LoadGauge, LoadValueRun);
    }

    public void Update(float? load, float? usedGb, float? totalGb)
    {
        _load.Set(load);

        if (usedGb is { } used && totalGb is { } total)
        {
            UsedText.Text = $"{used:0.0}";
            FreeText.Text = $"{Math.Max(0, total - used):0.0}";
            TotalText.Text = $"Total : {total:0.0} Go";
        }
        else
        {
            UsedText.Text = FreeText.Text = "--";
            TotalText.Text = "Total : -- Go";
        }
    }
}
