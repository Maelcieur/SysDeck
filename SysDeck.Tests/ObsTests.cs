using System.Text.Json;
using SysDeck.Models;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class ObsProtocolTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void ComputeAuth_MatchesDocumentedVector()
    {
        // Exemple de la documentation d'obs-websocket v5.
        var auth = ObsService.ComputeAuth("supersecretpassword", "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=",
            "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=");
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", auth);
    }

    [Fact]
    public void ComputeAuth_MatchesIndependentComputation()
    {
        const string password = "motdepasse", salt = "sel", challenge = "défi";
        var secret = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password + salt)));
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret + challenge)));
        Assert.Equal(expected, ObsService.ComputeAuth(password, salt, challenge));
    }

    [Fact]
    public void BuildRequest_HasOpSixWithIdAndData()
    {
        var message = ObsService.ParseMessage(ObsService.BuildRequest("SetCurrentProgramScene", "abc", new { sceneName = "Jeu" }));
        Assert.Equal(6, message.Op);
        Assert.Equal("SetCurrentProgramScene", message.Data.GetProperty("requestType").GetString());
        Assert.Equal("abc", message.Data.GetProperty("requestId").GetString());
        Assert.Equal("Jeu", message.Data.GetProperty("requestData").GetProperty("sceneName").GetString());
    }

    [Fact]
    public void BuildRequest_WithoutData_OmitsRequestData()
    {
        var message = ObsService.ParseMessage(ObsService.BuildRequest("ToggleStream", "1"));
        Assert.False(message.Data.TryGetProperty("requestData", out _));
    }

    [Fact]
    public void BuildIdentify_CarriesRpcVersionSubscriptionsAndAuth()
    {
        var message = ObsService.ParseMessage(ObsService.BuildIdentify("secret"));
        Assert.Equal(1, message.Op);
        Assert.Equal(1, message.Data.GetProperty("rpcVersion").GetInt32());
        Assert.Equal("secret", message.Data.GetProperty("authentication").GetString());
        var subscriptions = message.Data.GetProperty("eventSubscriptions").GetInt32();
        foreach (var flag in new[] { 1, 2, 4, 8, 64, 128, 1024 }) // General, Config, Scenes, Inputs, Outputs, SceneItems, Ui
            Assert.Equal(flag, subscriptions & flag);

        Assert.False(ObsService.ParseMessage(ObsService.BuildIdentify(null)).Data.TryGetProperty("authentication", out _));
    }

    [Fact]
    public void ParseMessage_ReadsHello()
    {
        var hello = ObsService.ParseMessage("""{"op":0,"d":{"obsWebSocketVersion":"5.5.2","rpcVersion":1,"authentication":{"challenge":"c","salt":"s"}}}""");
        Assert.Equal(0, hello.Op);
        Assert.Equal("5.5.2", hello.Data.GetProperty("obsWebSocketVersion").GetString());
        Assert.Equal("s", hello.Data.GetProperty("authentication").GetProperty("salt").GetString());
    }

    [Fact]
    public void ParseResponse_ReadsSuccessAndFailure()
    {
        var ok = ObsService.ParseResponse(ObsService.ParseMessage(
            """{"op":7,"d":{"requestType":"GetStreamStatus","requestId":"r1","requestStatus":{"result":true,"code":100},"responseData":{"outputActive":true}}}""").Data);
        Assert.True(ok.Success);
        Assert.Equal("r1", ok.Id);
        Assert.True(ok.Data.GetProperty("outputActive").GetBoolean());

        var failed = ObsService.ParseResponse(ObsService.ParseMessage(
            """{"op":7,"d":{"requestType":"GetInputMute","requestId":"r2","requestStatus":{"result":false,"code":600,"comment":"Introuvable"}}}""").Data);
        Assert.False(failed.Success);
        Assert.Equal(600, failed.Code);
        Assert.Equal("Introuvable", failed.Comment);
    }

    [Fact]
    public void ParseEvent_ReadsTypeAndData()
    {
        var message = ObsService.ParseMessage(
            """{"op":5,"d":{"eventType":"CurrentProgramSceneChanged","eventIntent":4,"eventData":{"sceneName":"Jeu"}}}""");
        Assert.Equal(5, message.Op);
        var obsEvent = ObsService.ParseEvent(message.Data);
        Assert.Equal("CurrentProgramSceneChanged", obsEvent.Type);
        Assert.Equal("Jeu", obsEvent.Data.GetProperty("sceneName").GetString());
    }

    [Fact]
    public void ParseSceneNames_ReturnsDisplayOrder()
    {
        var names = ObsService.ParseSceneNames(Json("""{"scenes":[{"sceneName":"C","sceneIndex":0},{"sceneName":"B","sceneIndex":1},{"sceneName":"A","sceneIndex":2}]}"""));
        Assert.Equal(new[] { "A", "B", "C" }, names);
    }

    [Fact]
    public void State_FollowsEvents_AndReportsChanges()
    {
        var state = new ObsState();
        Assert.True(state.Apply("CurrentProgramSceneChanged", Json("""{"sceneName":"Jeu"}""")));
        Assert.False(state.Apply("CurrentProgramSceneChanged", Json("""{"sceneName":"Jeu"}""")));
        Assert.Equal(true, state.IsOn(ObsAction.Scene, "Jeu", null));
        Assert.Equal(false, state.IsOn(ObsAction.Scene, "Pause", null));

        Assert.True(state.Apply("StreamStateChanged", Json("""{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_STARTED"}""")));
        Assert.Equal(true, state.IsOn(ObsAction.Stream, null, null));

        Assert.True(state.Apply("RecordStateChanged", Json("""{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_PAUSED"}""")));
        Assert.Equal(true, state.IsOn(ObsAction.Record, null, null));
        Assert.Equal(true, state.IsOn(ObsAction.RecordPause, null, null));
        Assert.True(state.Apply("RecordStateChanged", Json("""{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_RESUMED"}""")));
        Assert.Equal(false, state.IsOn(ObsAction.RecordPause, null, null));

        Assert.True(state.Apply("VirtualcamStateChanged", Json("""{"outputActive":true}""")));
        Assert.True(state.Apply("ReplayBufferStateChanged", Json("""{"outputActive":true}""")));
        Assert.Equal(true, state.IsOn(ObsAction.VirtualCam, null, null));
        Assert.Equal(true, state.IsOn(ObsAction.ReplayBuffer, null, null));
    }

    [Fact]
    public void State_TracksInputMuteAndSceneItems()
    {
        var state = new ObsState();
        Assert.Null(state.IsOn(ObsAction.InputMute, "Micro", null));
        Assert.True(state.Apply("InputMuteStateChanged", Json("""{"inputName":"Micro","inputMuted":true}""")));
        Assert.Equal(true, state.IsOn(ObsAction.InputMute, "Micro", null));
        Assert.False(state.Apply("InputMuteStateChanged", Json("""{"inputName":"Micro","inputMuted":true}""")));

        Assert.Null(state.IsOn(ObsAction.SourceVisibility, "Webcam", "Jeu"));
        state.SetItemId("Jeu", "Webcam", 7);
        state.SetItemEnabled("Jeu", 7, true);
        Assert.Equal(true, state.IsOn(ObsAction.SourceVisibility, "Webcam", "Jeu"));
        Assert.True(state.Apply("SceneItemEnableStateChanged", Json("""{"sceneName":"Jeu","sceneItemId":7,"sceneItemEnabled":false}""")));
        Assert.Equal(false, state.IsOn(ObsAction.SourceVisibility, "Webcam", "Jeu"));

        state.Clear();
        Assert.Null(state.IsOn(ObsAction.InputMute, "Micro", null));
    }

    [Fact]
    public void Service_DisconnectedByDefault()
    {
        using var obs = new ObsService();
        Assert.False(obs.IsConnected);
        Assert.Null(obs.IsOn(ObsAction.Stream, null, null));
    }

    [Fact]
    public async Task Service_ExecuteWhenDisconnected_ReturnsMessage()
    {
        using var obs = new ObsService();
        Assert.Equal("OBS n'est pas connecté.", await obs.ExecuteAsync(ObsAction.Stream, null, null));
    }
}

