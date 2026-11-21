using System.Windows.Media;

namespace Ostrov.Backdrop;

/// <summary>
/// Цвета мягкого фона вкладок без собственной палитры (М-019, решения пользователя 2026-11-13): «Время», «Проекты», «Буфер», «Новости», «Центр»,
/// «Настройки». Фон тот же по устройству, что у «Плеера» и «Погоды» (<see cref="CoverGlow"/>: четыре больших пятна по углам, лёгкий дрейф, как в присланном
/// примере «Aurora»): у каждой вкладки один цвет по теме раздела, в четырёх близких оттенках.
/// Фон тише и теснее, чем у плеера и погоды (по просьбе пользователя 2026-11-14): непрозрачность пятна в центре 12,5 % (было 44 %),
/// размер пятен 70 %. Поэтому цвета здесь не затемнены, а только на 15 % приглушены к серому (иначе при такой непрозрачности их не видно),
/// фиолетового нет.
/// </summary>
internal static class TabPalettes
{
    /// <summary>Непрозрачность пятна в центре (10–15 %, решение пользователя) и во сколько раз пятна меньше обычных.</summary>
    public const double CenterAlpha = 0.12, Spread = 0.7;

    /// <summary>Множитель к обычной силе фона (<see cref="CoverGlow"/>), чтобы в центре пятна вышло <see cref="CenterAlpha"/>.</summary>
    public static double Intensity => CenterAlpha / CoverGlow.CenterAlpha;

    /// <summary>«Главная» (хаб): полуночный синий #191970 и его оттенки (подборка пользователя).</summary>
    public static Color[] Hub { get; } = Make((18, 33, 94), (48, 63, 124), (18, 33, 94), (73, 88, 149)); // основа #12215E (выбор пользователя в стенде)

    /// <summary>«Время»: тёплый янтарный, в тон оранжевому таймера.</summary>
    public static Color[] Time { get; } = Make((255, 159, 10), (255, 190, 85), (232, 120, 40), (255, 172, 60));

    /// <summary>«Новости»: приглушённый синий.</summary>
    public static Color[] News { get; } = Make((70, 120, 200), (95, 150, 215), (50, 90, 170), (80, 130, 190));

    /// <summary>«Телефон»: приглушённый зелёно-серый.</summary>
    public static Color[] Phone { get; } = Make((95, 150, 130), (75, 125, 115), (110, 165, 140), (65, 110, 105));

    /// <summary>«Проекты»: полуночный синий.</summary>
    public static Color[] Projects { get; } = Make((25, 25, 112), (40, 52, 140), (30, 30, 120), (60, 70, 170));

    /// <summary>«Буфер»: графит с тёплым оттенком.</summary>
    public static Color[] Buffer { get; } = Make((135, 122, 108), (108, 98, 90), (152, 138, 124), (96, 88, 82));

    /// <summary>«Настройки»: полуночный синий с лёгким оттенком.</summary>
    public static Color[] Settings { get; } = Make((25, 25, 112), (55, 50, 150), (35, 35, 125), (75, 75, 180));

    /// <summary>Цвет оттенка заливки острова по вкладке (стенд island-demo, 8 %): вкладки без записи берут нейтральный.</summary>
    public static Color TintFor(string id) => id switch
    {
        "hub" => Color.FromRgb(0x12, 0x21, 0x5E),
        "player" => Color.FromRgb(0x7A, 0x3B, 0x5A),
        "weather" => Color.FromRgb(0x3B, 0x6A, 0x9A),
        "time" => Color.FromRgb(0xC9, 0x78, 0x0A),
        "projects" => Color.FromRgb(0x4A, 0x5A, 0x70),
        "buffer" => Color.FromRgb(0x6A, 0x56, 0x46),
        "phone" => Color.FromRgb(0x3F, 0x6A, 0x5A),
        "news" => Color.FromRgb(0x3B, 0x6A, 0x9A),
        _ => Color.FromRgb(0x5A, 0x5F, 0x66),
    };

    private static Color[] Make(params (byte R, byte G, byte B)[] colors) => colors.Select(c => Mute(Color.FromRgb(c.R, c.G, c.B))).ToArray();

    // Приглушение: 15 % к серому той же яркости, без затемнения (фон и так почти прозрачный).
    private static Color Mute(Color c)
    {
        var gray = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        byte Ch(double v) => (byte)Math.Round(v + (gray - v) * 0.15);
        return Color.FromRgb(Ch(c.R), Ch(c.G), Ch(c.B));
    }
}
