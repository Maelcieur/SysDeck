using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>
/// Fonds GIF animés : les images du GIF sont composées une fois (positions, effacements), réduites à la largeur
/// voulue et gardées en cache ; le pinceau les fait défiler avec une animation WPF (capturée aussi par le rendu Switch).
/// Le décodage se fait sur un thread à part : tant qu'il n'est pas fini, <see cref="CreateBrush"/> renvoie null
/// (l'appelant affiche l'image fixe) puis rappelle <c>onReady</c> sur son thread pour reconstruire le fond.
/// </summary>
public static class AnimatedGif
{
    private const int MaxFrames = 600;
    private const long MaxBytes = 400L * 1024 * 1024; // mémoire maximale des images composées d'un GIF

    private const long CacheBudgetBytes = 150L * 1024 * 1024; // budget total du cache, toutes entrées confondues

    private sealed record Frames(IReadOnlyList<BitmapSource> Images, IReadOnlyList<TimeSpan> Delays, long Bytes);

    private sealed class CacheEntry
    {
        public required string Path { get; init; }
        public Frames? Frames { get; init; }
        public long LastUse { get; set; }
    }

    // Le cache est partagé entre le thread UI (lecture) et les threads de décodage (écriture) : tout passe par le verrou.
    // Les images composées sont gelées (Freeze), donc utilisables depuis le thread UI.
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<(Dispatcher Dispatcher, Action Callback)>> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();
    private static long _useCounter;

    /// <summary>Images en cache pour cette clé ; sinon lance le décodage (une seule fois par clé) et renvoie faux.</summary>
    private static bool TryGet(string path, int maxWidth, string key, Action? onReady, out Frames? frames)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                hit.LastUse = ++_useCounter;
                frames = hit.Frames;
                return true;
            }

            frames = null;
            (Dispatcher, Action)? callback = onReady is null ? null : (Dispatcher.CurrentDispatcher, onReady);
            if (Pending.TryGetValue(key, out var waiting))
            {
                if (callback is { } c) waiting.Add(c);
                return false;
            }
            Pending[key] = callback is { } first ? [first] : [];
        }

        // WPF (DrawingVisual, RenderTargetBitmap) exige un thread STA ; il s'arrête une fois le GIF décodé.
        var thread = new Thread(() => Decode(path, maxWidth, key)) { IsBackground = true, Name = "Décodage GIF" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return false;
    }

    private static void Decode(string path, int maxWidth, string key)
    {
        var frames = Load(path, maxWidth);
        List<(Dispatcher Dispatcher, Action Callback)> callbacks;

        lock (CacheLock)
        {
            // Une nouvelle version du fichier ou une autre largeur rend les anciennes entrées inutiles.
            foreach (var oldKey in Cache.Where(e => string.Equals(e.Value.Path, path, StringComparison.OrdinalIgnoreCase)).Select(e => e.Key).ToList())
                Cache.Remove(oldKey);

            Cache[key] = new CacheEntry { Path = path, Frames = frames, LastUse = ++_useCounter };

            // Éviction LRU tant que le budget est dépassé (l'entrée qu'on vient d'ajouter est gardée).
            while (Cache.Sum(e => e.Value.Frames?.Bytes ?? 0) > CacheBudgetBytes && Cache.Count > 1)
            {
                var oldest = Cache.Where(e => e.Key != key).MinBy(e => e.Value.LastUse).Key;
                Cache.Remove(oldest);
            }

            callbacks = Pending.Remove(key, out var waiting) ? waiting : [];
        }

        foreach (var (dispatcher, callback) in callbacks)
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(callback);
        // Le Dispatcher créé implicitement pour ce thread est libéré avec lui.
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    public static bool IsGif(string? path)
        => !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pinceau animé pour ce GIF, ou null (pas un GIF, une seule image, fichier illisible, ou décodage en cours).
    /// Si le décodage vient d'être lancé, <paramref name="onReady"/> est appelé sur le thread appelant une fois
    /// le GIF prêt : il suffit alors de rappeler CreateBrush.
    /// </summary>
    public static ImageBrush? CreateBrush(string? path, int maxWidth, Action? onReady = null)
    {
        if (!IsGif(path) || !File.Exists(path)) return null;

        var key = $"{path}|{maxWidth}|{File.GetLastWriteTimeUtc(path!).Ticks}";
        if (!TryGet(path!, maxWidth, key, onReady, out var frames) || frames is null) return null;

        var brush = new ImageBrush(frames.Images[0]) { Stretch = Stretch.UniformToFill };
        var animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        var time = TimeSpan.Zero;
        for (var i = 0; i < frames.Images.Count; i++)
        {
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frames.Images[i], KeyTime.FromTimeSpan(time)));
            time += frames.Delays[i];
        }
        animation.Duration = time;
        brush.BeginAnimation(ImageBrush.ImageSourceProperty, animation);
        return brush;
    }

    private static Frames? Load(string path, int maxWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count < 2) return null;

            var first = decoder.Frames[0];
            var width = (int?)Query<ushort>(decoder.Metadata, "/logscrdesc/Width") ?? first.PixelWidth;
            var height = (int?)Query<ushort>(decoder.Metadata, "/logscrdesc/Height") ?? first.PixelHeight;
            if (width <= 0 || height <= 0) return null;

            var scale = Math.Min(1.0, maxWidth / (double)width);
            var w = Math.Max(1, (int)Math.Round(width * scale));
            var h = Math.Max(1, (int)Math.Round(height * scale));
            var full = new Rect(0, 0, w, h);

            var images = new List<BitmapSource>();
            var delays = new List<TimeSpan>();
            BitmapSource? baseImage = null; // ce qui reste affiché sous l'image suivante

            foreach (var frame in decoder.Frames)
            {
                if (images.Count >= MaxFrames || (long)(images.Count + 1) * w * h * 4 > MaxBytes) break;

                var meta = frame.Metadata as BitmapMetadata;
                var area = new Rect(
                    (Query<ushort>(meta, "/imgdesc/Left") ?? 0) * scale,
                    (Query<ushort>(meta, "/imgdesc/Top") ?? 0) * scale,
                    frame.PixelWidth * scale,
                    frame.PixelHeight * scale);

                var composite = Render(w, h, dc =>
                {
                    if (baseImage is not null) dc.DrawImage(baseImage, full);
                    dc.DrawImage(frame, area);
                });
                images.Add(composite);

                // Délai en centièmes de seconde ; 0 ou très court = 100 ms, comme les navigateurs.
                var delay = Query<ushort>(meta, "/grctlext/Delay") ?? 10;
                delays.Add(TimeSpan.FromMilliseconds(delay < 2 ? 100 : delay * 10));

                // Effacement avant l'image suivante : 2 = zone remise à vide, 3 = état précédent, sinon on garde.
                baseImage = (Query<byte>(meta, "/grctlext/Disposal") ?? 0) switch
                {
                    2 => Render(w, h, dc =>
                    {
                        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(area)));
                        dc.DrawImage(composite, full);
                        dc.Pop();
                    }),
                    3 => baseImage,
                    _ => composite
                };
            }
            return images.Count < 2 ? null : new Frames(images, delays, (long)images.Count * w * h * 4);
        }
        catch (Exception ex)
        {
            Log.Warn("GIF animé illisible", ex);
            return null;
        }
    }

    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static T? Query<T>(BitmapMetadata? metadata, string query) where T : struct
    {
        try
        {
            return metadata?.GetQuery(query) is T value ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
