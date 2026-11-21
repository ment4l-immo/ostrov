using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Services;

namespace Ostrov.Modules;

/// <summary>
/// Содержимое «нотча» — сильно вжатого по высоте острова, который висит у верхнего края во время полноэкранного видео
/// (<see cref="ActivityCenter.CompactWidth"/> × <see cref="ActivityCenter.CompactHeight"/>). Чёрную плашку со скруглением снизу рисует сам остров,
/// здесь только полоска внутри неё, в цвете своей активности (оранжевая у таймера и секундомера, белая у рендера After Effects).
/// Виды: убывающая полоска (таймер), бегущий отрезок (секундомер, рендер), стоящий серым отрезок (пауза),
/// сплошная полоска, которая мигает (конец таймера, ошибка рендера) или горит ровно (рендер готов).
/// </summary>
internal sealed class CompactStrip : Grid
{
    private const double SideMargin = 16, BarHeight = 4, RunWidth = 44;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush TrackBrush = Frozen(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush PausedBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));

    private enum Mode { Fraction, Running, Still, Solid, Dots }

    private readonly Brush _accent;
    private readonly Border _fill;
    private readonly Border _track;
    private readonly Canvas _dots = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private const int DotCount = 9;
    private const double DotSize = 4;
    private readonly TranslateTransform _slide = new();
    private Mode _mode = Mode.Fraction;
    private bool _paused;
    private double _blinkSeconds; // 0 — не мигает

    private static double TrackWidth => ActivityCenter.CompactWidth - 2 * SideMargin;

    public CompactStrip(Brush accent)
    {
        _accent = accent;
        Width = ActivityCenter.CompactWidth;
        Height = ActivityCenter.CompactHeight;
        _fill = new Border
        {
            Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2), HorizontalAlignment = HorizontalAlignment.Left,
            Background = accent, RenderTransform = _slide, Width = 0
        };
        _track = new Border
        {
            Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2), Background = TrackBrush, ClipToBounds = true,
            Margin = new Thickness(SideMargin, 0, SideMargin, 0), VerticalAlignment = VerticalAlignment.Center, Child = _fill
        };
        Children.Add(_track);
        // Точки (диктовка): ряд кружков, по которым бежит волна яркости. Не полоска, чтобы не путать с рендером и Claude.
        _dots.Width = TrackWidth; _dots.Height = DotSize;
        _dots.Margin = new Thickness(SideMargin, 0, SideMargin, 0);
        _dots.VerticalAlignment = VerticalAlignment.Center;
        _dots.HorizontalAlignment = HorizontalAlignment.Left;
        var gap = (TrackWidth - DotSize) / (DotCount - 1);
        for (var i = 0; i < DotCount; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = DotSize, Height = DotSize, Fill = accent, Opacity = 0.25 };
            Canvas.SetLeft(dot, i * gap);
            _dots.Children.Add(dot);
        }
        Children.Add(_dots);
        // Анимации идут только пока нотч на экране.
        IsVisibleChanged += (_, _) => ApplyAnimation();
    }

    /// <summary>Убывающая (или растущая) полоска: доля 0..1 от длины. Пауза — серым.</summary>
    public void ShowFraction(double fraction, bool paused)
    {
        var wasAnimated = _mode != Mode.Fraction || _blinkSeconds > 0; // раньше шла анимация: её надо снять
        UseDots(false);
        _mode = Mode.Fraction;
        _paused = paused;
        _blinkSeconds = 0;
        _fill.Width = Math.Clamp(fraction, 0, 1) * TrackWidth;
        _fill.Background = paused ? PausedBrush : _accent;
        _slide.X = 0;
        if (wasAnimated) ApplyAnimation();
    }

    /// <summary>Бегущий отрезок (как у рендера AE); на паузе стоит посередине серым.</summary>
    public void ShowRunning(bool paused)
    {
        if (_mode == Mode.Running && _paused == paused) return; // уже так: не перезапускаем анимацию
        UseDots(false);
        _mode = paused ? Mode.Still : Mode.Running;
        _paused = paused;
        _blinkSeconds = 0;
        _fill.Width = RunWidth;
        _fill.Background = paused ? PausedBrush : _accent;
        _slide.X = paused ? (TrackWidth - RunWidth) / 2 : -RunWidth;
        ApplyAnimation();
    }

    /// <summary>Сплошная полоска во всю длину: мигает плавно (<paramref name="blinkSeconds"/> — период; 0 — горит ровно).</summary>
    public void ShowSolid(double blinkSeconds)
    {
        if (_mode == Mode.Solid && Math.Abs(_blinkSeconds - blinkSeconds) < 0.01) return;
        UseDots(false);
        _mode = Mode.Solid;
        _paused = false;
        _blinkSeconds = blinkSeconds;
        _fill.Width = TrackWidth;
        _fill.Background = _accent;
        _slide.X = 0;
        ApplyAnimation();
    }

    /// <summary>Бегущие точки: волна яркости слева направо (диктовка).</summary>
    public void ShowDots()
    {
        if (_mode == Mode.Dots) return;
        _mode = Mode.Dots;
        _paused = false;
        _blinkSeconds = 0;
        UseDots(true);
        ApplyAnimation();
    }

    private void UseDots(bool on)
    {
        _dots.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _track.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        if (!on) foreach (UIElement d in _dots.Children) d.BeginAnimation(OpacityProperty, null);
    }

    private void ApplyAnimation()
    {
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _fill.BeginAnimation(OpacityProperty, null);
        _fill.Opacity = 1;
        if (!IsVisible) return;

        if (_mode == Mode.Dots)
        {
            // Волна: у каждой точки тот же цикл, начало сдвинуто по порядку, поэтому светлое пятно бежит слева направо и замыкается без стыка.
            const double period = 1000;
            for (var i = 0; i < _dots.Children.Count; i++)
            {
                _dots.Children[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, new Duration(TimeSpan.FromMilliseconds(period / 2)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * period / _dots.Children.Count),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
        }
        else if (_mode == Mode.Running)
        {
            // Светлый отрезок бежит слева направо и начинает заново: тот же спокойный ход, что у индикатора рендера AE.
            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-RunWidth, TrackWidth, new Duration(TimeSpan.FromMilliseconds(1500)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
        else if (_mode == Mode.Solid && _blinkSeconds > 0)
        {
            _fill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, new Duration(TimeSpan.FromSeconds(_blinkSeconds / 2)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
    }
}
