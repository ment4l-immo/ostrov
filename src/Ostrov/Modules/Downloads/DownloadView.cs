using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Downloads;

/// <summary>Размер и скорость по-русски: «850 КБ», «12,4 МБ», «1,2 ГБ», «5,2 МБ/с».</summary>
internal static class ByteFormat
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Size(long bytes)
    {
        double b = Math.Max(0, bytes);
        if (b < 1024 * 1024) return $"{Math.Max(1, Math.Round(b / 1024)).ToString("0", Ru)} КБ";
        if (b < 1024L * 1024 * 1024) return $"{(b / (1024 * 1024)).ToString(b < 100 * 1024 * 1024 ? "0.0" : "0", Ru)} МБ";
        return $"{(b / (1024.0 * 1024 * 1024)).ToString("0.0", Ru)} ГБ";
    }

    public static string Speed(double bytesPerSecond) => Size((long)bytesPerSecond) + "/с";
}

/// <summary>
/// Загрузка браузера на острове (М-021): плашка 288×56 (<c>wide: false</c>) или расширенный вид по правому клику (<c>wide: true</c>, «то же, но полнее»).
/// Идёт загрузка: «Загрузка · скорость», сколько скачано и бегущая белая полоска (процентов нет: общий размер снаружи браузера неизвестен).
/// Закончилась: белая галочка, «Загрузка завершена», имя файла и размер. Только белый и серый.
/// </summary>
internal sealed class DownloadView : Grid
{
    public const double PillWidth = 288, PillHeight = 56, WideWidth = 360, WideRunningHeight = 88, WideDoneHeight = 72;

    private readonly bool _wide;
    private readonly double _side;

    private readonly StackPanel _running;
    private readonly TextBlock _label, _size, _speed;
    private readonly Grid _done = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _title, _detail;

    public DownloadView(bool wide)
    {
        _wide = wide;
        _side = wide ? 20 : 18;
        Width = wide ? WideWidth : PillWidth;
        Height = wide ? WideRunningHeight : PillHeight;

        // Идёт загрузка: подпись слева, скачано справа, (в расширенном виде строка со скоростью), под ними бегущая полоска.
        _running = new StackPanel { Margin = new Thickness(_side, 0, _side, 0), VerticalAlignment = VerticalAlignment.Center };
        _label = new TextBlock { FontSize = wide ? 12 : 11, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 90, 0) };
        _size = new TextBlock { FontSize = wide ? 16 : 12, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new Grid { Children = { _label, _size } };
        _running.Children.Add(header);
        _speed = new TextBlock { FontSize = 14, Foreground = Hues.Ink, Margin = new Thickness(0, 1, 0, 0), Visibility = wide ? Visibility.Visible : Visibility.Collapsed };
        _running.Children.Add(_speed);
        _running.Children.Add(new RunningBar(Hues.Ink, Width - 2 * _side) { Margin = new Thickness(0, wide ? 10 : 9, 0, 0) });
        Children.Add(_running);

        // Готово: белый кружок с галочкой и два текста.
        _done.Margin = new Thickness(_side, 0, _side, wide ? 0 : 4);
        _done.VerticalAlignment = VerticalAlignment.Center;
        _done.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _done.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circleSize = wide ? 36 : 30;
        var circle = new Border
        {
            Width = circleSize, Height = circleSize, CornerRadius = new CornerRadius(circleSize / 2.0), Background = Hues.Ink,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, wide ? 14 : 12, 0),
            Child = new TextBlock
            {
                Text = "", FontFamily = Ui.IconFont, FontSize = wide ? 17 : 15, Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        SetColumn(circle, 0);
        _done.Children.Add(circle);
        _title = new TextBlock { Text = "Загрузка завершена", FontSize = wide ? 16 : 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
        _detail = new TextBlock { FontSize = wide ? 12 : 11, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, wide ? 1 : 0, 0, 0) };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail } };
        SetColumn(texts, 1);
        _done.Children.Add(texts);
        Children.Add(_done);
    }

    /// <summary>Высота вида для текущего состояния (у расширенного разная при загрузке и при «готово»).</summary>
    public double PreferredHeight => !_wide ? PillHeight : _done.Visibility == Visibility.Visible ? WideDoneHeight : WideRunningHeight;

    /// <param name="count">Сколько файлов качается сейчас.</param>
    public void ShowRunning(int count, string speed, string size)
    {
        _running.Visibility = Visibility.Visible;
        _done.Visibility = Visibility.Collapsed;
        var title = count > 1 ? $"Загрузки ×{count}" : "Загрузка";
        _label.Text = _wide ? title : $"{title} · {speed}";
        _speed.Text = speed;
        _size.Text = size;
        if (_wide) Height = WideRunningHeight;
    }

    /// <param name="name">Имя скачанного файла.</param>
    public void ShowDone(string name, string size)
    {
        _running.Visibility = Visibility.Collapsed;
        _done.Visibility = Visibility.Visible;
        _detail.Text = size.Length > 0 ? $"{name} · {size}" : name;
        if (_wide) Height = WideDoneHeight;
    }
}
