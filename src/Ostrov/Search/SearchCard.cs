using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Search;

/// <summary>
/// Карточка быстрого вопроса на острове: строка ввода (курсор уже в ней), под ней ответ ИИ (OpenAI) в самом острове.
/// Enter — спросить (первый вопрос или уточнение: диалог продолжается), Ctrl+Enter или кнопка справа — искать то же в Google в браузере,
/// Esc — закрыть. Модель сама решает, искать ли в интернете. Ответ идёт потоком, его можно листать колесом, выделять и копировать.
/// Если в строке чистая арифметика («2+2*3», «15% от 200», «sqrt(16)»), справа серым появляется живой результат «= 8», Enter копирует число
/// в буфер и закрывает карточку, а запроса к ИИ нет (<see cref="Calculator"/>). Всё остальное идёт к ИИ как обычно.
/// Ключ OpenAI вставляется один раз прямо в строку (в первый раз карточка попросит); команда «/ключ» просит ввести новый.
/// Карточка закрывается сама, если окно острова потеряло фокус (клик в другое место) или не удалось его получить.
/// </summary>
internal sealed class SearchCard : Grid
{
    public const double CardWidth = 520, CardHeight = 84;
    /// <summary>
    /// Предельная высота области с диалогом и отступ под ней; остров с ответом растёт вниз вместе с текстом (от <see cref="MinAnswerHeight"/>)
    /// до <see cref="ExpandedHeight"/> (≈ 400 px, решение пользователя М-008), дальше ответ прокручивается.
    /// </summary>
    public const double AnswerHeight = 300, AnswerBottom = 16, MinAnswerHeight = 64;
    public const double ExpandedHeight = CardHeight + AnswerHeight + AnswerBottom;
    /// <summary>Высота пустой карточки: строка ввода и под ней ряд подсказок-чипов.</summary>
    public const double IdleHeight = CardHeight + 32;
    private const int MaxHistoryTurns = 10;

    private readonly TextBox _box = Ui.BareTextBox();
    private readonly TextBlock _placeholder;
    private readonly Button _googleButton = Ui.IconButton("", 13, 30);
    // Живой результат примера сразу за набранным текстом («2+2*3 = 8»). Пока в строке пример, Enter копирует число и закрывает карточку.
    private readonly TextBlock _result = new()
    {
        FontSize = 16, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230,
        Margin = new Thickness(5, 0, 0, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false
    };
    private Grid? _inner; // строка внутри плашки: от её ширины зависит, сколько места остаётся полю ввода
    private readonly DispatcherTimer _copiedClose = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private string? _liveResult; // число для буфера обмена; null — в строке не пример
    private bool _copied;
    private Border _edge = null!;                 // световая кайма вокруг строки: бежит, пока строка пуста и разговора ещё нет
    private readonly RotateTransform _edgeRotate = new(0, 0.5, 0.5);
    private StackPanel _chips = null!;             // подсказки под строкой (пока нет ответа)
    private readonly Border _cmdChip = new() { CornerRadius = new CornerRadius(9), Opacity = 0, IsHitTestVisible = false, Margin = new Thickness(-7, 7, -7, 7), Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) };
    private CommandMatch? _liveCommand; // распознанная команда («таймер 25», «пауза»); null — в строке не команда
    private readonly Func<string, bool> _openUrl;
    private readonly DispatcherTimer _giveUp = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<ChatTurn> _history = [];
    private AnswerView? _answer;
    private CancellationTokenSource? _cts;
    private string _lastQuery = "";
    private string? _pendingQuery;
    private bool _keyMode;
    private double _answerTarget; // нынешняя высота области ответа: карточка только растёт вместе с текстом
    private PlaceholderTyper _typer = null!;
    private Window? _window;
    private bool _wasActive;

    /// <summary>Брать ли фокус при показе. Выключается только в проверках, чтобы не отбирать фокус у окон пользователя.</summary>
    internal static bool AutoFocus { get; set; } = true;

