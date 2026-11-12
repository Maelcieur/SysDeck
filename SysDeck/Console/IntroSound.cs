using System.IO;
using NAudio.Wave;
using SysDeck.Infrastructure;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Bande-son ORIGINALE de l'intro, synthétisée par le code (cloches, souffles, accords) : aucun son enregistré.
/// Chaque évènement est calé sur un emblème de l'animation (temps en secondes).
/// </summary>
public static class IntroSound
{
    public const int Rate = 44100;

    /// <summary>Génère la piste stéréo (float) de <paramref name="seconds"/> secondes.</summary>
    public static float[] Build(double seconds = 9.5)
    {
        var n = (int)(seconds * Rate);
        var l = new float[n];
        var r = new float[n];
        var rnd = new Random(7);

        // Souffle grave qui monte pendant que la lumière apparaît.
        Pad(l, r, 0.3, new[] { 33, 40 }, 4.3, 0.10);              // La 1, Mi 2
        Whoosh(l, r, rnd, 0.9, 1.5, true, 0.05);                  // air, avec le trait de lumière
        // Nappe chaude (La mineur 9) quand l'emblème se dessine.
        Pad(l, r, 2.3, new[] { 45, 52, 55, 59 }, 3.0, 0.07);      // La 2, Mi 3, Sol 3, Si 3
        // Révélation du nom : impact sourd, accord chaud, scintillement très discret.
        Boom(l, r, rnd, 4.4, 0.30);
        Pad(l, r, 4.4, new[] { 33, 45, 52, 57, 60, 64 }, 3.8, 0.10);
        Bell(l, r, 4.45, 76, 2.8, 0.025, 0);
        // Reflet qui balaie le nom : souffle aérien.
        Whoosh(l, r, rnd, 5.75, 1.1, true, 0.035);

        // Écho stéréo léger (impression d'espace), puis limitation douce.
        Echo(l, r, 0.23, 0.28);
        var stereo = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            stereo[i * 2] = (float)Math.Tanh(l[i] * 1.2);
            stereo[i * 2 + 1] = (float)Math.Tanh(r[i] * 1.2);
        }
        return stereo;
    }

    // ---------- Intro cyberpunk (calée sur ConsoleIntro.BuildCyber) ----------

    /// <summary>Bande-son de l'intro cyberpunk : chaque évènement tombe sur une image de l'animation.</summary>
    public static float[] BuildCyber(double seconds = 8.6)
    {
        var n = (int)(seconds * Rate);
        var l = new float[n];
        var r = new float[n];
        var rnd = new Random(31);

        // Bourdon sombre de fond.
        Pad(l, r, 0.1, new[] { 33 }, 7.4, 0.06);
        // Une ligne de terminal = un bip de données (0.35 s + 0.22 s par ligne).
        for (var i = 0; i < 7; i++) Blip(l, r, 0.35 + i * 0.22, 1400 + rnd.Next(900), 0.05, 0.035, -0.4 + i * 0.12);
        // Brouillage du texte.
        Crackle(l, r, rnd, 2.2, 0.22, 0.10);
        // Montée électronique puis impact sur l'apparition du nom.
        Sweep(l, r, 1.6, 1.0, 110, 880, 0.05);
        Boom(l, r, rnd, 2.6, 0.38);
        Crackle(l, r, rnd, 2.6, 0.12, 0.09);
        // Basse synthé qui pulse (darksynth) à partir de la stabilisation du néon.
        for (var k = 0; k < 8; k++) SawBass(l, r, 3.0 + k * 0.5, k % 4 == 3 ? 43 : 33, 0.42, 0.10);
        // « // MODE CONSOLE » tapé : un clic par lettre.
        for (var i = 0; i < 15; i++) Blip(l, r, 3.5 + i * 0.05, 2600, 0.012, 0.02, 0);
        // Sauts de glitch.
        Crackle(l, r, rnd, 4.2, 0.16, 0.09);
        Crackle(l, r, rnd, 5.6, 0.16, 0.09);
        // Extinction façon écran cathodique.
        Crackle(l, r, rnd, 7.3, 0.1, 0.08);
        Sweep(l, r, 7.35, 0.45, 1200, 60, 0.06);

        Echo(l, r, 0.18, 0.22);
        var stereo = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            stereo[i * 2] = (float)Math.Tanh(l[i] * 1.3);
            stereo[i * 2 + 1] = (float)Math.Tanh(r[i] * 1.3);
        }
        return stereo;
    }

    /// <summary>Bip « carré » (harmoniques impaires) très court.</summary>
    private static void Blip(float[] l, float[] r, double at, double freq, double len, double vol, double pan)
    {
        var start = (int)(at * Rate);
        for (var k = 0; k < len * Rate; k++)
        {
            var t = k / (double)Rate;
            var env = Math.Min(1, t / 0.002) * Math.Min(1, (len - t) / 0.004);
            var v = Math.Sin(2 * Math.PI * freq * t) + Math.Sin(6 * Math.PI * freq * t) / 3 + Math.Sin(10 * Math.PI * freq * t) / 5;
            Add(l, r, start + k, v * env * vol, pan);
        }
    }

    /// <summary>Basse en dent de scie (harmoniques 1 à 10), attaque nette et décroissance.</summary>
    private static void SawBass(float[] l, float[] r, double at, int midi, double len, double vol)
    {
        var f = Freq(midi);
        var start = (int)(at * Rate);
        for (var k = 0; k < len * Rate; k++)
        {
            var t = k / (double)Rate;
            var env = Math.Min(1, t / 0.004) * Math.Exp(-t * 5);
            double v = 0;
            for (var h = 1; h <= 10; h++) v += Math.Sin(2 * Math.PI * f * h * t) / h * Math.Exp(-h * t * 3);   // s'assombrit
            Add(l, r, start + k, v * env * vol, 0);
        }
    }

    /// <summary>Glissando (montée ou descente) en dent de scie douce.</summary>
    private static void Sweep(float[] l, float[] r, double at, double len, double f0, double f1, double vol)
    {
        var start = (int)(at * Rate);
        double phase = 0;
        for (var k = 0; k < len * Rate; k++)
        {
            var p = k / (len * Rate);
            var f = f0 * Math.Pow(f1 / f0, p);
            phase += 2 * Math.PI * f / Rate;
            var env = Math.Sin(Math.PI * Math.Min(1, p * 1.2));
            var v = Math.Sin(phase) + Math.Sin(2 * phase) / 2 + Math.Sin(3 * phase) / 3;
            Add(l, r, start + k, v * env * vol, 0);
        }
    }

    /// <summary>Grésillement numérique : bruit échantillonné grossièrement, haché.</summary>
    private static void Crackle(float[] l, float[] r, Random rnd, double at, double len, double vol)
    {
        var start = (int)(at * Rate);
        double hold = 0;
        var step = 0;
        for (var k = 0; k < len * Rate; k++)
        {
            if (step-- <= 0) { hold = rnd.NextDouble() * 2 - 1; step = 20 + rnd.Next(80); }
            var t = k / (double)Rate;
            var gate = ((int)(t * 70)) % 3 != 1 ? 1 : 0;
            Add(l, r, start + k, hold * gate * vol * Math.Exp(-t * 6), (rnd.NextDouble() - 0.5) * 0.6);
        }
    }

    /// <summary>
    /// Joue la piste. Avec <paramref name="deviceId"/> (sortie de la TV), le son va DIRECTEMENT sur la TV, même si
    /// Windows change la sortie par défaut pendant la resynchronisation HDMI. Renvoie le lecteur (à arrêter pour couper).
    /// </summary>
    public static IDisposable? Play(float[] stereo, string? deviceId = null)
    {
        try
        {
            var bytes = new byte[stereo.Length * 4];
            Buffer.BlockCopy(stereo, 0, bytes, 0, bytes.Length);
            var stream = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
            var output = CreateOutput(deviceId);
            output.Init(stream);
            output.Play();
            return new Player(output, stream);
        }
        catch (Exception ex)
        {
            Log.Warn("Intro : lecture du son impossible", ex);
            return null;
        }
    }

    /// <summary>Sortie WASAPI sur l'appareil demandé (la TV), sinon la sortie par défaut.</summary>
    public static IWavePlayer CreateOutput(string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                return new WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 120);
            }
            catch (Exception ex)
            {
                Log.WarnOnce("intro-device", "Intro : sortie de la TV introuvable, sortie par défaut utilisée", ex);
            }
        }
        return new WaveOutEvent { DesiredLatency = 120 };
    }

    private sealed class Player(IWavePlayer output, WaveStream stream) : IDisposable
    {
        public void Dispose()
        {
            try { output.Stop(); } catch { }
            output.Dispose();
            stream.Dispose();
        }
    }

    // ---------- Synthèse ----------

    private static double Freq(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);

    private static void Add(float[] l, float[] r, int i, double v, double pan)
    {
        if (i < 0 || i >= l.Length) return;
        l[i] += (float)(v * Math.Min(1, 1 - pan));
        r[i] += (float)(v * Math.Min(1, 1 + pan));
    }

    /// <summary>Cloche : partiels inharmoniques, attaque nette, décroissance exponentielle.</summary>
    private static void Bell(float[] l, float[] r, double at, int midi, double len, double vol, double pan)
    {
        var f = Freq(midi);
        var start = (int)(at * Rate);
        var count = (int)(len * Rate);
        for (var k = 0; k < count; k++)
        {
            var t = k / (double)Rate;
            var env = Math.Min(1, t / 0.004) * Math.Exp(-t * 4.0 / len * 2);
            var v = Math.Sin(2 * Math.PI * f * t) + 0.45 * Math.Sin(2 * Math.PI * f * 2.76 * t) * Math.Exp(-t * 6)
                    + 0.25 * Math.Sin(2 * Math.PI * f * 5.4 * t) * Math.Exp(-t * 10);
            Add(l, r, start + k, v * env * vol, pan);
        }
    }

    /// <summary>Nappe : sinus légèrement désaccordés, attaque et relâchement doux.</summary>
    private static void Pad(float[] l, float[] r, double at, int[] notes, double len, double vol)
    {
        var start = (int)(at * Rate);
        var count = (int)(len * Rate);
        foreach (var (note, idx) in notes.Select((n, i) => (n, i)))
        {
            var f = Freq(note);
            var pan = notes.Length == 1 ? 0 : -0.5 + idx / (double)(notes.Length - 1);
            for (var k = 0; k < count; k++)
            {
                var t = k / (double)Rate;
                var env = Math.Min(1, t / 0.25) * Math.Min(1, (len - t) / (len * 0.6));
                var v = Math.Sin(2 * Math.PI * f * t) * 0.6 + Math.Sin(2 * Math.PI * f * 1.003 * t) * 0.4 + Math.Sin(2 * Math.PI * f * 2 * t) * 0.15;
                Add(l, r, start + k, v * env * vol / notes.Length * 2.2, pan);
            }
        }
    }

    /// <summary>Souffle : bruit filtré dont la brillance monte (ou descend).</summary>
    private static void Whoosh(float[] l, float[] r, Random rnd, double at, double len, bool rising, double vol)
    {
        var start = (int)(at * Rate);
        var count = (int)(len * Rate);
        double lp = 0;
        for (var k = 0; k < count; k++)
        {
            var p = k / (double)count;
            var cutoff = rising ? 0.02 + p * 0.25 : 0.27 - p * 0.25;
            lp += cutoff * (rnd.NextDouble() * 2 - 1 - lp);
            var env = Math.Sin(Math.PI * p);
            Add(l, r, start + k, lp * env * vol * 3, rising ? -0.6 + 1.2 * p : 0.6 - 1.2 * p);
        }
    }

    /// <summary>Impact grave : sinus qui plonge (90 -> 38 Hz) + souffle bref.</summary>
    private static void Boom(float[] l, float[] r, Random rnd, double at, double vol)
    {
        var start = (int)(at * Rate);
        double phase = 0;
        for (var k = 0; k < Rate * 1.6; k++)
        {
            var t = k / (double)Rate;
            var f = 38 + 52 * Math.Exp(-t * 6);
            phase += 2 * Math.PI * f / Rate;
            var v = Math.Sin(phase) * Math.Exp(-t * 2.2) + (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 25) * 0.25;
            Add(l, r, start + k, v * vol, 0);
        }
    }

    private static void Echo(float[] l, float[] r, double delay, double feedback)
    {
        var d = (int)(delay * Rate);
        for (var i = d; i < l.Length; i++)
        {
            l[i] += (float)(r[i - d] * feedback);
            r[i] += (float)(l[i - d] * feedback);
        }
    }
}
