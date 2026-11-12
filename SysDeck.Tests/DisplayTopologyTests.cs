using System.IO;
using System.Text.Json;
using SysDeck.Services.ConsoleMode;
using Xunit;
using static SysDeck.Services.ConsoleMode.DisplayNative;

namespace SysDeck.Tests;

public class DisplayTopologyTests
{
    private const string PathMain = @"\\?\DISPLAY#MAIN#1&a&0&UID256#{guid}";
    private const string PathTv = @"\\?\DISPLAY#TV#1&b&0&UID4352#{guid}";

    private static (PathInfo, ModeInfo, ModeInfo) Active(long luid, uint srcId, uint tgtId, int x, int y, uint w, uint h, uint rot = 1)
    {
        var l = Luid.From(luid);
        var src = new ModeInfo { InfoType = ModeSource, Id = srcId, AdapterId = l, D0 = w, D1 = h, D2 = 4, D3 = (uint)x, D4 = (uint)y };
        var tgt = new ModeInfo { InfoType = ModeTarget, Id = tgtId, AdapterId = l, D0 = 148500000 };
        var p = new PathInfo
        {
            SourceAdapter = l, SourceId = srcId, SourceModeIdx = 0, TargetAdapter = l, TargetId = tgtId,
            TargetModeIdx = 1, Rotation = rot, TargetAvailable = 1, Flags = PathActive,
        };
        return (p, src, tgt);
    }

    private static DisplaySnapshot TwoScreens(long luid)
    {
        var (p1, s1, t1) = Active(luid, 0, 256, 0, 0, 2560, 1440);
        var (p2, s2, t2) = Active(luid, 1, 4352, 2560, 0, 1920, 1080, 3);
        p2.SourceModeIdx = 2; p2.TargetModeIdx = 3;
        var snap = new DisplaySnapshot();
        snap.Paths.Add(new DisplayPathRecord { Raw = DisplaySnapshot.ToBytes(p1), DevicePath = PathMain, FriendlyName = "Principal" });
        snap.Paths.Add(new DisplayPathRecord { Raw = DisplaySnapshot.ToBytes(p2), DevicePath = PathTv, FriendlyName = "TV" });
        foreach (var m in new[] { s1, t1, s2, t2 }) snap.Modes.Add(DisplaySnapshot.ToBytes(m));
        return snap;
    }

    private static DisplayTarget Target(long luid, uint id, string devPath, string name)
        => new(DisplayTargetId.Make(luid, id, devPath), name, devPath, id, luid, 5, true, null, null);

    [Fact]
    public void NativeStructs_HaveExpectedSizes()
    {
        Assert.Equal(72, System.Runtime.InteropServices.Marshal.SizeOf<PathInfo>());
        Assert.Equal(64, System.Runtime.InteropServices.Marshal.SizeOf<ModeInfo>());
    }

    [Fact]
    public void Snapshot_JsonRoundTrip_KeepsEverything()
    {
        var snap = TwoScreens(0x1234);
        var back = JsonSerializer.Deserialize<DisplaySnapshot>(JsonSerializer.Serialize(snap))!;
        Assert.Equal(snap.Paths.Count, back.Paths.Count);
        Assert.Equal(snap.Paths[1].Raw, back.Paths[1].Raw);
        Assert.Equal(snap.Modes[3], back.Modes[3]);
        Assert.Equal(PathTv, back.Paths[1].DevicePath);
        Assert.True(snap.Signature().SetEquals(back.Signature()));
        Assert.Equal(2, back.Signature().Count);
    }

    [Fact]
    public void State_JsonRoundTrip()
    {
        var state = new ConsoleState
        {
            TvTargetId = DisplayTargetId.Make(0xABC, 4352, PathTv), TvAudioDeviceId = "{tv}", PreviousAudioDeviceId = "{pc}",
            DisableTvWhenIdle = true, Display = TwoScreens(1), Icons = new DesktopIconModel(),
        };
        state.Icons.Icons.Add(new DesktopIconPosition("Corbeille", 12, 34));
        var back = JsonSerializer.Deserialize<ConsoleState>(JsonSerializer.Serialize(state))!;
        Assert.Equal(state.TvTargetId, back.TvTargetId);
        Assert.Equal("{pc}", back.PreviousAudioDeviceId);
        Assert.True(back.DisableTvWhenIdle);
        Assert.Equal(new DesktopIconPosition("Corbeille", 12, 34), back.Icons!.Icons[0]);
        Assert.Equal(2, back.Display!.Paths.Count);
    }

