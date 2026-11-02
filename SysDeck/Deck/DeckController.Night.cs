using System.Windows.Threading;
using Microsoft.Win32;
using SysDeck.Infrastructure;
using SysDeck.Services;

namespace SysDeck.Deck;

/// <summary>Kit du mode nuit de <see cref="DeckController"/> (rendu, empreinte, envoi à la Switch).</summary>
public sealed partial class DeckController
{
    // ---------- Mode nuit ----------

    private const int NightRefreshSeconds = 30; // période de vérification du rendu de nuit, même sans réglage modifié
    private bool _nightActive;
    private string _nightSignature = "";
    private byte[]? _nightHash;
    private DateTime _nightChecked;
    private DateTime _nightBrightnessChanged;
    private bool _nightBrightnessLive; // la luminosité est passée par la commande seule (v5)
    private static readonly TimeSpan NightBrightnessDebounce = TimeSpan.FromMilliseconds(400);

    public void SetNightClock(bool enabled)
    {
        _settings.NightClock = enabled;
        _nightSignature = ""; // prise en compte au prochain passage du timer
    }

    /// <summary>
    /// Protocole v5 : la luminosité part seule (512 octets) vers un kit déjà stocké. Un ancien payload (sans la
    /// commande) oblige à renvoyer le kit complet de 3,7 Mo : on laisse alors passer les changements rapprochés
    /// (curseur) et on n'envoie que la dernière valeur.
    /// </summary>
    public void SetNightBrightness(int percent)
    {
        _settings.NightBrightness = Math.Clamp(percent, 1, 100);
        if (_nightActive && _link.NightBrightnessSupported)
        {
            _link.SetNightBrightness(_settings.NightBrightness);
            _nightBrightnessLive = true;
            return;
        }
        _nightBrightnessChanged = DateTime.Now;
        _nightHash = null; // la luminosité n'est pas dans l'empreinte : forcer le renvoi du kit
        _nightSignature = "";
    }

    /// <summary>Réglages qui changent l'image de nuit : une différence relance le rendu sans attendre.</summary>
    private string NightSignature()
    {
        var clock = _settings.Find("clock");
        return string.Join("|", _settings.CurrentPage, _settings.BackgroundColor,
            _settings.BackgroundImage, _settings.BackgroundDim, _settings.ClockOpacity,
            clock is { Visible: true } ? $"{clock.X},{clock.Y},{clock.Width},{clock.Height}" : "");
    }

    /// <summary>
    /// Tient le kit nuit de la Switch à jour : retiré si l'option est désactivée ; sinon, connectée, rendu à chaque
    /// changement de réglage et toutes les 30 s, envoyé seulement si le résultat a changé.
    /// </summary>
    private void NightTick(bool force = false)
    {
        try
        {
            if (!_settings.NightClock || !_settings.Enabled)
            {
                if (_nightActive) _link.SetNightKit(null);
                _nightActive = false;
                _nightHash = null;
                return;
            }
            if (!_link.IsConnected) return;

            if (_nightBrightnessLive && !_link.NightBrightnessSupported)
            {
                // Ancien payload : la commande a été refusée, le kit complet porte la luminosité.
                _nightBrightnessLive = false;
                _nightHash = null;
                _nightSignature = "";
                _nightBrightnessChanged = DateTime.Now;
            }

            var signature = NightSignature();
            var now = DateTime.Now;
            // Luminosité en cours de réglage : on attend le prochain passage (la signature est déjà vide, rien n'est perdu).
            if (!force && now - _nightBrightnessChanged < NightBrightnessDebounce) return;
            if (_nightActive && signature == _nightSignature && (now - _nightChecked).TotalSeconds < NightRefreshSeconds) return;
            _nightSignature = signature;
            _nightChecked = now;

            var parts = Screen.RenderNight(_bitmap);
            var brightness = _settings.NightBrightness;
            var hash = HashNight(parts);
            if (_nightActive && _nightHash is not null && hash.AsSpan().SequenceEqual(_nightHash)) return;

            _nightHash = hash;
            _nightActive = true;
            _link.SetNightKit(BuildNightKit(parts, brightness, now));
        }
        catch (Exception ex)
        {
            Log.WarnOnce("deck:night", "Kit nuit de la Switch en échec", ex);
        }
    }

    /// <summary>
    /// Empreinte du rendu de nuit (sans la luminosité, qui voyage seule). Un fond GIF animé est ignoré (il changerait
    /// à chaque vérification).
    /// </summary>
    private byte[] HashNight(NightParts parts)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        if (!AnimatedGif.IsGif(_settings.BackgroundImage)) hash.AppendData(parts.Image);
        hash.AppendData(parts.Glyphs);
        if (parts.BatteryPixels is not null) hash.AppendData(parts.BatteryPixels);
        if (parts.Battery is { } battery) // position : l'icône change de coin si l'horloge est déplacée
            hash.AppendData(BitConverter.GetBytes(HashCode.Combine(battery.X, battery.Y, battery.Width, battery.TextGap)));
        hash.AppendData(BitConverter.GetBytes(HashCode.Combine(parts.CenterX, parts.TopY, parts.GlyphHeight)));
        foreach (var width in parts.GlyphWidths) hash.AppendData(BitConverter.GetBytes(width));
        return hash.GetHashAndReset();
    }

    /// <summary>Assemble le kit : en-tête de 512 octets, image de nuit, glyphes, le tout complété à 512 octets.</summary>
    private static byte[] BuildNightKit(NightParts parts, int brightness, DateTime now)
    {
        var battery = parts.BatteryPixels ?? Array.Empty<byte>();
        var kit = new byte[DeckProtocol.PaddedLength(
            DeckProtocol.BlockSize + DeckProtocol.NightImageBytes + parts.Glyphs.Length + battery.Length)];
        DeckProtocol.WriteNightHeader(kit, brightness, DeckProtocol.LocalEpoch(now), parts.CenterX, parts.TopY,
            parts.GlyphHeight, parts.GlyphWidths, parts.Battery);
        parts.Image.CopyTo(kit, DeckProtocol.BlockSize);
        parts.Glyphs.CopyTo(kit, DeckProtocol.BlockSize + DeckProtocol.NightImageBytes);
        battery.CopyTo(kit, DeckProtocol.BlockSize + DeckProtocol.NightImageBytes + parts.Glyphs.Length);
        return kit;
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => FinalNightSync();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) FinalNightSync();
    }

    /// <summary>
    /// Extinction ou veille du PC : dernier kit à jour (rendu sur le thread d'interface), puis heure et BYE sur le thread USB.
    /// Appelé depuis le thread des événements système ; ne bloque pas plus de ~4 s.
    /// </summary>
    private void FinalNightSync()
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() => NightTick(force: true), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1.5));
            _link.FlushAndBye(2500);
        }
        catch (Exception ex)
        {
            Log.WarnOnce("deck:night-final", "Envoi final à la Switch en échec", ex);
        }
    }
}
