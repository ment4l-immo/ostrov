using System.Globalization;
using System.Windows.Media;
using Ostrov.Backdrop;

namespace Ostrov.Modules.Weather;

/// <summary>
/// Фраза-сводка под температурой («Дождь начнётся около 18:00») и цвета мягкого фона вкладки по погоде.
/// Всё считается по уже загруженным данным, в сеть ничего не уходит.
/// </summary>
internal static class WeatherSummary
{
    private static readonly CultureInfo Ru = new("ru-RU");

    // Осадки в часе: не меньше 0,2 мм и вероятность не меньше 50 % (меньше — шум прогноза).
    private static bool Wet(HourPoint h) => h.Precip >= 0.2 && h.PrecipProb >= 50;

    private static bool Dry(HourPoint h) => h.Precip < 0.1 || h.PrecipProb < 30;

    /// <summary>Фраза для «сейчас» по времени города. Всегда непустая.</summary>
    public static string Make(WeatherData d, DateTime now)
    {
        var kind = WeatherCodes.Describe(d.Code).Kind;
        var hours = d.Days.SelectMany(day => day.Hours).Where(h => h.Time >= now.Date.AddHours(now.Hour)).OrderBy(h => h.Time).Take(26).ToList();
        var nowWet = kind is WeatherKind.Rain or WeatherKind.Snow or WeatherKind.Thunder;

        // 1) Осадки идут: когда закончатся (два сухих часа подряд).
        if (nowWet)
        {
            var noun = kind switch { WeatherKind.Thunder => "Гроза", WeatherKind.Snow => "Снег", _ => "Дождь" };
            for (var i = 1; i + 1 < hours.Count && hours[i].Time <= now.AddHours(12); i++)
                if (Dry(hours[i]) && Dry(hours[i + 1]))
                    return $"{noun} закончится {When(hours[i].Time, now)}";
            return $"{noun} не утихнет ещё долго";
        }

        // 2) Осадки ожидаются в ближайшие сутки: когда начнутся.
        for (var i = 1; i < hours.Count && hours[i].Time <= now.AddHours(24); i++)
        {
            if (!Wet(hours[i])) continue;
            var noun = hours[i].Temp <= 0.5 ? "Снег" : "Дождь";
            if ((hours[i].Time - now).TotalMinutes <= 75) return $"{noun} скоро начнётся";
            return hours[i].Time.Date == now.Date
                ? $"{noun} начнётся около {hours[i].Time:HH:mm}"
                : $"{noun} завтра около {hours[i].Time:HH:mm}";
        }

        // 3) Без осадков: заметное потепление или похолодание в ближайшие 12 часов.
        var ahead = hours.Where(h => h.Time <= now.AddHours(12)).ToList();
        if (ahead.Count > 1)
        {
            var max = ahead.MaxBy(h => h.Temp)!;
            var min = ahead.MinBy(h => h.Temp)!;
            var up = max.Temp - d.Temperature;
            var down = d.Temperature - min.Temp;
            if (Math.Max(up, down) >= 5)
            {
                var warm = up >= down;
                var peak = warm ? max : min;
                return $"{Part(peak.Time, now)} {(warm ? "потеплеет" : "похолодает")} до {WeatherFormat.Temp(peak.Temp)}";
            }
        }

        // 4) Сильный ветер.
        var gust = ahead.Count > 0 ? ahead.Max(h => h.Gust) : 0;
        if (gust >= 15) return $"Порывы ветра до {Math.Round(gust):0} м/с";

        return "Ближайшие сутки без осадков";
    }

    // «около 18:00» сегодня, «завтра около 06:00» завтра (время округлено до часа).
    private static string When(DateTime t, DateTime now) =>
        t.Date == now.Date ? $"около {t:HH:mm}" : $"завтра около {t:HH:mm}";

    // Часть суток для фразы про температуру: «Утром», «Днём», «Вечером», «Ночью» (в разговорной форме, с заглавной).
    private static string Part(DateTime t, DateTime now)
    {
        var h = t.Hour;
        var part = h is >= 5 and < 11 ? "Утром" : h is >= 11 and < 17 ? "Днём" : h is >= 17 and < 23 ? "Вечером" : "Ночью";
        return t.Date != now.Date && part != "Ночью" ? $"Завтра {part.ToLower(Ru)}" : part;
    }