public class ObsTileTests
{
    [Fact]
    public void OldItem_WithoutObsFields_Deserializes()
    {
        var back = JsonSerializer.Deserialize<ShortcutItem>("""{"Name":"Bloc-notes","Path":"C:\\notepad.exe","Arguments":"","Kind":"App"}""")!;
        Assert.Equal(TileKind.App, back.Kind);
        Assert.Null(back.ObsAction);
        Assert.Null(back.ObsTarget);
        Assert.Null(back.ObsScene);
    }

    [Fact]
    public void ObsItem_RoundTrips_WithEnumsAsText()
    {
        var item = new ShortcutItem
        {
            Name = "Webcam",
            Kind = TileKind.Obs,
            ObsAction = ObsAction.SourceVisibility,
            ObsTarget = "Webcam",
            ObsScene = "Jeu",
            ConfirmTap = true
        };
        var json = JsonSerializer.Serialize(item);
        Assert.Contains("\"Obs\"", json);
        Assert.Contains("\"SourceVisibility\"", json);

        var back = JsonSerializer.Deserialize<ShortcutItem>(json)!;
        Assert.Equal(TileKind.Obs, back.Kind);
        Assert.Equal(ObsAction.SourceVisibility, back.ObsAction);
        Assert.Equal("Webcam", back.ObsTarget);
        Assert.Equal("Jeu", back.ObsScene);
        Assert.True(back.ConfirmTap);
    }

