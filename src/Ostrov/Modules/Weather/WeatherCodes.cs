namespace Ostrov.Modules.Weather;

/// <summary>Общие правила показа чисел: температура со знаком и градусом, направление ветра по сторонам света.</summary>
internal static class WeatherFormat
{
    public static string Temp(double t)
    {
        var r = (int)Math.Round(t, MidpointRounding.AwayFromZero);
        return r > 0 ? $"+{r}°" : r < 0 ? $"−{-r}°" : "0°";
    }

    /// <summary>Откуда дует ветер (как в прогнозах): С, СВ, В, ЮВ, Ю, ЮЗ, З, СЗ.</summary>
    public static string Compass(int degrees)
    {
        string[] sides = ["С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ"];
        return sides[(int)Math.Round(((degrees % 360) + 360) % 360 / 45.0) % 8];
    }
}

internal enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Rain, Snow, Thunder }

/// <summary>Коды погоды WMO (их отдаёт Open-Meteo) → русское описание и вид иконки.</summary>
internal static class WeatherCodes
{
    public static (string Text, WeatherKind Kind) Describe(int code) => code switch
    {
        0 => ("Ясно", WeatherKind.Clear),
        1 => ("Малооблачно", WeatherKind.Clear),
        2 => ("Переменная облачность", WeatherKind.PartlyCloudy),
        3 => ("Пасмурно", WeatherKind.Cloudy),
        45 or 48 => ("Туман", WeatherKind.Fog),
        51 or 53 or 55 => ("Морось", WeatherKind.Rain),
        56 or 57 => ("Ледяная морось", WeatherKind.Rain),
        61 => ("Небольшой дождь", WeatherKind.Rain),
        63 => ("Дождь", WeatherKind.Rain),
        65 => ("Сильный дождь", WeatherKind.Rain),
        66 or 67 => ("Ледяной дождь", WeatherKind.Rain),
        71 => ("Небольшой снег", WeatherKind.Snow),
        73 => ("Снег", WeatherKind.Snow),
        75 => ("Сильный снег", WeatherKind.Snow),
        77 => ("Снежные зёрна", WeatherKind.Snow),
        80 => ("Небольшой ливень", WeatherKind.Rain),
        81 => ("Ливень", WeatherKind.Rain),
        82 => ("Сильный ливень", WeatherKind.Rain),
        85 or 86 => ("Снегопад", WeatherKind.Snow),
        95 => ("Гроза", WeatherKind.Thunder),
        96 or 99 => ("Гроза с градом", WeatherKind.Thunder),
        _ => ("Нет данных", WeatherKind.Cloudy)
    };
}
