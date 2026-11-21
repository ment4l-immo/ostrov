using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Claude;

/// <summary>Общие значения оформления «Claude работает»: коралловый — цвет Claude (как у его запросов).</summary>
internal static class ClaudeStyle
{
    public static readonly Brush Coral = Frozen(PermissionCard.Coral);

    public static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>Одна работающая сессия в раскрытом списке: название, проект, что делает и сколько идёт.</summary>
internal sealed record ClaudeSessionRow(string Title, string Project, string Step, string Elapsed);

/// <summary>
/// Пассивный индикатор Claude в слегка расширенном свёрнутом острове (как у рендера AE, но коралловый):
/// пока Claude работает — «Claude · шаг» (при нескольких сессиях «Claude ×2 · шаг»), сколько уже идёт и бегущая полоска;
/// по окончании — галочка, «Claude закончил» и проект со временем. «Закончил» висит до клика.
/// Если работают две сессии и больше, при наведении справа появляется стрелка; по клику на неё плашка вытягивается вниз
/// списком сессий (название, проект · шаг, время).
/// </summary>
internal sealed class ClaudeActivityView : Grid
{
    public const double ViewWidth = 288, ViewHeight = 56;

    private const double SideMargin = 18, BarHeight = 4, HighlightWidth = 84, RowHeight = 38, ListBottom = 12;
    private const int MaxRows = 4;
    private const string ChevronDown = "", ChevronUp = "";

    private static readonly Brush Track = ClaudeStyle.Frozen(Hues.SurfaceHighColor);

