using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Services;

namespace Ostrov.Modules;

/// <summary>
/// Кружок (<see cref="ActivityCenter.BubbleSize"/>) для активности без часов и обложки: Claude (коралловый) и рендер After Effects (белый),
/// когда они не главные. Идёт работа: кольцо с бегущей дугой (оборот за 1,1 с, только пока кружок виден).
/// Результат (готово, ошибка): закрашенный круг со значком (галочка, «!»).
/// </summary>
internal sealed class StatusBubble : Grid
{
    private const double Diameter = 20;
    private static readonly Brush Track = ArcRing.Frozen(Color.FromRgb(0x33, 0x33, 0x33));

    private readonly ArcRing _ring;
    private readonly RotateTransform _spin = new();
    private readonly Border _disc;
    private readonly TextBlock _glyph = new() { Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private bool _working = true, _animating;

    /// <param name="centerGlyph">Значок Segoe Fluent Icons в центре кольца (у загрузок: стрелка вниз), чтобы кружки одного цвета не путались; null — без значка.</param>
    public StatusBubble(Brush color, string? centerGlyph = null)
    {
        Width = Height = ActivityCenter.BubbleSize;
        _ring = new ArcRing
        {
            Width = Diameter, Height = Diameter, RingThickness = 2.4, ArcBrush = color, TrackBrush = Track, Fraction = 0.28,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _spin
        };
        _disc = new Border { Width = Diameter, Height = Diameter, CornerRadius = new CornerRadius(Diameter / 2), Background = color, Visibility = Visibility.Collapsed, Child = _glyph };
        Children.Add(_ring);
        if (centerGlyph != null)
        {
            _center = new TextBlock
            {
                Text = centerGlyph, FontFamily = Ui.IconFont, FontSize = 9, Foreground = color,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Children.Add(_center);
        }
        Children.Add(_disc);
        IsVisibleChanged += (_, _) => ApplyAnimation();
    }

    private readonly TextBlock? _center;

    /// <summary>Идёт работа: кольцо с бегущей дугой.</summary>
    public void ShowWorking()
    {
        _working = true;
        _ring.Visibility = Visibility.Visible;
        _disc.Visibility = Visibility.Collapsed;
        if (_center != null) _center.Visibility = Visibility.Visible;
        ApplyAnimation();
    }

    /// <summary>Готово или ошибка: круг со значком. <paramref name="iconFont"/> — значок из Segoe Fluent Icons (галочка), иначе обычный текст («!»).</summary>
    public void ShowResult(string glyph, bool iconFont)
    {
        _working = false;
        _ring.Visibility = Visibility.Collapsed;
        _disc.Visibility = Visibility.Visible;
        if (_center != null) _center.Visibility = Visibility.Collapsed;
        _glyph.FontFamily = iconFont ? Ui.IconFont : new FontFamily("Segoe UI");
        _glyph.FontSize = iconFont ? 11 : 14;
        _glyph.FontWeight = iconFont ? FontWeights.Normal : FontWeights.Bold;
        _glyph.Text = glyph;
        ApplyAnimation();
    }

    private void ApplyAnimation()
    {
        var run = _working && IsVisible;
        if (run == _animating) return;
        _animating = run;
        if (!run) { _spin.BeginAnimation(RotateTransform.AngleProperty, null); return; }
        _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(1100))) { RepeatBehavior = RepeatBehavior.Forever });
    }
}

/// <summary>
/// Дорожка с бегущим отрезком (4 px): «идёт работа» на расширенных видах (Claude, рендер After Effects). Тот же ход, что у плашек:
/// отрезок бежит слева направо и начинает заново (плавный разгон и торможение). Анимация идёт, только пока полоска видна.
/// </summary>
internal sealed class RunningBar : Border
{
    private const double BarHeight = 4;
    private static readonly Brush Track = ArcRing.Frozen(Hues.SurfaceHighColor);

    private readonly TranslateTransform _slide;
    private readonly double _highlight, _trackWidth;
    private bool _animating;

    /// <param name="trackWidth">Ширина дорожки (нужна, чтобы отрезок добегал до конца).</param>
    public RunningBar(Brush color, double trackWidth, double highlightWidth = 96)
    {
        _highlight = highlightWidth;
        _trackWidth = trackWidth;
        _slide = new TranslateTransform(-highlightWidth, 0);
        Height = BarHeight;
        CornerRadius = new CornerRadius(BarHeight / 2);
        Background = Track;
        ClipToBounds = true;
        Child = new Border
        {
            Width = highlightWidth, Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2), Background = color,
            HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _slide
        };
        IsVisibleChanged += (_, _) => ApplyAnimation();
    }

    private void ApplyAnimation()
    {
        var run = IsVisible;
        if (run == _animating) return;
        _animating = run;
        if (!run) { _slide.BeginAnimation(TranslateTransform.XProperty, null); return; }
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-_highlight, _trackWidth, new Duration(TimeSpan.FromMilliseconds(1500)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}
