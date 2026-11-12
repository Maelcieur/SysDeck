using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>Bruitages de l'interface TV.</summary>
public enum UiSound { Move, Select, Back, Tab, Launch, Open, Error, Glitch }

/// <summary>
/// Bruitages courts et discrets, synthétisés au démarrage (aucun fichier son) et joués sur la sortie de la TV par un
/// mélangeur WASAPI laissé ouvert : pas de délai, et plusieurs sons peuvent se chevaucher.
/// </summary>
public sealed class UiSounds : IDisposable
{
    private const int Rate = 48000;
    private readonly Dictionary<UiSound, float[]> _clips = new(), _cyberClips = new();

    /// <summary>Bruitages synthétiques (bips) assortis à l'intro cyberpunk.</summary>
    public bool Cyber { get; set; }
    private MixingSampleProvider? _mixer;
    private IWavePlayer? _output;
    private long _lastMove;

    public UiSounds(string? deviceId, bool cyber = false)
    {
        Cyber = cyber;
        try
        {
            foreach (UiSound s in Enum.GetValues(typeof(UiSound))) { _clips[s] = Build(s); _cyberClips[s] = BuildCyber(s); }
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)) { ReadFully = true };
            _output = CreateOutput(deviceId);
            _output.Init(_mixer);
            _output.Play();
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : bruitages indisponibles", ex);
            Dispose();
        }
    }

    public void Play(UiSound sound)
    {
        if (_mixer == null) return;
        // Déplacements répétés (stick maintenu) : pas plus d'un « tic » toutes les 45 ms.
        if (sound == UiSound.Move)
        {
            var now = Environment.TickCount64;
            if (now - _lastMove < 45) return;
            _lastMove = now;
        }
        try { _mixer.AddMixerInput(new ClipProvider((Cyber ? _cyberClips : _clips)[sound])); }
        catch (Exception ex) { Log.WarnOnce("ui-sound", "Mode console : bruitage", ex); }
    }

    private static IWavePlayer CreateOutput(string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return new WasapiOut(enumerator.GetDevice(deviceId), AudioClientShareMode.Shared, true, 40);
            }
            catch (Exception ex) { Log.WarnOnce("ui-sound-dev", "Mode console : sortie de la TV indisponible pour les bruitages", ex); }
        }
        return new WasapiOut(AudioClientShareMode.Shared, 40);
    }

    public void Dispose()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
        _mixer = null;
    }

    // ---------- Synthèse ----------

    /// <summary>Clip stéréo (L, R entrelacés).</summary>
    public static float[] Build(UiSound s)
    {
        return s switch
        {
            // Clic feutré : petite attaque aiguë très brève + corps grave.
            UiSound.Move => Mix(0.05, (t, _) => Click(t) * 0.035 + Tone(t, 620, 0.04, 0.001, 110) * 0.05),
            UiSound.Tab => Mix(0.08, (t, _) => Click(t) * 0.035 + Tone(t, 520, 0.07, 0.001, 60) * 0.05 + Tone(t, 780, 0.06, 0.001, 75) * 0.025),
            // « Toc » doux et grave pour valider ; même timbre, descendant, pour revenir.
            UiSound.Select => Mix(0.2, (t, _) => Click(t) * 0.04 + Tone(t, 330, 0.18, 0.002, 30) * 0.11 + Tone(t, 495, 0.15, 0.002, 38) * 0.05),
            UiSound.Back => Mix(0.2, (t, _) => Tone(t, 262, 0.16, 0.002, 32) * 0.09 + Tone(t - 0.045, 196, 0.15, 0.002, 30) * 0.08),
            // Ouverture (fiche, thèmes) : petite nappe qui monte.
            UiSound.Open => Mix(0.4, (t, _) => Tone(t, 392, 0.38, 0.06, 7) * 0.05 + Tone(t, 587, 0.36, 0.09, 8) * 0.035),
            // Lancement d'un jeu : montée grave et ample.
            UiSound.Launch => Mix(1.0, (t, _) =>
                Tone(t, 110, 1.1, 0.18, 2.6) * 0.14 + Tone(t, 165, 1.05, 0.22, 3) * 0.08 + Tone(t, 220, 1.0, 0.28, 3.4) * 0.06 +
                Tone(t - 0.12, 330, 0.9, 0.3, 4) * 0.03),
            UiSound.Glitch => Glitch(),
            // Erreur : double « bom » étouffé.
            UiSound.Error => Mix(0.32, (t, _) => Tone(t, 98, 0.16, 0.003, 16) * 0.16 + Tone(t - 0.13, 92, 0.18, 0.003, 15) * 0.14),
            _ => Array.Empty<float>(),
        };
    }

    /// <summary>Jeu « cyber » : bips carrés, montée électronique, basse.</summary>
    public static float[] BuildCyber(UiSound s)
    {
        return s switch
        {
            UiSound.Move => Mix(0.03, (t, _) => Square(t, 1250, 0.025) * 0.035),
            UiSound.Tab => Mix(0.07, (t, _) => Square(t, 900, 0.025) * 0.035 + Square(t - 0.035, 1350, 0.025) * 0.03),
            UiSound.Select => Mix(0.13, (t, _) => Square(t, 880, 0.05) * 0.045 + Square(t - 0.055, 1320, 0.07) * 0.045),
            UiSound.Back => Mix(0.13, (t, _) => Square(t, 1320, 0.05) * 0.04 + Square(t - 0.055, 660, 0.07) * 0.045),
            UiSound.Open => Mix(0.25, (t, _) => Square(t, 400 + t * 3200, 0.22) * 0.03),
            UiSound.Launch => Mix(1.0, (t, _) => Square(t, 220 + t * t * 1400, 0.6) * 0.035 + Saw(t - 0.6, 55, 0.4) * 0.14),
            UiSound.Error => Mix(0.3, (t, _) => Square(t, 110, 0.28) * 0.07),
            UiSound.Glitch => Glitch(),
            _ => Array.Empty<float>(),
        };
    }

    /// <summary>Onde carrée adoucie (harmoniques 1, 3, 5) avec petite enveloppe.</summary>
    private static double Square(double t, double freq, double len)
    {
        if (t < 0 || t > len) return 0;
        var env = Math.Min(1, t / 0.002) * Math.Min(1, (len - t) / 0.006);
        return (Math.Sin(2 * Math.PI * freq * t) + Math.Sin(6 * Math.PI * freq * t) / 3 + Math.Sin(10 * Math.PI * freq * t) / 5) * env;
    }

    /// <summary>Basse en dent de scie qui s'assombrit.</summary>
    private static double Saw(double t, double freq, double len)
    {
        if (t < 0 || t > len) return 0;
        double v = 0;
        for (var h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * freq * h * t) / h * Math.Exp(-h * t * 4);
        return v * Math.Min(1, t / 0.003) * Math.Exp(-t * 4);
    }

    /// <summary>Grésillement numérique court : bruit « écrasé » en paliers, haché.</summary>
    private static float[] Glitch()
    {
        var rnd = new Random(5);
        double hold = 0;
        var step = 0;
        return Mix(0.16, (t, i) =>
        {
            if (step-- <= 0) { hold = rnd.NextDouble() * 2 - 1; step = 30 + rnd.Next(60); }   // échantillonnage grossier
            var gate = ((int)(t * 90)) % 3 != 1 ? 1 : 0;                                       // hachures
            return hold * gate * Math.Exp(-t * 16) * 0.06 + Tone(t, 1200, 0.16, 0.001, 35) * 0.02;
        });
    }

    /// <summary>Petit clic très bref (attaque d'un bouton) : sinus aigu qui s'éteint en quelques millisecondes.</summary>
    private static double Click(double t) => Tone(t, 2800, 0.008, 0.0003, 700);

    /// <summary>Sinus doux (attaque <paramref name="attack"/> s, décroissance exponentielle) ; 0 hors de [0, len].</summary>
    private static double Tone(double t, double freq, double len, double attack, double decay)
    {
        if (t < 0 || t > len) return 0;
        var env = Math.Min(1, t / attack) * Math.Exp(-t * decay) * Math.Min(1, (len - t) / 0.01);
        return Math.Sin(2 * Math.PI * freq * t) * env;
    }

    private static float[] Mix(double seconds, Func<double, int, double> f)
    {
        var n = (int)(seconds * Rate);
        var data = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var v = (float)Math.Clamp(f(i / (double)Rate, i), -1, 1);
            data[i * 2] = v;
            data[i * 2 + 1] = v;
        }
        return data;
    }

    /// <summary>Lecture d'un clip une seule fois dans le mélangeur.</summary>
    private sealed class ClipProvider(float[] data) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
