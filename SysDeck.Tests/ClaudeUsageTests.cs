using System.Text.Json;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class ClaudeUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static ClaudeUsageSnapshot? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ClaudeUsageService.Parse(doc.RootElement, "pro", Now);
    }

    [Fact]
    public void ExpectedFormat_ReadsBothWindows()
    {
        var s = Parse("""
            { "five_hour": { "utilization": 42.5, "resets_at": "2026-11-01T15:00:00Z" },
              "seven_day": { "utilization": 10, "resets_at": "2026-11-05T18:00:00+00:00" } }
            """);

        Assert.NotNull(s);
        Assert.Equal(ClaudeUsageState.Ok, s!.State);
        Assert.Equal(42.5, s.SessionPercent);
        Assert.Equal(10, s.WeekPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), s.SessionResetsAt);
        Assert.Equal("pro", s.Plan);
        Assert.Equal(Now, s.UpdatedAt);
        Assert.False(s.NetworkError);
    }

    [Fact]
    public void OneWindowMissing_StillOk()
    {
        var s = Parse("""{ "five_hour": null, "seven_day": { "utilization": 75 } }""");

        Assert.NotNull(s);
        Assert.Null(s!.SessionPercent);
        Assert.Equal(75, s.WeekPercent);
        Assert.Null(s.WeekResetsAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "usage": { "session": 12 } }""")]
    [InlineData("""{ "five_hour": { "utilization": "42" } }""")]
    [InlineData("""{ "five_hour": { "utilization": 150 }, "seven_day": { "utilization": -1 } }""")]
    [InlineData("[1, 2]")]
    public void UnknownFormat_ReturnsNull(string json) => Assert.Null(Parse(json));

    [Fact]
    public void BadResetDate_IgnoredButPercentKept()
    {
        var s = Parse("""{ "five_hour": { "utilization": 5, "resets_at": "bientôt" } }""");

        Assert.Equal(5, s!.SessionPercent);
        Assert.Null(s.SessionResetsAt);
    }
}