    private readonly Grid _workingPanel = new() { Margin = new Thickness(SideMargin, 0, SideMargin, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new()
    {
        Text = "Claude · работает", FontSize = 11, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200
    };
    private readonly TextBlock _elapsed = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _arrow = new()
    {
        FontFamily = Ui.IconFont, FontSize = 10, Foreground = Ui.Gray, Text = ChevronDown, Opacity = 0,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TranslateTransform _slide = new(-HighlightWidth, 0);

    private readonly Grid _donePanel = new() { Margin = new Thickness(SideMargin, 0, SideMargin, 4), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _title = new() { Text = "Claude закончил", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 11, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly StackPanel _list = new() { Margin = new Thickness(SideMargin, 0, SideMargin, ListBottom), Visibility = Visibility.Collapsed };
    private readonly List<(TextBlock Title, TextBlock Detail, TextBlock Elapsed)> _rowViews = [];

    private bool _animating, _working = true, _hover, _expanded, _arrowAvailable;

    public ClaudeActivityView()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(ViewHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Пока Claude работает: подпись слева, время справа (и стрелка), под ними бегущая полоска.
        _workingPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _workingPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        header.Children.Add(_label);
        header.Children.Add(_elapsed);
        header.Children.Add(_arrow);
        SetRow(header, 0);
        _workingPanel.Children.Add(header);
        var highlight = new Border
        {
            Width = HighlightWidth, Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2),
            Background = ClaudeStyle.Coral, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _slide
        };
        var bar = new Border
        {
            Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2), Background = Track,
            ClipToBounds = true, Margin = new Thickness(0, 9, 0, 0), Child = highlight
        };
        SetRow(bar, 1);
        _workingPanel.Children.Add(bar);
        SetRow(_workingPanel, 0);
        Children.Add(_workingPanel);

        // Закончил: коралловый кружок с галочкой и два текста.
        _donePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _donePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = ClaudeStyle.Coral,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = "", FontFamily = Ui.IconFont, FontSize = 15, Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        SetColumn(circle, 0);
        _donePanel.Children.Add(circle);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail } };
        SetColumn(texts, 1);
        _donePanel.Children.Add(texts);
        SetRow(_donePanel, 0);
        Children.Add(_donePanel);

        SetRow(_list, 1);
        Children.Add(_list);

        IsVisibleChanged += (_, _) => ApplyAnimation();
    }

    /// <summary>Нужная высота: обычная, а с раскрытым списком больше на строки сессий.</summary>
    public double PreferredHeight => _expanded ? ViewHeight + Math.Min(_rowViews.Count, MaxRows) * RowHeight + ListBottom : ViewHeight;

    /// <summary>Попадает ли точка (в координатах плашки) в зону стрелки справа вверху.</summary>
    public static bool ArrowHit(Point p) => p.X >= ViewWidth - SideMargin - 30 && p.X <= ViewWidth && p.Y <= 36;

    /// <param name="sessions">Сколько сессий Claude работает сейчас (при двух и больше добавляется «×N»).</param>
    /// <param name="step">Что делает сейчас («читает ClaudeWork.cs», «думает»); null — просто «работает».</param>
    public void ShowWorking(string elapsedText, int sessions, string? step = null)
    {
        _working = true;
        _workingPanel.Visibility = Visibility.Visible;
        _donePanel.Visibility = Visibility.Collapsed;
        _label.Text = (sessions > 1 ? $"Claude ×{sessions} · " : "Claude · ") + (step ?? "работает");
        _elapsed.Text = elapsedText;
        ApplyAnimation();
    }

    public void SetElapsed(string elapsedText) => _elapsed.Text = elapsedText;

    /// <summary>Стрелка нужна, пока работают две сессии и больше; <paramref name="expanded"/> — список раскрыт.</summary>
    public void SetSessions(IReadOnlyList<ClaudeSessionRow> rows, bool expanded)
    {
        _arrowAvailable = rows.Count >= 2;
        _expanded = _arrowAvailable && expanded;
        _elapsed.Margin = new Thickness(0, 0, _arrowAvailable ? 20 : 0, 0);
        _label.MaxWidth = _arrowAvailable ? 180 : 200;
        _arrow.Text = _expanded ? ChevronUp : ChevronDown;
        _list.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateRows(rows);
        UpdateArrow();
        Height = PreferredHeight;
    }

    /// <summary>Обновить тексты строк на месте (раз в секунду), не меняя число строк и высоту.</summary>
    public void UpdateRows(IReadOnlyList<ClaudeSessionRow> rows)
    {
        var shown = Math.Min(rows.Count, MaxRows);
        while (_rowViews.Count < shown)
        {
            var title = new TextBlock { FontSize = 12, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { FontSize = 11, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
            var elapsed = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { Height = RowHeight };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Children = { title, detail } };
            SetColumn(texts, 0);
            SetColumn(elapsed, 1);
            row.Children.Add(texts);
            row.Children.Add(elapsed);
            _list.Children.Add(row);
            _rowViews.Add((title, detail, elapsed));
        }
        while (_rowViews.Count > shown)
        {
            _rowViews.RemoveAt(_rowViews.Count - 1);
            _list.Children.RemoveAt(_list.Children.Count - 1);
        }
        for (var i = 0; i < shown; i++)
        {
            var r = rows[i];
            _rowViews[i].Title.Text = r.Title;
            _rowViews[i].Detail.Text = r.Project + " · " + r.Step;
            _rowViews[i].Elapsed.Text = r.Elapsed;
        }
    }

    public void SetArrowHover(bool hover)
    {
        _hover = hover;
        UpdateArrow();
    }

    // Стрелка видна при наведении (и пока список раскрыт), если сессий две и больше.
    private void UpdateArrow() => _arrow.Opacity = _arrowAvailable && _working && (_hover || _expanded) ? 1 : 0;

    /// <param name="detail">Проект и сколько работал, например «ostrov · 2:15».</param>
    public void ShowDone(string detail)
    {
        _working = false;
        _expanded = false;
        _workingPanel.Visibility = Visibility.Collapsed;
        _donePanel.Visibility = Visibility.Visible;
        _list.Visibility = Visibility.Collapsed;
        _detail.Text = detail;
        Height = ViewHeight;
        UpdateArrow();
        ApplyAnimation();
    }

    // Светлый отрезок бежит по полоске слева направо и начинает заново (тот же спокойный ход, что у рендера AE); только пока видно.
    private void ApplyAnimation()
    {
        var shouldRun = _working && IsVisible;
        if (shouldRun == _animating) return;
        _animating = shouldRun;
        if (!shouldRun)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-HighlightWidth, ViewWidth - 2 * SideMargin, new Duration(TimeSpan.FromMilliseconds(1500)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}

/// <summary>
/// Окошко «Claude закончил» для полноэкранного видео: нотч вырастает до размера обычного свёрнутого острова (200 × 36),
/// плавно мигает до клика, без звука. Слева коралловый кружок с галочкой, справа надпись коралловым.
/// </summary>
internal sealed class ClaudeDoneAlert : Grid
{
    public const double ViewWidth = 200, ViewHeight = 36;

    public ClaudeDoneAlert()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = ClaudeStyle.Coral,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 3),
            Child = new TextBlock
            {
                Text = "", FontFamily = Ui.IconFont, FontSize = 11, Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        });
        Children.Add(new TextBlock
        {
            Text = "Claude закончил", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ClaudeStyle.Coral,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 4.8)
        });
        IsVisibleChanged += (_, _) => ApplyBlink();
    }

    // Мигание только пока окошко на экране: плавно гаснет до 35 % и возвращается (период 1,2 с, как у «Время вышло»).
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
