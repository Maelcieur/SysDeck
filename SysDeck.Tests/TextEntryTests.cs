using System.Text.Json;
using SysDeck.Deck;
using SysDeck.Models;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class TextEntryModelTests
{
    private DateTime _now = new(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);

    private TextEntryModel Model(int max = 500, string? initial = null) => new(max, initial, () => _now);

    [Fact]
    public void EmptyText_StartsWithOneCapital()
    {
        var model = Model();
        model.Press("s");
        model.Press("a");
        Assert.Equal("Sa", model.Text);
        Assert.Equal(ShiftMode.Off, model.Shift);
    }

    [Fact]
    public void DoubleShift_Locks_AndSingleTapUnlocks()
    {
        var model = Model(initial: "x");
        model.Press(TextEntryModel.ShiftKey);
        _now = _now.AddMilliseconds(200);
        model.Press(TextEntryModel.ShiftKey);
        Assert.Equal(ShiftMode.Locked, model.Shift);
        model.Press("g");
        model.Press("g");
        Assert.Equal("xGG", model.Text);
        _now = _now.AddSeconds(2);
        model.Press(TextEntryModel.ShiftKey);
        Assert.Equal(ShiftMode.Off, model.Shift);
    }

    [Fact]
    public void SlowSecondShift_CancelsInsteadOfLocking()
    {
        var model = Model(initial: "x");
        model.Press(TextEntryModel.ShiftKey);
        _now = _now.AddSeconds(1);
        model.Press(TextEntryModel.ShiftKey);
        Assert.Equal(ShiftMode.Off, model.Shift);
    }

    [Fact]
    public void Shift_UppercasesAccents()
    {
        var model = Model(initial: "x");
        model.Press(TextEntryModel.AccentsKey);
        model.Press(TextEntryModel.ShiftKey);
        model.Press("é");
        Assert.Equal("xÉ", model.Text);
        Assert.Equal(KeyboardLayer.Accents, model.Layer);
    }

    [Fact]
    public void Layers_SwitchAndShareTheBottomRow()
    {
        var model = Model();
        model.Press(TextEntryModel.SymbolsKey);
        Assert.Equal(KeyboardLayer.Symbols, model.Layer);
        Assert.Contains("1", model.CurrentRows[0]);
        model.Press(TextEntryModel.LettersKey);
        Assert.Equal("a", model.CurrentRows[0][0]);
        Assert.All(new[] { KeyboardLayer.Letters, KeyboardLayer.Symbols, KeyboardLayer.Accents },
            layer => Assert.Contains(TextEntryModel.EnterKey, TextEntryModel.Rows(layer)[^1]));
    }

    [Fact]
    public void Enter_ReturnsTrue_WithoutTyping()
    {
        var model = Model(initial: "gg");
        Assert.True(model.Press(TextEntryModel.EnterKey));
        Assert.Equal("gg", model.Text);
    }

    [Fact]
    public void Backspace_RemovesAWholeEmoji()
    {
        var model = Model(initial: "ok");
        model.Insert("👍🏽");
        model.Backspace();
        Assert.Equal("ok", model.Text);
        model.Press(TextEntryModel.BackKey);
        Assert.Equal("o", model.Text);
    }

    [Fact]
    public void MaxLength_IsEnforced_WithoutSplittingAnEmoji()
    {
        var model = Model(max: 5, initial: "abcd");
        model.Insert("🔥");
        Assert.Equal("abcd", model.Text);
        model.Press("e");
        model.Press("f");
        Assert.Equal("abcde", model.Text);
        Assert.Equal(0, model.Remaining);
    }

    [Fact]
    public void InsertWord_AddsSpacesAroundEmote()
    {
        var model = Model(initial: "salut");
        model.InsertWord("Kappa");
        Assert.Equal("salut Kappa ", model.Text);
        model.InsertWord("LUL");
        Assert.Equal("salut Kappa LUL ", model.Text);
    }

    [Fact]
    public void InsertWord_RefusesAnEmoteThatDoesNotFit()
    {
        var model = Model(max: 8, initial: "hey");
        model.InsertWord("PogChamp");
        Assert.Equal("hey", model.Text);
    }

    [Fact]
    public void InitialText_IsTruncated_AndNoForcedCapital()
    {
        var model = Model(max: 3, initial: "abcdef");
        Assert.Equal("abc", model.Text);
        Assert.Equal(ShiftMode.Off, model.Shift);
    }
}

