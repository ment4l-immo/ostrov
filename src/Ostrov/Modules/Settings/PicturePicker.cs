using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Settings;

/// <summary>
/// Выбор картинками вместо текстового переключателя (просьба пользователя 2026-11-20: меньше текста в настройках): слева название и подсказка,
/// справа ряд образцов с подписью под каждым, выбранный в светлой рамке. Сами картинки рисует <see cref="SettingsPictures"/>.
/// </summary>
internal sealed class PicturePicker : Grid
{
    private readonly Border[] _frames;
    private readonly TextBlock[] _labels;

    public event Action<int>? Changed;

    public PicturePicker(string title, string hint, string[] labels, Func<int, FrameworkElement> makePreview, double tileWidth = 66, double tileHeight = 44)
    {
        Height = tileHeight + 40;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 12, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Hues.Ink });
        texts.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Ui.Gray, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Children.Add(texts);

        _frames = new Border[labels.Length];
        _labels = new TextBlock[labels.Length];
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        SetColumn(row, 1);
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var scene = makePreview(i);
            scene.Width = tileWidth;
            scene.Height = tileHeight;
            var frame = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Padding = new Thickness(1.5), Child = scene };
            _frames[i] = frame;
            var label = new TextBlock { Text = labels[i], FontSize = 11, Foreground = Ui.Gray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            _labels[i] = label;
            var item = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, Children = { frame, label } };
            item.MouseLeftButtonUp += (_, _) => { Select(index); Changed?.Invoke(index); };
            row.Children.Add(item);
        }
        Children.Add(row);
    }

    public void Select(int index)
    {
        for (var i = 0; i < _frames.Length; i++)
        {
            var on = i == index;
            _frames[i].BorderBrush = on ? Hues.Ink : Brushes.Transparent;
            _labels[i].Foreground = on ? Hues.Ink : Ui.Gray;
        }
    }
}
