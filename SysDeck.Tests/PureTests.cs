using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Deck;
using SysDeck.Infrastructure;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class DeckLinkTests
{
    private const int W = DeckProtocol.ScreenWidth, H = DeckProtocol.ScreenHeight, Stride = W * 4;

    private static void Paint(byte[] f, int x, int y) => f[y * Stride + x * 4] = 0xFF;

    [Fact]
    public void IdenticalFrames_NoBands()
    {
        var bands = new List<(int, int, int, int)>();
        DeckLink.FindChangedBands(new byte[Stride * H], new byte[Stride * H], bands);
        Assert.Empty(bands);
    }

    [Fact]
    public void SinglePixel_GivesTightBand()
    {
        var f = new byte[Stride * H];
        Paint(f, 100, 50);
        var bands = new List<(int, int, int, int)>();
        DeckLink.FindChangedBands(f, new byte[Stride * H], bands);
        Assert.Equal(new[] { (50, 51, 100, 101) }, bands);
    }

    [Fact]
    public void CloseRows_Merge_FarRows_Split()
    {
        var f = new byte[Stride * H];
        Paint(f, 10, 10); Paint(f, 20, 15);
        Paint(f, 30, 100);
        var bands = new List<(int, int, int, int)>();
        DeckLink.FindChangedBands(f, new byte[Stride * H], bands);
        Assert.Equal(2, bands.Count);
        Assert.Equal((10, 16, 10, 21), bands[0]);
        Assert.Equal((100, 101, 30, 31), bands[1]);
    }

    [Fact]
    public void NearlyFullWidth_WidenedToFullWidth()
    {
        var f = new byte[Stride * H];
        Paint(f, 5, 7); Paint(f, W - 5, 7);
        var bands = new List<(int, int, int, int)>();
        DeckLink.FindChangedBands(f, new byte[Stride * H], bands);
        Assert.Equal(new[] { (7, 8, 0, W) }, bands);
    }

    [Fact]
    public void MoreThanSixBands_CollapsedToOne()
    {
        var f = new byte[Stride * H];
        for (int i = 0; i < 8; i++) Paint(f, 10 + i * 10, i * 50);
        var bands = new List<(int, int, int, int)>();
        DeckLink.FindChangedBands(f, new byte[Stride * H], bands);
        Assert.Single(bands);
        Assert.Equal(0, bands[0].Item1);
        Assert.Equal(7 * 50 + 1, bands[0].Item2);
        Assert.True(bands[0].Item3 <= 10 && bands[0].Item4 >= 81);
    }
}

