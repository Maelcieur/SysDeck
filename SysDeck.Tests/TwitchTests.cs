using System.Net;
using System.Net.Http;
using System.Text.Json;
using SysDeck.Models;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class TwitchAuthParsingTests
{
    [Fact]
    public void ParseDeviceCode_ReadsAllFields()
    {
        var code = TwitchService.ParseDeviceCode(
            """{"device_code":"dev123","expires_in":1800,"interval":5,"user_code":"ABCDEFGH","verification_uri":"https://www.twitch.tv/activate?public=true&device-code=ABCDEFGH"}""")!;
        Assert.Equal("dev123", code.DeviceCode);
        Assert.Equal("ABCDEFGH", code.UserCode);
        Assert.Equal("https://www.twitch.tv/activate?public=true&device-code=ABCDEFGH", code.VerificationUri);
        Assert.Equal(5, code.Interval);
        Assert.Equal(1800, code.ExpiresIn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("""{"user_code":"A"}""")]
    public void ParseDeviceCode_RejectsIncompleteAnswers(string json) => Assert.Null(TwitchService.ParseDeviceCode(json));

    [Fact]
    public void ParseToken_ReadsSuccess()
    {
        var token = TwitchService.ParseToken(200,
            """{"access_token":"acc","expires_in":14400,"refresh_token":"ref","scope":["clips:edit"],"token_type":"bearer"}""");
        Assert.Equal(TwitchTokenOutcome.Success, token.Outcome);
        Assert.Equal("acc", token.AccessToken);
        Assert.Equal("ref", token.RefreshToken);
        Assert.Equal(14400, token.ExpiresIn);
    }

    [Theory]
    [InlineData(400, """{"status":400,"message":"authorization_pending"}""", TwitchTokenOutcome.Pending)]
    [InlineData(400, """{"status":400,"message":"slow_down"}""", TwitchTokenOutcome.SlowDown)]
    [InlineData(400, """{"status":400,"message":"access_denied"}""", TwitchTokenOutcome.Denied)]
    [InlineData(400, """{"status":400,"message":"expired_token"}""", TwitchTokenOutcome.Expired)]
    [InlineData(400, """{"status":400,"message":"Invalid refresh token"}""", TwitchTokenOutcome.Rejected)]
    [InlineData(401, """{"error":"Unauthorized","status":401,"message":"invalid client"}""", TwitchTokenOutcome.Rejected)]
    [InlineData(500, "", TwitchTokenOutcome.Failed)]
    public void ParseToken_ReadsPendingAndFailures(int status, string json, TwitchTokenOutcome expected)
    {
        var token = TwitchService.ParseToken(status, json);
        Assert.Equal(expected, token.Outcome);
        Assert.Null(token.AccessToken);
    }

    [Fact]
    public void ParseIdentity_ReadsLoginAndUserId()
    {
        var identity = TwitchService.ParseIdentity(
            """{"client_id":"x","login":"fenix","scopes":["clips:edit"],"user_id":"12345","expires_in":5520000}""")!;
        Assert.Equal("fenix", identity.Login);
        Assert.Equal("12345", identity.UserId);
        Assert.Equal(5520000, identity.ExpiresIn);
        Assert.Null(TwitchService.ParseIdentity("""{"status":401,"message":"invalid access token"}"""));
    }

    [Fact]
    public void Forms_UsePublicClient_WithoutSecret()
    {
        var device = TwitchService.DeviceForm();
        Assert.Equal(TwitchService.ClientId, device["client_id"]);
        Assert.Equal(TwitchService.Scopes, device["scopes"]);
        foreach (var scope in new[] { "clips:edit", "channel:manage:broadcast", "user:write:chat", "channel:edit:commercial",
                     "moderator:manage:chat_settings", "moderator:manage:shield_mode", "moderator:read:followers" })
            Assert.Contains(scope, device["scopes"].Split(' '));

        var poll = TwitchService.PollForm("dev");
        Assert.Equal("dev", poll["device_code"]);
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", poll["grant_type"]);

        var refresh = TwitchService.RefreshForm("r1");
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("r1", refresh["refresh_token"]);
        Assert.DoesNotContain("client_secret", refresh.Keys);
        Assert.DoesNotContain("client_secret", poll.Keys);

        var revoke = TwitchService.RevokeForm("tok");
        Assert.Equal("tok", revoke["token"]);
        Assert.Equal(TwitchService.ClientId, revoke["client_id"]);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/activate?device-code=A", true)]
    [InlineData("https://twitch.tv/activate", true)]
    [InlineData("http://www.twitch.tv/activate", false)]
    [InlineData("https://twitch.tv.evil.example/activate", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("", false)]
    public void IsSafeVerificationUri_OnlyAcceptsHttpsTwitch(string uri, bool expected)
        => Assert.Equal(expected, TwitchService.IsSafeVerificationUri(uri));
}

public class TwitchHelixTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void BuildMarkerBody_OmitsEmptyDescription_AndCapsLength()
    {
        Assert.False(Json(TwitchService.BuildMarkerBody("1", " ")).TryGetProperty("description", out _));
        var body = Json(TwitchService.BuildMarkerBody("1", "Belle action"));
        Assert.Equal("1", body.GetProperty("user_id").GetString());
        Assert.Equal("Belle action", body.GetProperty("description").GetString());
        Assert.Equal(140, Json(TwitchService.BuildMarkerBody("1", new string('x', 300))).GetProperty("description").GetString()!.Length);
    }

    [Fact]
    public void BuildChatMessageBody_CarriesBothIds()
    {
        var body = Json(TwitchService.BuildChatMessageBody("10", "20", "Salut tout le monde é"));
        Assert.Equal("10", body.GetProperty("broadcaster_id").GetString());
        Assert.Equal("20", body.GetProperty("sender_id").GetString());
        Assert.Equal("Salut tout le monde é", body.GetProperty("message").GetString());
    }

    [Fact]
    public void BuildCommercialBody_HasNumericLength()
    {
        var body = Json(TwitchService.BuildCommercialBody("10", 90));
        Assert.Equal(JsonValueKind.Number, body.GetProperty("length").ValueKind);
        Assert.Equal(90, body.GetProperty("length").GetInt32());
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("180", 180)]
    [InlineData("45", 30)]
    [InlineData("", 30)]
    [InlineData(null, 30)]
    public void CommercialLength_KeepsOnlyAllowedValues(string? text, int expected)
        => Assert.Equal(expected, TwitchService.CommercialLength(text));

    [Fact]
    public void BuildChatSettingsBody_TogglesTheRightField()
    {
        var slow = Json(TwitchService.BuildChatSettingsBody(TwitchAction.SlowMode, true));
        Assert.True(slow.GetProperty("slow_mode").GetBoolean());
        Assert.Equal(30, slow.GetProperty("slow_mode_wait_time").GetInt32());
        Assert.False(Json(TwitchService.BuildChatSettingsBody(TwitchAction.SlowMode, false)).TryGetProperty("slow_mode_wait_time", out _));

        Assert.True(Json(TwitchService.BuildChatSettingsBody(TwitchAction.EmoteOnly, true)).GetProperty("emote_mode").GetBoolean());
        var followers = Json(TwitchService.BuildChatSettingsBody(TwitchAction.FollowersOnly, true));
        Assert.True(followers.GetProperty("follower_mode").GetBoolean());
        Assert.Equal(0, followers.GetProperty("follower_mode_duration").GetInt32());
        Assert.False(Json(TwitchService.BuildChatSettingsBody(TwitchAction.SubscribersOnly, false)).GetProperty("subscriber_mode").GetBoolean());
    }

    [Fact]
    public void BuildShieldTitleAndCategoryBodies()
    {
        Assert.True(Json(TwitchService.BuildShieldBody(true)).GetProperty("is_active").GetBoolean());
        Assert.Equal("Soirée \"jeux\"", Json(TwitchService.BuildTitleBody("Soirée \"jeux\"")).GetProperty("title").GetString());
        Assert.Equal("509658", Json(TwitchService.BuildCategoryBody("509658")).GetProperty("game_id").GetString());
    }

    [Fact]
    public void ParseStream_ReadsLiveAndOffline()
    {
        var live = TwitchService.ParseStream(
            """{"data":[{"id":"1","user_id":"10","type":"live","viewer_count":42,"started_at":"2026-11-02T18:30:00Z"}],"pagination":{}}""")!;
        Assert.True(live.Live);
        Assert.Equal(42, live.Viewers);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero), live.StartedAt);

        var offline = TwitchService.ParseStream("""{"data":[],"pagination":{}}""")!;
        Assert.False(offline.Live);
        Assert.Equal(0, offline.Viewers);
        Assert.Null(offline.StartedAt);

        Assert.Null(TwitchService.ParseStream("n'importe quoi"));
        Assert.Null(TwitchService.ParseStream("""{"status":401}"""));
    }

    [Fact]
    public void ParseChatSettings_ReadsModes()
    {
        var chat = TwitchService.ParseChatSettings(
            """{"data":[{"broadcaster_id":"1","slow_mode":true,"slow_mode_wait_time":30,"follower_mode":false,"subscriber_mode":true,"emote_mode":false}]}""")!;
        Assert.True(chat.Slow);
        Assert.False(chat.Followers);
        Assert.True(chat.Subscribers);
        Assert.False(chat.Emote);
        Assert.Null(TwitchService.ParseChatSettings("""{"data":[]}"""));
    }

    [Fact]
    public void ParseShield_ClipCategoriesAndSendErrors()
    {
        Assert.Equal(true, TwitchService.ParseShield("""{"data":[{"is_active":true,"moderator_id":"1"}]}"""));
        Assert.Equal(false, TwitchService.ParseShield("""{"data":[{"is_active":false}]}"""));
        Assert.Null(TwitchService.ParseShield("{}"));

        Assert.Equal("https://clips.twitch.tv/AwesomeClip/edit",
            TwitchService.ParseClipEditUrl("""{"data":[{"id":"AwesomeClip","edit_url":"https://clips.twitch.tv/AwesomeClip/edit"}]}"""));

        var categories = TwitchService.ParseCategories("""{"data":[{"id":"509658","name":"Just Chatting","box_art_url":"x"},{"id":"21779","name":"League of Legends"},{"name":"sans id"}]}""");
        Assert.Equal(new[] { "Just Chatting", "League of Legends" }, categories.Select(c => c.Name));
        Assert.Equal("509658", categories[0].Id);

        Assert.Null(TwitchService.ParseChatSendError("""{"data":[{"message_id":"a","is_sent":true}]}"""));
        Assert.Contains("trop long", TwitchService.ParseChatSendError(
            """{"data":[{"message_id":"","is_sent":false,"drop_reason":{"code":"msg_length","message":"trop long"}}]}"""));
        Assert.Null(TwitchService.ParseCommercialError("""{"data":[{"length":60,"message":"","retry_after":480}]}"""));
        Assert.Contains("Missing", TwitchService.ParseCommercialError("""{"data":[{"length":0,"message":"Missing scope","retry_after":0}]}"""));
    }

    [Fact]
    public void State_FollowsPollsAndReportsChanges()
    {
        var state = new TwitchState();
        Assert.Null(state.IsOn(TwitchAction.SlowMode));
        Assert.True(state.SetChat(new TwitchChatSettings(true, false, false, true)));
        Assert.False(state.SetChat(new TwitchChatSettings(true, false, false, true)));
        Assert.Equal(true, state.IsOn(TwitchAction.SlowMode));
        Assert.Equal(false, state.IsOn(TwitchAction.EmoteOnly));
        Assert.Equal(true, state.IsOn(TwitchAction.SubscribersOnly));
        Assert.Null(state.IsOn(TwitchAction.ShieldMode));
        Assert.True(state.SetShield(true));
        Assert.Equal(true, state.IsOn(TwitchAction.ShieldMode));
        Assert.Null(state.IsOn(TwitchAction.Clip));

        Assert.True(state.SetStream(new TwitchStreamInfo(true, 10, null)));
        Assert.True(state.Stream.Live);
        state.Clear();
        Assert.False(state.Stream.Live);
        Assert.Null(state.IsOn(TwitchAction.SlowMode));
    }

    [Fact]
    public void TranslateError_GivesReadableMessages()
    {
        Assert.Equal("La chaîne n'est pas en direct.",
            TwitchService.TranslateError(404, """{"error":"Not Found","status":404,"message":"Clipping is not possible for an offline channel."}"""));
        Assert.Contains("reconnecte-toi", TwitchService.TranslateError(401, """{"status":401,"message":"Invalid OAuth token"}"""));
        Assert.Contains("droit", TwitchService.TranslateError(403, """{"status":403,"message":"Missing scope"}"""));
        Assert.Contains("limite", TwitchService.TranslateError(429, "{}"));
        Assert.Contains("HTTP 503", TwitchService.TranslateError(503, ""));
        Assert.Equal("Twitch : The title is too long", TwitchService.TranslateError(400, """{"status":400,"message":"The title is too long"}"""));
        Assert.Equal("Twitch a refusé la requête (HTTP 400).", TwitchService.TranslateError(400, "pas du json"));
    }
}

