using System.Buffers.Binary;
using System.Text.Json;
using SysDeck.Deck;
using SysDeck.Models;
using Xunit;

namespace SysDeck.Tests;

public class JoyConTests
{
    private static byte[] Reply(int version, uint buttons = 0, uint pressed = 0, byte flags = 0)
    {
        var block = new byte[DeckProtocol.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(block, DeckProtocol.ReplyMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), (ushort)version);
        block[16] = 80;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(404), buttons);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(408), pressed);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(412), 2100);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(414), 2000);
        block[420] = flags;
        block[421] = 4 << 1 | 1; // plein, en charge
        return block;
    }

    [Fact]
    public void Reply_V6_ReadsJoyCon()
    {
        var a = 1u << (int)JoyConButton.A;
        var reply = DeckProtocol.ParseReply(Reply(6, buttons: a, pressed: a, flags: DeckProtocol.JoyConLeft | DeckProtocol.JoyConRight));

        Assert.Equal(6, reply.Version);
        Assert.True(reply.JoyCon.IsDown(JoyConButton.A));
        Assert.True(reply.JoyCon.WasPressed(JoyConButton.A));
        Assert.False(reply.JoyCon.IsDown(JoyConButton.B));
        Assert.True(reply.JoyCon.LeftConnected && reply.JoyCon.RightConnected);
        Assert.Equal(2100, reply.JoyCon.LeftX);
        Assert.Equal(100, DeckProtocol.JoyConState.BatteryPercent(reply.JoyCon.BatteryLeft, true));
        Assert.True(DeckProtocol.JoyConState.BatteryCharging(reply.JoyCon.BatteryLeft));
    }

    [Fact]
    public void Reply_V5_HasNoJoyCon()
    {
        var reply = DeckProtocol.ParseReply(Reply(5, buttons: 0xFFFF, flags: 3));
        Assert.Equal(5, reply.Version);
        Assert.False(reply.JoyCon.AnyConnected);
        Assert.Equal(0u, reply.JoyCon.Buttons);
    }

    [Fact]
    public void Stick_CalibratesOnRestAndHasDeadZone()
    {
        var stick = new JoyConStick();
        Assert.Equal((0, 0), stick.Read(2000, 2100, invertY: false)); // repos = centre
        Assert.Equal((0, 0), stick.Read(2200, 2300, invertY: false)); // zone morte
        var (x, y) = stick.Read(2000 + 1700, 2100 + 1700, invertY: false);
        Assert.Equal(1, x, 3);
        Assert.Equal(1, y, 3);
        Assert.Equal(-1, stick.Read(2000, 2100 + 1700, invertY: true).Y, 3); // Switch Lite : Y inversé
        Assert.Equal((0, 0), new JoyConStick().Read(0, 0, false)); // absent
    }

    [Theory]
    [InlineData(0.9, 0.1, false, 1, 0)]
    [InlineData(0.1, 0.9, false, 0, -1)] // stick vers le haut = monter dans la grille
    [InlineData(0.3, 0.2, false, 0, 0)]
    [InlineData(0.9, 0.1, true, 0, 0)]   // volume : horizontal ignoré
    [InlineData(0, -0.8, true, 0, 1)]
    public void Stick_Direction(double x, double y, bool vertical, int dx, int dy)
        => Assert.Equal((dx, dy), DeckController.Direction(x, y, vertical));

    [Fact]
    public void Cursor_MovesToNearestTileSkippingGaps()
    {
        var a = new ShortcutItem { Name = "a" };
        var b = new ShortcutItem { Name = "b" };
        var c = new ShortcutItem { Name = "c" };
        var d = new ShortcutItem { Name = "d" };
        var cells = new Dictionary<ShortcutItem, (int Col, int Row)> { [a] = (0, 0), [b] = (3, 0), [c] = (1, 1), [d] = (3, 2) };

        Assert.Same(a, TileGrid.First(cells));
        Assert.Same(b, TileGrid.Neighbor(cells, a, 1, 0)); // case vide sautée, même ligne préférée
        Assert.Same(c, TileGrid.Neighbor(cells, a, 0, 1));
        Assert.Same(d, TileGrid.Neighbor(cells, b, 0, 1));
        Assert.Null(TileGrid.Neighbor(cells, a, -1, 0));
    }

    [Fact]
    public void Page_OverridesCommonBinding()
    {
        var global = JoyConSettings.CreateDefault();
        var page = new JoyConSettings { Buttons = { [JoyConButton.A] = new JoyConBinding() }, RightStick = JoyConStickMode.Mouse };

        Assert.Equal(JoyConCommand.CursorPress, JoyConInfo.Resolve(JoyConButton.A, global, null)!.Command);
        Assert.True(JoyConInfo.Resolve(JoyConButton.A, global, page)!.IsEmpty); // « aucune » propre à la page
        Assert.Equal(JoyConCommand.PageNext, JoyConInfo.Resolve(JoyConButton.R, global, page)!.Command);
        Assert.Equal(JoyConStickMode.Mouse, JoyConInfo.ResolveStick(false, global, page));
        Assert.Equal(JoyConStickMode.Cursor, JoyConInfo.ResolveStick(true, global, page));
    }

    [Fact]
    public void Settings_RoundTripAndOldFilesGetDefaults()
    {
        var settings = new DeckSettings();
        settings.Pages.Add(new DeckPage { Name = "P", JoyCon = new JoyConSettings { LeftStick = JoyConStickMode.Scroll } });
        settings.JoyCon.Buttons[JoyConButton.ZR] = new JoyConBinding { Command = JoyConCommand.Tile, Tile = new ShortcutItem { Name = "Muet", Kind = TileKind.MicMute } };

        var copy = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!;
        copy.Migrate();
        Assert.Equal("Muet", copy.JoyCon.Buttons[JoyConButton.ZR].Tile!.Name);
        Assert.Equal(JoyConStickMode.Scroll, copy.Pages[0].JoyCon!.LeftStick);

        var old = JsonSerializer.Deserialize<DeckSettings>("""{"TileSize":120,"JoyCon":null}""")!;
        old.Migrate();
        Assert.Equal(JoyConCommand.PageNext, old.JoyCon.Buttons[JoyConButton.R].Command);
    }
}
