using SysDeck.Services.ConsoleMode;
using Xunit;

namespace SysDeck.Tests;

public class TvRemoteTests
{
    [Fact]
    public void Proto_RoundTripsNestedMessages()
    {
        var bytes = AndroidTvRemote.Proto.Msg(w =>
        {
            w.Varint(1, 2);
            w.Varint(2, 200);
            w.Message(10, r => { r.String(1, "atvremote"); r.String(2, "SysDeck"); });
        });
        var fields = AndroidTvRemote.Proto.Read(bytes);
        Assert.Equal(2UL, fields.Single(f => f.Field == 1).Varint);
        Assert.Equal(200UL, fields.Single(f => f.Field == 2).Varint);
        var inner = AndroidTvRemote.Proto.Read(fields.Single(f => f.Field == 10).Bytes!);
        Assert.Equal("SysDeck", System.Text.Encoding.UTF8.GetString(inner.Single(f => f.Field == 2).Bytes!));
    }

    [Fact]
    public void Proto_Varint_UsesSevenBitGroups()
    {
        Assert.Equal(new byte[] { 0xC8, 0x01 }, AndroidTvRemote.Proto.VarintBytes(200));
        Assert.Equal(new byte[] { 0x01 }, AndroidTvRemote.Proto.VarintBytes(1));
    }

    [Fact]
    public void ClientCertificate_CanBeReloaded_AndGivesAStableSecret()
    {
        var pfx = AndroidTvRemote.CreateClientCertificate();
        using var a = AndroidTvRemote.LoadCertificate(pfx);
        using var b = AndroidTvRemote.LoadCertificate(AndroidTvRemote.CreateClientCertificate());
        var s1 = AndroidTvRemote.ComputeSecret(a, b, "A1B2C3");
        var s2 = AndroidTvRemote.ComputeSecret(a, b, "A1B2C3");
        Assert.Equal(32, s1.Length);
        Assert.Equal(s1, s2);
        Assert.True(a.HasPrivateKey);
    }

    [Fact]
    public void Combo_Masks()
    {
        Assert.Equal(0x0030, GamepadComboWatcher.MaskOf("ViewMenu"));
        Assert.Equal(0x0310, GamepadComboWatcher.MaskOf("LbRbMenu"));
        Assert.Equal(0, GamepadComboWatcher.MaskOf("Off"));
        Assert.Contains(GamepadComboWatcher.Combos, c => c.Id == "ViewMenu");
    }
}
