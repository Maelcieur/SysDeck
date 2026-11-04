using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class SimHubAirTests
{
    [Fact]
    public void ParseLine_ReadsNameAndValue()
    {
        Assert.Equal(("dcp.gd.Gear", "3"), SimHubService.ParseLine("Property dcp.gd.Gear string 3"));
        Assert.Equal(("dcp.GameName", "Assetto Corsa Competizione"), SimHubService.ParseLine("Property dcp.GameName string Assetto Corsa Competizione"));
        Assert.Equal(("dcp.gd.Fuel", (string?)null), SimHubService.ParseLine("Property dcp.gd.Fuel double (null)"));
        Assert.Null(SimHubService.ParseLine("SimHub Property Server"));
    }

    [Fact]
    public void Build_ConvertsValues()
    {
        var data = SimHubService.Build(new Dictionary<string, string?>
        {
            ["dcp.GameName"] = "ACC", ["dcp.GameRunning"] = "True", ["dcp.gd.Gear"] = "4",
            ["dcp.gd.SpeedKmh"] = "183.6", ["dcp.gd.Rpms"] = "7200", ["dcp.gd.MaxRpm"] = "8000",
            ["dcp.gd.LastLapTime"] = "00:01:47.1230000", ["dcp.gd.BestLapTime"] = "00:00:00", ["dcp.gd.Position"] = "3",
            ["dcp.gd.Fuel"] = "23,5"
        });
        Assert.True(data.Running);
        Assert.Equal("4", data.Gear);
        Assert.Equal(183.6, data.Speed, 3);
        Assert.Equal(TimeSpan.FromMilliseconds(107123), data.LastLap);
        Assert.Null(data.BestLap); // 0 = pas encore de tour
        Assert.Equal(3, data.Position);
        Assert.Equal("1:47.123", SimHubService.FormatLap(data.LastLap));
        Assert.Equal("-:--.---", SimHubService.FormatLap(null));
    }

    [Fact]
    public void ParseAirQuality_ReadsIndexAndPollutants()
    {
        var air = WeatherService.ParseAirQuality(
            """{"current":{"european_aqi":34,"pm2_5":8.2,"pm10":14.9,"nitrogen_dioxide":11.0,"ozone":52.3}}""", "Rouen, France");
        Assert.NotNull(air);
        Assert.Equal(34, air!.Aqi);
        Assert.Equal(8.2, air.Pm25!.Value, 3);
        Assert.Equal("Correct", WeatherService.AirLevel(air.Aqi).Label);
        Assert.Null(WeatherService.ParseAirQuality("""{"current":{"european_aqi":null}}""", ""));
    }
}
