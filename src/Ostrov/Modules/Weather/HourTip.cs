using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Weather;

/// <summary>
/// Карточка часа при наведении на график: небольшой скруглённый прямоугольник-таблица. Слева иконка погоды на это время, дальше
/// столбцы «Время · Температура · Осадки · Ветер»: сверху название (мелко, светлее), снизу значение. Столбцы разделены тонкими
/// линиями, которые не доходят до краёв карточки (отступ 15 % высоты сверху и снизу). Фон — цвета этой погоды (те же палитры,
/// что у фона вкладки, но темнее, чтобы белый текст читался), без дождя и снега.
/// </summary>
internal sealed class HourTip : Border
{
    public const double CardHeight = 48;
    private static readonly Brush LabelInk = Frozen(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LineInk = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush EdgeInk = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly WeatherIcon _icon = new() { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 8, 0) };
    private readonly TextBlock _time = Value(), _temp = Value(), _rain = Value(), _wind = Value();

    public HourTip()
    {
        Height = CardHeight;
        CornerRadius = new CornerRadius(13);
        BorderBrush = EdgeInk;
        BorderThickness = new Thickness(1);
        IsHitTestVisible = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(Separator());
        row.Children.Add(Cell("Время", _time));
        row.Children.Add(Separator());
        row.Children.Add(Cell("Температура", _temp));
        row.Children.Add(Separator());
        row.Children.Add(Cell("Осадки", _rain));
        row.Children.Add(Separator());
        row.Children.Add(Cell("Ветер", _wind));
        Child = row;
    }

    private static TextBlock Value() => new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, Margin = new Thickness(0, 1, 0, 0) };

    private static FrameworkElement Cell(string label, TextBlock value) => new StackPanel
    {
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 11, 0),
        Children = { new TextBlock { Text = label, FontSize = 10, Foreground = LabelInk }, value }
    };

    // Линия 1 px, не доходит до краёв: 15 % высоты карточки сверху и снизу.
    private static FrameworkElement Separator()
    {
        var gap = Math.Round(CardHeight * 0.15);
        return new Border { Width = 1, Background = LineInk, Margin = new Thickness(0, gap, 0, gap) };
    }

    public void Show(HourPoint p)
    {
        var kind = WeatherCodes.Describe(p.Code).Kind;
        _icon.Kind = kind;
        _icon.IsDay = p.IsDay;
        _time.Text = $"{p.Time:HH}:00";
        _temp.Text = WeatherFormat.Temp(p.Temp);
        _rain.Text = $"{p.PrecipProb}%";
        _wind.Text = $"{p.Wind:0} м/с";

        // Диагональный градиент из (уже приглушённых) цветов погоды, чуть темнее: белый текст читается на любой погоде.
        var palette = WeatherSummary.Palette(kind, p.IsDay);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Dark(palette[0]), 0));
        brush.GradientStops.Add(new GradientStop(Dark(palette[1]), 0.5));
        brush.GradientStops.Add(new GradientStop(Dark(palette[3]), 1));
        brush.Freeze();
        Background = brush;
    }

    private static Color Dark(Color c) => Color.FromRgb((byte)(c.R * 0.80), (byte)(c.G * 0.80), (byte)(c.B * 0.80));
}
