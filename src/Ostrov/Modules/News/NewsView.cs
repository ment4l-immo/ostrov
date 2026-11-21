using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Modules.Settings;
using Ostrov.Search;
using Ostrov.Services;

namespace Ostrov.Modules.News;

/// <summary>
/// Вкладка «Новости»: заголовки из лент по выбранным сферам (игры, нейросети и ИИ), лучшие по вашим интересам сверху.
/// Сверху «Все | Игры | ИИ», обновить и «Темы» (подтемы и свои слова). На строке при наведении 👍 и 👎: лайк поднимает похожее,
/// дизлайк прячет новость и опускает похожее (учится только на компьютере). Клик по строке открывает статью в браузере.
/// Ленты обновляются, только когда остров раскрыт и данные старше 30 минут; открытая вкладка показывает кэш сразу.
/// </summary>
internal sealed class NewsView : Grid
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static readonly Brush Selected = Frozen(Color.FromRgb(0x40, 0x40, 0x40));
    private static readonly Brush FieldBackground = Frozen(Hues.GlassSurfaceColor);

    public const double RowHeight = 46;
    private const int MaxRows = 30;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    private const string GlyphLike = "", GlyphDislike = "", GlyphRefresh = "", GlyphClose = "";

    private readonly NewsProfile _profile = NewsStore.LoadProfile();
    private List<NewsItem> _items = NewsStore.LoadCache();
    private readonly Func<string, bool> _open;

    private readonly SegmentedControl _sphere = new("Все", "Игры", "ИИ");
    private readonly Button _refresh = Ui.IconButton(GlyphRefresh, 13, 28);
    private readonly Button _topics = Ui.Pill("Темы");
    private readonly TextBlock _status = new() { Foreground = Ui.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 1) };
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _editor = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _empty = new() { Foreground = Ui.Gray, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly WrapPanel _custom = new();
    private readonly TextBox _customBox = Ui.BareTextBox();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private CancellationTokenSource? _loading;
    private readonly List<Action> _repaintTopics = [];
    private bool _reviewing;
    private DateTimeOffset _lastReviewAttempt = DateTimeOffset.MinValue;
    private bool _editing;
    private NewsReaderPanel? _reader;

    /// <summary>Открыта статья для чтения (остров выше обычного).</summary>
    public bool IsReading => _reader != null;
    private Brush? _topicsNormal;
    private bool _hooked;

    /// <param name="open">Как открыть ссылку (по умолчанию браузер). Подменяется для проверки.</param>
    public NewsView(Func<string, bool>? open = null)
    {
        _open = open ?? OpenInBrowser;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _sphere.HorizontalAlignment = HorizontalAlignment.Left;
        _sphere.Select(0);
        _sphere.Changed += _ => Rebuild();
        _refresh.ToolTip = "Обновить";
        _refresh.Click += (_, _) => _ = RefreshAsync(force: true);
        _topics.Margin = new Thickness(0);
        _topicsNormal = _topics.Background;
        _topics.Height = 28;
        _topics.Click += (_, _) => ToggleEditor();

        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { _sphere } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _status, _refresh, new Border { Width = 4 }, _topics } };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(left);
        header.Children.Add(right);
        SetRow(header, 0);
        Children.Add(header);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list, Margin = new Thickness(0, 0, 0, 6) };
        Ui.SoftBottom(_scroll);
        SetRow(_scroll, 1);
        SetRow(_empty, 1);
        SetRow(_editor, 1);
        Children.Add(_scroll);
        Children.Add(_empty);
        Children.Add(_editor);
        BuildEditor();

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _status.Text = ""; };
        Loaded += (_, _) =>
        {
            Rebuild();
            _ = RefreshAsync(force: false);
            MaybeAutoReview();
            if (!_hooked && Window.GetWindow(this) is { })
            {
                _hooked = true;
                IslandBus.Expanding += () =>
                {
                    if (!IsLoaded) return;
                    if (_reader != null) CloseArticle(); // остров раскрыт заново: показываем список, а не прежнюю статью
                    _ = RefreshAsync(force: false);
                    MaybeAutoReview();
                };
            }
        };
    }

    private static bool OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); return true; }
        catch { return false; }
    }

    private string? CurrentSphere => _sphere.SelectedIndex switch { 1 => NewsCatalog.Games, 2 => NewsCatalog.Ai, _ => null };

    // ───── Загрузка ─────

    private async Task RefreshAsync(bool force)
    {
        if (_loading != null) return;
        if (!force && _items.Count > 0 && DateTime.UtcNow - NewsStore.CacheWrittenUtc < StaleAfter) return;
        var cts = _loading = new CancellationTokenSource();
        Say("Обновляю…", persist: true);
        try
        {
            var (items, ok) = await Task.Run(() => NewsFetcher.RefreshAsync(NewsCatalog.Feeds, cts.Token), cts.Token);
            if (ok == 0) { Say(_items.Count == 0 ? "Нет сети" : "Нет сети, показан кэш"); }
            else
            {
                _items = items;
                Say($"Обновлено {DateTime.Now:HH:mm}");
            }
        }
        catch (OperationCanceledException) { }
        catch { Say("Не удалось обновить"); }
        finally
        {
            _loading = null;
            Rebuild();
        }
    }

    private void Say(string text, bool persist = false)
    {
        _status.Text = text;
        _statusTimer.Stop();
        if (!persist) _statusTimer.Start();
    }

    // ───── Список ─────

    private void Rebuild()
    {
        _list.Children.Clear();
        if (_profile.Suggestion is { } suggestion) _list.Children.Add(BuildSuggestionCard(suggestion));
        var ranked = NewsRanker.Rank(_items, _profile, DateTimeOffset.Now, CurrentSphere).Take(MaxRows).ToList();
        _empty.Text = _items.Count == 0 ? (_loading != null ? "Загружаю новости…" : "Новостей пока нет.\nНажми «обновить»") : "Здесь пока ничего нового";
        _empty.Visibility = !_editing && ranked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in ranked) _list.Children.Add(BuildRow(item));
    }

    private UIElement BuildRow(NewsItem item)
    {
        _profile.Reactions.TryGetValue(item.Id, out var reaction);
        var liked = reaction?.Value == 1;

        var title = new TextBlock { Text = item.Title, FontSize = 13, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
        var caption = new TextBlock
        {
            Text = $"{item.Source} · {Ago(item.Published, DateTimeOffset.Now)}", FontSize = 11.5, Foreground = Ui.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0)
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, caption } };

        var like = Ui.IconButton(GlyphLike, 12, 26);
        like.ToolTip = liked ? "Убрать лайк" : "Интересно";
        like.Opacity = liked ? 1 : 0;
        var dislike = Ui.IconButton(GlyphDislike, 12, 26);
        dislike.ToolTip = "Неинтересно: скрыть и показывать меньше такого";
        dislike.Opacity = 0;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(texts, 0);
        SetColumn(like, 1);
        SetColumn(dislike, 2);
        texts.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(texts);
        grid.Children.Add(like);
        grid.Children.Add(dislike);

        var row = new Border
        {
            Height = RowHeight, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 4, 0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = grid, ToolTip = item.Title
        };
        var glow = new HoverGlow();
        row.MouseEnter += (_, a) => { glow.Follow(row, a); row.Background = glow.Brush; like.Opacity = 1; dislike.Opacity = 1; };
        row.MouseMove += (_, a) => glow.Follow(row, a);
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; like.Opacity = liked ? 1 : 0; dislike.Opacity = 0; };
        row.MouseLeftButtonUp += (_, _) => OpenArticle(item); // читаем здесь же, без браузера
        like.Click += (_, _) => React(item, liked ? 0 : 1);
        dislike.Click += (_, _) => React(item, -1);
        return row;
    }

    // ───── Недельный подбор ИИ ─────

    // Сам, раз в неделю (и когда оценок хватает); без ключа OpenAI молча ничего не делает. После неудачи следующая попытка не раньше чем через час.
    private void MaybeAutoReview()
    {
        var now = DateTimeOffset.Now;
        if (_reviewing || now - _lastReviewAttempt < TimeSpan.FromHours(1) || !NewsReview.IsDue(_profile, now) || OpenAiKeyStore.Load() == null) return;
        _ = ReviewAsync(manual: false);
    }

    private async Task ReviewAsync(bool manual)
    {
        if (_reviewing) return;
        _reviewing = true;
        var now = _lastReviewAttempt = DateTimeOffset.Now;
        if (manual) Say("Подбираю…", persist: true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var (ok, suggestion, error) = await NewsReview.RunAsync(_profile, now, timeout.Token);
            if (!ok) { if (manual) Say(error ?? "Не получилось"); return; }
            _profile.LastAiReview = now;
            _profile.Suggestion = suggestion;
            NewsStore.SaveProfile(_profile);
            if (manual) Say(suggestion == null ? "ИИ: менять нечего" : "Есть предложение");
            if (suggestion != null && _editing) ToggleEditor(); // карточка стоит над списком: из «Тем» выходим к ней
            Rebuild();
        }
        finally { _reviewing = false; }
    }

    private UIElement BuildSuggestionCard(NewsSuggestion suggestion)
    {
        var stack = new StackPanel();
        var apply = Ui.Pill("Применить", primary: true, padding: 12);
        apply.Margin = new Thickness(0);
        apply.Height = 26;
        apply.Click += (_, _) =>
        {
            NewsReview.Apply(_profile, suggestion);
            NewsStore.SaveProfile(_profile);
            foreach (var repaint in _repaintTopics) repaint(); // отметки подтем и слова в «Темах» обновить
            RebuildCustom();
            Say("Применено");
            Rebuild();
        };
        var reject = Ui.Pill("Отклонить", padding: 12);
        reject.Margin = new Thickness(6, 0, 0, 0);
        reject.Height = 26;
        reject.Click += (_, _) =>
        {
            _profile.Suggestion = null;
            NewsStore.SaveProfile(_profile);
            Rebuild();
        };
        // Заголовок слева, кнопки справа в той же строке: карточка остаётся невысокой, под ней видны новости.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { apply, reject } };
        SetColumn(buttons, 1);
        header.Children.Add(new TextBlock { Text = "Подбор ИИ · по твоим оценкам", FontSize = 11, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(buttons);
        stack.Children.Add(header);
        if (suggestion.Summary.Length > 0)
            stack.Children.Add(new TextBlock { Text = suggestion.Summary, FontSize = 13, Foreground = Hues.Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var line in NewsReview.Describe(suggestion))
            stack.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = Ui.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
        return new Border { CornerRadius = new CornerRadius(12), Background = FieldBackground, Padding = new Thickness(12, 8, 10, 9), Margin = new Thickness(0, 0, 0, 8), Child = stack };
    }

    // ───── Чтение ─────

    private void OpenArticle(NewsItem item)
    {
        if (_reader != null) return;
        _profile.Reactions.TryGetValue(item.Id, out var reaction);
        var reader = _reader = new NewsReaderPanel(item, reaction?.Value ?? 0, _open);
        SetRow(reader, 0);
        SetRowSpan(reader, 2);
        reader.BackRequested += CloseArticle;
        reader.Reacted += value =>
        {
            React(item, value);
            if (value == -1) CloseArticle(); // дизлайк скрывает новость: возвращаемся к списку
            else reader.SetReaction(value);
        };
        foreach (UIElement child in Children) child.Visibility = Visibility.Collapsed;
        Children.Add(reader);
        IslandBus.SuspendAutoCollapse(true); // читаем: мышь может стоять неподвижно
        IslandBus.RequestHeight("news");
    }

    private void CloseArticle()
    {
        if (_reader is not { } reader) return;
        reader.Stop();
        Children.Remove(reader);
        _reader = null;
        foreach (UIElement child in Children) child.Visibility = Visibility.Visible;
        _editor.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = Visibility.Collapsed;
        Rebuild();
        IslandBus.SuspendAutoCollapse(false);
        IslandBus.RequestHeight("news");
    }

    private void React(NewsItem item, int value)
    {
        NewsRanker.React(_profile, item, value, DateTimeOffset.Now);
        NewsStore.SaveProfile(_profile);
        Rebuild();
    }

    /// <summary>«только что», «12 мин назад», «3 ч назад», «вчера», «4 дн. назад».</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(2)) return "только что";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} мин назад";
        if (span < TimeSpan.FromHours(24)) return $"{(int)span.TotalHours} ч назад";
        var days = (now.Date - when.ToLocalTime().Date).Days;
        return days <= 1 ? "вчера" : $"{days} дн. назад";
    }

    // ───── Темы ─────

    private void ToggleEditor()
    {
        _editing = !_editing;
        _editor.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        _topics.Background = _editing ? Selected : _topicsNormal;
        _topics.Content = _editing ? "Готово" : "Темы";
        if (!_editing) Rebuild();
        else _empty.Visibility = Visibility.Collapsed;
    }

    private void BuildEditor()
    {
        _editor.Children.Clear();
        foreach (var sphere in new[] { NewsCatalog.Games, NewsCatalog.Ai })
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            wrap.Children.Add(Label(sphere == NewsCatalog.Games ? "Игры" : "ИИ"));
            foreach (var topic in NewsCatalog.Subtopics.Where(t => t.Sphere == sphere)) wrap.Children.Add(TopicChip(topic));
            _editor.Children.Add(wrap);
        }

        _customBox.FontSize = 12.5;
        _customBox.VerticalAlignment = VerticalAlignment.Center;
        _customBox.MinWidth = 90;
        var hint = new TextBlock { Text = "Добавить слово…", FontSize = 12.5, Foreground = Dim, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        _customBox.TextChanged += (_, _) => hint.Visibility = _customBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _customBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var word = _customBox.Text.Trim();
            if (word.Length < 2 || _profile.CustomKeywords.Contains(word, StringComparer.OrdinalIgnoreCase)) { _customBox.Text = ""; return; }
            _profile.CustomKeywords.Add(word);
            NewsStore.SaveProfile(_profile);
            _customBox.Text = "";
            RebuildCustom();
        };
        _customBox.GotKeyboardFocus += (_, _) => IslandBus.SuspendAutoCollapse(true);
        _customBox.LostKeyboardFocus += (_, _) => IslandBus.SuspendAutoCollapse(false);
        var field = new Border { Height = 26, CornerRadius = new CornerRadius(13), Background = FieldBackground, Padding = new Thickness(11, 0, 11, 1), Margin = new Thickness(0, 0, 6, 6), Child = new Grid { Children = { hint, _customBox } } };
        field.MouseLeftButtonDown += (_, _) => _customBox.Focus();
        _custom.Children.Clear();
        _editor.Children.Add(_custom);
        _customField = field;
        RebuildCustom();

        var review = Ui.Pill("Подобрать по оценкам", padding: 12);
        review.Height = 26;
        review.Margin = new Thickness(0, 0, 6, 6);
        review.ToolTip = "ИИ посмотрит на твои лайки и дизлайки и предложит правки тем. На OpenAI уходят только эти заголовки. Раз в неделю то же происходит само.";
        review.Click += (_, _) =>
        {
            if (NewsReview.NotReadyReason(_profile) is { } reason) { Say(reason); return; }
            if (OpenAiKeyStore.Load() == null) { Say("Нужен ключ OpenAI: вставь его в настройках"); return; }
            _ = ReviewAsync(manual: true);
        };
        _reviewButton = review;
        RebuildCustom(); // кнопка подбора стоит в одной строке со «Свои слова»: высота редактора не растёт
    }

    private Button? _reviewButton;

    private Border? _customField;

    // Подпись раздела в одной строке с кнопками: высота как у кнопок, текст по центру.
    private static Grid Label(string text) => new()
    {
        Height = 26, Margin = new Thickness(2, 0, 10, 6), MinWidth = 40,
        Children = { new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center } }
    };

    private void RebuildCustom()
    {
        _custom.Children.Clear();
        _custom.Children.Add(Label("Свои слова"));
        foreach (var word in _profile.CustomKeywords.ToList())
        {
            var chip = Ui.Pill(word + "  ×", padding: 11);
            chip.Height = 26;
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.ToolTip = "Убрать слово";
            chip.Click += (_, _) =>
            {
                _profile.CustomKeywords.Remove(word);
                NewsStore.SaveProfile(_profile);
                RebuildCustom();
            };
            _custom.Children.Add(chip);
        }
        if (_customField != null) _custom.Children.Add(_customField);
        if (_reviewButton != null) _custom.Children.Add(_reviewButton);
    }

    private Button TopicChip(NewsSubtopic topic)
    {
        var chip = Ui.Pill(topic.Title, padding: 12);
        chip.Height = 26;
        chip.Margin = new Thickness(0, 0, 6, 6);
        void Paint() => chip.Background = _profile.ActiveSubtopics.Contains(topic.Id) ? Selected : FieldBackground;
        _repaintTopics.Add(Paint);
        chip.Click += (_, _) =>
        {
            if (!_profile.ActiveSubtopics.Remove(topic.Id)) _profile.ActiveSubtopics.Add(topic.Id);
            NewsStore.SaveProfile(_profile);
            Paint();
        };
        Paint();
        return chip;
    }
}