    [Fact]
    public void Remap_AfterLuidChange_FollowsDevicePath()
    {
        var snap = TwoScreens(0x1111);
        var current = new[] { Target(0x2222, 256, PathMain, "Principal"), Target(0x2222, 4352, PathTv.ToLowerInvariant(), "TV") };
        var re = snap.RemapTo(current, out var missing);
        Assert.NotNull(re);
        Assert.Empty(missing);
        var (paths, modes) = re!.ToNative();
        Assert.All(paths, p => Assert.Equal(0x2222, p.TargetAdapter.ToLong()));
        Assert.All(paths, p => Assert.Equal(0x2222, p.SourceAdapter.ToLong()));
        Assert.All(modes, m => Assert.Equal(0x2222, m.AdapterId.ToLong()));
        Assert.True(snap.Signature().SetEquals(re.Signature())); // position, taille, rotation inchangées
    }

    [Fact]
    public void Remap_MissingScreen_DropsPathAndPrunesModes()
    {
        var snap = TwoScreens(0x1111);
        var re = snap.RemapTo(new[] { Target(0x3333, 256, PathMain, "Principal") }, out var missing);
        Assert.NotNull(re);
        Assert.Single(missing);
        Assert.Single(re!.Paths);
        Assert.Equal(2, re.Modes.Count);
        var (paths, _) = re.ToNative();
        Assert.Equal(0u, paths[0].SourceModeIdx);
        Assert.Equal(1u, paths[0].TargetModeIdx);
    }

    [Fact]
    public void Remap_NoScreenFound_ReturnsNull()
        => Assert.Null(TwoScreens(1).RemapTo(Array.Empty<DisplayTarget>(), out _));

    [Fact]
    public void TargetId_ParseAndMatch()
    {
        var id = DisplayTargetId.Make(0x7FFF0001, 4352, PathTv);
        Assert.True(DisplayTargetId.TryParse(id, out var luid, out var tid, out var path));
        Assert.Equal(0x7FFF0001, luid);
        Assert.Equal(4352u, tid);
        Assert.Equal(PathTv, path);
        Assert.True(DisplayTargetId.Matches(id, 0x99, 1, PathTv.ToUpperInvariant())); // LUID changé, même moniteur
        Assert.False(DisplayTargetId.Matches(id, 0x7FFF0001, 4352, PathMain));
        Assert.True(DisplayTargetId.Matches(DisplayTargetId.Make(5, 7, ""), 5, 7, ""));
        Assert.False(DisplayTargetId.TryParse("n'importe quoi", out _, out _, out _));
    }

    [Fact]
    public void Compact_RemovesUnusedModes_AndNormalizeOrigin_ShiftsToZero()
    {
        var (p1, s1, t1) = Active(1, 0, 256, 1920, 0, 2560, 1440);
        var (p2, s2, t2) = Active(1, 1, 4352, 0, 0, 1920, 1080);
        p2.SourceModeIdx = 2; p2.TargetModeIdx = 3;
        var (paths, modes) = DisplayTopology.Compact(new[] { p1 }, new[] { s1, t1, s2, t2 });
        Assert.Equal(2, modes.Length);
        DisplayTopology.NormalizeOrigin(paths, modes);
        Assert.Equal(0, (int)modes[0].D3);
    }

    [Fact]
    public void RecoverIfNeeded_WithoutStateFile_DoesNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "sysdeck-test-" + Guid.NewGuid() + ".json");
        var service = new ConsoleModeService(statePath: path);
        Assert.False(service.IsActive);
        Assert.False(service.RecoverIfNeeded());
    }
}
