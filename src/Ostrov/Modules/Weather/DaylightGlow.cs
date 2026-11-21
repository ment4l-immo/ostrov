using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostrov.Modules.Weather;

/// <summary>
/// «Свет дня» (выбор пользователя 2026-11-15): еле заметное пятно света за сутки проходит по плашке слева направо, как солнце по небу:
/// утром слева и тёплое, днём по центру и светлое, вечером справа и янтарное; ночью остаётся ровный холодный отсвет.
/// Форма совпадает с островом (скруглён снизу), чтобы свет не выходил за чёрную плашку. Стоит на плашке пустого острова и на плашке диктовки.
/// Время восхода и заката общее для всех плашек: его выставляет плашка пустого острова, когда приходит погода.
/// </summary>
internal sealed class DaylightGlow : Border
{
    /// <summary>Яркость света в центре пятна. Сначала 3 %, затем «7–10 %», 2026-11-16 пользователь попросил «чуть-чуть побольше»: 11 %.</summary>
    public const double GlowAlpha = 0.11;

    // Пока погода не загрузилась: восход и закат, запомненные с прошлого раза (иначе при запуске свет показывал бы «условный день» 7–19 и потом резко
    // перескакивал на настоящий); если запоминать нечего (первый запуск), условные 7–19.
    private static double _riseHour = Services.AppSettings.Load().LastSunRise ?? 7, _setHour = Services.AppSettings.Load().LastSunSet ?? 19;

    public DaylightGlow()
    {
        CornerRadius = new CornerRadius(0, 0, 18, 18);
        IsHitTestVisible = false;
        Update(DateTime.Now);
    }

    /// <summary>Настоящие восход и закат города (часы от полуночи).</summary>
    public static void SetSunTimes(double riseHour, double setHour)
    {
        // Запоминаем на следующий запуск (записываем, только если время сдвинулось больше чем на минуту).
        if (Math.Abs(riseHour - _riseHour) > 1.0 / 60 || Math.Abs(setHour - _setHour) > 1.0 / 60)
        {
            var settings = Services.AppSettings.Load();
            settings.LastSunRise = riseHour;
            settings.LastSunSet = setHour;
            settings.Save();
        }
        _riseHour = riseHour;
        _setHour = setHour;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B);

    /// <summary>Пересчитать пятно на заданный момент (достаточно раз в минуту: движения глазом не видно).</summary>
    public void Update(DateTime now)
    {
        var hour = now.TimeOfDay.TotalHours;
        var day = Math.Clamp((hour - _riseHour) / Math.Max(1, _setHour - _riseHour), 0, 1); // 0 — восход, 1 — закат
        var isDay = hour >= _riseHour && hour <= _setHour;
        // Насколько «день»: у восхода и заката свет плавно набирается и уходит (примерно за 40 минут).
        var edge = Math.Min(hour - _riseHour, _setHour - hour);
        var sun = isDay ? Math.Clamp(edge / 0.66, 0, 1) : 0;

        var warm = Color.FromRgb(255, 190, 130);   // утро и вечер
        var noon = Color.FromRgb(255, 248, 232);   // день
        var cool = Color.FromRgb(150, 180, 255);   // ночь
        var height = Math.Sin(Math.PI * day);       // 0 у горизонта, 1 в полдень
        var sunColor = Mix(warm, noon, height);

        var brush = new RadialGradientBrush
        {
            // Пятно едет от левого края к правому; ночью стоит по центру и шире (ровный отсвет).
            Center = new Point(sun > 0 ? 0.08 + 0.84 * day : 0.5, 0.3),
            GradientOrigin = new Point(sun > 0 ? 0.08 + 0.84 * day : 0.5, 0.3),
            RadiusX = sun > 0 ? 0.46 : 0.9,
            RadiusY = sun > 0 ? 1.5 : 2.2
        };
        var color = Mix(cool, sunColor, sun);
        var alpha = GlowAlpha * (sun > 0 ? 0.55 + 0.45 * sun : 0.55); // ночью и на заре тише
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, alpha), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, alpha * 0.45), 0.55));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0), 1));
        brush.Freeze();
        Background = brush;
    }
}
