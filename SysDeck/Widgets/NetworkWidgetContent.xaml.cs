namespace SysDeck.Widgets;

public partial class NetworkWidgetContent : UserControl
{
    public NetworkWidgetContent() => InitializeComponent();

    public void Update(double downloadBytesPerSecond, double uploadBytesPerSecond)
    {
        DownloadText.Text = FormatSpeed(downloadBytesPerSecond);
        UploadText.Text = FormatSpeed(uploadBytesPerSecond);
        History.Push(downloadBytesPerSecond, uploadBytesPerSecond);
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        var kb = bytesPerSecond / 1024.0;
        return kb < 1000
            ? $"{kb:0.0} Ko/s"
            : $"{kb / 1024.0:0.0} Mo/s";
    }
}
