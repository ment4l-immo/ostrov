using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ostrov.Modules.Player;

/// <summary>
/// Серый блок «о треке» под названием и исполнителем в режиме текста: альбом, «год · жанр», «Трек 3 из 12».
/// Пустые строки скрыты; когда данные приходят из сети, новые строки мягко проявляются.
/// </summary>
internal sealed class TrackInfoBlock : StackPanel
{
    public static readonly Color Ink = Color.FromRgb(0x82, 0x82, 0x82);

    private readonly TextBlock _album = Line(maxHeight: 34), _meta = Line(maxHeight: 34), _track = Line(maxHeight: 17);

    public TrackInfoBlock()
    {
        Children.Add(_album);
        Children.Add(_meta);
        Children.Add(_track);
        Show(TrackInfo.Empty);
    }

    /// <summary>Подписи блока (для «умных чернил»: на светлом фоне они темнеют).</summary>
    public IReadOnlyList<TextBlock> Texts => [_album, _meta, _track];

    public void Show(TrackInfo info)
    {
        var meta = string.Join(" · ", new[] { info.Year?.ToString(), info.Genre }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var track = info.TrackNumber is { } n ? (info.TrackCount is { } c ? $"Трек {n} из {c}" : $"Трек {n}") : "";
        Set(_album, info.Album ?? "");
        Set(_meta, meta);
        Set(_track, track);
    }

    private static TextBlock Line(double maxHeight) => new()
    {
        FontSize = 12, Foreground = new SolidColorBrush(Ink), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = maxHeight, Margin = new Thickness(0, 0, 0, 4)
    };

    private static void Set(TextBlock block, string text)
    {
        if (block.Text == text) return;
        var appears = block.Text.Length == 0 && text.Length > 0;
        block.Text = text;
        block.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (appears)
        {
            // Данные пришли позже названия: строка проявляется, а не выскакивает.
            block.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            block.BeginAnimation(OpacityProperty, null);
            block.Opacity = 1;
        }
    }
}
