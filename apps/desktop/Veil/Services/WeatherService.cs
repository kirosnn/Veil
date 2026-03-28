using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Veil.Configuration;

namespace Veil.Services;

internal sealed class WeatherService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private WeatherSnapshot? _snapshot;
    private DateTime _lastRefreshUtc;

    internal WeatherSnapshot? Snapshot => _snapshot;
    internal bool HasData => _snapshot is not null;
    internal event Action? StateChanged;

    internal Task InitializeAsync()
    {
        return RefreshAsync(true);
    }

    internal async Task RefreshAsync(bool force = false)
    {
        if (!force && _snapshot is not null && (DateTime.UtcNow - _lastRefreshUtc) < TimeSpan.FromMinutes(15))
        {
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            if (!force && _snapshot is not null && (DateTime.UtcNow - _lastRefreshUtc) < TimeSpan.FromMinutes(15))
            {
                return;
            }

            string primaryCityName = string.IsNullOrWhiteSpace(_settings.WeatherPrimaryCity)
                ? GuessPrimaryCityName()
                : _settings.WeatherPrimaryCity;
            WeatherCityForecast? primaryCity = await TryGetForecastAsync(primaryCityName, includeHourly: true);
            if (primaryCity is null)
            {
                string fallbackPrimaryCityName = GuessPrimaryCityName();
                if (!string.Equals(fallbackPrimaryCityName, primaryCityName, StringComparison.OrdinalIgnoreCase))
                {
                    primaryCity = await TryGetForecastAsync(fallbackPrimaryCityName, includeHourly: true);
                }
            }

            if (primaryCity is null)
            {
                return;
            }

            string[] otherCityNames = _settings.WeatherSecondaryCities.ToArray();
            var otherCities = new List<WeatherCityForecast>(otherCityNames.Length);

            foreach (string cityName in otherCityNames)
            {
                if (string.Equals(cityName, primaryCity.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                WeatherCityForecast? cityForecast = await TryGetForecastAsync(cityName, includeHourly: false);
                if (cityForecast is not null)
                {
                    otherCities.Add(cityForecast);
                }
            }

            _snapshot = new WeatherSnapshot(
                primaryCity,
                primaryCity.HourlyForecast,
                otherCities);
            _lastRefreshUtc = DateTime.UtcNow;
        }
        catch
        {
        }
        finally
        {
            _refreshLock.Release();
        }

        StateChanged?.Invoke();
    }

    private static async Task<WeatherCityForecast?> TryGetForecastAsync(string cityName, bool includeHourly)
    {
        try
        {
            return await GetForecastAsync(cityName, includeHourly);
        }
        catch
        {
            return null;
        }
    }

    internal static string GetConditionLabel(int weatherCode)
    {
        return weatherCode switch
        {
            0 => "Clear",
            1 or 2 => "Partly Cloudy",
            3 => "Cloudy",
            45 or 48 => "Fog",
            51 or 53 or 55 or 56 or 57 => "Drizzle",
            61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "Rain",
            71 or 73 or 75 or 77 or 85 or 86 => "Snow",
            95 or 96 or 99 => "Thunderstorm",
            _ => "Weather"
        };
    }

    private static async Task<WeatherCityForecast> GetForecastAsync(string cityName, bool includeHourly)
    {
        var location = await GeocodeAsync(cityName);
        if (location is null)
        {
            throw new InvalidOperationException($"Could not geocode {cityName}.");
        }

        string forecastUrl =
            $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude.ToString(CultureInfo.InvariantCulture)}" +
            $"&longitude={location.Longitude.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m" +
            "&hourly=temperature_2m,weather_code,is_day,precipitation_probability" +
            "&daily=temperature_2m_max,temperature_2m_min,sunrise,sunset" +
            "&forecast_days=2&timezone=auto";

        using var stream = await HttpClient.GetStreamAsync(forecastUrl);
        using var document = await JsonDocument.ParseAsync(stream);
        JsonElement root = document.RootElement;
        JsonElement current = root.GetProperty("current");
        JsonElement daily = root.GetProperty("daily");
        JsonElement dailyHighs = daily.GetProperty("temperature_2m_max");
        JsonElement dailyLows = daily.GetProperty("temperature_2m_min");
        JsonElement dailySunrise = daily.GetProperty("sunrise");
        JsonElement dailySunset = daily.GetProperty("sunset");
        string displayName = location.Name;

        double currentTemperature = current.GetProperty("temperature_2m").GetDouble();
        double apparentTemperature = current.GetProperty("apparent_temperature").GetDouble();
        int humidityPercent = current.GetProperty("relative_humidity_2m").GetInt32();
        int currentWeatherCode = current.GetProperty("weather_code").GetInt32();
        bool isDay = current.GetProperty("is_day").GetInt32() == 1;
        double windSpeedKph = current.GetProperty("wind_speed_10m").GetDouble();
        double highTemperature = dailyHighs.EnumerateArray().First().GetDouble();
        double lowTemperature = dailyLows.EnumerateArray().First().GetDouble();
        DateTime sunrise = DateTime.Parse(dailySunrise.EnumerateArray().First().GetString()!, CultureInfo.InvariantCulture);
        DateTime sunset = DateTime.Parse(dailySunset.EnumerateArray().First().GetString()!, CultureInfo.InvariantCulture);

        IReadOnlyList<WeatherHourlyForecast> hourlyForecast = includeHourly
            ? BuildHourlyForecast(root.GetProperty("hourly"))
            : Array.Empty<WeatherHourlyForecast>();

        return new WeatherCityForecast(
            displayName,
            currentTemperature,
            apparentTemperature,
            humidityPercent,
            currentWeatherCode,
            isDay,
            windSpeedKph,
            highTemperature,
            lowTemperature,
            sunrise,
            sunset,
            hourlyForecast);
    }

    private static IReadOnlyList<WeatherHourlyForecast> BuildHourlyForecast(JsonElement hourly)
    {
        JsonElement times = hourly.GetProperty("time");
        JsonElement temperatures = hourly.GetProperty("temperature_2m");
        JsonElement weatherCodes = hourly.GetProperty("weather_code");
        JsonElement isDayValues = hourly.GetProperty("is_day");
        JsonElement precipitationProbabilities = hourly.GetProperty("precipitation_probability");
        JsonElement[] timeItems = times.EnumerateArray().ToArray();
        JsonElement[] temperatureItems = temperatures.EnumerateArray().ToArray();
        JsonElement[] weatherCodeItems = weatherCodes.EnumerateArray().ToArray();
        JsonElement[] isDayItems = isDayValues.EnumerateArray().ToArray();
        JsonElement[] precipitationItems = precipitationProbabilities.EnumerateArray().ToArray();
        var forecasts = new List<WeatherHourlyForecast>(5);
        DateTime now = DateTime.Now;

        for (int i = 0; i < timeItems.Length; i++)
        {
            if (!DateTime.TryParse(timeItems[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
            {
                continue;
            }

            if (time < now.AddMinutes(-30))
            {
                continue;
            }

            forecasts.Add(new WeatherHourlyForecast(
                time,
                temperatureItems[i].GetDouble(),
                weatherCodeItems[i].GetInt32(),
                isDayItems[i].GetInt32() == 1,
                precipitationItems[i].GetInt32()));

            if (forecasts.Count == 5)
            {
                break;
            }
        }

        return forecasts;
    }

    private static async Task<WeatherLocation?> GeocodeAsync(string cityName)
    {
        string geocodingUrl =
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(cityName)}&count=1&language=en&format=json";
        using var stream = await HttpClient.GetStreamAsync(geocodingUrl);
        using var document = await JsonDocument.ParseAsync(stream);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement result = results.EnumerateArray().First();
        string name = result.GetProperty("name").GetString() ?? cityName;
        double latitude = result.GetProperty("latitude").GetDouble();
        double longitude = result.GetProperty("longitude").GetDouble();
        return new WeatherLocation(name, latitude, longitude);
    }

    private static string GuessPrimaryCityName()
    {
        string timeZoneId = TimeZoneInfo.Local.Id;
        if (timeZoneId.Contains('/'))
        {
            string guessed = timeZoneId[(timeZoneId.LastIndexOf('/') + 1)..].Replace('_', ' ').Trim();
            if (!string.IsNullOrWhiteSpace(guessed))
            {
                return guessed;
            }
        }

        return timeZoneId switch
        {
            "Romance Standard Time" => "Paris",
            "GMT Standard Time" => "London",
            "W. Europe Standard Time" => "Berlin",
            "Eastern Standard Time" => "New York",
            "Central Standard Time" => "Chicago",
            "Mountain Standard Time" => "Denver",
            "Pacific Standard Time" => "Los Angeles",
            "Tokyo Standard Time" => "Tokyo",
            "China Standard Time" => "Beijing",
            _ => "Paris"
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Veil/1.0");
        return client;
    }

    private sealed record WeatherLocation(string Name, double Latitude, double Longitude);
}

internal sealed record WeatherSnapshot(
    WeatherCityForecast PrimaryCity,
    IReadOnlyList<WeatherHourlyForecast> HourlyForecast,
    IReadOnlyList<WeatherCityForecast> OtherCities);

internal sealed record WeatherCityForecast(
    string DisplayName,
    double TemperatureC,
    double ApparentTemperatureC,
    int HumidityPercent,
    int WeatherCode,
    bool IsDay,
    double WindSpeedKph,
    double HighTemperatureC,
    double LowTemperatureC,
    DateTime SunriseLocal,
    DateTime SunsetLocal,
    IReadOnlyList<WeatherHourlyForecast> HourlyForecast);

internal sealed record WeatherHourlyForecast(
    DateTime Time,
    double TemperatureC,
    int WeatherCode,
    bool IsDay,
    int PrecipitationProbabilityPercent);
