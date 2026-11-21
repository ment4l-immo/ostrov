using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Ostrov.Modules;

/// <summary>
/// Мягкое свечение под курсором в строках списков («Проекты», «Буфер», «Новости»; М-019, решение пользователя: «мягкое, без движения, еле заметное»).
/// Вместо ровной заливки строка получает радиальный белый градиент: самое светлое место под курсором по горизонтали, к краям строки
/// заливка слабеет, но не пропадает совсем (строка по-прежнему читается как «выбранная»). Само свечение никуда не плывёт и не живёт во времени:
/// оно просто идёт за курсором, пока тот над строкой. Кисть своя у каждой строки, поэтому её центр можно двигать.
/// </summary>
internal sealed class HoverGlow
{
    // Белый: в центре ≈ 11 % непрозрачности, на краях ≈ 4 % (раньше ровные 15 %: «еле заметное» значит слабее и мягче).
    private const byte CenterAlpha = 0x1C, EdgeAlpha = 0x0A;

    private readonly RadialGradientBrush _brush = new()
    {
        Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.55, RadiusY = 1.4,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(CenterAlpha, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb((byte)((CenterAlpha + EdgeAlpha) / 2), 0xFF, 0xFF, 0xFF), 0.5),
            new GradientStop(Color.FromArgb(EdgeAlpha, 0xFF, 0xFF, 0xFF), 1)
        }
    };

    /// <summary>Кисть для фона строки в подсвеченном состоянии.</summary>
    public Brush Brush => _brush;

    /// <summary>Поставить центр свечения под курсор (по горизонтали); вертикально свечение по центру строки.</summary>
    public void Follow(FrameworkElement row, MouseEventArgs e)
    {
        var width = row.ActualWidth;
        if (width < 1) return;
        var x = Math.Clamp(e.GetPosition(row).X / width, 0, 1);
        var center = new Point(x, 0.5);
        _brush.Center = center;
        _brush.GradientOrigin = center;
    }

    /// <summary>Вернуть центр на середину строки (подсвечена без мыши, например стрелками на клавиатуре).</summary>
    public void Reset()
    {
        _brush.Center = _brush.GradientOrigin = new Point(0.5, 0.5);
    }
}
