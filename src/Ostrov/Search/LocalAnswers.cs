using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using Ostrov.Modules.Weather;
using Ostrov.Services;

namespace Ostrov.Search;

/// <summary>
/// Ответы Alt+Q без ИИ и без ключа OpenAI (работают и без VPN): «100 долларов в рубли», «курс евро», «погода», «погода завтра».
/// Фраза должна быть понятной: «100 usd в rub», «сколько 50 евро в тенге», «курс доллара»; всё сомнительное уходит ИИ как раньше.
/// </summary>
internal static class LocalAnswers
{
    // Начала слов (в нижнем регистре) → код валюты.
    private static readonly (string[] Stems, string Code)[] Currencies =
    [
        (["доллар", "долар", "бакс", "usd", "$"], "USD"),
        (["евро", "eur", "€"], "EUR"),
        (["рубл", "руб", "rub", "rur", "₽"], "RUB"),
        (["тенге", "kzt", "₸"], "KZT"),
        (["гривн", "uah", "₴"], "UAH"),
        (["фунт", "gbp", "£"], "GBP"),
        (["юан", "cny", "rmb"], "CNY"),
        (["иен", "йен", "jpy", "¥"], "JPY"),
        (["франк", "chf"], "CHF"),
        (["злот", "pln"], "PLN"),
        (["лир", "try"], "TRY"),
        (["драм", "amd"], "AMD"),
        (["лари", "gel"], "GEL"),
        (["манат", "azn"], "AZN"),
        (["дирхам", "aed"], "AED"),
        (["рупи", "inr"], "INR"),
        (["вон", "krw"], "KRW"),
    ];

    private static string? Code(string word)
    {
        var w = word.Trim().ToLowerInvariant().Trim('.', ',', '?', '!');
        if (w.Length == 0) return null;
        foreach (var (stems, code) in Currencies)
            foreach (var stem in stems)
                if (w == stem || (stem.Length >= 3 && w.StartsWith(stem, StringComparison.Ordinal) && w.Length <= stem.Length + 4)) return code;
        return null;
    }

    /// <summary>«100 долларов в рубли», «сколько 50 евро в тенге», «курс доллара», «курс евро к рублю».</summary>
    public static (double Amount, string From, string To)? TryCurrency(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 60) return null;
        var text = Regex.Replace(input.Trim().TrimEnd('.', '!', '?'), @"\s+", " ").ToLowerInvariant().Replace('ё', 'е');
        const RegexOptions o = RegexOptions.CultureInvariant;

        var rate = Regex.Match(text, @"^(?:какой |покажи |дай )?курс (?<c1>\S+)(?: (?:к|в|по отношению к|to) (?<c2>\S+))?$", o);
        if (rate.Success)
        {
            var from = Code(rate.Groups["c1"].Value);
            var to = rate.Groups["c2"].Success ? Code(rate.Groups["c2"].Value) : "RUB";
            return from != null && to != null && from != to ? (1, from, to) : null;
        }

        var m = Regex.Match(text, @"^(?:сколько |скока |переведи |конвертируй |посчитай )?(?:будет |это )?(?<amt>\d[\d ]*(?:[.,]\d+)?)?\s*(?<c1>[^\d\s]+) (?:в|во|на|to|in|=|->) (?<c2>[^\d\s]+)$", o);
        if (!m.Success) return null;
        var c1 = Code(m.Groups["c1"].Value);
        var c2 = Code(m.Groups["c2"].Value);
        if (c1 == null || c2 == null || c1 == c2) return null;
        var amount = 1.0;
        if (m.Groups["amt"].Success && !double.TryParse(m.Groups["amt"].Value.Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) return null;
        if (amount <= 0 || amount > 1e12) return null;
        return (amount, c1, c2);
    }

    /// <summary>0 — сегодня или сейчас, 1 — завтра; null — не про погоду (город и другие уточнения уходят ИИ).</summary>
    public static int? TryWeather(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 40) return null;
        var text = Regex.Replace(input.Trim().TrimEnd('.', '!', '?'), @"\s+", " ").ToLowerInvariant();
        const RegexOptions o = RegexOptions.CultureInvariant;
        if (Regex.IsMatch(text, @"^(?:какая |покажи |дай |что с |а )?(?:сейчас |сегодня )?погод[аыу](?: (?:сейчас|сегодня|на сегодня|на улице|за окном))?$", o)) return 0;
        if (Regex.IsMatch(text, @"^(?:какая |покажи |дай )?(?:будет )?погод[аыу] (?:завтра|на завтра)$", o)
            || Regex.IsMatch(text, @"^какая (?:будет )?завтра погода$", o)) return 1;
        return null;
    }

    /// <summary>Карточка погоды из прогноза Open-Meteo по городу из вкладки «Погода».</summary>
    public static async Task<FrameworkElement> WeatherCardAsync(int dayOffset, CancellationToken ct)
    {
        var settings = AppSettings.Load();
        if (string.IsNullOrWhiteSpace(settings.WeatherCity))
            return new ActionCard(ActionCard.Warning(), "Город не выбран", "Открой вкладку «Погода» и найди свой город: после этого «погода» будет отвечать здесь.", error: true);
        try
        {
            var city = new City(settings.WeatherCity, "", settings.WeatherLatitude, settings.WeatherLongitude);
            var data = await WeatherService.GetAsync(city, ct);
            if (dayOffset == 0)
            {
                var (text, kind) = WeatherCodes.Describe(data.Code);
                var icon = new WeatherIcon { Width = 22, Height = 22, Kind = kind, IsDay = data.IsDay };
                var detail = WeatherSummary.Make(data, data.LocalNow) + $". Ощущается {WeatherFormat.Temp(data.FeelsLike)}, ветер {data.WindMs:0} м/с";
                if (data.Days.Count > 0) detail += $", за день {WeatherFormat.Temp(data.Days[0].Min)} … {WeatherFormat.Temp(data.Days[0].Max)}";
                return new ActionCard(icon, $"{settings.WeatherCity}: {WeatherFormat.Temp(data.Temperature)}, {text.ToLowerInvariant()}", detail);
            }
            if (data.Days.Count < 2) return new ActionCard(ActionCard.Warning(), "Нет прогноза на завтра", "Попробуй позже.", error: true);
            var d = data.Days[1];
            var (dayText, dayKind) = WeatherCodes.Describe(d.Code);
            var tomorrowIcon = new WeatherIcon { Width = 22, Height = 22, Kind = dayKind, IsDay = true };
            var rain = d.PrecipProbMax >= 30 ? $"осадки до {d.PrecipProbMax}%, " : "без осадков, ";
            var info = $"{rain}ветер до {d.WindMax:0} м/с";
            if (d.Sunrise is { } rise && d.Sunset is { } set) info += $", светло с {rise:HH:mm} до {set:HH:mm}";
            return new ActionCard(tomorrowIcon, $"{settings.WeatherCity}, завтра: {WeatherFormat.Temp(d.Min)} … {WeatherFormat.Temp(d.Max)}, {dayText.ToLowerInvariant()}", info);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new ActionCard(ActionCard.Warning(), "Погода не загрузилась", "Нет связи с сервисом погоды. Проверь интернет и VPN.", error: true);
        }
    }
}
