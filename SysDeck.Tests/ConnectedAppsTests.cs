using System.Text.Json;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class ConnectedAppsTests
{
    [Fact]
    public void OldConfig_WithoutAnything_ShowsNoApp()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"DockIconSize":50}""")!;
        Assert.True(config.MigrateLegacySettings());
        Assert.Empty(config.ConnectedApps!);
    }

    [Fact]
    public void OldConfig_WithObsEnabledAndTwitchConnected_ShowsBoth()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"Obs":{"Enabled":true},"Twitch":{"Enabled":true,"Login":"moi"}}""")!;
        config.MigrateLegacySettings();
        Assert.Equal(new[] { ConnectedApps.Obs, ConnectedApps.Twitch }, config.ConnectedApps);
    }

    [Fact]
    public void OldConfig_WithObsPasswordOnly_ShowsObs()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"Obs":{"Enabled":false,"PasswordProtected":"abc"}}""")!;
        config.MigrateLegacySettings();
        Assert.Equal(new[] { ConnectedApps.Obs }, config.ConnectedApps);
    }

    [Fact]
    public void ExistingList_IsKept_InOrder_AndRoundTrips()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"Obs":{"Enabled":true},"ConnectedApps":["twitch"]}""")!;
        config.MigrateLegacySettings();
        Assert.Equal(new[] { ConnectedApps.Twitch }, config.ConnectedApps);

        config.ConnectedApps!.Add(ConnectedApps.Obs);
        var back = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
        Assert.Equal(new[] { ConnectedApps.Twitch, ConnectedApps.Obs }, back.ConnectedApps);
    }

    [Fact]
    public void Addable_ListsMissingApps()
    {
        var addable = ConnectedApps.Addable(new[] { ConnectedApps.Obs });
        Assert.Equal(new[] { ConnectedApps.Twitch, ConnectedApps.Voicemod }, addable.Select(a => a.Id));
        Assert.All(addable, app => Assert.True(app.Available));
    }

    [Fact]
    public void Add_RefusesDuplicatesAndUnknown_KeepsOrder()
    {
        var shown = new List<string>();
        Assert.True(ConnectedApps.Add(shown, ConnectedApps.Twitch));
        Assert.False(ConnectedApps.Add(shown, ConnectedApps.Twitch));
        Assert.True(ConnectedApps.Add(shown, ConnectedApps.Voicemod));
        Assert.False(ConnectedApps.Add(shown, "inconnu"));
        Assert.Equal(new[] { ConnectedApps.Twitch, ConnectedApps.Voicemod }, shown);
    }
}