public class TwitchTileTests
{
    [Fact]
    public void OldItem_WithoutTwitchFields_Deserializes()
    {
        var back = JsonSerializer.Deserialize<ShortcutItem>("""{"Name":"OBS","Path":"","Arguments":"","Kind":"Obs","ObsAction":"Stream"}""")!;
        Assert.Equal(TileKind.Obs, back.Kind);
        Assert.Null(back.TwitchAction);
        Assert.Null(back.TwitchText);
        Assert.Null(back.TwitchId);
    }

    [Fact]
    public void TwitchItem_RoundTrips_WithEnumsAsText()
    {
        var item = new ShortcutItem
        {
            Name = "Catégorie",
            Kind = TileKind.Twitch,
            TwitchAction = TwitchAction.SetCategory,
            TwitchText = "Just Chatting",
            TwitchId = "509658",
            ConfirmTap = true
        };
        var json = JsonSerializer.Serialize(item);
        Assert.Contains("\"Twitch\"", json);
        Assert.Contains("\"SetCategory\"", json);

        var back = JsonSerializer.Deserialize<ShortcutItem>(json)!;
        Assert.Equal(TileKind.Twitch, back.Kind);
        Assert.Equal(TwitchAction.SetCategory, back.TwitchAction);
        Assert.Equal("Just Chatting", back.TwitchText);
        Assert.Equal("509658", back.TwitchId);
        Assert.True(back.ConfirmTap);
    }

