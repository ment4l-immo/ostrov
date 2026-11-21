using System.Windows;
using System.Windows.Media;

namespace Ostrov.Backdrop;

/// <summary>Что падает на фоне вкладки «Погода».</summary>
internal enum PrecipKind { None, Rain, Snow }

/// <summary>Цвета краёв цветного фона: из них складывается цвет обводки острова (слева, снизу по центру, справа). Amount: 0 — обводка белая, 1 — полностью цветная.</summary>
internal readonly record struct EdgeTint(Color Left, Color Bottom, Color Right, double Amount);

/// <summary>
/// Связь вкладки «Плеер» с цветным фоном острова: палитра обложки, «вкладка открыта», кадры, цвета краёв для обводки
/// и яркость фона под элементом (чтобы текст там, где фон светлый, плавно темнел). Фон рисует <see cref="CoverGlow"/> в окне острова.
/// </summary>
internal static class IslandBackdrop
{
    public static Color[]? Palette { get; private set; }
    public static bool Active { get; private set; }
    public static PrecipKind Precip { get; private set; }
    public static double PrecipIntensity { get; private set; }
    public static CoverGlow? Surface { get; set; }

    /// <summary>Сила (множитель непрозрачности) и размах (множитель размера пятен) фона владельца: у плеера и погоды 1, у остальных вкладок тише и теснее.</summary>
    public static double Intensity { get; private set; } = 1;
    public static double Spread { get; private set; } = 1;
    public static EdgeTint Tint { get; private set; } = new(Colors.White, Colors.White, Colors.White, 0);

    /// <summary>Изменилась палитра, активность вкладки или карточка поверх вкладок.</summary>
    public static event Action? StateChanged;

    /// <summary>Кадр анимации фона (секунды с прошлого кадра). Идёт, только пока фон виден.</summary>
    public static event Action<double>? Frame;

    public static event Action<EdgeTint>? TintChanged;

    // Фон один на весь остров, им пользуются вкладки «Плеер» (цвета обложки) и «Погода» (цвета погоды). Чья вкладка открыта сейчас,
    // тот и владелец: чужие вызовы (например, плеер сменил обложку, пока открыта погода) фон не трогают.
    private static object? _owner;

    /// <summary>Вкладка открылась и берёт фон себе с этой палитрой (цвета перетекают плавно). Осадки на фоне при этом сброшены.</summary>
    public static void Claim(object owner, Color[]? palette, double intensity = 1, double spread = 1)
    {
        _owner = owner;
        Palette = palette;
        Intensity = intensity;
        Spread = spread;
        Active = true;
        Precip = PrecipKind.None;
        PrecipIntensity = 0;
        StateChanged?.Invoke();
    }

    /// <summary>Едва заметные дождь или снег на фоне (вкладка «Погода»). Интенсивность 0..1 — сколько капель или снежинок.</summary>
    public static void SetPrecip(object owner, PrecipKind kind, double intensity)
    {
        if (_owner != owner) return;
        Precip = kind;
        PrecipIntensity = kind == PrecipKind.None ? 0 : Math.Clamp(intensity, 0, 1);
        StateChanged?.Invoke();
    }

    /// <summary>Новая палитра от владельца (смена обложки или погоды).</summary>
    public static void SetPalette(object owner, Color[]? palette)
    {
        if (_owner != owner) return;
        Palette = palette;
        StateChanged?.Invoke();
    }

    /// <summary>Вкладка закрылась: фон плавно уходит, если он всё ещё её.</summary>
    public static void Release(object owner)
    {
        if (_owner != owner) return;
        _owner = null;
        Active = false;
        StateChanged?.Invoke();
    }

    public static void NotifyChanged() => StateChanged?.Invoke();

    public static void RaiseFrame(double dt) => Frame?.Invoke(dt);

    public static void PublishTint(EdgeTint tint)
    {
        Tint = tint;
        TintChanged?.Invoke(tint);
    }

    /// <summary>Средняя яркость фона (0..1) под элементом; 0, если фона нет.</summary>
    public static double LuminanceUnder(FrameworkElement element) => Surface?.LuminanceUnder(element) ?? 0;
}
