using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Claude;

/// <summary>
/// Расширенная плашка Claude (правый клик, М-017): те же данные, что на обычной, но без обрезки и покрупнее.
/// Работает: «Claude» (и «×N» при нескольких сессиях), время, шаг целиком, проект, бегущая полоска; при двух сессиях и больше внизу сразу список сессий.
/// Закончил: коралловая галочка, «Claude закончил», проект · время (и сколько ещё знаков ждёт клика).
/// </summary>
internal sealed class ClaudeWideView : Grid
{
    public const double ViewWidth = 360;
    private const double WorkingHeight = 104, DoneHeight = 72, Side = 20, RowHeight = 38, ListBottom = 12;
    private const int MaxRows = 4;

    private readonly StackPanel _working = new() { Margin = new Thickness(Side, 14, Side, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _title = new() { Text = "Claude", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ClaudeStyle.Coral, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _elapsed = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _step = new() { FontSize = 14, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
    private readonly TextBlock _project = new() { FontSize = 12, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
    private readonly StackPanel _list = new() { Margin = new Thickness(Side, 0, Side, ListBottom), Visibility = Visibility.Collapsed };
    private readonly List<(TextBlock Title, TextBlock Detail, TextBlock Elapsed)> _rowViews = [];

    private readonly Grid _done = new() { Margin = new Thickness(Side, 0, Side, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _doneTitle = new() { Text = "Claude закончил", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _doneDetail = new() { FontSize = 12, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };

    private bool _isWorking = true;
    private int _shownRows;

    public ClaudeWideView()
    {
        Width = ViewWidth;
        // Верхняя строка занимает всё, что не ушло под список сессий: «закончил» центрируется по высоте, а не прижимается к верху.
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.Children.Add(_title);
        header.Children.Add(_elapsed);
        _working.Children.Add(header);
        _working.Children.Add(_step);
        _working.Children.Add(_project);
        _working.Children.Add(new RunningBar(ClaudeStyle.Coral, ViewWidth - 2 * Side) { Margin = new Thickness(0, 10, 0, 0) });
        SetRow(_working, 0);
        Children.Add(_working);

        _done.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _done.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = ClaudeStyle.Coral,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = "", FontFamily = Ui.IconFont, FontSize = 17, Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        SetColumn(circle, 0);
        _done.Children.Add(circle);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _doneTitle, _doneDetail } };
        SetColumn(texts, 1);
        _done.Children.Add(texts);
        SetRow(_done, 0);
        Children.Add(_done);

        SetRow(_list, 1);
        Children.Add(_list);
        Height = WorkingHeight;
    }

    /// <summary>Нужная высота: у «закончил» своя, у «работает» с списком сессий выше.</summary>
    public double PreferredHeight => !_isWorking ? DoneHeight : WorkingHeight + (_shownRows >= 2 ? _shownRows * RowHeight + ListBottom : 0);

    public void ShowWorking(string elapsedText, int sessions, string? step, string project, IReadOnlyList<ClaudeSessionRow> rows)
    {
        _isWorking = true;
        _working.Visibility = Visibility.Visible;
        _done.Visibility = Visibility.Collapsed;
        _title.Text = sessions > 1 ? $"Claude ×{sessions}" : "Claude";
        _elapsed.Text = elapsedText;
        _step.Text = step ?? "работает";
        _project.Text = project;
        UpdateRows(rows);
        Height = PreferredHeight;
    }

    public void SetElapsed(string elapsedText) => _elapsed.Text = elapsedText;

    /// <param name="detail">Проект и сколько работал, например «ostrov · 2:15»; при очереди знаков добавляется «ещё N».</param>
    public void ShowDone(string detail)
    {
        _isWorking = false;
        _working.Visibility = Visibility.Collapsed;
        _list.Visibility = Visibility.Collapsed;
        _done.Visibility = Visibility.Visible;
        _doneDetail.Text = detail;
        _shownRows = 0;
        Height = DoneHeight;
    }

    /// <summary>Список сессий: виден, пока работают две и больше.</summary>
    public void UpdateRows(IReadOnlyList<ClaudeSessionRow> rows)
    {
        var shown = rows.Count >= 2 ? Math.Min(rows.Count, MaxRows) : 0;
        _list.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            _rowViews[i].Title.Text = rows[i].Title;
            _rowViews[i].Detail.Text = rows[i].Project + " · " + rows[i].Step;
            _rowViews[i].Elapsed.Text = rows[i].Elapsed;
        }
        _shownRows = shown;
        if (_isWorking) Height = PreferredHeight;
    }
}