    [Fact]
    public void TwitchTile_WithoutService_IsOfflineAndNormal()
    {
        var tile = new ShortcutItem { Kind = TileKind.Twitch, TwitchAction = TwitchAction.SlowMode };
        Assert.True(TileRuntime.TwitchOffline(tile));
        Assert.True(TileRuntime.IntegrationOffline(tile));
        Assert.Equal(IntegrationLook.Normal, TileRuntime.IntegrationLookOf(tile));
        Assert.False(TileRuntime.IntegrationOffline(new ShortcutItem { Kind = TileKind.App }));
    }

    [Fact]
    public void TwitchTile_DefaultGlyph_FollowsAction_AndKeepsChosenIcon()
    {
        foreach (var (action, _) in AddTwitchTileWindow.Actions)
        {
            var glyph = TileRuntime.TwitchGlyph(new ShortcutItem { Kind = TileKind.Twitch, TwitchAction = action });
            Assert.False(string.IsNullOrEmpty(glyph));
            Assert.Contains(SysDeck.Controls.ShortcutIcons.Symbols, s => s.Glyph == glyph);
        }
        var shield = new ShortcutItem { Name = "Bouclier", Kind = TileKind.Twitch, TwitchAction = TwitchAction.ShieldMode };
        Assert.Equal(TileRuntime.TwitchGlyph(shield), TileRuntime.ForDisplay(shield).IconGlyph);
        var own = new ShortcutItem { Kind = TileKind.Twitch, TwitchAction = TwitchAction.Clip, IconGlyph = "X" };
        Assert.Same(own, TileRuntime.ForDisplay(own));
    }

