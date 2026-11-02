using System.IO;
using System.Buffers.Binary;
using SysDeck.Infrastructure;

namespace SysDeck.Deck;

// NOTE : le firmware de la Switch est dans le dossier payload/ du dépôt (src/deck_proto.h, src/usb_descriptors.c).
// Toute modification du format des paquets doit être faite des deux côtés (version, magics, commandes,
// statuts, nombre d'événements).

/// <summary>
/// Protocole USB SwitchDeck. Doit rester identique à payload/src/deck_proto.h.
/// Le PC envoie un bloc commande de 512 octets (+ données éventuelles, multiple de 512),
/// la Switch répond par un bloc de 512 octets contenant les événements tactiles.
/// </summary>
public static class DeckProtocol
{
    public const int Version = 5;

    /// <summary>Octet « charging » de la réponse : bit 0 = en charge, bit 1 = première réponse d'une nouvelle session
    /// (la Switch est restée connectée entre deux sessions : le PC doit renvoyer tout l'affichage et la luminosité).</summary>
    public const byte ReplyCharging = 1, ReplyNewSession = 2;
    public const byte ButtonVolDown = 2, ButtonVolUp = 4; // bits de Reply.Buttons (BTN_VOL_* de la BDK)
    public const int BlockSize = 512;
    public const uint CmdMagic = 0x4B434453;   // "SDCK"
    public const uint ReplyMagic = 0x52434453; // "SDCR"
    public const int ScreenWidth = 1280;
    public const int ScreenHeight = 720;
    public const int MaxEvents = 48;

    public enum Cmd : ushort
    {
        Ping = 1,
        Rect = 2,
        Present = 3,
        Brightness = 4,
        Bye = 5,
        Night = 6,     // kit nuit : en-tête + image + glyphes (stocké, sans changer l'affichage)
        NightTime = 7, // recale l'heure de nuit (arg = heure locale en secondes epoch « locale »)
        NightBrightness = 8, // (v5) change seulement la luminosité de veille du kit déjà reçu (arg = 0-100)
    }

    public const ushort FlagPresent = 1;

    public enum Status : ushort
    {
        Ok = 0,
        BadCmd = 1,
        BadRect = 2,
        BadNight = 3,
    }

    // Kit nuit : voir deck_proto.h (DECK_CMD_NIGHT).
    public const uint NightMagic = 0x4B4E4453; // "SDNK"
    public const ushort NightVersion = 1;
    public const int NightGlyphs = 11; // '0'..'9' puis ':'
    public const int NightBatteryGlyphs = 11; // '0'..'9' puis '%'

    /// <summary>
    /// Icône de batterie du kit nuit (v4) : position du coin haut-gauche, taille du contour, intérieur à remplir
    /// (relatif au contour), éclair, texte « NN% » à droite et couleurs XRGB du remplissage.
    /// </summary>
    public sealed record NightBattery(int X, int Y, int Width, int Height, int FillX, int FillY, int FillWidth,
        int FillHeight, int FillRadius, int BoltWidth, int BoltHeight, int TextHeight, int TextGap, int[] TextWidths,
        uint ColorNormal, uint ColorLow, uint ColorCritical, uint ColorCharging);

    public const int NightImageBytes = ScreenWidth * ScreenHeight * 4;
    public const int MaxDataBytes = 8 * 1024 * 1024; // limite d'une commande côté Switch (USB_EP_BULK_OUT_MAX_XFER)

    public enum TouchType : ushort
    {
        Down = 1,
        Move = 2,
        Up = 3,
    }

    public readonly record struct TouchEvent(TouchType Type, int X, int Y);

    public sealed record Reply(
        Status Status, uint Seq, int Battery, bool Charging, byte Buttons, TouchEvent[] Events, bool NewSession = false);

    /// <summary>Taille des données arrondie au bloc supérieur.</summary>
    public static int PaddedLength(int length) => (length + BlockSize - 1) / BlockSize * BlockSize;

