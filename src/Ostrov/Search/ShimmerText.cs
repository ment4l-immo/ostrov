using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ostrov.Search;

/// <summary>
/// Надпись «Думаю…» с бегущим бликом (как «Thinking» в веб-чатах): серый текст, по которому раз в пару секунд проходит светлая полоса.
/// Блик — градиентная кисть шрифта с анимированным сдвигом; других таймеров нет, анимация останавливается, когда надпись убрали из окна.
/// </summary>
internal sealed class ShimmerText : TextBlock
{
    private static readonly Color Base = Color.FromRgb(0x8A, 0x91, 0x99), Glint = Color.FromRgb(0xF2, 0xF2, 0xF2);
    private const double From = -1.4, To = 1.4;

    private readonly TranslateTransform _shift = new(From, 0);

    public ShimmerText(string text)
    {
        Text = text;
        FontSize = 13;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = GradientSpreadMethod.Pad, RelativeTransform = _shift,
            GradientStops =
            {
                new GradientStop(Base, 0), new GradientStop(Base, 0.32), new GradientStop(Glint, 0.5),
                new GradientStop(Base, 0.68), new GradientStop(Base, 1)
            }
        };
        Foreground = brush;
        Start();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _shift.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void Start() =>
        _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(From, To, new Duration(TimeSpan.FromSeconds(2.0)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
}
