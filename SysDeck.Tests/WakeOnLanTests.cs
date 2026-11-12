using SysDeck.Services.ConsoleMode;
using Xunit;

namespace SysDeck.Tests;

public class WakeOnLanTests
{
    [Fact]
    public void MagicPacket_IsSixFfThenSixteenMacs()
    {
        var p = WakeOnLan.MagicPacket("24-B7-2A-0D-9A-34");
        Assert.Equal(102, p.Length);
        Assert.All(p.Take(6), b => Assert.Equal(0xFF, b));
        Assert.Equal(new byte[] { 0x24, 0xB7, 0x2A, 0x0D, 0x9A, 0x34 }, p.Skip(96).ToArray());
    }

    [Fact]
    public void ParseMac_AcceptsSeparators_RejectsGarbage()
    {
        Assert.Equal(new byte[] { 0x24, 0xB7, 0x2A, 0x0D, 0x9A, 0x34 }, WakeOnLan.ParseMac("24:b7:2a:0d:9a:34"));
        Assert.Null(WakeOnLan.ParseMac("pas une mac"));
        Assert.Null(WakeOnLan.ParseMac(""));
    }

    [Fact]
    public void HdmiInputUri_FollowsPhilipsMapping()
    {
        Assert.EndsWith("%2FHW5", TvRemoteService.HdmiInputUri(1));
        Assert.EndsWith("%2FHW6", TvRemoteService.HdmiInputUri(2));
        Assert.EndsWith("%2FHW7", TvRemoteService.HdmiInputUri(3));
        Assert.EndsWith("%2FHW8", TvRemoteService.HdmiInputUri(4));
        Assert.StartsWith("content://android.media.tv/passthrough/", TvRemoteService.HdmiInputUri(1));
    }

    [Fact]
    public void InputCandidates_StartWithStandardKey_AndIncludePhilips()
    {
        var list = TvRemoteService.InputCandidates(4);
        Assert.Equal("key:246", list[0].Command);
        Assert.Contains(list, c => c.Command == "link:" + TvRemoteService.HdmiInputUri(4));
        Assert.Equal(list.Count, list.Select(c => c.Command).Distinct().Count());
    }
}