    public static void WriteCmd(Span<byte> block, Cmd cmd, uint seq, ushort flags = 0,
        int x = 0, int y = 0, int w = 0, int h = 0, int dataLen = 0, uint arg = 0)
    {
        block[..BlockSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(block[0..], CmdMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(block[4..], (ushort)cmd);
        BinaryPrimitives.WriteUInt16LittleEndian(block[6..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], seq);
        BinaryPrimitives.WriteUInt16LittleEndian(block[12..], (ushort)x);
        BinaryPrimitives.WriteUInt16LittleEndian(block[14..], (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(block[16..], (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(block[18..], (ushort)h);
        BinaryPrimitives.WriteUInt32LittleEndian(block[20..], (uint)dataLen);
        BinaryPrimitives.WriteUInt32LittleEndian(block[24..], arg);
    }

    /// <summary>Heure locale en secondes depuis 1970 comptées en heure locale (sans fuseau), pour la RTC de la Switch.</summary>
    public static uint LocalEpoch(DateTime local)
        => (uint)(DateTime.SpecifyKind(local, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Met à jour la luminosité dans l'en-tête d'un kit nuit déjà assemblé (renvoi ultérieur cohérent).</summary>
    public static void PatchNightBrightness(Span<byte> kit, int brightness)
        => BinaryPrimitives.WriteUInt16LittleEndian(kit[6..], (ushort)Math.Clamp(brightness, 0, 100));

    /// <summary>Écrit l'en-tête du kit nuit (bloc de 512 octets, deck_night_hdr_t).</summary>
    public static void WriteNightHeader(Span<byte> block, int brightness, uint localTime, int centerX, int topY,
        int glyphHeight, ReadOnlySpan<int> glyphWidths, NightBattery? battery = null)
    {
        block[..BlockSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(block[0..], NightMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(block[4..], NightVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(block[6..], (ushort)Math.Clamp(brightness, 0, 100));
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], localTime);
        BinaryPrimitives.WriteUInt16LittleEndian(block[12..], (ushort)centerX);
        BinaryPrimitives.WriteUInt16LittleEndian(block[14..], (ushort)topY);
        BinaryPrimitives.WriteUInt16LittleEndian(block[16..], (ushort)glyphHeight);
        for (int i = 0; i < NightGlyphs; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(block[(18 + i * 2)..], (ushort)(i < glyphWidths.Length ? glyphWidths[i] : 0));

        if (battery is null) return; // octets à zéro : la Switch affiche un simple texte de batterie
        PutU16(block, 40, battery.X); PutU16(block, 42, battery.Y); PutU16(block, 44, battery.Width); PutU16(block, 46, battery.Height);
        PutU16(block, 48, battery.FillX); PutU16(block, 50, battery.FillY); PutU16(block, 52, battery.FillWidth); PutU16(block, 54, battery.FillHeight);
        PutU16(block, 56, battery.FillRadius); PutU16(block, 58, battery.BoltWidth); PutU16(block, 60, battery.BoltHeight);
        PutU16(block, 62, battery.TextHeight); PutU16(block, 64, battery.TextGap);
        for (int i = 0; i < NightBatteryGlyphs; i++) PutU16(block, 66 + i * 2, battery.TextWidths[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(block[88..], battery.ColorNormal);
        BinaryPrimitives.WriteUInt32LittleEndian(block[92..], battery.ColorLow);
        BinaryPrimitives.WriteUInt32LittleEndian(block[96..], battery.ColorCritical);
        BinaryPrimitives.WriteUInt32LittleEndian(block[100..], battery.ColorCharging);
    }

    private static void PutU16(Span<byte> block, int offset, int value)
        => BinaryPrimitives.WriteUInt16LittleEndian(block[offset..], (ushort)value);

    /// <summary>Met à jour l'heure locale d'un en-tête de kit déjà écrit.</summary>
    public static void SetNightTime(Span<byte> block, uint localTime)
        => BinaryPrimitives.WriteUInt32LittleEndian(block[8..], localTime);

    public static Reply ParseReply(ReadOnlySpan<byte> block)
    {
        if (block.Length < BlockSize || BinaryPrimitives.ReadUInt32LittleEndian(block) != ReplyMagic)
            throw new InvalidDataException("Réponse SwitchDeck invalide.");

        var status = (Status)BinaryPrimitives.ReadUInt16LittleEndian(block[6..]);
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
        byte battery = block[16];
        bool charging = (block[17] & ReplyCharging) != 0;
        bool newSession = (block[17] & ReplyNewSession) != 0;
        byte buttons = block[18];
        int declared = block[19];
        int count = Math.Min(declared, MaxEvents);
        // Au-delà de MaxEvents le bloc de 512 octets ne contient plus rien d'exploitable : les derniers événements
        // (peut-être un Up) sont perdus. On le signale ; le contrôleur se resynchronise sur « aucun doigt ».
        if (declared > MaxEvents)
            Log.WarnOnce("deck:events-truncated", $"Réponse Switch : {declared} événements tactiles annoncés, {MaxEvents} lus");

        var events = new TouchEvent[count];
        for (int i = 0; i < count; i++)
        {
            var ev = block[(20 + i * 8)..];
            events[i] = new TouchEvent(
                (TouchType)BinaryPrimitives.ReadUInt16LittleEndian(ev),
                BinaryPrimitives.ReadUInt16LittleEndian(ev[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(ev[4..]));
        }

        return new Reply(status, seq, battery == 0xFF ? -1 : battery, charging, buttons, events, newSession);
    }
}
