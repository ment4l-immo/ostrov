using System.Windows.Media;

namespace Ostrov.Services;

/// <summary>
/// Цвета из подборки пользователя (2026-11-20, «умеренно»): светло-серый #E9ECEF вместо чисто белого у текстов, значков и белых кнопок,
/// графит #212529 для плиток, пластин вкладок и выключенных кнопок, полуночный синий #191970 в мягком фоне «Главной», «Проектов» и «Настроек»,
/// оранжево-красный #FB5607 только для внимания (запись диктовки, мало места на диске, «Точно?» у выключения).
/// Не меняются: коралловый Claude и оранжевый #FF9F0A таймера и секундомера («часы»).
/// </summary>
public static class Hues
{
    public static readonly Color InkColor = Color.FromRgb(0xE9, 0xEC, 0xEF);
    public static readonly Color SurfaceColor = Color.FromRgb(0x21, 0x25, 0x29);
    /// <summary>Чуть светлее графита: дорожки, подсветка при наведении, «приподнятые» плашки на графите.</summary>
    public static readonly Color SurfaceHighColor = Color.FromRgb(0x2B, 0x30, 0x35);
    public static readonly Color MidnightColor = Color.FromRgb(0x19, 0x19, 0x70);
    public static readonly Color AlertColor = Color.FromRgb(0xFB, 0x56, 0x07);

    /// <summary>Графит и графит посветлее с прозрачностью 45 % (разделы, переключатели, поля, карточки: выбор пользователя в стенде island-demo 2026-11-20, раньше 7,5 %): фон острова чуть виден сквозь них.</summary>
    public const byte GlassAlpha = 0x8C; // 55 % непрозрачности
    public static readonly Color GlassSurfaceColor = Color.FromArgb(GlassAlpha, 0x21, 0x25, 0x29);
    public static readonly Color GlassSurfaceHighColor = Color.FromArgb(GlassAlpha, 0x2B, 0x30, 0x35);

    public static readonly SolidColorBrush Ink = Frozen(InkColor);
    public static readonly SolidColorBrush Surface = Frozen(SurfaceColor);
    public static readonly SolidColorBrush SurfaceHigh = Frozen(SurfaceHighColor);
    public static readonly SolidColorBrush Alert = Frozen(AlertColor);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
