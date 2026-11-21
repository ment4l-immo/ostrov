using System.Windows;
using System.Windows.Media;

namespace Ostrov.Backdrop;

/// <summary>
/// Заливка острова с «затуханием» просвета к боковым краям: в центре прозрачность, выбранная в настройках, а от самого края (≈ 10 % просвета,
/// то есть 90 % непрозрачности) она плавно уходит к центру по широкой кривой. Размытие под островом (<see cref="BlurBehind"/>) идёт от края до края,
/// но его сила системой не регулируется: видимость размытого за плашкой задаёт только эта заливка, поэтому у краёв его почти не видно, к середине больше.
/// Общая для острова и для образцов в настройках.
/// </summary>
internal static class GlassBrush
{
    public const byte EdgeAlpha = 0xCF; // 81 %: плотность у самого края (выбор пользователя в стенде island-demo; раньше 90 %)

    public static Brush Create(byte centerAlpha)
    {
        var edge = Math.Max(centerAlpha, EdgeAlpha);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        // Плавная кривая от края к центру (без ступеньки): несколько промежуточных точек.
        (double Offset, double Mix)[] left = [(0, 0), (0.08, 0.15), (0.18, 0.45), (0.30, 0.8), (0.42, 1)];
        foreach (var (offset, mix) in left) brush.GradientStops.Add(new GradientStop(Alpha(edge, centerAlpha, mix), offset));
        for (var i = left.Length - 1; i >= 0; i--) brush.GradientStops.Add(new GradientStop(Alpha(edge, centerAlpha, left[i].Mix), 1 - left[i].Offset));
        brush.Freeze();
        return brush;
    }

    private static Color Alpha(byte edge, byte center, double mix) => Color.FromArgb((byte)Math.Round(edge + (center - edge) * mix), 0, 0, 0);
}
