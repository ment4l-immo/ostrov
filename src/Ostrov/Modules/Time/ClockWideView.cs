using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Ostrov.Modules.Time;

/// <summary>
/// Расширенная плашка таймера или секундомера (правый клик, М-017): те же данные, что на обычной, но крупнее и с подписью.
/// Слева кольцо со значком (44 px), справа время (26 px) и под ним подпись: у таймера «Таймер · 25 мин» («На паузе · 25 мин»),
/// у секундомера «Секундомер», «Круг 3 · 0:23» или «Остановлен».
/// </summary>
internal sealed class ClockWideView : Grid
{
    public const double ViewWidth = 280, ViewHeight = 76;

    private readonly ClockGlyph _glyph = new(44, 4.4, 18)
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 0, 0)
    };

    private readonly TextBlock _time = new()
    {
        FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = ClockStyle.Orange, HorizontalAlignment = HorizontalAlignment.Right
    };

    private readonly TextBlock _label = new()
    {
        FontSize = 12, Foreground = Ui.Gray, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 0)
    };

    public ClockWideView()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        Children.Add(_glyph);
        Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 22, 0),
            Children = { _time, _label }
        });
    }

    public void Set(ClockKind kind, double timerFraction, double stopwatchAngle, string time, string label, bool paused)
    {
        _glyph.Set(kind, timerFraction, stopwatchAngle, paused);
        _time.Text = time;
        _time.Foreground = paused ? ClockStyle.Paused : ClockStyle.Orange;
        _label.Text = label;
    }
}
