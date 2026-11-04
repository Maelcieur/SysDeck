using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>Ville du module Météo (la position est retrouvée une fois par le géocodage d'Open-Meteo).</summary>
public sealed class WeatherSettings : ObservableSettings
{
    private string _city = "";
    private string _place = "";
    private double? _latitude, _longitude;

    /// <summary>Ville saisie par l'utilisateur.</summary>
    public string City { get => _city; set => Set(ref _city, value ?? ""); }

    /// <summary>Nom trouvé par le géocodage (« Rouen, France »), affiché par le module.</summary>
    public string Place { get => _place; set => Set(ref _place, value ?? ""); }

    public double? Latitude { get => _latitude; set => Set(ref _latitude, value); }
    public double? Longitude { get => _longitude; set => Set(ref _longitude, value); }
}

/// <summary>Famille d'icône météo (dessinée par WeatherIcon).</summary>
public enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Storm }

public sealed record WeatherDay(DateTime Date, int Code, double Min, double Max);

/// <summary>Qualité de l'air actuelle : indice européen (0 = excellent, 100+ = extrêmement mauvais) et polluants en µg/m³.</summary>
public sealed record AirQualitySnapshot(string Place, int Aqi, double? Pm25, double? Pm10, double? No2, double? O3, DateTime Time);

public sealed record WeatherSnapshot(string Place, double Temperature, double FeelsLike, int Humidity, double Wind, int Code,
    bool IsDay, IReadOnlyList<WeatherDay> Days, DateTime Time);

