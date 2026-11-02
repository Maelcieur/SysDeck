using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class AnimatedGifTests
{
    /// <summary>Exécute le test sur un thread STA avec son Dispatcher, comme le thread de l'interface.</summary>
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "délai dépassé");
        if (error is not null) throw new Exception("Échec sur le thread STA", error);
    }

    private static string WriteTwoFrameGif()
    {
        var encoder = new GifBitmapEncoder();
        foreach (var color in new[] { Colors.Red, Colors.Blue })
        {
            var pixels = Enumerable.Repeat(new[] { color.B, color.G, color.R, (byte)255 }, 16 * 16).SelectMany(p => p).ToArray();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4)));
        }
        var path = Path.Combine(Path.GetTempPath(), $"sysdeck-test-{Guid.NewGuid():N}.gif");
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    [Fact]
    public void Decoding_IsAsync_ThenCallbackOnCallerThread()
    {
        RunOnSta(() =>
        {
            var path = WriteTwoFrameGif();
            try
            {
                var callerThread = Environment.CurrentManagedThreadId;
                var frame = new DispatcherFrame();
                int? callbackThread = null;

                // Premier appel : pas encore en cache, décodage lancé à part, image fixe à afficher en attendant.
                var first = AnimatedGif.CreateBrush(path, 64, () =>
                {
                    callbackThread = Environment.CurrentManagedThreadId;
                    frame.Continue = false;
                });
                Assert.Null(first);

                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                timeout.Tick += (_, _) => frame.Continue = false;
                timeout.Start();
                Dispatcher.PushFrame(frame);
                timeout.Stop();

                Assert.Equal(callerThread, callbackThread);
                // Second appel : le GIF est en cache, le pinceau animé est prêt tout de suite.
                Assert.NotNull(AnimatedGif.CreateBrush(path, 64));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
}
