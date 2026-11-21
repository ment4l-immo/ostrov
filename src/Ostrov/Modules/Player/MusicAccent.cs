using System.Windows.Media;
using Ostrov.Backdrop;

namespace Ostrov.Modules.Player;

/// <summary>
/// Акцентный цвет обложки играющей песни для волны (решение пользователя 2026-11-14): один цвет, самый выразительный из палитры обложки,
/// не слишком тёмный (иначе не виден на чёрном), показывается на 50 % непрозрачности. Нет обложки или она серая: белый на 50 %.
/// </summary>
internal static class MusicAccent
{
    public const byte Alpha = 0x80; // 50 %

    public static Color Current { get; private set; } = Color.FromArgb(Alpha, 0xFF, 0xFF, 0xFF);

    public static event Action? Changed;

    public static void Set(ImageSource? image)
    {
        var next = Pick(image);
        if (next == Current) return;
        Current = next;
        Changed?.Invoke();
    }

    private static Color Pick(ImageSource? image)
    {
        var palette = CoverPalette.FromImage(image);
        if (palette == null || palette.Length == 0) return Color.FromArgb(Alpha, 0xFF, 0xFF, 0xFF);
        // Самый выразительный: насыщенный и не крайне светлый и не крайне тёмный.
        var best = palette[0];
        var bestScore = -1.0;
        foreach (var c in palette)
        {
            var (_, s, l) = ColorMath.ToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
            var score = s * (1 - Math.Abs(2 * l - 1));
            if (score > bestScore) { bestScore = score; best = c; }
        }
        var (h, sat, light) = ColorMath.ToHsl(best.R / 255.0, best.G / 255.0, best.B / 255.0);
        light = Math.Clamp(light, 0.6, 0.78); // на чёрном при 50 % непрозрачности темнее 0,6 почти не видно
        var (r, g, b) = ColorMath.FromHsl(h, sat, light);
        return Color.FromArgb(Alpha, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}
