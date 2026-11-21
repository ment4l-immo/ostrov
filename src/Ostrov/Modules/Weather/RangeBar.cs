using System.Windows;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Weather;

/// <summary>
/// Вертикальная шкала температур дня на фоне всей недели: серая дорожка и белый отрезок от максимума до минимума.
/// High и Low — положение концов отрезка сверху вниз (0 — самая тёплая точка недели, 1 — самая холодная).
/// </summary>
internal sealed class RangeBar : FrameworkElement
{
    public static readonly DependencyProperty HighProperty = DependencyProperty.Register(
        nameof(High), typeof(double), typeof(RangeBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowProperty = DependencyProperty.Register(
        nameof(Low), typeof(double), typeof(RangeBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double High { get => (double)GetValue(HighProperty); set => SetValue(HighProperty, value); }
    public double Low { get => (double)GetValue(LowProperty); set => SetValue(LowProperty, value); }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x33, 0x33, 0x33));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 6) return;

        const double bar = 5;
        var x = (w - bar) / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(x, 0, bar, h), bar / 2, bar / 2);

        var top = Math.Clamp(High, 0, 1) * h;
        var bottom = Math.Clamp(Low, 0, 1) * h;
        if (bottom - top < bar) // совсем маленький перепад — всё равно рисуем заметную точку-отрезок
        {
            var mid = (top + bottom) / 2;
            top = Math.Clamp(mid - bar / 2, 0, h - bar);
            bottom = top + bar;
        }
        dc.DrawRoundedRectangle(Hues.Ink, null, new Rect(x, top, bar, bottom - top), bar / 2, bar / 2);
    }
}