public class TwitchEntryParsingTests
{
    [Fact]
    public void UserEmotes_UseTemplate_AndCursor()
    {
        var page = TwitchService.ParseUserEmotes("""
            {"data":[{"id":"25","name":"Kappa","emote_type":"globals"},{"id":"emotesv2_abc","name":"moiHype","emote_type":"subscriptions"}],
             "template":"https://static-cdn.jtvnw.net/emoticons/v2/{{id}}/{{format}}/{{theme_mode}}/{{scale}}",
             "pagination":{"cursor":"next123"}}
            """);
        Assert.Equal(2, page.Emotes.Count);
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0", page.Emotes[0].ImageUrl);
        Assert.Equal("subscriptions", page.Emotes[1].Type);
        Assert.Equal("next123", page.Cursor);
    }

    [Fact]
    public void UserEmotes_LastPage_HasNoCursor_AndFallsBackWithoutTemplate()
    {
        var page = TwitchService.ParseUserEmotes("""{"data":[{"id":"1","name":"A"}],"pagination":{}}""");
        Assert.Null(page.Cursor);
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/1/static/dark/2.0", page.Emotes[0].ImageUrl);
        Assert.Empty(TwitchService.ParseUserEmotes("pas du json").Emotes);
    }

    [Fact]
    public void Scopes_AreReadFromValidate()
    {
        var scopes = TwitchService.ParseScopes("""{"login":"moi","user_id":"1","scopes":["clips:edit","user:read:emotes"],"expires_in":100}""");
        Assert.Contains(TwitchService.EmotesScope, scopes);
        Assert.Empty(TwitchService.ParseScopes("""{"login":"moi"}"""));
    }

    [Fact]
    public void Scopes_RequestedAtLogin_IncludeEmotes()
        => Assert.Contains(TwitchService.EmotesScope, TwitchService.Scopes.Split(' '));

    [Theory]
    [InlineData("https://static-cdn.jtvnw.net/ttv-boxart/33214-52x72.jpg", "https://static-cdn.jtvnw.net/ttv-boxart/33214-144x192.jpg")]
    [InlineData("https://static-cdn.jtvnw.net/ttv-boxart/33214-{width}x{height}.jpg", "https://static-cdn.jtvnw.net/ttv-boxart/33214-144x192.jpg")]
    [InlineData(null, null)]
    public void BoxArt_IsEnlarged(string? url, string? expected) => Assert.Equal(expected, TwitchService.LargeBoxArt(url));

    [Fact]
    public void Categories_KeepTheirBoxArt()
    {
        var list = TwitchService.ParseCategories("""{"data":[{"id":"33214","name":"Fortnite","box_art_url":"https://x/33214-52x72.jpg"}]}""");
        Assert.Equal("https://x/33214-144x192.jpg", list.Single().BoxArtUrl);
    }

    [Fact]
    public void ChannelTitle_IsRead()
    {
        Assert.Equal("Mon live", TwitchService.ParseChannelTitle("""{"data":[{"broadcaster_id":"1","title":"Mon live"}]}"""));
        Assert.Null(TwitchService.ParseChannelTitle("""{"data":[]}"""));
    }

    [Fact]
    public void TwitchAsk_IsOptional_AndRoundTrips()
    {
        var old = JsonSerializer.Deserialize<ShortcutItem>("""{"Name":"Msg","Kind":"Twitch","TwitchAction":"ChatMessage","TwitchText":"gg"}""")!;
        Assert.False(old.TwitchAsk);
        Assert.False(old.AsksTwitchText);

        var tile = new ShortcutItem { Kind = TileKind.Twitch, TwitchAction = TwitchAction.SetCategory, TwitchAsk = true };
        var back = JsonSerializer.Deserialize<ShortcutItem>(JsonSerializer.Serialize(tile))!;
        Assert.True(back.AsksTwitchText);

        // Une action qui ne prend pas de texte ne demande jamais rien, même si le drapeau est resté.
        Assert.False(new ShortcutItem { Kind = TileKind.Twitch, TwitchAction = TwitchAction.Clip, TwitchAsk = true }.AsksTwitchText);
    }
}
