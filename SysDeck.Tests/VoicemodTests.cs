using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SysDeck.Models;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class VoicemodTests
{
    private static string Scramble(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key).Select(b => (byte)(b ^ 0x5A)).ToArray());

    [Fact]
    public void Key_IsUnscrambled_AndNeverStoredInClear()
    {
        var scrambled = Scramble("controlapi-test");
        Assert.DoesNotContain("controlapi", scrambled);
        Assert.Equal("controlapi-test", VoicemodService.Unscramble(scrambled));
        Assert.Null(VoicemodService.Unscramble(""));
        Assert.Null(VoicemodService.Unscramble(null));
    }

    [Fact]
    public void BuildMessage_HasActionIdAndPayload()
    {
        var json = JsonNode.Parse(VoicemodService.BuildMessage("registerClient", "abc", new JsonObject { ["clientKey"] = "k" }))!;
        Assert.Equal("registerClient", (string?)json["action"]);
        Assert.Equal("abc", (string?)json["id"]);
        Assert.Equal("k", (string?)json["payload"]!["clientKey"]);
        Assert.NotNull(JsonNode.Parse(VoicemodService.BuildMessage("getVoices", "x"))!["payload"]);
    }

    [Theory]
    [InlineData("""{"action":"registerClient","id":"1","payload":{"status":{"code":200,"message":"OK"}}}""", 200)]
    [InlineData("""{"action":"registerClient","id":"1","payload":{"status":{"code":"401","message":"bad"}}}""", 401)]
    [InlineData("""{"action":"registerClient","id":"1"}""", null)]
    public void RegisterStatus_ReadsNumberOrString(string json, int? expected)
        => Assert.Equal(expected, VoicemodService.RegisterStatus(VoicemodService.ParseMessage(json)));

    [Fact]
    public void RegisterStatus_ReadsTheRealReplyShape()
    {
        // Réponse observée de Voicemod : le statut est dans actionObject.
        var reply = VoicemodService.ParseMessage("""{"action":"registerClient","actionID":"4a","actionObject":{"status":{"code":200,"description":"Authorized"}},"actionType":"registerClient","id":"4a"}""");
        Assert.Equal(200, VoicemodService.RegisterStatus(reply));
    }

    [Fact]
    public void ParseSoundboards_KeepsBoardNameCustomFlagAndImage()
    {
        var root = JsonDocument.Parse("""
            {"soundboards":[
              {"id":"a","name":"Horror","isCustom":false,"enabled":true,"sounds":[{"id":"s1","name":"Scream","imageURL":"https://content-storage.voicemod.net/x"}]},
              {"id":"b","name":"Mes Sons","isCustom":true,"enabled":true,"sounds":[{"id":"s2","name":"Bravo","imageURL":""},{"name":"sans id"}]},
              {"id":"c","name":"Off","isCustom":false,"enabled":false,"sounds":[{"id":"s3","name":"Caché"}]}
            ]}
            """).RootElement;
        var sounds = VoicemodService.ParseSoundboards(root);
        Assert.Equal(new[] { "s1", "s2" }, sounds.Select(s => s.FileName));
        Assert.Equal("Horror", sounds[0].Profile);
        Assert.False(sounds[0].Custom);
        Assert.True(sounds[1].Custom);
        Assert.Equal("https://content-storage.voicemod.net/x", sounds[0].ImageUrl);
        Assert.Null(sounds[1].ImageUrl);
    }

    [Fact]
    public void ParseVoices_ReadsNewCustomAndImage()
    {
        var root = JsonDocument.Parse("""{"voices":[{"id":"v","friendlyName":"Fuzzy","enabled":true,"isNew":true,"isCustom":false,"imageURL":"https://content-storage.voicemod.net/abc"}]}""").RootElement;
        var voice = VoicemodService.ParseVoices(root).Single();
        Assert.True(voice.IsNew);
        Assert.False(voice.IsCustom);
        Assert.Equal("https://content-storage.voicemod.net/abc", voice.ImageUrl);
    }

    [Theory]
    [InlineData("https://content-storage.voicemod.net/abc", true)]
    [InlineData("http://content-storage.voicemod.net/abc", false)]
    [InlineData("https://voicemod.net.evil.com/abc", false)]
    [InlineData(null, false)]
    public void ImageUrl_OnlyVoicemodHttps(string? url, bool ok) => Assert.Equal(ok, VoicemodService.IsVoicemodImageUrl(url));

    [Fact]
    public void ParseMessage_ReadsIdOrActionId_AndPendingMsg()
    {
        var reply = VoicemodService.ParseMessage("""{"actionType":"getMuteMicStatus","actionID":"42","actionObject":{"value":true}}""");
        Assert.Equal("42", reply.Id);
        Assert.Equal("getMuteMicStatus", reply.ActionType);
        Assert.Equal("Pending authentication", VoicemodService.ParseMessage("""{"msg":"Pending authentication"}""").Msg);
    }

    [Fact]
    public void State_FollowsEventsAndReplies()
    {
        var state = new VoicemodState();
        Assert.True(state.Apply(VoicemodService.ParseMessage("""{"action":"voiceChangerEnabledEvent"}""")));
        Assert.True(state.IsOn(VoicemodAction.VoiceChanger, null));
        Assert.False(state.Apply(VoicemodService.ParseMessage("""{"action":"voiceChangerEnabledEvent"}""")));
        state.Apply(VoicemodService.ParseMessage("""{"action":"muteMicrophoneEnabledEvent"}"""));
        Assert.True(state.IsOn(VoicemodAction.MuteMic, null));
        state.Apply(VoicemodService.ParseMessage("""{"action":"muteMicDisabledEvent"}"""));
        Assert.False(state.IsOn(VoicemodAction.MuteMic, null));
        state.Apply(VoicemodService.ParseMessage("""{"actionType":"getHearMyselfStatus","id":"1","actionObject":{"value":true}}"""));
        Assert.True(state.IsOn(VoicemodAction.HearMyself, null));
        state.Apply(VoicemodService.ParseMessage("""{"action":"hearMySelfDisabledEvent"}"""));
        Assert.False(state.IsOn(VoicemodAction.HearMyself, null));
        state.Apply(VoicemodService.ParseMessage("""{"action":"backgroundEffectsEnabledEvent"}"""));
        Assert.True(state.IsOn(VoicemodAction.Background, null));
    }

    [Fact]
    public void State_TracksTheLoadedVoice()
    {
        var state = new VoicemodState();
        state.Apply(VoicemodService.ParseMessage("""{"action":"voiceLoadedEvent","actionObject":{"voiceID":"robot"}}"""));
        Assert.True(state.IsOn(VoicemodAction.LoadVoice, "robot"));
        Assert.False(state.IsOn(VoicemodAction.LoadVoice, "baby"));
        state.Apply(VoicemodService.ParseMessage("""{"actionType":"getCurrentVoice","id":"2","actionObject":{"voiceID":"baby"}}"""));
        Assert.True(state.IsOn(VoicemodAction.LoadVoice, "baby"));
        Assert.Null(state.IsOn(VoicemodAction.PlaySound, "x.wav"));
    }

    [Fact]
    public void ParseVoices_SkipsBadEntries()
    {
        var root = JsonDocument.Parse("""{"voices":[{"id":"robot","friendlyName":"Robot","enabled":true,"favorited":true},{"id":"x","enabled":false},{"friendlyName":"sans id"}]}""").RootElement;
        var voices = VoicemodService.ParseVoices(root);
        Assert.Equal(2, voices.Count);
        Assert.True(voices[0].Favorite);
        Assert.Equal("x", voices[1].Name);
        Assert.False(voices[1].Enabled);
    }

    [Fact]
    public void ParseSounds_AcceptsBothCasings()
    {
        var root = JsonDocument.Parse("""{"listOfMemes":[{"Name":"Airhorn","FileName":"airhorn.wav","Profile":"Default"},{"fileName":"sad.mp3"}]}""").RootElement;
        var sounds = VoicemodService.ParseSounds(root);
        Assert.Equal(new[] { "airhorn.wav", "sad.mp3" }, sounds.Select(s => s.FileName));
        Assert.Equal("sad", sounds[1].Name);
    }

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D };

    [Fact]
    public void ParseBitmap_PrefersTransparent_AndAcceptsDataPrefixAndResult()
    {
        var b64 = Convert.ToBase64String(Png);
        var direct = JsonDocument.Parse($$"""{"default":"AAAA","transparent":"{{b64}}"}""").RootElement;
        Assert.Equal(Png, VoicemodService.ParseBitmap(direct));

        var wrapped = JsonDocument.Parse("""{"result":{"transparent":"","default":"data:image/png;base64,""" + b64 + "\"}}").RootElement;
        Assert.Equal(Png, VoicemodService.ParseBitmap(wrapped));

        Assert.Null(VoicemodService.ParseBitmap(JsonDocument.Parse("""{"default":"pas du base64 !"}""").RootElement));
        Assert.Null(VoicemodService.ParseBitmap(default));
    }

    [Fact]
    public void ImageExtension_FollowsSignature()
    {
        Assert.Equal(".png", VoicemodService.ImageExtension(Png));
        Assert.Equal(".jpg", VoicemodService.ImageExtension(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal(".gif", VoicemodService.ImageExtension("GIF89a"u8.ToArray()));
    }

    [Fact]
    public void Tile_RoundTrips_AndOldFilesLoad()
    {
        var tile = new ShortcutItem { Kind = TileKind.Voicemod, VoicemodAction = VoicemodAction.PlaySound, VoicemodTarget = "airhorn.wav", VoicemodTargetName = "Airhorn" };
        var json = JsonSerializer.Serialize(tile);
        Assert.Contains("\"PlaySound\"", json);
        var back = JsonSerializer.Deserialize<ShortcutItem>(json)!;
        Assert.Equal(TileKind.Voicemod, back.Kind);
        Assert.Equal("airhorn.wav", back.VoicemodTarget);
        Assert.Null(JsonSerializer.Deserialize<ShortcutItem>("""{"Name":"x"}""")!.VoicemodAction);
    }

    [Fact]
    public void Config_WithoutVoicemod_Loads_AndEnabledVoicemodIsShown()
    {
        var old = JsonSerializer.Deserialize<AppConfig>("""{"DockIconSize":50}""")!;
        old.MigrateLegacySettings();
        Assert.False(old.Voicemod.Enabled);

        var enabled = JsonSerializer.Deserialize<AppConfig>("""{"Voicemod":{"Enabled":true}}""")!;
        enabled.MigrateLegacySettings();
        Assert.Contains(ConnectedApps.Voicemod, enabled.ConnectedApps!);
    }

    [Fact]
    public void DefaultGlyph_FollowsMicState_WithoutService()
    {
        TileRuntime.Voicemod = null;
        var tile = new ShortcutItem { Kind = TileKind.Voicemod, VoicemodAction = VoicemodAction.MuteMic };
        Assert.Equal(TileRuntime.MicGlyph, TileRuntime.VoicemodGlyph(tile));
        Assert.True(TileRuntime.VoicemodOffline(tile));
    }
}
