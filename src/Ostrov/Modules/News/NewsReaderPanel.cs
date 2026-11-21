using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Search;
using Ostrov.Services;

namespace Ostrov.Modules.News;

/// <summary>
/// Чтение новости прямо на острове (без браузера): стрелка «назад», заголовок, источник и время, обложка и текст статьи (<see cref="ArticleReader"/>),
/// кнопка «Кратко» (ИИ пересказывает по-русски в 3–5 строках, как в Alt+Q, на ключе OpenAI из настроек), «В браузере», 👍 и 👎.
/// Если текст достать не удалось (сайт закрыт для автоматических запросов), «Кратко» всё равно работает: модель сама открывает страницу.
/// Пересказ запоминается до закрытия программы, чтобы не платить дважды за одну статью.
/// </summary>
internal sealed class NewsReaderPanel : Grid
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush BodyBrush = Frozen(Color.FromRgb(0xDC, 0xDC, 0xDC));
    private static readonly Brush SummaryBackground = Frozen(Hues.SurfaceColor);
    private static readonly Dictionary<string, string> Summaries = [];

    private const string GlyphBack = "", GlyphLike = "", GlyphDislike = "";

    private readonly NewsItem _item;
    private readonly Func<string, bool> _open;
    private readonly StackPanel _content = new();
    private readonly TextBlock _status = new() { FontSize = 12.5, Foreground = Ui.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly Border _summaryBox = new() { CornerRadius = new CornerRadius(12), Background = SummaryBackground, Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
    private readonly TextBlock _summaryText = new() { FontSize = 13, Foreground = Hues.Ink, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
    private readonly Button _summarize = Ui.Pill("Кратко", primary: true);
    private readonly Button _like = Ui.IconButton(GlyphLike, 13, 30);
    private readonly Button _dislike = Ui.IconButton(GlyphDislike, 13, 30);
    private readonly ScrollViewer _scroll;
    private CancellationTokenSource? _loadCts, _summaryCts;
    private string? _articleText;
    private bool _summarizing;

    public event Action? BackRequested;
    public event Action<int>? Reacted; // 1 — интересно, −1 — нет, 0 — снять

    public NewsReaderPanel(NewsItem item, int reaction, Func<string, bool> open)
    {
        _item = item;
        _open = open;
        Margin = new Thickness(0, 0, 0, 12); // зазор до полосы вкладок (не меньше ~10 px)
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var back = Ui.IconButton(GlyphBack, 14, 30);
        back.ToolTip = "Назад к списку";
        back.Click += (_, _) => BackRequested?.Invoke();
        var title = new TextBlock
        {
            Text = item.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0)
        };
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumn(title, 1);
        bar.Children.Add(back);
        bar.Children.Add(title);
        SetRow(bar, 0);
        Children.Add(bar);

        var caption = new TextBlock
        {
            Text = $"{item.Source} · {NewsView.Ago(item.Published, DateTimeOffset.Now)}", FontSize = 11.5, Foreground = Ui.Gray,
            Margin = new Thickness(36, 0, 0, 8)
        };
        SetRow(caption, 1);
        Children.Add(caption);

        _summaryBox.Child = new StackPanel
        {
            Children = { new TextBlock { Text = "Кратко · ИИ", FontSize = 11, Foreground = Ui.Gray, Margin = new Thickness(0, 0, 0, 4) }, _summaryText }
        };
        _content.Children.Add(_summaryBox);
        _content.Children.Add(_status);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _content, Margin = new Thickness(0, 0, 0, 8) };
        SetRow(_scroll, 2);
        Children.Add(_scroll);

        _summarize.Margin = new Thickness(0);
        _summarize.Click += (_, _) => _ = SummarizeAsync();
        var browser = Ui.Pill("В браузере");
        browser.Margin = new Thickness(6, 0, 0, 0);
        browser.Click += (_, _) => { if (_open(_item.Link)) IslandBus.RequestCollapse(); else _status.Text = "Не удалось открыть ссылку"; };
        _like.ToolTip = "Интересно";
        _like.Click += (_, _) => Reacted?.Invoke(_likeOn ? 0 : 1);
        _dislike.ToolTip = "Неинтересно: скрыть и показывать меньше такого";
        _dislike.Click += (_, _) => Reacted?.Invoke(-1);
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { _summarize, browser } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { _like, _dislike } };
        SetColumn(right, 1);
        buttons.Children.Add(left);
        buttons.Children.Add(right);
        SetRow(buttons, 3);
        Children.Add(buttons);

        SetReaction(reaction);
        if (Summaries.TryGetValue(item.Id, out var known)) ShowSummary(known);
        Loaded += (_, _) => _ = LoadArticleAsync();
        Unloaded += (_, _) => Stop();
    }

    private bool _likeOn;

    /// <summary>Подсветить лайк, если он стоит.</summary>
    public void SetReaction(int value)
    {
        _likeOn = value == 1;
        _like.Foreground = _likeOn ? Hues.Ink : Ui.Gray;
        _like.ToolTip = _likeOn ? "Убрать лайк" : "Интересно";
        _dislike.Foreground = Ui.Gray;
    }

    public void Stop()
    {
        _loadCts?.Cancel();
        _summaryCts?.Cancel();
    }

    // ───── Текст статьи ─────

    private async Task LoadArticleAsync()
    {
        if (_loadCts != null) return;
        var cts = _loadCts = new CancellationTokenSource();
        _status.Text = "Загружаю статью…";
        ArticleReader.Page page;
        try { page = await ArticleReader.FetchAsync(_item.Link, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        var text = page.Text;
        _articleText = text;
        // Обложка: сначала с самой страницы (крупнее), запасная из ленты; приходит отдельно от текста, текст ждать её не будет.
        string?[] candidates = [page.ImageUrl, _item.Image];
        var covers = candidates.OfType<string>().Distinct().ToList();
        if (covers.Count > 0) _ = ShowCoverAsync(covers, cts.Token);
        if (text == null)
        {
            _status.Text = "Текст статьи достать не получилось (сайт не отдаёт его без браузера). Нажми «Кратко»: ИИ откроет страницу сам, или открой статью в браузере.";
            return;
        }
        _status.Visibility = Visibility.Collapsed;
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var heading = block.StartsWith("## ", StringComparison.Ordinal);
            _content.Children.Add(new TextBlock
            {
                Text = heading ? block[3..] : block, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = heading ? Hues.Ink : BodyBrush, Margin = new Thickness(0, heading ? 6 : 0, 0, 9)
            });
        }
    }

    private const double CoverHeight = 160;

    // Тёмная заготовка стоит на месте сразу (текст не прыгает, когда картинка догрузится); не получилось скачать — заготовка убирается.
    private async Task ShowCoverAsync(List<string> urls, CancellationToken token)
    {
        var cover = new Border { Height = CoverHeight, CornerRadius = new CornerRadius(12), Background = SummaryBackground, Margin = new Thickness(0, 0, 0, 10) };
        _content.Children.Insert(0, cover);
        System.Windows.Media.Imaging.BitmapSource? image = null;
        try
        {
            foreach (var url in urls)
            {
                image = await ArticleReader.FetchImageAsync(url, token);
                if (image != null) break;
            }
        }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;
        if (image == null) { _content.Children.Remove(cover); return; }
        var picture = new Border
        {
            CornerRadius = new CornerRadius(12), Opacity = 0,
            Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill }
        };
        cover.Child = picture;
        picture.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(260))));
    }

    // ───── Кратко от ИИ ─────

    private async Task SummarizeAsync()
    {
        if (_summarizing) return;
        var key = OpenAiKeyStore.Load();
        if (key == null)
        {
            ShowSummary("Нужен ключ OpenAI: вставь его в настройках (шестерёнка справа, «ИИ и поиск»).");
            return;
        }
        _summarizing = true;
        _summarize.IsEnabled = false;
        _summaryCts?.Cancel();
        var cts = _summaryCts = new CancellationTokenSource();
        ShowSummary("Читаю и пересказываю…");
        var model = AppSettings.Load().OpenAiModel is { Length: > 0 } m ? m : OpenAiChat.DefaultModel;
        var reply = new System.Text.StringBuilder();
        var failed = false;
        try
        {
            await foreach (var ev in OpenAiChat.AskAsync([new ChatTurn(true, BuildPrompt())], key, model, cts.Token))
            {
                switch (ev.Kind)
                {
                    case ChatEventKind.Searching: if (reply.Length == 0) ShowSummary("Открываю страницу…"); break;
                    case ChatEventKind.Text: reply.Append(ev.Text); ShowSummary(reply.ToString()); break;
                    case ChatEventKind.Error:
                        failed = true;
                        ShowSummary(ev.AuthProblem ? "Ключ OpenAI не подошёл: вставь новый в настройках." : ev.Text);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { failed = true; ShowSummary("Не удалось получить пересказ: " + ex.Message); }
        finally
        {
            if (cts == _summaryCts) { _summarizing = false; _summarize.IsEnabled = true; }
        }
        if (!failed && reply.Length > 0) Summaries[_item.Id] = reply.ToString();
    }

    private string BuildPrompt()
    {
        const string how = "Перескажи эту новость по-русски очень кратко: 3–5 коротких строк, главное и почему это важно. Простым текстом, без заголовков, без вступлений и без списка источников.";
        if (_articleText is { Length: > 0 } text)
            return $"{how}\n\nЗаголовок: {_item.Title}\nИсточник: {_item.Source}\n\nТекст статьи:\n{(text.Length > 6000 ? text[..6000] : text)}";
        return $"{how}\n\nТекст статьи недоступен, открой страницу сам.\nЗаголовок: {_item.Title}\nАдрес: {_item.Link}";
    }

    private void ShowSummary(string text)
    {
        _summaryBox.Visibility = Visibility.Visible;
        _summaryText.Inlines.Clear();
        MiniMarkdown.AddInlines(_summaryText.Inlines, text, url => _open(url));
        _scroll.ScrollToTop();
    }
}