public class DeckProtocolTests
{
    [Fact]
    public void PatchNightBrightness_ChangeSeulementLeChampLuminosite()
    {
        var kit = new byte[DeckProtocol.BlockSize];
        DeckProtocol.WriteNightHeader(kit, 40, 1234, 640, 300, 100, new int[11]);
        var avant = (byte[])kit.Clone();

        DeckProtocol.PatchNightBrightness(kit, 85);

        Assert.Equal(85, BinaryPrimitives.ReadUInt16LittleEndian(kit.AsSpan(6)));
        for (int i = 0; i < kit.Length; i++)
            if (i != 6 && i != 7) Assert.Equal(avant[i], kit[i]);
        DeckProtocol.PatchNightBrightness(kit, 500); // plafonnée
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(kit.AsSpan(6)));
    }

    [Fact]
    public void ProtocoleV7_CommandeActionSwitch()
    {
        Assert.Equal(7, DeckProtocol.Version); // v7 : actions matérielles Switch
        Assert.Equal(9, (int)DeckProtocol.Cmd.SwitchAction);
        Assert.Equal(8, (int)DeckProtocol.Cmd.NightBrightness);
    }

    private static byte[] ReplyBlock(byte declared, byte flags = 0, byte battery = 80)
    {
        var b = new byte[DeckProtocol.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, DeckProtocol.ReplyMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)DeckProtocol.Status.Ok);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 42);
        b[16] = battery; b[17] = flags; b[18] = 0x5; b[19] = declared;
        return b;
    }

    [Fact]
    public void ParseReply_ReadsFieldsAndEvents()
    {
        var b = ReplyBlock(2, DeckProtocol.ReplyCharging | DeckProtocol.ReplyNewSession);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), (ushort)DeckProtocol.TouchType.Down);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(24), 200);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), (ushort)DeckProtocol.TouchType.Up);
        var r = DeckProtocol.ParseReply(b);
        Assert.Equal(42u, r.Seq);
        Assert.Equal(80, r.Battery);
        Assert.True(r.Charging); Assert.True(r.NewSession);
        Assert.Equal(0x5, r.Buttons);
        Assert.Equal(2, r.Events.Length);
        Assert.Equal(new DeckProtocol.TouchEvent(DeckProtocol.TouchType.Down, 300, 200), r.Events[0]);
        Assert.Equal(DeckProtocol.TouchType.Up, r.Events[1].Type);
    }

    [Fact]
    public void ParseReply_TruncatesTo48Events()
    {
        var r = DeckProtocol.ParseReply(ReplyBlock(255));
        Assert.Equal(DeckProtocol.MaxEvents, r.Events.Length);
    }

    [Fact]
    public void ParseReply_UnknownBatteryIsMinusOne()
        => Assert.Equal(-1, DeckProtocol.ParseReply(ReplyBlock(0, battery: 0xFF)).Battery);

    [Fact]
    public void ParseReply_BadMagicOrShortBlock_Throws()
    {
        var b = ReplyBlock(0); b[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => DeckProtocol.ParseReply(b));
        Assert.Throws<InvalidDataException>(() => DeckProtocol.ParseReply(new byte[100]));
    }

    [Fact]
    public void WriteNightHeader_LayoutAndClamp()
    {
        var b = new byte[DeckProtocol.BlockSize];
        Array.Fill(b, (byte)0xAA);
        DeckProtocol.WriteNightHeader(b, 250, 12345, 640, 100, 60, new[] { 10, 11, 12 });
        Assert.Equal(DeckProtocol.NightMagic, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)));
        Assert.Equal(12345u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(640, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12)));
        Assert.Equal(60, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(16)));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(22)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(24)));
        Assert.All(b[40..], x => Assert.Equal(0, x));
    }

    [Fact]
    public void WriteNightHeader_WithBattery_WritesColorsAndWidths()
    {
        var b = new byte[DeckProtocol.BlockSize];
        var widths = Enumerable.Range(1, 11).ToArray();
        var bat = new DeckProtocol.NightBattery(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, widths, 0x11, 0x22, 0x33, 0x44);
        DeckProtocol.WriteNightHeader(b, 50, 0, 0, 0, 0, ReadOnlySpan<int>.Empty, bat);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(42)));
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(64)));
        Assert.Equal(11, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(66 + 10 * 2)));
        Assert.Equal(0x44u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(100)));
    }

    [Fact]
    public void LocalEpoch_TreatsLocalTimeAsUtcAndIgnoresKind()
    {
        Assert.Equal(0u, DeckProtocol.LocalEpoch(new DateTime(1970, 1, 1)));
        Assert.Equal(86400u + 3661, DeckProtocol.LocalEpoch(new DateTime(1970, 1, 2, 1, 1, 1, DateTimeKind.Local)));
    }

    [Fact]
    public void PaddedLength_RoundsUpToBlock()
    {
        Assert.Equal(0, DeckProtocol.PaddedLength(0));
        Assert.Equal(512, DeckProtocol.PaddedLength(1));
        Assert.Equal(512, DeckProtocol.PaddedLength(512));
        Assert.Equal(1024, DeckProtocol.PaddedLength(513));
    }
}

