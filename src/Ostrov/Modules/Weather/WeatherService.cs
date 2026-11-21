using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Ostrov.Modules.Weather;

/// <summary>Город из поиска. Subtitle — регион и страна для подсказки в списке.</summary>
internal sealed record City(string Name, string Subtitle, double Latitude, double Longitude);

/// <summary>Один час прогноза. Время — местное для города.</summary>
internal sealed record HourPoint(DateTime Time, double Temp, int PrecipProb, double Precip, double Wind, double Gust, int WindDir, int Code = 0, bool IsDay = true);

internal sealed record DayForecast(
    DateOnly Date, int Code, double Max, double Min,
    DateTime? Sunrise, DateTime? Sunset,
    double PrecipSum, int PrecipProbMax, double WindMax, double GustMax, int WindDirDominant,
    IReadOnlyList<HourPoint> Hours);

/// <summary>LocalNow — «сейчас» по времени города (а не компьютера): от него считаются дуга солнца и отметка на графике.</summary>
internal sealed record WeatherData(
    double Temperature, double FeelsLike, double WindMs, int Code, bool IsDay, DateTime LocalNow,
    IReadOnlyList<DayForecast> Days, DateTime Updated);

/// <summary>
/// Open-Meteo (бесплатно, без ключа). Единственные данные, которые уходят в сеть: название для поиска города
/// и координаты выбранного города.
/// </summary>
internal static class WeatherService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OstrovIsland/1.0");
        return client;
    }

    public static async Task<IReadOnlyList<City>> SearchAsync(string query, CancellationToken ct)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=4&language=ru&format=json&name="
                  + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

        var list = new List<City>();
        foreach (var r in results.EnumerateArray())
        {
            var parts = new List<string>();
            if (r.TryGetProperty("admin1", out var admin) && admin.GetString() is { Length: > 0 } a) parts.Add(a);
            if (r.TryGetProperty("country", out var country) && country.GetString() is { Length: > 0 } c) parts.Add(c);
            list.Add(new City(
                r.GetProperty("name").GetString() ?? query,
                string.Join(", ", parts),
                r.GetProperty("latitude").GetDouble(),
                r.GetProperty("longitude").GetDouble()));
        }
        return list;
    }

    public static async Task<WeatherData> GetAsync(City city, CancellationToken ct)
    {
        var url = "https://api.open-meteo.com/v1/forecast"
                  + $"?latitude={city.Latitude.ToString(Inv)}&longitude={city.Longitude.ToString(Inv)}"
                  + "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,is_day"
                  + "&hourly=temperature_2m,precipitation_probability,precipitation,wind_speed_10m,wind_gusts_10m,wind_direction_10m,weather_code,is_day"
                  + "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_sum,"
                  + "precipitation_probability_max,wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant"
                  + "&wind_speed_unit=ms&timezone=auto&forecast_days=5";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var root = doc.RootElement;
        var cur = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        var hourly = root.GetProperty("hourly");

        // Часы раскладываем по дням.
        var hTime = hourly.GetProperty("time");
        var hours = new List<HourPoint>();
        for (var i = 0; i < hTime.GetArrayLength(); i++)
            hours.Add(new HourPoint(
                ParseTime(hTime[i].GetString())!.Value,
                Num(hourly, "temperature_2m", i),
                (int)Num(hourly, "precipitation_probability", i),
                Num(hourly, "precipitation", i),
                Num(hourly, "wind_speed_10m", i),
                Num(hourly, "wind_gusts_10m", i),
                (int)Num(hourly, "wind_direction_10m", i),
                (int)Num(hourly, "weather_code", i),
                Num(hourly, "is_day", i) >= 1));

        var dates = daily.GetProperty("time");
        var days = new List<DayForecast>();
        for (var i = 0; i < dates.GetArrayLength(); i++)
        {
            var date = DateOnly.ParseExact(dates[i].GetString()!, "yyyy-MM-dd", Inv);
            days.Add(new DayForecast(
                date,
                (int)Num(daily, "weather_code", i), Num(daily, "temperature_2m_max", i), Num(daily, "temperature_2m_min", i),
                ParseTime(Str(daily, "sunrise", i)), ParseTime(Str(daily, "sunset", i)),
                Num(daily, "precipitation_sum", i), (int)Num(daily, "precipitation_probability_max", i),
                Num(daily, "wind_speed_10m_max", i), Num(daily, "wind_gusts_10m_max", i),
                (int)Num(daily, "wind_direction_10m_dominant", i),
                hours.Where(h => DateOnly.FromDateTime(h.Time) == date).ToList()));
        }

        return new WeatherData(
            cur.GetProperty("temperature_2m").GetDouble(),
            cur.GetProperty("apparent_temperature").GetDouble(),
            cur.GetProperty("wind_speed_10m").GetDouble(),
            cur.GetProperty("weather_code").GetInt32(),
            cur.GetProperty("is_day").GetInt32() == 1,
            ParseTime(cur.GetProperty("time").GetString()) ?? DateTime.Now,
            days, DateTime.Now);
    }

    // В ответе бывают null (например, нет вероятности осадков или полярный день без восхода) — не падаем.
    private static double Num(JsonElement group, string name, int i) =>
        group.TryGetProperty(name, out var arr) && i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.Number
            ? arr[i].GetDouble() : 0;

    private static string? Str(JsonElement group, string name, int i) =>
        group.TryGetProperty(name, out var arr) && i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.String
            ? arr[i].GetString() : null;

    private static DateTime? ParseTime(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm", Inv, DateTimeStyles.None, out var t) ? t : null;
}