    [Theory]
    [InlineData(TwitchAction.Clip, "", "Clip")]
    [InlineData(TwitchAction.Commercial, "90", "Pub 90 s")]
    [InlineData(TwitchAction.SetCategory, "Just Chatting", "Just Chatting")]
    [InlineData(TwitchAction.SetCategory, "", "Catégorie")]
    [InlineData(TwitchAction.ShieldMode, "", "Bouclier")]
    public void SuggestedName_FollowsActionAndArgument(TwitchAction action, string argument, string expected)
        => Assert.Equal(expected, AddTwitchTileWindow.Suggest(action, argument));

    [Fact]
    public void Confirmation_IsProposedForCommercialAndShieldOnly()
    {
        Assert.True(AddTwitchTileWindow.ConfirmByDefault(TwitchAction.Commercial));
        Assert.True(AddTwitchTileWindow.ConfirmByDefault(TwitchAction.ShieldMode));
        Assert.False(AddTwitchTileWindow.ConfirmByDefault(TwitchAction.Clip));
    }

    [Fact]
    public void Describe_MentionsActionAndConfirmation()
    {
        var text = AddTwitchTileWindow.Describe(new ShortcutItem
        {
            Kind = TileKind.Twitch, TwitchAction = TwitchAction.Commercial, TwitchText = "120", ConfirmTap = true
        });
        Assert.Equal("Twitch · pub de 120 s · confirmation", text);
    }
}

