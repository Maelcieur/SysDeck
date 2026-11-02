using System.Diagnostics;
using System.Text.Json;
using SysDeck.Models;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class TileKindTests
{
    [Fact]
    public void OldItem_WithoutKind_IsApp()
    {
        var back = JsonSerializer.Deserialize<ShortcutItem>("""{"Name":"Bloc-notes","Path":"C:\\notepad.exe","Arguments":""}""")!;
        Assert.Equal(TileKind.App, back.Kind);
        Assert.False(back.ConfirmTap);
        Assert.False(back.RunAsAdmin);
        Assert.Null(back.Command);
        Assert.Null(back.DeviceId);
    }

    [Fact]
    public void OldItem_WithHotkey_IsHotkey()
    {
        var back = JsonSerializer.Deserialize<ShortcutItem>("""{"Name":"Muet","Path":"","Hotkey":"Control+Shift+M"}""")!;
        Assert.Equal(TileKind.Hotkey, back.Kind);
        Assert.True(back.IsHotkey);
    }

    [Fact]
    public void NewKinds_RoundTrip()
    {
        var item = new ShortcutItem { Name = "Éteindre", Kind = TileKind.Command, Command = "shutdown /s /t 0", RunAsAdmin = true, ConfirmTap = true };
        var json = JsonSerializer.Serialize(item);
        Assert.Contains("\"Command\"", json); // énumération écrite en texte, pas en nombre
        var back = JsonSerializer.Deserialize<ShortcutItem>(json)!;
        Assert.Equal(TileKind.Command, back.Kind);
        Assert.Equal("shutdown /s /t 0", back.Command);
        Assert.True(back.RunAsAdmin);
        Assert.True(back.ConfirmTap);

        var mic = JsonSerializer.Deserialize<ShortcutItem>(JsonSerializer.Serialize(new ShortcutItem { Kind = TileKind.MicMute, DeviceId = "{abc}" }))!;
        Assert.Equal(TileKind.MicMute, mic.Kind);
        Assert.Equal("{abc}", mic.DeviceId);
        Assert.True(mic.IsMuteTile);
    }
}

public class TapConfirmationTests
{
    private static (TapConfirmation Confirm, Action<double> Advance) Create()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        return (new TapConfirmation(() => now), s => now = now.AddSeconds(s));
    }

    [Fact]
    public void FirstPress_Arms_SecondWithinDelay_Executes()
    {
        var (confirm, advance) = Create();
        var tile = new object();
        Assert.False(confirm.IsArmed(tile));
        Assert.False(confirm.Press(tile));
        Assert.True(confirm.IsArmed(tile));
        advance(1.5);
        Assert.True(confirm.Press(tile));
        Assert.False(confirm.IsArmed(tile));
    }

    [Fact]
    public void SecondPressAfterDelay_ArmsAgain()
    {
        var (confirm, advance) = Create();
        var tile = new object();
        Assert.False(confirm.Press(tile));
        advance(3.5);
        Assert.False(confirm.IsArmed(tile));
        Assert.False(confirm.Press(tile)); // réarmée, pas exécutée
        advance(1);
        Assert.True(confirm.Press(tile));
    }

    [Fact]
    public void TilesAreIndependent()
    {
        var (confirm, _) = Create();
        var a = new object();
        var b = new object();
        confirm.Press(a);
        Assert.False(confirm.Press(b));
        Assert.True(confirm.Press(a));
    }
}

public class CommandRunnerTests
{
    [Fact]
    public void BuildStartInfo_Normal_IsHiddenCmd()
    {
        var info = CommandRunner.BuildStartInfo("shutdown /s /t 0", runAsAdmin: false);
        Assert.Equal("cmd.exe", info.FileName);
        Assert.Equal("/c shutdown /s /t 0", info.Arguments);
        Assert.True(info.CreateNoWindow);
        Assert.False(info.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.True(string.IsNullOrEmpty(info.Verb));
    }

    [Fact]
    public void BuildStartInfo_Admin_UsesRunasVerb()
    {
        var info = CommandRunner.BuildStartInfo("shutdown /r /t 0", runAsAdmin: true);
        Assert.Equal("cmd.exe", info.FileName);
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
    }

    [Fact]
    public void Presets_AreComplete_AndDangerousOnesConfirm()
    {
        var names = CommandRunner.Presets.Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Éteindre", "Redémarrer", "Veille", "Verrouiller", "Fermer la session" }, names);
        foreach (var name in new[] { "Éteindre", "Redémarrer", "Veille", "Fermer la session" })
            Assert.True(CommandRunner.Presets.Single(p => p.Name == name).Confirm);
        Assert.Contains("hiberne", CommandRunner.Presets.Single(p => p.Name == "Veille").Description);
    }
}