    [Fact]
    public void ObsTile_WithoutService_IsOfflineAndNormal()
    {
        var tile = new ShortcutItem { Kind = TileKind.Obs, ObsAction = ObsAction.Stream };
        Assert.True(TileRuntime.ObsOffline(tile));
        Assert.Equal(IntegrationLook.Normal, TileRuntime.ObsLookOf(tile));
        Assert.False(TileRuntime.ObsOffline(new ShortcutItem { Kind = TileKind.App }));
    }

    [Fact]
    public void ObsTile_DefaultGlyph_FollowsAction()
    {
        var muted = new ShortcutItem { Name = "Micro", Kind = TileKind.Obs, ObsAction = ObsAction.InputMute, ObsTarget = "Micro" };
        Assert.Equal(TileRuntime.MicGlyph, TileRuntime.ForDisplay(muted).IconGlyph); // inconnu : micro normal
        var own = new ShortcutItem { Kind = TileKind.Obs, ObsAction = ObsAction.Stream, IconGlyph = "X" };
        Assert.Same(own, TileRuntime.ForDisplay(own));
    }

    [Theory]
    [InlineData(ObsAction.Scene, "Jeu", "Jeu")]
    [InlineData(ObsAction.Scene, "", "Scène")]
    [InlineData(ObsAction.Stream, "", "Stream")]
    [InlineData(ObsAction.InputMute, "Micro", "Micro")]
    public void SuggestedName_FollowsActionAndTarget(ObsAction action, string target, string expected)
        => Assert.Equal(expected, AddObsTileWindow.Suggest(action, target));
}

public class ObsSettingsTests
{
    [Fact]
    public void Password_RoundTripsThroughDpapi_AndIsNotStoredInClear()
    {
        var settings = new ObsSettings { Enabled = true, Password = "supersecretpassword" };
        Assert.NotNull(settings.PasswordProtected);
        Assert.DoesNotContain("supersecretpassword", settings.PasswordProtected);
        Assert.NotEqual(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("supersecretpassword")), settings.PasswordProtected);
        Assert.Equal("supersecretpassword", settings.Password);

        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("supersecretpassword", json);
        Assert.Equal("supersecretpassword", JsonSerializer.Deserialize<ObsSettings>(json)!.Password);
    }

    [Fact]
    public void EmptyPassword_StoresNothing()
    {
        var settings = new ObsSettings { Password = "abc" };
        settings.Password = "";
        Assert.Null(settings.PasswordProtected);
        Assert.Null(settings.Password);
    }

    [Fact]
    public void OldConfig_WithoutObs_LoadsWithDefaults()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"DockIconSize":50}""")!;
        config.MigrateLegacySettings();
        Assert.False(config.Obs.Enabled);
        Assert.Equal("127.0.0.1", config.Obs.Host);
        Assert.Equal(4455, config.Obs.Port);
        Assert.Null(config.Obs.Password);

        var broken = JsonSerializer.Deserialize<AppConfig>("""{"Obs":{"Host":"","Port":0}}""")!;
        broken.MigrateLegacySettings();
        Assert.Equal("127.0.0.1", broken.Obs.Host);
        Assert.Equal(4455, broken.Obs.Port);
    }
}
