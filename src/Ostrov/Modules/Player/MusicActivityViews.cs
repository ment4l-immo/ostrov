using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Player;

/// <summary>Плашка свёрнутого острова с музыкой: слева мини-обложка, справа волна по частотам (как у Apple). Размер как у обычного свёрнутого острова.</summary>
internal sealed class MusicPill : Grid
{
    public const double ViewWidth = 200, ViewHeight = 36;

    // Волна стоит на линии центров значков таймера (1,5 px выше центра плашки); обложка по просьбе пользователя ещё на 1 px выше
    // (нижнее поле 5 вместо 3 поднимает центр на 1 px). Волна поменьше и потоньше: столбики 2 px (зазор 2,5), высота до 14 px вместо 3 и 18.
    private readonly Border _cover = new()
    {
        Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Hues.SurfaceColor),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 5)
    };

    public MusicPill()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Children.Add(_cover);
        Children.Add(new WaveBars(5, 2, 2.5, 2, 14)
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 3)
        });
    }

    public void SetCover(ImageSource? image) =>
        _cover.Background = image == null
            ? new SolidColorBrush(Hues.SurfaceColor)
            : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
}

/// <summary>Кружок музыки (когда она вторая активность): круглая обложка, поверх тёмная вуаль и три столбика волны.</summary>
internal sealed class MusicBubble : Grid
{
    private readonly Border _cover = new()
    {
        Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Hues.SurfaceColor),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    public MusicBubble()
    {
        Width = Height = Services.ActivityCenter.BubbleSize;
        Children.Add(_cover);
        Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        });
        Children.Add(new WaveBars(3, 1.6, 2, 1.6, 10)
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
    }

    public void SetCover(ImageSource? image) =>
        _cover.Background = image == null
            ? new SolidColorBrush(Hues.SurfaceColor)
            : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
}