    /// <summary>Что показывать падающим на фоне по текущему коду погоды (гроза без осадков на фоне) и сколько: морось и слабый дождь реже, ливень гуще.</summary>
    public static (PrecipKind Kind, double Intensity) Precipitation(int code) => code switch
    {
        51 or 53 or 55 or 56 or 57 => (PrecipKind.Rain, 0.35),
        61 or 80 => (PrecipKind.Rain, 0.55),
        63 or 66 or 67 or 81 => (PrecipKind.Rain, 0.8),
        65 or 82 => (PrecipKind.Rain, 1.0),
        71 or 77 => (PrecipKind.Snow, 0.5),
        73 or 85 => (PrecipKind.Snow, 0.8),
        75 or 86 => (PrecipKind.Snow, 1.0),
        _ => (PrecipKind.None, 0),
    };

    // ---------- цвета фона ----------

    /// <summary>
    /// Четыре цвета мягкого фона: солнце тёплое, дождь холодно-синий, снег светлый, гроза тёмно-синяя, ночью глубже и темнее.
    /// Цвета приглушены (<see cref="Mute"/>): яркие слишком бросались в глаза. Фиолетового нет (пожелание пользователя).
    /// </summary>
    public static Color[] Palette(WeatherKind kind, bool isDay) => Raw(kind, isDay).Select(Mute).ToArray();

    // Приглушение: 20 % к серому той же яркости (меньше насыщенность) и на 30 % темнее.
    private static Color Mute(Color c)
    {
        var gray = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        byte Ch(double v) => (byte)Math.Round((v + (gray - v) * 0.2) * 0.70);
        return Color.FromRgb(Ch(c.R), Ch(c.G), Ch(c.B));
    }

    private static Color[] Raw(WeatherKind kind, bool isDay) => (kind, isDay) switch
    {
        (WeatherKind.Clear, true) => Colors4((255, 170, 50), (255, 205, 110), (255, 125, 70), (255, 150, 90)),
        (WeatherKind.PartlyCloudy, true) => Colors4((120, 170, 225), (255, 190, 100), (150, 170, 200), (255, 160, 90)),
        (WeatherKind.Cloudy, true) => Colors4((130, 142, 160), (105, 118, 140), (150, 158, 172), (95, 105, 125)),
        (WeatherKind.Fog, true) => Colors4((150, 155, 162), (128, 133, 142), (165, 168, 175), (120, 125, 135)),
        (WeatherKind.Rain, true) => Colors4((60, 125, 210), (45, 95, 175), (85, 145, 215), (55, 80, 150)),
        (WeatherKind.Snow, true) => Colors4((150, 195, 235), (190, 215, 242), (120, 165, 215), (170, 205, 240)),
        (WeatherKind.Thunder, true) => Colors4((80, 95, 170), (55, 75, 140), (95, 110, 185), (45, 60, 120)),

        (WeatherKind.Clear or WeatherKind.PartlyCloudy, false) => Colors4((30, 55, 140), (35, 65, 135), (25, 80, 150), (40, 75, 155)),
        (WeatherKind.Cloudy or WeatherKind.Fog, false) => Colors4((60, 70, 100), (50, 58, 90), (75, 82, 110), (45, 52, 80)),
        (WeatherKind.Rain, false) => Colors4((35, 80, 150), (30, 55, 120), (50, 100, 160), (35, 50, 100)),
        (WeatherKind.Snow, false) => Colors4((90, 120, 170), (120, 145, 185), (70, 100, 150), (105, 130, 175)),
        _ => Colors4((60, 75, 150), (40, 55, 115), (75, 90, 160), (35, 48, 100)),
    };

    private static Color[] Colors4(params (byte R, byte G, byte B)[] c) =>
        c.Select(x => Color.FromRgb(x.R, x.G, x.B)).ToArray();
}