public class JsonFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sysdeck-tests-" + Guid.NewGuid().ToString("N"));
    public JsonFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    public record Cfg(string Name, int N);

    [Fact]
    public void Save_ThenLoad_RoundTrips_NoTempLeft()
    {
        var p = Path.Combine(_dir, "sub", "a.json");
        Assert.True(JsonFile.Save(p, new Cfg("x", 3)));
        Assert.Equal(new Cfg("x", 3), JsonFile.Load(p, () => new Cfg("d", 0)));
        Assert.False(File.Exists(p + ".tmp"));
    }

    [Fact]
    public void Save_Overwrites_ExistingFile()
    {
        var p = Path.Combine(_dir, "a.json");
        JsonFile.Save(p, new Cfg("old", 1));
        Assert.True(JsonFile.Save(p, new Cfg("new", 2)));
        Assert.Equal("new", JsonFile.Load(p, () => new Cfg("d", 0)).Name);
    }

    [Fact]
    public void Load_Missing_ReturnsDefault()
        => Assert.Equal("d", JsonFile.Load(Path.Combine(_dir, "none.json"), () => new Cfg("d", 0)).Name);

    [Fact]
    public void Load_Corrupt_SetsAsideAndReturnsDefault_ThenSaveAllowed()
    {
        var p = Path.Combine(_dir, "bad.json");
        File.WriteAllText(p, "{ not json");
        Assert.Equal("d", JsonFile.Load(p, () => new Cfg("d", 0)).Name);
        Assert.Single(Directory.GetFiles(_dir, "bad.corrupt-*.json"));
        Assert.True(JsonFile.Save(p, new Cfg("ok", 1)));
    }

    [Fact]
    public void Load_Locked_RefusesSubsequentSave_AndKeepsContent()
    {
        var p = Path.Combine(_dir, "locked.json");
        File.WriteAllText(p, "{\"Name\":\"real\",\"N\":9}");
        using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal("d", JsonFile.Load(p, () => new Cfg("d", 0)).Name);
        }
        Assert.False(JsonFile.Save(p, new Cfg("default", 0)));
        Assert.Contains("real", File.ReadAllText(p));
        Assert.Empty(Directory.GetFiles(_dir, "locked.corrupt-*"));
    }
}

public class PowerOnHistoryTests
{
    [Theory]
    [InlineData(12, 8, 20, true)]
    [InlineData(20, 8, 20, false)]
    [InlineData(8, 8, 20, true)]
    [InlineData(2, 22, 6, true)]
    [InlineData(12, 22, 6, false)]
    [InlineData(3, 5, 5, true)]
    public void IsDayHour(double h, double s, double e, bool expected)
        => Assert.Equal(expected, PowerOnHistory.IsDayHour(h, s, e));

    [Fact]
    public void SplitDayNight_AcrossMidnight_SumsToTotal()
    {
        var from = new DateTime(2025, 1, 1, 18, 0, 0);
        var until = new DateTime(2025, 1, 2, 10, 0, 0);
        var (day, night) = PowerOnHistory.SplitDayNight(from, until, 8, 20);
        Assert.Equal(4 * 3600, day);
        Assert.Equal(12 * 3600, night);
    }

    [Fact]
    public void SplitDayNight_EmptyRange_Zero()
    {
        var t = new DateTime(2025, 1, 1, 9, 0, 0);
        Assert.Equal((0d, 0d), PowerOnHistory.SplitDayNight(t, t, 8, 20));
    }
}

public class DrawingHelpersTests
{
    [Fact]
    public void Lerp_EndpointsMidpointAndClamp()
    {
        var a = Color.FromRgb(0, 100, 200); var b = Color.FromRgb(100, 200, 0);
        Assert.Equal(a, DrawingHelpers.Lerp(a, b, 0));
        Assert.Equal(b, DrawingHelpers.Lerp(a, b, 1));
        Assert.Equal(Color.FromRgb(50, 150, 100), DrawingHelpers.Lerp(a, b, 0.5));
        Assert.Equal(b, DrawingHelpers.Lerp(a, b, 7));
        Assert.Equal(a, DrawingHelpers.Lerp(a, b, -3));
    }

    [Fact]
    public void Lerp_ResultIsOpaque()
        => Assert.Equal(255, DrawingHelpers.Lerp(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(10, 255, 255, 255), 0.5).A);

    [Fact]
    public void WithAlpha_KeepsRgb()
        => Assert.Equal(Color.FromArgb(7, 1, 2, 3), DrawingHelpers.WithAlpha(Color.FromRgb(1, 2, 3), 7));
}
