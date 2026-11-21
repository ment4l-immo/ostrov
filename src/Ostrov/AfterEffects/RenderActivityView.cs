using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.AfterEffects;

/// <summary>
/// Пассивный индикатор рендера After Effects в слегка расширенном свёрнутом острове:
/// пока идёт рендер — подпись, сколько уже идёт, и «бегущая» полоска; по окончании — галочка и «Рендер завершён»;
/// при ошибке — восклицательный знак и «Проверь After Effects». Цвета только белый и серый.
/// Анимации пользователь доделает на этапе 6.
/// </summary>
internal sealed class RenderActivityView : Grid
{
    public const double ViewWidth = 288, ViewHeight = 56;

    private const double SideMargin = 18, BarHeight = 4, HighlightWidth = 84;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Track = Frozen(Hues.SurfaceHighColor);

    private readonly Grid _progressPanel = new() { Margin = new Thickness(SideMargin, 0, SideMargin, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _elapsed = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TranslateTransform _slide = new(-HighlightWidth, 0);

    private readonly Grid _resultPanel = new() { Margin = new Thickness(SideMargin, 0, SideMargin, 4), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _icon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black };
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 11, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis };

    private bool _animating;

    public RenderActivityView()
    {
        Width = ViewWidth;
        Height = ViewHeight;

        // Пока идёт рендер: «After Effects · рендер» слева, время справа, под ними бегущая полоска.
        _progressPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _progressPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = "After Effects · рендер", FontSize = 11, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid();
        header.Children.Add(label);
        header.Children.Add(_elapsed);
        SetRow(header, 0);
        _progressPanel.Children.Add(header);

        var highlight = new Border
        {
            Width = HighlightWidth, Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2),
            Background = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _slide
        };
        var bar = new Border
        {
            Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2), Background = Track,
            ClipToBounds = true, Margin = new Thickness(0, 9, 0, 0), Child = highlight
        };
        SetRow(bar, 1);
        _progressPanel.Children.Add(bar);
        Children.Add(_progressPanel);

        // Готово / ошибка: белый кружок со значком и два текста.
        _resultPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _resultPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Hues.Ink,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Child = _icon
        };
        SetColumn(circle, 0);
        _resultPanel.Children.Add(circle);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail } };
        SetColumn(texts, 1);
        _resultPanel.Children.Add(texts);
        Children.Add(_resultPanel);
    }

    public void Update(RenderView view)
    {
        if (view.Phase == RenderPhase.Rendering)
        {
            _progressPanel.Visibility = Visibility.Visible;
            _resultPanel.Visibility = Visibility.Collapsed;
            _elapsed.Text = RenderTracker.FormatElapsed(view.Elapsed);
            StartAnimation();
            return;
        }

        StopAnimation();
        _progressPanel.Visibility = Visibility.Collapsed;
        _resultPanel.Visibility = Visibility.Visible;
        if (view.Phase == RenderPhase.Done)
        {
            _icon.FontFamily = Ui.IconFont;
            _icon.FontSize = 15;
            _icon.FontWeight = FontWeights.Normal;
            _icon.Text = ""; // галочка
        }
        else
        {
            _icon.FontFamily = new FontFamily("Segoe UI");
            _icon.FontSize = 19;
            _icon.FontWeight = FontWeights.Bold;
            _icon.Text = "!";
        }
        _title.Text = view.Title;
        _detail.Text = view.Detail;
    }

    // Светлый отрезок бежит по полоске слева направо и начинает заново (подчёркнуто спокойно: плавный разгон и торможение).
    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        var run = new DoubleAnimation(-HighlightWidth, ViewWidth - 2 * SideMargin, new Duration(TimeSpan.FromMilliseconds(1500)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, run);
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
