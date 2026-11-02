using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>
/// Spectre du son qui sort du PC (capture « loopback » WASAPI du périphérique de sortie par défaut) :
/// tout ce qui s'entend — musique, jeux, vidéos — sans rien installer ni configurer.
/// La capture tourne sur un thread audio ; <see cref="ComputeBands"/> est appelé par l'affichage
/// (~30 fois par seconde) et fait la transformée de Fourier sur les derniers échantillons.
/// </summary>
public sealed class AudioSpectrumService : IDisposable
{
    private const int FftLog2 = 11;              // 2048 échantillons ≈ 43 ms à 48 kHz
    private const int FftSize = 1 << FftLog2;
    private const double MinFrequency = 40;
    private const double MaxFrequency = 16000;

    private readonly float[] _ring = new float[FftSize];
    private readonly object _ringLock = new();
    private int _ringPos;
    private DateTime _lastDataUtc;

    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = Enumerable.Range(0, FftSize)
        .Select(i => (float)FastFourierTransform.HannWindow(i, FftSize)).ToArray();

    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device; // gardé tant que la capture l'utilise, libéré à l'arrêt
    private string? _deviceId;
    private int _sampleRate = 48000;

    /// <summary>Nom du périphérique écouté (« Haut-parleurs (Realtek) »…), null si aucun.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>Démarre (ou redémarre) la capture sur la sortie audio par défaut actuelle.</summary>
    public void Start()
    {
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device = device;
            _deviceId = device.ID;
            DeviceName = device.FriendlyName;

            // Le périphérique est confié à la capture : il n'est libéré qu'à son arrêt (Stop).
            var capture = new WasapiLoopbackCapture(device);
            _sampleRate = capture.WaveFormat.SampleRate;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    Log.WarnOnce("spectrum:stopped", "Capture audio interrompue (sortie débranchée ?)", e.Exception);
            };
            capture.StartRecording();
            _capture = capture;
        }
        catch (Exception ex)
        {
            DeviceName = null;
            _deviceId = null;
            Log.WarnOnce("spectrum:start", "Capture du son de sortie impossible", ex);
        }
    }

    public void Stop()
    {
        var capture = _capture;
        var device = _device;
        _capture = null;
        _device = null;
        if (capture is null)
        {
            device?.Dispose();
            return;
        }
        try
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.StopRecording();
            capture.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Arrêt de la capture audio", ex);
        }
        finally
        {
            device?.Dispose();
        }
    }

    /// <summary>Redémarre la capture si la sortie par défaut a changé (casque branché, autre sortie choisie).</summary>
    public void FollowDefaultDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device.ID != _deviceId || _capture?.CaptureState != NAudio.CoreAudioApi.CaptureState.Capturing)
                Start();
        }
        catch
        {
            // aucune sortie audio pour l'instant : on réessaiera au prochain contrôle
            if (_capture is not null) Stop();
            DeviceName = null;
            _deviceId = null;
        }
    }

    // Le format « Extensible » (le plus courant en WASAPI) annonce flottant ou entier par ce GUID, pas par la taille.
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Convertit un échantillon (flottant 32 bits ou entier 16/24/32 bits) en flottant de -1 à 1.</summary>
    private static float DecodeSample(byte[] buffer, int at, int bytesPerSample, bool isFloat)
    {
        if (isFloat && bytesPerSample == 4) return BitConverter.ToSingle(buffer, at);
        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(buffer, at) / 32768f,
            // 24 bits little-endian : placé en haut d'un entier 32 bits pour garder le signe, puis ramené.
            3 => ((buffer[at] << 8 | buffer[at + 1] << 16 | buffer[at + 2] << 24) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(buffer, at) / 2147483648f,
            _ => 0
        };
    }

    /// <summary>Thread audio : mélange les canaux en mono et remplit le tampon circulaire.</summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture) return;
        var format = capture.WaveFormat;
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = bytesPerSample * channels;
        if (frameSize == 0) return;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);

        lock (_ringLock)
        {
            for (var offset = 0; offset + frameSize <= e.BytesRecorded; offset += frameSize)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    var at = offset + c * bytesPerSample;
                    sum += DecodeSample(e.Buffer, at, bytesPerSample, isFloat);
                }
                _ring[_ringPos] = sum / channels;
                _ringPos = (_ringPos + 1) % FftSize;
            }
            _lastDataUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Niveaux de 0 à 1 pour <paramref name="count"/> bandes réparties en échelle logarithmique
    /// (comme l'oreille : autant de barres pour les basses que pour les aigus).
    /// </summary>
    public double[] ComputeBands(int count, double sensitivity)
    {
        var bands = new double[count];

        lock (_ringLock)
        {
            // Rien ne joue : WASAPI n'envoie plus de données, les barres doivent retomber.
            if (DateTime.UtcNow - _lastDataUtc > TimeSpan.FromMilliseconds(150))
                return bands;

            for (var i = 0; i < FftSize; i++)
            {
                var sample = _ring[(_ringPos + i) % FftSize];
                _fft[i].X = sample * _window[i];
                _fft[i].Y = 0;
            }
        }

        FastFourierTransform.FFT(true, FftLog2, _fft);

        var binWidth = (double)_sampleRate / FftSize;
        var ratio = Math.Pow(MaxFrequency / MinFrequency, 1.0 / count);
        for (var b = 0; b < count; b++)
        {
            var low = MinFrequency * Math.Pow(ratio, b);
            var high = low * ratio;
            var first = Math.Clamp((int)(low / binWidth), 1, FftSize / 2 - 1);
            var last = Math.Clamp((int)Math.Ceiling(high / binWidth), first, FftSize / 2 - 1);

            double peak = 0;
            for (var k = first; k <= last; k++)
                peak = Math.Max(peak, Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y));

            // Décibels, avec une pente de +3 dB/octave : la musique a naturellement beaucoup moins
            // d'énergie dans les aigus, sans compensation les barres de droite ne bougeraient jamais.
            var centre = Math.Sqrt(low * high);
            var db = 20 * Math.Log10(peak + 1e-12) + 3 * Math.Log2(centre / 200);
            const double floorDb = -72, ceilingDb = -18;
            bands[b] = Math.Clamp((db - floorDb) / (ceilingDb - floorDb) * sensitivity, 0, 1);
        }
        return bands;
    }

    public void Dispose() => Stop();
}
