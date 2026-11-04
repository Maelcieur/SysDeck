using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class WeatherTests
{
    [Fact]
    public void ParseGeocoding_ReturnsFirstPlaceWithCountry()
    {
        var json = """{"results":[{"name":"Rouen","latitude":49.44,"longitude":1.1,"country":"France"}]}""";
        var place = WeatherService.ParseGeocoding(json);
        Assert.NotNull(place);
        Assert.Equal("Rouen, France", place!.Value.Place);
        Assert.Equal(49.44, place.Value.Lat, 3);
    }

    [Fact]
    public void ParseGeocoding_NoResult_ReturnsNull()
        => Assert.Null(WeatherService.ParseGeocoding("""{"generationtime_ms":0.5}"""));

    [Fact]
    public void ParseForecast_ReadsCurrentAndDays()
    {
        var json = """
        {"current":{"temperature_2m":12.4,"relative_humidity_2m":81,"apparent_temperature":10.1,"is_day":0,"weather_code":61,"wind_speed_10m":14.2},
         "daily":{"time":["2026-11-04","2026-11-05"],"weather_code":[61,3],"temperature_2m_max":[13.0,11.5],"temperature_2m_min":[7.2,null]}}
        """;
        var w = WeatherService.ParseForecast(json, "Rouen, France");
        Assert.NotNull(w);
        Assert.Equal(12.4, w!.Temperature, 3);
        Assert.Equal(81, w.Humidity);
        Assert.False(w.IsDay);
        Assert.Equal(WeatherKind.Rain, WeatherService.Kind(w.Code));
        Assert.Equal(2, w.Days.Count);
        Assert.Equal(new DateTime(2026, 11, 5), w.Days[1].Date);
        Assert.Equal(0, w.Days[1].Min); // valeur absente
    }

    [Theory]
    [InlineData(0, WeatherKind.Clear)]
    [InlineData(2, WeatherKind.PartlyCloudy)]
    [InlineData(45, WeatherKind.Fog)]
    [InlineData(53, WeatherKind.Drizzle)]
    [InlineData(81, WeatherKind.Rain)]
    [InlineData(86, WeatherKind.Snow)]
    [InlineData(99, WeatherKind.Storm)]
    public void Kind_MapsWmoCodes(int code, WeatherKind kind) => Assert.Equal(kind, WeatherService.Kind(code));
}
