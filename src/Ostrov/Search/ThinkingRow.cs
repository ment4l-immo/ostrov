using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Ostrov.Search;

/// <summary>
/// Строка раздумий в ответе: шар слева, справа подпись («Думаю…», «Ищу в интернете…»), по вертикали они центрированы друг относительно друга.
/// Когда ИИ начинает отвечать, строка плавно гаснет и схлопывается по высоте (текст ответа поднимается на её место), потом сообщает <see cref="Finished"/>.
/// </summary>
internal sealed class ThinkingRow : Grid
{
    /// <summary>Высота строки: шар 32 px и по 1 px воздуха (вместе с вопросом помещается в минимальную высоту области ответа).</summary>
    public const double RowHeight = 34;

    /// <summary>Отступ шара слева: свечение вокруг него не должно упираться в край области ответа (она обрезает всё, что за краем).</summary>
    private const double OrbIndent = 6;

    private readonly ThinkingOrb _orb = new();
    private readonly ShimmerText _label;

    /// <summary>Строка погасла и схлопнулась: её можно убрать из документа.</summary>
    public event Action? Finished;

    public bool IsFading { get; private set; }

    public ThinkingRow(string text)
    {
        Height = RowHeight;
        HorizontalAlignment = HorizontalAlignment.Left;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _orb.Margin = new Thickness(OrbIndent, 0, 0, 0);
        _label = new ShimmerText(text) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 4) }; // 4 снизу поднимают надпись на 2 px: центр по буквам, а не по строке с «хвостом» у «у»
        SetColumn(_orb, 0);
        SetColumn(_label, 1);
        Children.Add(_orb);
        Children.Add(_label);
    }

    public void SetLabel(string text)
    {
        if (_label.Text != text) _label.Text = text;
    }

    /// <summary>Сколько строка ещё занимает по высоте, пока схлопывается (иначе 0): остров не должен подстраиваться под место, которое вот-вот освободится.</summary>
    public double CollapsingHeight => IsFading ? Height : 0;

    public void BeginFadeOut()
    {
        if (IsFading) return;
        IsFading = true;

        _orb.BeginFadeOut();
        // Шар гаснет быстро (180 мс) и почти невидим к тому, как строка начинает схлопываться (170 мс): иначе её нижний край резал бы ещё яркий шар пополам.
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        var collapse = new DoubleAnimation(RowHeight, 0, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = TimeSpan.FromMilliseconds(170),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        collapse.Completed += (_, _) => Finished?.Invoke();
        BeginAnimation(HeightProperty, collapse);
    }
}