/// <summary>
/// Météo par Open-Meteo (gratuit, sans clé) : géocodage de la ville, puis conditions actuelles et prévisions
/// sur quelques jours. Lecture toutes les 15 minutes, seulement si un module Météo est affiché.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly WeatherSettings _settings;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(15) };
    private bool _enabled, _busy;

    public WeatherService(WeatherSettings settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>Dernière météo lue (null tant qu'aucune lecture n'a abouti).</summary>
    public WeatherSnapshot? Current { get; private set; }

    /// <summary>Dernière qualité de l'air lue (même ville que la météo).</summary>
    public AirQualitySnapshot? Air { get; private set; }

    /// <summary>Dernière erreur en clair (null si la dernière lecture a réussi).</summary>
    public string? Error { get; private set; }

    public event Action? Updated;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                _timer.Start();
                if (Current is null || DateTime.Now - Current.Time > _timer.Interval) _ = RefreshAsync();
            }
            else _timer.Stop();
        }
    }

    /// <summary>Ville changée : oublie la position et relit tout de suite.</summary>
    public async Task<bool> SetCityAsync(string city)
    {
        _settings.City = city.Trim();
        _settings.Latitude = _settings.Longitude = null;
        _settings.Place = "";
        Current = null;
        Air = null;
        await RefreshAsync();
        return Error is null;
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.City))
            {
                Error = "Choisis une ville dans la personnalisation du module (onglet Style).";
                Current = null;
                Air = null;
                return;
            }
            if (_settings.Latitude is null || _settings.Longitude is null)
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=fr&format=json&name=" + Uri.EscapeDataString(_settings.City);
                var place = ParseGeocoding(await Http.GetStringAsync(url));
                if (place is null)
                {
                    Error = $"Ville « {_settings.City} » introuvable.";
                    Current = null;
                    Air = null;
                    return;
                }
                (_settings.Latitude, _settings.Longitude, _settings.Place) = place.Value;
            }
            var lat = _settings.Latitude.Value.ToString(CultureInfo.InvariantCulture);
            var lon = _settings.Longitude.Value.ToString(CultureInfo.InvariantCulture);
            var json = await Http.GetStringAsync(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=5");
            Current = ParseForecast(json, _settings.Place);
            Error = Current is null ? "Réponse météo illisible." : null;
            try
            {
                Air = ParseAirQuality(await Http.GetStringAsync(
                    $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={lat}&longitude={lon}" +
                    "&current=european_aqi,pm2_5,pm10,nitrogen_dioxide,ozone&timezone=auto"), _settings.Place);
            }
            catch (Exception ex)
            {
                Log.Warn($"Qualité de l'air : lecture impossible ({ex.Message})");
            }
        }
        catch (Exception ex)
        {
            Error = "Météo indisponible (pas de connexion ?).";
            Log.Warn($"Météo : lecture impossible ({ex.Message})");
        }
        finally
        {
            _busy = false;
            Updated?.Invoke();
        }
    }

    /// <summary>Premier résultat du géocodage : latitude, longitude et nom « Ville, Pays ».</summary>
    public static (double Lat, double Lon, string Place)? ParseGeocoding(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        var name = r.GetProperty("name").GetString() ?? "";
        if (r.TryGetProperty("country", out var country) && country.GetString() is { Length: > 0 } c) name += ", " + c;
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), name);
    }

    public static WeatherSnapshot? ParseForecast(string json, string place)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("current", out var cur)) return null;
        var days = new List<WeatherDay>();
        if (doc.RootElement.TryGetProperty("daily", out var daily))
        {
            var time = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var max = daily.GetProperty("temperature_2m_max");
            var min = daily.GetProperty("temperature_2m_min");
            for (var i = 0; i < time.GetArrayLength(); i++)
            {
                if (!DateTime.TryParse(time[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                days.Add(new WeatherDay(date, Number(codes[i]) is { } c ? (int)c : 0, Number(min[i]) ?? 0, Number(max[i]) ?? 0));
            }
        }
        return new WeatherSnapshot(place,
            Number(cur, "temperature_2m") ?? 0, Number(cur, "apparent_temperature") ?? 0,
            (int)(Number(cur, "relative_humidity_2m") ?? 0), Number(cur, "wind_speed_10m") ?? 0,
            (int)(Number(cur, "weather_code") ?? 0), (Number(cur, "is_day") ?? 1) != 0, days, DateTime.Now);
    }

    public static AirQualitySnapshot? ParseAirQuality(string json, string place)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("current", out var cur) || Number(cur, "european_aqi") is not { } aqi) return null;
        return new AirQualitySnapshot(place, (int)Math.Round(aqi), Number(cur, "pm2_5"), Number(cur, "pm10"),
            Number(cur, "nitrogen_dioxide"), Number(cur, "ozone"), DateTime.Now);
    }

    /// <summary>Indice européen de qualité de l'air → libellé et couleur (#RRGGBB) de l'échelle officielle.</summary>
    public static (string Label, string Color) AirLevel(int aqi) => aqi switch
    {
        < 20 => ("Bon", "#50F0E6"),
        < 40 => ("Correct", "#50CCAA"),
        < 60 => ("Moyen", "#F0E641"),
        < 80 => ("Médiocre", "#FF5050"),
        < 100 => ("Très mauvais", "#C8285A"),
        _ => ("Extrêmement mauvais", "#9B3FB4")
    };

    private static double? Number(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) ? Number(v) : null;

    private static double? Number(JsonElement v) => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>Code météo WMO → famille d'icône.</summary>
    public static WeatherKind Kind(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        >= 51 and <= 57 => WeatherKind.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => WeatherKind.Rain,
        (>= 71 and <= 77) or 85 or 86 => WeatherKind.Snow,
        >= 95 => WeatherKind.Storm,
        _ => WeatherKind.Cloudy
    };

    /// <summary>Code météo WMO → texte court en français.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Ciel dégagé",
        1 => "Peu nuageux",
        2 => "Partiellement nuageux",
        3 => "Couvert",
        45 or 48 => "Brouillard",
        >= 51 and <= 55 => "Bruine",
        56 or 57 => "Bruine verglaçante",
        61 => "Pluie faible",
        63 => "Pluie",
        65 => "Forte pluie",
        66 or 67 => "Pluie verglaçante",
        71 => "Neige faible",
        73 => "Neige",
        75 => "Forte neige",
        77 => "Grains de neige",
        80 or 81 => "Averses",
        82 => "Fortes averses",
        85 or 86 => "Averses de neige",
        95 => "Orage",
        96 or 99 => "Orage avec grêle",
        _ => "—"
    };

    public void Dispose() => _timer.Stop();
}
