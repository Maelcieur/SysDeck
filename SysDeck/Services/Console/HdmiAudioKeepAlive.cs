using NAudio.CoreAudioApi;
using NAudio.Wave;
using SysDeck.Infrastructure;

namespace SysDeck.Services.ConsoleMode;

/// <summary>
/// Garde un flux audio SILENCIEUX ouvert sur la sortie de la TV pendant tout le mode console.
/// Sans lui, chaque fin de son (fin de l'intro, menu silencieux) met la liaison audio HDMI en veille : beaucoup de TV
/// se resynchronisent alors (écran noir un instant) et Windows perd brièvement la sortie de la TV.
/// Juste après la bascule d'écrans, Windows recrée la sortie HDMI (AUDCLNT_E_DEVICE_INVALIDATED) : on réessaie
/// toutes les secondes, et on rouvre le flux si Windows l'invalide plus tard.
/// </summary>
public sealed class HdmiAudioKeepAlive : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private WasapiOut? _output;
    private string? _deviceId;

    public void Start(string deviceId)
    {
        Stop();
        _deviceId = deviceId;
        var cts = new CancellationTokenSource();
        lock (_gate) _cts = cts;
        _ = Task.Run(() => OpenLoop(cts.Token));
    }

    private async Task OpenLoop(CancellationToken ct)
    {
        var tries = 0;
        while (!ct.IsCancellationRequested)
        {
            if (TryOpen(ct)) { Log.Info($"Mode console : flux audio silencieux ouvert sur la TV (essai {tries + 1})."); return; }
            if (++tries == 30) Log.Warn("Mode console : sortie audio de la TV toujours indisponible après 30 s, nouvel essai toutes les 5 s.");
            try { await Task.Delay(tries < 30 ? 1000 : 5000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private bool TryOpen(CancellationToken ct)
    {
        WasapiOut? output = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(_deviceId);
            if (device.State != DeviceState.Active) return false;
            var format = device.AudioClient.MixFormat;
            var silence = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, Math.Clamp(format.Channels, 1, 2)));
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            output.Init(silence);
            output.PlaybackStopped += (_, e) =>
            {
                // Sortie recréée par Windows (resynchro HDMI...) : on rouvre le flux.
                if (e.Exception == null || ct.IsCancellationRequested) return;
                lock (_gate) if (_output == output) _output = null;
                try { output.Dispose(); } catch { }
                _ = Task.Run(() => OpenLoop(ct));
            };
            lock (_gate)
            {
                if (ct.IsCancellationRequested) { output.Dispose(); return true; }
                _output = output;
            }
            output.Play();
            return true;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("hdmi-keepalive", "Mode console : sortie audio de la TV pas encore prête (nouvel essai)", ex);
            try { output?.Dispose(); } catch { }
            return false;
        }
    }

    public void Stop()
    {
        WasapiOut? o;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            o = _output;
            _output = null;
        }
        if (o == null) return;
        try { o.Stop(); } catch { }
        try { o.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
