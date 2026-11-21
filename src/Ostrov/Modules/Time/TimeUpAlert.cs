using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Ostrov.Modules.Time;

/// <summary>
/// Окошко «Время вышло» для полноэкранного видео: нотч на конце таймера вырастает до размера обычного свёрнутого острова
/// (<see cref="ClockPill.ViewWidth"/> × <see cref="ClockPill.ViewHeight"/>) и плавно мигает, пока не кликнут. Без звука.
/// Слева значок часов с пустым кольцом, справа надпись, оранжевым, как весь раздел «Время».
/// </summary>
internal sealed class TimeUpAlert : Grid
{
    private readonly ClockGlyph _glyph = new(22, 2.4, 10)
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 3)
    };

    // Те же поля, что у цифр плашки таймера: значок и надпись стоят на одной линии.
    private readonly TextBlock _text = new()
    {
        Text = "Время вышло", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ClockStyle.Orange,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 4.8)
    };

    // Чип «+5» между значком и надписью: клик по этой области продлевает таймер ещё на 5 минут, остальное окошко просто закрывается.
    public const double ChipLeft = 44, ChipRight = 88;

    public TimeUpAlert()
    {
        Width = ClockPill.ViewWidth;
        Height = ClockPill.ViewHeight;
        _glyph.Set(ClockKind.Timer, 0, 0, paused: false);
        Children.Add(_glyph);
        Children.Add(new Border
        {
            Width = ChipRight - ChipLeft - 4, Height = 20, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ChipLeft + 2, 0, 0, 3),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x38, 0xFF, 0x9F, 0x0A)),
            Child = new TextBlock
            {
                Text = "+5", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ClockStyle.Orange,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        });
        Children.Add(_text);
        IsVisibleChanged += (_, _) => ApplyBlink();
    }

    // Мигание только пока окошко на экране: плавно гаснет до 35 % и возвращается (период 1,2 с).
    private void ApplyBlink()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) return;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromSeconds(0.6)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}
