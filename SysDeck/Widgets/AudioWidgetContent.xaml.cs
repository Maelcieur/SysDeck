using System.Windows;
using SysDeck.Services;


namespace SysDeck.Widgets;

public partial class AudioWidgetContent : UserControl
{
    public event Action<AudioDeviceInfo>? SetDefaultRequested;
    public event Action<AudioDeviceInfo>? HideRequested;

    public AudioWidgetContent() => InitializeComponent();

    public void SetDevices(IEnumerable<AudioDeviceInfo> devices) => DevicesList.ItemsSource = devices.ToList();

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioDeviceInfo device })
            SetDefaultRequested?.Invoke(device);
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioDeviceInfo device })
            HideRequested?.Invoke(device);
    }
}
