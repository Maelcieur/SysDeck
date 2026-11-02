using System.Windows;
using SysDeck.Controls;

namespace SysDeck.Widgets;

public partial class MouseWidgetContent : UserControl
{
    private readonly GaugeReadout _battery;

    public MouseWidgetContent()
    {
        InitializeComponent();
        _battery = new GaugeReadout(BatteryGauge, BatteryValueRun);
    }

    public void Update(int percent, bool charging)
    {
        _battery.Set(Math.Clamp(percent, 0, 100));
        MouseChargingText.Text = charging ? "En charge" : "Sur batterie";
        MouseChargingBolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        MouseLowText.Visibility = !charging && percent <= 15 ? Visibility.Visible : Visibility.Collapsed;
    }
}
