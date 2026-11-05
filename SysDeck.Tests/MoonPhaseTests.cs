using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class MoonPhaseTests
{
    // Phases connues (UTC) : l'âge calculé doit tomber à moins de 8 heures de la phase.
    [Theory]
    [InlineData("2024-04-08T18:21:00Z", 0.0)]   // nouvelle lune (éclipse totale)
    [InlineData("2024-04-23T23:49:00Z", 0.5)]   // pleine lune
    [InlineData("2025-01-29T12:36:00Z", 0.0)]   // nouvelle lune
    [InlineData("2025-03-14T06:55:00Z", 0.5)]   // pleine lune (éclipse)
    [InlineData("2023-08-01T18:31:00Z", 0.5)]   // pleine lune
    public void Age_MatchesKnownPhases(string utc, double fraction)
    {
        var age = MoonPhase.Age(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        var target = fraction * MoonPhase.SynodicMonth;
        var diff = Math.Abs(age - target);
        diff = Math.Min(diff, MoonPhase.SynodicMonth - diff);
        Assert.True(diff < 8 / 24.0, $"écart {diff * 24:0.0} h");
    }

    [Fact]
    public void NamesAndIllumination()
    {
        Assert.Equal("Nouvelle lune", MoonPhase.Name(0.2));
        Assert.Equal("Pleine lune", MoonPhase.Name(MoonPhase.SynodicMonth / 2));
        Assert.Equal("Premier croissant", MoonPhase.Name(4));
        Assert.Equal("Gibbeuse décroissante", MoonPhase.Name(19));
        Assert.Equal("Dernier croissant", MoonPhase.Name(26));
        Assert.Equal(1, MoonPhase.Illumination(MoonPhase.SynodicMonth / 2), 3);
        Assert.Equal(0.5, MoonPhase.Illumination(MoonPhase.SynodicMonth / 4), 3);
    }

    [Fact]
    public void Next_FindsFollowingFullMoon()
    {
        var from = new DateTime(2024, 4, 10, 12, 0, 0, DateTimeKind.Utc);
        var full = MoonPhase.Next(from, 0.5).ToUniversalTime();
        Assert.True(Math.Abs((full - new DateTime(2024, 4, 23, 23, 49, 0, DateTimeKind.Utc)).TotalHours) < 8);
    }
}