public class TwitchSettingsTests
{
    [Fact]
    public void Tokens_RoundTripThroughDpapi_AndAreNotStoredInClear()
    {
        var settings = new TwitchSettings { Enabled = true, Login = "fenix", UserId = "1", AccessToken = "access-secret-1", RefreshToken = "refresh-secret-2" };
        Assert.NotNull(settings.AccessTokenProtected);
        Assert.NotEqual("access-secret-1", settings.AccessTokenProtected);
        Assert.DoesNotContain("access-secret-1", settings.AccessTokenProtected);
        Assert.NotEqual("refresh-secret-2", settings.RefreshTokenProtected);
        Assert.Equal("access-secret-1", settings.AccessToken);
        Assert.Equal("refresh-secret-2", settings.RefreshToken);

        settings.ExpiresAt = new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("access-secret-1", json);
        Assert.DoesNotContain("refresh-secret-2", json);
        var back = JsonSerializer.Deserialize<TwitchSettings>(json)!;
        Assert.Equal("access-secret-1", back.AccessToken);
        Assert.Equal("refresh-secret-2", back.RefreshToken);
        Assert.Equal(settings.ExpiresAt, back.ExpiresAt);
        Assert.Equal("fenix", back.Login);
    }

    [Fact]
    public void Clear_ForgetsAccountAndTokens()
    {
        var settings = new TwitchSettings { Enabled = true, Login = "fenix", UserId = "1", AccessToken = "a", RefreshToken = "r", ExpiresAt = DateTimeOffset.UtcNow };
        settings.Clear();
        Assert.False(settings.Enabled);
        Assert.Null(settings.Login);
        Assert.Null(settings.AccessTokenProtected);
        Assert.Null(settings.RefreshTokenProtected);
        Assert.Null(settings.ExpiresAt);
    }

    [Fact]
    public void UnreadableToken_IsTreatedAsAbsent()
    {
        var settings = new TwitchSettings { AccessTokenProtected = "pas-du-base64-valide!" };
        Assert.Null(settings.AccessToken);
    }

    [Fact]
    public void OldConfig_WithoutTwitch_LoadsWithDefaults()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"DockIconSize":50}""")!;
        config.MigrateLegacySettings();
        Assert.False(config.Twitch.Enabled);
        Assert.Null(config.Twitch.Login);
        Assert.Null(config.Twitch.AccessToken);

        var broken = JsonSerializer.Deserialize<AppConfig>("""{"Twitch":null}""")!;
        broken.MigrateLegacySettings();
        Assert.NotNull(broken.Twitch);
    }
}

public class TwitchServiceTests
{
    private sealed class FakeTwitch : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public int RefreshCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Calls.Add($"{request.Method} {url}");
            if (url.EndsWith("/oauth2/token"))
            {
                RefreshCount++;
                var form = request.Content!.ReadAsStringAsync(cancellationToken).Result;
                Assert.Contains("grant_type=refresh_token", form);
                Assert.Contains("refresh_token=ancien-refresh", form);
                Assert.DoesNotContain("client_secret", form);
                return Task.FromResult(Reply(HttpStatusCode.OK, """{"access_token":"nouvel-access","refresh_token":"nouveau-refresh","expires_in":14400}"""));
            }
            if (url.EndsWith("/oauth2/validate"))
            {
                var ok = request.Headers.Authorization is { Scheme: "OAuth", Parameter: "nouvel-access" };
                return Task.FromResult(ok
                    ? Reply(HttpStatusCode.OK, """{"login":"fenix","user_id":"77","expires_in":14000}""")
                    : Reply(HttpStatusCode.Unauthorized, """{"status":401,"message":"invalid access token"}"""));
            }
            return Task.FromResult(Reply(HttpStatusCode.NotFound, "{}"));
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json)
            => new(status) { Content = new StringContent(json) };
    }

    [Fact]
    public async Task ExpiredToken_IsRefreshedOnce_AndNewRefreshTokenIsSavedAtOnce()
    {
        var handler = new FakeTwitch();
        using var service = new TwitchService(handler);
        var settings = new TwitchSettings
        {
            Enabled = true,
            AccessToken = "ancien-access",
            RefreshToken = "ancien-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var saves = 0;
        service.Configure(settings, () => saves++);

        for (var i = 0; i < 100 && !service.IsConnected; i++) await Task.Delay(50);

        Assert.True(service.IsConnected);
        Assert.Equal("fenix", service.Login);
        Assert.Equal("77", service.UserId);
        Assert.Equal(1, handler.RefreshCount);
        Assert.Equal("nouveau-refresh", settings.RefreshToken);
        Assert.Equal("nouvel-access", settings.AccessToken);
        Assert.True(settings.ExpiresAt > DateTimeOffset.UtcNow.AddHours(3));
        Assert.True(saves >= 1);
    }

    [Fact]
    public async Task Execute_WhenNotConnected_ReturnsReadableError()
    {
        using var service = new TwitchService(new FakeTwitch());
        var result = await service.ExecuteAsync(TwitchAction.Clip, null, null);
        Assert.Equal("Twitch n'est pas connecté.", result.Error);
        Assert.Null(service.IsOn(TwitchAction.SlowMode));
    }
}
