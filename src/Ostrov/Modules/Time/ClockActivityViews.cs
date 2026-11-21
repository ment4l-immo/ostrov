using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ostrov.Modules.Time;

internal enum ClockKind { Timer, Stopwatch }

/// <summary>Общие значения оформления «часов»: оранжевый — третий цвет острова, только таймер и секундомер.</summary>
internal static class ClockStyle
{
    public const string TimerGlyph = "";     // Segoe Fluent Icons: Clock
    public const string StopwatchGlyph = ""; // Segoe Fluent Icons: Stopwatch

    public static readonly Brush Orange = ArcRing.DefaultArcBrush;
    public static readonly Brush Paused = ArcRing.Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
    public static readonly Brush Track = ArcRing.Frozen(Color.FromRgb(0x33, 0x33, 0x33));
}

/// <summary>
/// Иконка в кольце. Таймер: дуга убывает (доля оставшегося). Секундомер: тонкое кольцо и точка, которая идёт по кругу
/// (оборот за минуту). На паузе всё серое.
/// </summary>
internal sealed class ClockGlyph : Grid
{
    private readonly ArcRing _ring;
    private readonly OrbitRing _orbit;
    private readonly TextBlock _glyph;

    public ClockGlyph(double size, double thickness, double glyphSize)
    {
        Width = Height = size;
        _ring = new ArcRing { Width = size, Height = size, RingThickness = thickness, TrackBrush = ClockStyle.Track };
        _orbit = new OrbitRing
        {
            Width = size, Height = size, RingThickness = thickness, DotRadius = thickness * 0.95,
            TrackBrush = ClockStyle.Track, Visibility = Visibility.Collapsed
        };
        _glyph = new TextBlock
        {
            FontFamily = Ui.IconFont, FontSize = glyphSize, Foreground = ClockStyle.Orange,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        Children.Add(_ring);
        Children.Add(_orbit);
        Children.Add(_glyph);
    }

    /// <param name="timerFraction">Доля оставшегося времени (только таймер).</param>
    /// <param name="stopwatchAngle">Угол точки секундомера в градусах от верха по часовой (только секундомер).</param>
    public void Set(ClockKind kind, double timerFraction, double stopwatchAngle, bool paused)
    {
        _glyph.Text = kind == ClockKind.Timer ? ClockStyle.TimerGlyph : ClockStyle.StopwatchGlyph;
        // У секундомера кнопка сверху утяжеляет верх значка: сам циферблат лежит на ~0,8 px ниже центра кольца (замер), поднимаем.
        _glyph.RenderTransform = kind == ClockKind.Stopwatch ? new TranslateTransform(0, -0.8) : Transform.Identity;
        var brush = paused ? ClockStyle.Paused : ClockStyle.Orange;
        _glyph.Foreground = brush;
        if (kind == ClockKind.Timer)
        {
            _ring.Visibility = Visibility.Visible;
            _orbit.Visibility = Visibility.Collapsed;
            _ring.ArcBrush = brush;
            _ring.Fraction = timerFraction;
        }
        else
        {
            _ring.Visibility = Visibility.Collapsed;
            _orbit.Visibility = Visibility.Visible;
            _orbit.DotBrush = brush;
            _orbit.Angle = stopwatchAngle;
        }
    }
}

/// <summary>Плашка свёрнутого острова: слева иконка часов в кольце, справа время (как у Apple). Размер как у обычного свёрнутого острова.</summary>
internal sealed class ClockPill : Grid
{
    public const double ViewWidth = 200, ViewHeight = 36;

    // Общая линия центров значка и цифр: на 1,5 px выше геометрического центра плашки (36 px), иначе группа выглядит провисшей.
    // Нижнее поле m сдвигает центр вверх на m/2. Без поля центр цифр по замеру на 18,9 (ниже центра плашки), поэтому поле у них 4,8, у значка 3.
    private readonly ClockGlyph _glyph = new(22, 2.4, 10)
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 3)
    };

    private readonly TextBlock _time = new()
    {
        FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ClockStyle.Orange,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 4.8)
    };

    public ClockPill()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        Children.Add(_glyph);
        Children.Add(_time);
    }

    public void Set(ClockKind kind, double timerFraction, double stopwatchAngle, string text, bool paused)
    {
        _glyph.Set(kind, timerFraction, stopwatchAngle, paused);
        _time.Text = text;
        _time.Foreground = paused ? ClockStyle.Paused : ClockStyle.Orange;
    }
}

/// <summary>Кружок: та же иконка в кольце, когда часы — вторая активность рядом с основной плашкой (круг 30 px, центр на линии значков плашки).</summary>
internal sealed class ClockBubble : Grid
{
    private readonly ClockGlyph _glyph = new(20, 2.2, 9)
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    public ClockBubble()
    {
        Width = Height = Services.ActivityCenter.BubbleSize;
        Children.Add(_glyph);
    }

    public void Set(ClockKind kind, double timerFraction, double stopwatchAngle, bool paused) =>
        _glyph.Set(kind, timerFraction, stopwatchAngle, paused);
}
