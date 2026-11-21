using System.Windows;
using Ostrov.Services;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Ostrov.Modules;

/// <summary>
/// Светлая плашка выбранного пункта в переключателе-таблетке («Общие | ИИ и поиск | Программы» и подобные).
/// Не появляется на кнопке, а перекатывается: при выборе переезжает на новый пункт, у которого передний край бежит быстрее заднего
/// (плашка на ходу растягивается); при наведении на соседний пункт тянется к нему (занимает оба), а когда курсор уходит, сжимается обратно.
/// Подсветки самих кнопок при наведении нет (иначе серые прямоугольники поверх плашки).
/// </summary>
internal class ElasticBar : Border
{
    // Левый и правый край плашки анимируются отдельно: так можно двигать их с разной скоростью.
    private static readonly DependencyProperty LeftProperty = DependencyProperty.Register(
        nameof(Left), typeof(double), typeof(ElasticBar), new PropertyMetadata(0.0, (d, _) => ((ElasticBar)d).Reshape()));
    private static readonly DependencyProperty RightProperty = DependencyProperty.Register(
        nameof(Right), typeof(double), typeof(ElasticBar), new PropertyMetadata(0.0, (d, _) => ((ElasticBar)d).Reshape()));

    protected double Left { get => (double)GetValue(LeftProperty); private set => SetValue(LeftProperty, value); }
    protected double Right { get => (double)GetValue(RightProperty); private set => SetValue(RightProperty, value); }
    protected bool Placed { get; private set; }

    public ElasticBar()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
    }

    private void Reshape()
    {
        Margin = new Thickness(Left, 0, 0, 0);
        Width = Math.Max(0, Right - Left);
    }

    /// <summary>Встать на место [l, r]: сразу или плавно; передний край (в сторону движения) бежит быстрее заднего, плашка на ходу растягивается.</summary>
    public void Place(double l, double r, bool animate)
    {
        if (!animate || !Placed)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(RightProperty, null);
            Left = l;
            Right = r;
            Placed = true;
            return;
        }
        if (Math.Abs(l - Left) < 0.5 && Math.Abs(r - Right) < 0.5) return;
        var toRight = r > Right + 0.5 || (Math.Abs(r - Right) <= 0.5 && l > Left);
        Edge(LeftProperty, l, toRight ? 300 : 180);
        Edge(RightProperty, r, toRight ? 180 : 300);
    }

    private void Edge(DependencyProperty property, double to, int ms) =>
        BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
}

internal sealed class PillSlider : ElasticBar
{
    private static readonly Brush Fill = Frozen(Color.FromArgb(Hues.GlassAlpha, 0x40, 0x40, 0x40));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly List<Button> _buttons;
    private readonly Grid _grid;
    private int _selected = -1, _hover = -1;

    private PillSlider(Grid grid, List<Button> buttons)
    {
        _grid = grid;
        _buttons = buttons;
        Background = Fill;
        Opacity = 0;
    }

    /// <summary>Подключить плашку к переключателю: контейнер с таблетками, внутри StackPanel с кнопками (<see cref="Ui.Pill"/>).</summary>
    public static PillSlider Attach(Border container)
    {
        var panel = (Panel)container.Child;
        var buttons = panel.Children.OfType<Button>().ToList();
        container.Child = null; // панель нельзя держать в двух местах сразу
        var grid = new Grid();
        var slider = new PillSlider(grid, buttons);
        grid.Children.Add(slider);
        grid.Children.Add(panel);
        container.Child = grid;
        slider.CornerRadius = new CornerRadius((buttons.FirstOrDefault()?.Height is { } h and > 0 ? h : 26) / 2);
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = buttons[i];
            b.Background = Brushes.Transparent;
            b.Resources["PillSlider"] = slider;
            b.Resources["NoHover"] = true;
            b.MouseEnter += (_, _) => { slider._hover = index; slider.Apply(true); };
            b.MouseLeave += (_, _) =>
            {
                if (slider._hover != index) return;
                slider._hover = -1;
                // Курсор мог просто перейти на соседнюю кнопку: решаем после того, как придёт её MouseEnter.
                slider.Dispatcher.BeginInvoke(() => slider.Apply(true), DispatcherPriority.Input);
            };
        }
        // Пересчёт при смене размера или повторном подключении к дереву не обрывает идущую анимацию: уже стоящая плашка переезжает плавно
        // (если место не изменилось, ничего не происходит); мгновенно встаёт только в первый раз.
        panel.SizeChanged += (_, _) => slider.Apply(slider.Placed);
        grid.Loaded += (_, _) => slider.Apply(slider.Placed);
        return slider;
    }

    /// <summary>Выбрать пункт (вызывается из <see cref="Ui.MarkSelected"/>).</summary>
    public void Select(Button button)
    {
        var index = _buttons.IndexOf(button);
        if (index < 0 || index == _selected) return;
        _selected = index;
        _hover = -1;
        Apply(Placed);
    }

    private (double L, double R)? Span(int index)
    {
        var b = _buttons[index];
        if (b.ActualWidth <= 0) return null;
        // Положение по раскладке (смещение внутри родителя), а не TranslatePoint: тот учитывает сжатие кнопки при нажатии
        // (клик приходится на момент сжатия до 96 %) и падает, если кнопка сейчас вне дерева (вкладку убрали при листании).
        if (VisualTreeHelper.GetParent(b) is not Visual parent || VisualTreeHelper.GetParent(parent) != _grid) return null;
        var x = VisualTreeHelper.GetOffset(b).X + VisualTreeHelper.GetOffset(parent).X;
        return (x, x + b.ActualWidth);
    }

    private void Apply(bool animate)
    {
        if (_selected < 0) return;
        if (Span(_selected) is not { } s) return; // ещё не разложено: придёт SizeChanged или Loaded
        var (l, r) = s;
        if (_hover >= 0 && _hover != _selected && Span(_hover) is { } h)
        {
            l = Math.Min(l, h.L);
            r = Math.Max(r, h.R);
        }
        if (_buttons[_selected].ActualHeight > 0) Height = _buttons[_selected].ActualHeight;
        Opacity = 1;

        Place(l, r, animate);
    }
}
