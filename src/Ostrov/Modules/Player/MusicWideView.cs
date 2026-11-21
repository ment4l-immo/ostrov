using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Player;

/// <summary>
/// Расширенная плашка музыки (правый клик, М-017): обложка покрупнее, название и исполнитель (на обычной плашке их нет, там обложка и волна)
/// и та же волна по настоящим частотам. Данные те же, что у плеера, ничего нового не запрашивается.
/// </summary>
internal sealed class MusicWideView : Grid
{
    public const double ViewWidth = 340, ViewHeight = 72;

    private readonly Border _cover = new()
    {
        Width = 48, Height = 48, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Hues.SurfaceColor),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0)
    };
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 12, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };

    public MusicWideView()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) }); // обложка с отступами
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        SetColumn(_cover, 0);
        Children.Add(_cover);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Children = { _title, _artist } };
        SetColumn(texts, 1);
        Children.Add(texts);
        var wave = new WaveBars(5, 3, 3, 3, 24)
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 22, 0)
        };
        SetColumn(wave, 2);
        Children.Add(wave);
    }

    public void SetCover(ImageSource? image) =>
        _cover.Background = image == null
            ? new SolidColorBrush(Hues.SurfaceColor)
            : new ImageBrush(image) { Stretch = Stretch.UniformToFill };

    public void SetTrack(string title, string artist)
    {
        _title.Text = title;
        _artist.Text = artist;
        _artist.Visibility = artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