    public bool IsClosed { get; private set; }

    /// <summary>Область с ответом показана (остров вытянут).</summary>
    internal bool AnswerShown { get; private set; }

    internal AnswerView? Answer => _answer;

    /// <summary>Сейчас идёт запрос (ответ ещё приходит).</summary>
    internal bool Busy { get; private set; }

    /// <param name="openUrl">Открыть адрес в браузере по умолчанию. false — не получилось.</param>
    public SearchCard(Func<string, bool> openUrl)
    {
        _openUrl = openUrl;

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(CardHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = "", FontFamily = Ui.IconFont, FontSize = 16, Foreground = Ui.Gray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 12, 0)
        };
        _box.FontSize = 16;
        // Поле ввода по ширине текста, чтобы результат примера вставал сразу за ним; слишком длинный текст прокручивается внутри (см. UpdateBoxMaxWidth).
        _box.HorizontalAlignment = HorizontalAlignment.Left;
        _box.VerticalAlignment = VerticalAlignment.Center;
        _placeholder = new TextBlock
        {
            Text = IdlePlaceholder, FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x99)),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0) // поле ввода сдвигает текст на пару пикселей: подсказка стоит там же
        };
        _typer = new PlaceholderTyper(_placeholder, BuildExamples());
        _googleButton.ToolTip = "Искать в Google в браузере (Ctrl+Enter)";
        _googleButton.Margin = new Thickness(8, 0, -9, 0);
        _googleButton.Click += (_, _) => OpenGoogle();

        // Колонки: значок | поле ввода (по тексту) | результат примера | пустое место | кнопка Google.
        var inner = _inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(icon, 0);
        SetColumn(_box, 1);
        SetColumn(_cmdChip, 1);
        SetColumn(_placeholder, 1);
        SetColumn(_result, 2);
        SetColumn(_googleButton, 4);
        inner.Children.Add(icon);
        inner.Children.Add(_cmdChip); // под полем ввода: серый фон за командой
        inner.Children.Add(_placeholder);
        inner.Children.Add(_box);
        inner.Children.Add(_result);
        inner.Children.Add(_googleButton);

        var pill = new Border
        {
            Height = 44, CornerRadius = new CornerRadius(22), Background = new SolidColorBrush(Hues.SurfaceColor),
            Padding = new Thickness(16, 0, 16, 1), VerticalAlignment = VerticalAlignment.Center, Child = inner
        };
        // Строка ввода и поверх неё световая кайма (видна, пока строка пуста).
        var pillHost = new Grid { Height = 44, VerticalAlignment = VerticalAlignment.Center };
        pillHost.Children.Add(pill);
        pillHost.Children.Add(BuildEdge());
        SetRow(pillHost, 0);
        Children.Add(pillHost);
        BuildChips();
        pill.MouseLeftButtonDown += (_, _) => _box.Focus(); // клик в пустое место плашки ставит курсор в строку
        inner.SizeChanged += (_, _) => UpdateBoxMaxWidth();

        _box.TextChanged += (_, e) =>
        {
            _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Подсказка с примерами печатается, только пока строка пуста: стёрли набранное — снова с «Спроси что угодно».
            if (_box.Text.Length > 0) _typer.Stop();
            else if (_history.Count == 0 && !_keyMode && IsLoaded && !_typer.Running) _typer.Start();
            UpdateResult();
            UpdateDecor();
            // Ключ вставлен целиком (за одно изменение ≥ 20 знаков): сохраняем сразу, без Enter. Ручной набор это не задевает.
            if (_keyMode && e.Changes.Any(c => c.AddedLength >= 20) && OpenAiKeyStore.LooksLikeKey(_box.Text.Trim()))
                SubmitKey(_box.Text.Trim());
        };
        _box.PreviewKeyDown += OnKeyDown;
        _copiedClose.Tick += (_, _) => { _copiedClose.Stop(); var after = _afterCloseAction; _afterCloseAction = null; Close(); if (after != null) _ = after(); };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _giveUp.Tick += (_, _) =>
        {
            _giveUp.Stop();
            if (_window is { IsActive: true }) _wasActive = true;
            else Close(); // окно так и не получило фокус: не оставляем карточку висеть
        };
    }

    // ───── Оформление пустого окна ─────

    private Border BuildEdge()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), RelativeTransform = _edgeRotate };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.34));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.66));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
        return _edge = new Border
        {
            CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1.2), BorderBrush = brush,
            IsHitTestVisible = false, Opacity = 0
        };
    }

    // Подсказки под строкой: клик сразу выполняет команду или задаёт вопрос ИИ (решение пользователя 2026-11-14). Их больше тридцати;
    // на экране четыре, они меняются при каждом открытии карточки и сами раз в несколько секунд, пока строка пуста.
    private sealed record Chip(string Text, bool Command);

    private static readonly Chip[] ChipPool =
    [
        // Команды
        new("Таймер 5 минут", true), new("Таймер 10 минут", true), new("Таймер 15 минут", true), new("Таймер 25 минут", true), new("Таймер 45 минут", true),
        new("Секундомер", true), new("Стоп секундомер", true),
        new("Пауза", true), new("Играй", true), new("Дальше", true), new("Назад", true),
        new("Громкость 30", true), new("Громкость 60", true),
        new("Открой погоду", true), new("Открой плеер", true), new("Открой время", true), new("Открой проекты", true),
        new("Итоги дня", true), new("Открой буфер", true), new("Открой новости", true), new("Открой настройки", true), new("Открой главную", true),
        // Вопросы к ИИ
        new("Курс доллара", false), new("Погода завтра", false), new("Что такое REST API?", false), new("Что такое вайбкодинг?", false),
        new("Время в Токио", false), new("Факт дня", false), new("Идея для пет-проекта", false), new("Объясни WebSocket", false),
        new("Анекдот про программистов", false), new("Как работает git rebase?", false), new("HDD или SSD?", false),
        new("Переведи: hello world", false), new("Совет по тайм-менеджменту", false), new("Что нового в ИИ?", false),
        // Возможности, которые ИИ выполняет сам (пересчёт валют с карточкой, напоминание)
        new("100 долларов в рубли", false), new("Сколько 50 евро в тенге", false), new("Напомни через час отдохнуть", false)
    ];

    private readonly DispatcherTimer _chipsTimer = new() { Interval = TimeSpan.FromSeconds(9) };
    private readonly Random _chipRandom = new();

    // Четыре подсказки: хотя бы одна команда и один вопрос, вместе помещаются в ширину карточки (по числу знаков), без повторов с прежними.
    private List<Chip> PickChips(IReadOnlyCollection<string> previous)
    {
        var pool = ChipPool.Where(c => !previous.Contains(c.Text)).OrderBy(_ => _chipRandom.Next()).ToList();
        var picked = new List<Chip>();
        var budget = 46;
        void Take(Chip? c)
        {
            if (c == null || picked.Count >= 4 || picked.Contains(c) || c.Text.Length > budget) return;
            picked.Add(c);
            budget -= c.Text.Length;
        }
        Take(pool.FirstOrDefault(c => c.Command));
        Take(pool.FirstOrDefault(c => !c.Command));
        foreach (var c in pool) Take(c);
        return picked.OrderBy(_ => _chipRandom.Next()).ToList();
    }

    private void BuildChips()
    {
        _chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -10, 0, 0), Height = 26 };
        FillChips(PickChips([]));
        SetRow(_chips, 1);
        Children.Add(_chips);
        _chipsTimer.Tick += (_, _) => RotateChips();
    }

    private void FillChips(List<Chip> chips)
    {
        _chips.Children.Clear();
        var fill = new SolidColorBrush(Hues.SurfaceColor);
        foreach (var chip in chips)
        {
            var button = Ui.Pill(chip.Text, padding: 12);
            button.Height = 26;
            button.FontSize = 12.5;
            button.Margin = new Thickness(3, 0, 3, 0);
            button.Background = fill;
            button.Foreground = Ui.Gray;
            var phrase = chip.Text;
            button.Click += (_, _) => RunChip(phrase);
            _chips.Children.Add(button);
        }
    }

    // Нажали подсказку: фраза встаёт в строку и сразу выполняется (команда) или уходит ИИ (вопрос).
    private void RunChip(string phrase)
    {
        if (_copied || IsClosed) return;
        _box.Text = phrase;
        _box.CaretIndex = phrase.Length;
        if (_liveCommand != null) RunCommand();
        else Ask(phrase);
    }

    // Раз в несколько секунд подсказки плавно сменяются новыми (только пока строка пуста и разговора нет).
    private void RotateChips()
    {
        if (IsClosed || _chips.Visibility != Visibility.Visible || _box.Text.Length > 0 || _copied || AnswerShown || _keyMode) return;
        var current = _chips.Children.OfType<Button>().Select(b => b.Content as string ?? "").ToList();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (IsClosed) return;
            FillChips(PickChips(current));
            _chips.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        };
        _chips.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Убрать подсказки (начался разговор или ввод ключа) и вернуть остров к высоте одной строки, если ответа ещё нет.</summary>
    private void HideChips()
    {
        if (_chips.Visibility == Visibility.Collapsed) return;
        _chips.Visibility = Visibility.Collapsed;
        if (!AnswerShown && !IsClosed) IslandBus.ShowOverlay(this, CardWidth, CardHeight);
    }

    // Кайма видна только в пустом окне: строка пуста, разговора нет. Начал печатать — плавно гаснет.
    private void UpdateDecor()
    {
        if (_edge == null) return;
        var idle = _box.Text.Length == 0 && _history.Count == 0 && !_keyMode && !AnswerShown;
        _edge.BeginAnimation(OpacityProperty, new DoubleAnimation(idle ? 1 : 0, TimeSpan.FromMilliseconds(250)));
    }

    private void SetCommandChip(bool on)
    {
        if ((_cmdChip.Opacity > 0.5) == on && !_cmdChip.HasAnimatedProperties) return;
        _cmdChip.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(160)));
    }

    private string IdlePlaceholder => _history.Count == 0 ? "Спроси что угодно" : "Уточнить…";

    private void SetPlaceholder(string text)
    {
        _typer.Stop();
        _placeholder.Text = text;
    }

    // Обычная подсказка: до первого вопроса печатаются примеры, после него стоит «Уточнить…».
    private void ShowIdlePlaceholder()
    {
        if (_history.Count == 0 && !_keyMode && _box.Text.Length == 0)
        {
            if (!_typer.Running) _typer.Start();
        }
        else SetPlaceholder(IdlePlaceholder);
    }

    /// <summary>Примеры для пустой строки: первым всегда «Спроси что угодно»; погода берёт город из вкладки «Погода».</summary>
    internal static IReadOnlyList<string> BuildExamples()
    {
        var city = AppSettings.Load().WeatherCity;
        return
        [
            "Спроси что угодно",
            string.IsNullOrWhiteSpace(city) ? "Какая погода завтра?" : $"Погода завтра: {city.Trim()}",
            "Сколько сейчас времени в Токио?",
            "Объясни, что такое WebSocket",
            "Курс доллара сегодня",
            "2+2*3 или 15% от 200",
            "Таймер 25 минут",
            "Открой погоду или проект",
            "Когда выходит следующий Battlefield?"
        ];
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = _box.Text.Trim();
            if (_keyMode) { SubmitKey(text); return; }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OpenGoogle(); return; }
            if (_copied) return; // результат уже скопирован, карточка вот-вот закроется
            var forceAsk = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift); // Shift+Enter: это вопрос к ИИ, даже если фраза похожа на команду или пример
            if (!forceAsk && _liveCommand != null) { RunCommand(); return; }
            if (!forceAsk && _liveResult != null) { CopyResult(); return; } // в строке пример: Enter копирует результат, к ИИ не идём
            if (text.Length == 0) return;
            if (text is "/ключ" or "/key") { EnterKeyMode(null, "Вставь новый ключ OpenAI (sk-…)"); return; }
            Ask(text);
        }
    }

    // ───── Калькулятор ─────

    private static readonly Brush ResultDim = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x99));

    /// <summary>Число, которое Enter положит в буфер обмена, пока в строке пример (проверки).</summary>

    /// <summary>Как положить текст в буфер обмена. Подменяется только в проверках, чтобы не трогать настоящий буфер.</summary>
    internal static Action<string> SetClipboardText { get; set; } = text => Clipboard.SetText(text);

    /// <summary>Если в строке пример («2+2*3», «15% от 200»), показывает справа серым «= 8» и подсказку про Enter; иначе прячет результат.</summary>
    private void UpdateResult()
    {
        if (_copied) return; // показано «Скопировано», карточка сейчас закроется
        string? display = null;
        _liveResult = null;
        if (!_keyMode && Calculator.TryEvaluate(_box.Text, out var value))
        {
            _liveResult = Calculator.FormatPlain(value);
            display = Calculator.FormatDisplay(value);
        }
        _liveCommand = !_keyMode && display == null ? CommandParser.TryParse(_box.Text) : null;
        SetCommandChip(_liveCommand != null);
        if (_liveCommand != null)
        {
            // Команда выделяется серым фоном за набранными словами (слова не дублируются), справа только значок Enter.
            _result.Inlines.Clear();
            _result.Inlines.Add(new Run("\u2002\u21B5") { Foreground = ResultDim, FontSize = 14 });
            _result.Visibility = Visibility.Visible;
            UpdateBoxMaxWidth();
            return;
        }
        if (display == null)
        {
            _result.Visibility = Visibility.Collapsed;
            UpdateBoxMaxWidth();
            return;
        }
        _result.Inlines.Clear();
        _result.Inlines.Add(new Run("=\u2009") { Foreground = ResultDim }); // тонкий пробел: число стоит вплотную к «=»
        _result.Inlines.Add(new Run(display) { Foreground = Hues.Ink });
        _result.Inlines.Add(new Run("  \u21B5") { Foreground = ResultDim, FontSize = 14 });
        _result.Visibility = Visibility.Visible;
        UpdateBoxMaxWidth();
    }

    /// <summary>Строке ввода отдаём всё место, кроме значка, кнопки Google и результата: длинный пример прокручивается внутри строки, а результат остаётся виден.</summary>
    private void UpdateBoxMaxWidth()
    {
        if (_inner is not { ActualWidth: > 1 } inner) return;
        var resultWidth = 0.0;
        if (_result.Visibility == Visibility.Visible)
        {
            _result.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            resultWidth = _result.DesiredSize.Width + _result.Margin.Left;
        }
        var free = inner.ActualWidth - inner.ColumnDefinitions[0].ActualWidth - inner.ColumnDefinitions[4].ActualWidth - resultWidth - 4;
        _box.MaxWidth = Math.Max(60, free);
    }

    /// <summary>Выполнить распознанную команду: на миг «✓ …» и закрытие карточки; при ошибке карточка остаётся с её текстом.</summary>
    internal async void RunCommand()
    {
        if (_liveCommand is not { } command || _copied) return;
        _copied = true;
        if (command.AfterClose)
        {
            // Открытие вкладки: сначала закрываем карточку, потом просим остров открыться на вкладке.
            ShowDone(command.Label);
            var run = command.Run;
            _afterCloseAction = async () => { await Task.Delay(450); Application.Current?.Dispatcher.BeginInvoke(() => { _ = run(); }); };
            return;
        }
        string? error;
        try { error = await command.Run(); }
        catch (Exception ex) { error = ex.Message; }
        if (error != null)
        {
            _copied = false;
            _result.Inlines.Clear();
            _result.Inlines.Add(new Run(error) { Foreground = ResultDim });
            return;
        }
        ShowDone(command.Label);
    }

    private Func<Task>? _afterCloseAction;

    private void ShowDone(string label)
    {
        _result.Inlines.Clear();
        _result.Inlines.Add(new Run("✓ Готово") { Foreground = Ui.Gray });
        _result.Visibility = Visibility.Visible;
        _copiedClose.Start();
    }

    /// <summary>Положить результат в буфер обмена, на миг показать «Скопировано» и закрыть карточку.</summary>
    internal void CopyResult()
    {
        if (_liveResult is not { } text || _copied) return;
        for (var attempt = 0; ; attempt++)
        {
            try { SetClipboardText(text); break; }
            catch (ExternalException) // буфер на миг занят другой программой
            {
                if (attempt < 3) { Thread.Sleep(60); continue; }
                _result.Inlines.Clear();
                _result.Inlines.Add(new Run("Не удалось скопировать") { Foreground = ResultDim });
                return;
            }
        }
        _copied = true;
        _result.Inlines.Clear();
        _result.Inlines.Add(new Run("✓ Скопировано") { Foreground = Ui.Gray });
        _result.Visibility = Visibility.Visible;
        _copiedClose.Start();
    }

    // ───── Ключ ─────

    private void EnterKeyMode(string? pendingQuery, string prompt)
    {
        _keyMode = true;
        HideChips();
        UpdateDecor();
        _pendingQuery = pendingQuery;
        _box.Text = "";
        SetPlaceholder(prompt);
    }

    internal void SubmitKey(string text)
    {
        if (text.Length == 0) return;
        if (!OpenAiKeyStore.LooksLikeKey(text))
        {
            _box.Text = "";
            SetPlaceholder("Это не ключ: он начинается с sk-");
            return;
        }
        if (!OpenAiKeyStore.Save(text))
        {
            _box.Text = "";
            SetPlaceholder("Не удалось сохранить ключ");
            return;
        }
        IslandBus.ForgetClipboardText(text); // ключ мог остаться в истории буфера обмена: убираем
        _box.Text = "";
        _keyMode = false;
        ShowIdlePlaceholder();
        if (_pendingQuery is { } q)
        {
            _pendingQuery = null;
            Ask(q);
        }
    }

    // ───── Вопрос и ответ ─────

    /// <summary>Задать вопрос (первый или уточнение): остров вытягивается, ответ идёт потоком.</summary>
    internal async void Ask(string query)
    {
        if (IsClosed || query.Length == 0) return;
        // «Итоги дня»: считаются на компьютере, без ИИ и без ключа.
        if (DaySummaryService.TryParse(query) is { } period) { ShowSummary(query, period); return; }
        // Пересчёт валют и погода тоже без ИИ: работают без ключа OpenAI и без VPN.
        if (LocalAnswers.TryCurrency(query) is { } money) { ShowLocal(query, "Считаю…", _ => Task.FromResult<FrameworkElement>(new CurrencyCard(money.Amount, money.From, money.To))); return; }
        if (LocalAnswers.TryWeather(query) is { } day) { ShowLocal(query, "Смотрю прогноз…", ct => LocalAnswers.WeatherCardAsync(day, ct)); return; }
        var key = OpenAiKeyStore.Load();
        if (key == null)
        {
            EnterKeyMode(query, "Вставь ключ OpenAI (sk-…)");
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _lastQuery = query;
        var answer = EnsureAnswer();
        _history.Add(new ChatTurn(true, query));
        var turn = answer.BeginTurn(query);
        _box.Text = "";
        ShowIdlePlaceholder();

        var reply = new StringBuilder();
        var notes = new List<string>(); // что сделали по просьбе ИИ (запись для истории диалога)
        var failed = false;
        Busy = true;
        try
        {
            var model = AppSettings.Load().OpenAiModel is { Length: > 0 } m ? m : OpenAiChat.DefaultModel;
            await foreach (var ev in OpenAiChat.AskAsync(Recent(), key, model, cts.Token, actions: true))
            {
                switch (ev.Kind)
                {
                    case ChatEventKind.Searching: turn.SetStatus("Ищу в интернете…"); break;
                    case ChatEventKind.Text: reply.Append(ev.Text); turn.AppendText(ev.Text); break;
                    case ChatEventKind.Source: if (ev.Url != null) turn.AddSource(ev.Url, ev.Title); break;
                    case ChatEventKind.Incomplete: turn.SetNote("Ответ оборван: он получился слишком длинным."); break;
                    case ChatEventKind.Action:
                        // ИИ понял просьбу как действие (напоминание, после рендера, пересчёт валют): выполняем и показываем карточку.
                        if (AiActions.Run(ev.Text, ev.Args) is { } done) { turn.AddWidget(done.Card); notes.Add(done.Note); }
                        break;
                    case ChatEventKind.Error:
                        failed = true;
                        turn.SetError(ev.Text);
                        if (ev.AuthProblem)
                        {
                            OpenAiKeyStore.Clear();
                            EnterKeyMode(query, "Ключ не подошёл. Вставь новый (sk-…)");
                        }
                        break;
                    case ChatEventKind.Done: turn.Finish(); break;
                }
            }
        }
        catch (OperationCanceledException) { return; } // карточку закрыли или задали новый вопрос
        catch (Exception ex)
        {
            failed = true;
            turn.SetError("Не удалось получить ответ: " + ex.Message);
        }

        if (cts != _cts) return; // уже идёт более новый запрос
        Busy = false;
        if (!failed && reply.Length == 0 && notes.Count > 0)
        {
            // Ответ был только действием: в истории остаётся короткая запись о нём, чтобы уточнение («а на час позже») имело смысл.
            _history.Add(new ChatTurn(false, string.Join(" ", notes)));
            turn.Finish();
        }
        else if (failed || reply.Length == 0)
        {
            _history.RemoveAt(_history.Count - 1); // неудачный вопрос в историю диалога не берём
            if (!failed) turn.Finish();
        }
        else _history.Add(new ChatTurn(false, reply.ToString()));
    }

    // Ответ без ИИ: одна карточка (курс, погода). В историю диалога для ИИ не попадает.
    private async void ShowLocal(string query, string status, Func<CancellationToken, Task<FrameworkElement>> build)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _lastQuery = query;
        var answer = EnsureAnswer();
        var turn = answer.BeginTurn(query);
        _box.Text = "";
        ShowIdlePlaceholder();
        turn.SetStatus(status);
        Busy = true;
        try
        {
            var card = await build(cts.Token);
            if (cts != _cts) return;
            turn.AddWidget(card);
            turn.Finish();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts == _cts) turn.SetError("Не получилось: " + ex.Message);
        }
        finally { if (cts == _cts) Busy = false; }
    }

    // Итоги дня карточками в ответе (данные из журнала дня и git). В историю диалога для ИИ не попадают.
    private async void ShowSummary(string query, SummaryPeriod period)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _lastQuery = query;
        var answer = EnsureAnswer();
        var turn = answer.BeginTurn(query);
        _box.Text = "";
        ShowIdlePlaceholder();
        turn.SetStatus("Собираю итоги…");
        Busy = true;
        try
        {
            var cards = await DaySummaryService.BuildAsync(period);
            if (cts != _cts) return;
            foreach (var card in cards) turn.AddWidget(card);
            turn.Finish();
        }
        catch (Exception ex)
        {
            if (cts == _cts) turn.SetError("Не удалось собрать итоги: " + ex.Message);
        }
        finally { if (cts == _cts) Busy = false; }
    }

    private IReadOnlyList<ChatTurn> Recent() =>
        _history.Count <= MaxHistoryTurns * 2 ? _history : _history.Skip(_history.Count - MaxHistoryTurns * 2).ToList();

    private AnswerView EnsureAnswer()
    {
        if (_answer == null)
        {
            _answer = new AnswerView(url => { if (_openUrl(url)) Close(); });
            _answer.ContentHeightChanged += OnAnswerContentHeight;
            SetRow(_answer, 1);
            Children.Add(_answer);
        }
        if (!AnswerShown)
        {
            // Сначала место только под «Думаю…», дальше остров растёт вместе с текстом (OnAnswerContentHeight).
            AnswerShown = true;
            _chips.Visibility = Visibility.Collapsed;
            UpdateDecor();
            _answerTarget = MinAnswerHeight;
            _answer.Height = MinAnswerHeight;
            _answer.Margin = new Thickness(0, 0, 0, AnswerBottom);
            IslandBus.ShowOverlay(this, CardWidth, CardHeight + MinAnswerHeight + AnswerBottom);
        }
        return _answer;
    }

    // Текст ответа вырос: область ответа и остров плавно вытягиваются вниз (до предела). Только растём, чтобы карточка не дёргалась при перерисовках.
    private void OnAnswerContentHeight(double extent)
    {
        if (IsClosed || _answer == null) return;
        var target = Math.Clamp(Math.Ceiling(extent) + 6, MinAnswerHeight, AnswerHeight);
        if (target < _answerTarget + 3) return;
        _answerTarget = target;
        _answer.Height = target;
        IslandBus.ShowOverlay(this, CardWidth, CardHeight + target + AnswerBottom);
    }

    /// <summary>Высота области ответа сейчас (проверки).</summary>

    /// <summary>Искать в Google в браузере: то, что набрано в строке, а если строка пустая — последний вопрос.</summary>
    internal void OpenGoogle()
    {
        var query = _box.Text.Trim();
        if (query.Length == 0) query = _lastQuery;
        if (query.Length == 0) return;
        if (_openUrl(SearchService.BuildUrl(query))) Close();
        else SetPlaceholder("Не удалось открыть браузер");
    }

    // ───── Окно ─────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        _edgeRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever });
        _chipsTimer.Start();
        UpdateDecor();
        if (_box.Text.Length == 0 && _history.Count == 0 && !_keyMode) _typer.Start(); // подсказка с примерами
        if (_window == null) return;
        _window.Activated += OnActivated;
        _window.Deactivated += OnDeactivated;
        if (!AutoFocus) return;
        // Сочетание клавиш не клик: окно острова фокус само не берёт, поэтому берём его явно.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsClosed) return;
            BringToFront(_window);
            _box.Focus();
            Keyboard.Focus(_box);
            _giveUp.Start();
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Карточку сняли (в том числе запросом Claude): ничего больше не трогаем, чужую карточку закрывать нельзя.
        IsClosed = true;
        _edgeRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _chipsTimer.Stop();
        _giveUp.Stop();
        _typer.Stop();
        _cts?.Cancel();
        if (_window != null)
        {
            _window.Activated -= OnActivated;
            _window.Deactivated -= OnDeactivated;
            _window = null;
        }
    }

    private void OnActivated(object? sender, EventArgs e) => _wasActive = true;

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_wasActive) Close();
    }

    public void Close()
    {
        if (IsClosed) return;
        IsClosed = true;
        _chipsTimer.Stop();
        _giveUp.Stop();
        _copiedClose.Stop();
        _cts?.Cancel();
        IslandBus.ClearOverlay();
    }

    /// <summary>Вывести окно острова на передний план. Если система не даёт (защита от кражи фокуса), «нажимаем» Alt от своего процесса.</summary>
    private static void BringToFront(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!window.IsActive) window.Activate();
        if (GetForegroundWindow() == hwnd) return;
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        SetForegroundWindow(hwnd);
    }

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
