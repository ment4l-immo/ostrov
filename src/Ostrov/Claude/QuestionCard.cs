using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Claude;

/// <summary>
/// Вопросы Claude (AskUserQuestion) на острове: по одному вопросу на экране. Одиночный выбор: нажал вариант, пошёл следующий.
/// Мультивыбор: отмечаешь варианты и жмёшь «Готово». «Другое…» открывает поле ввода прямо на острове (Enter отправляет).
/// «В VS Code» отпускает вопрос обычному окну. Пояснения к вариантам серым мелким под названием (до 2 строк).
/// Если вопрос или вариант обрезан («…»), наведение на него на треть секунды раскрывает всё сразу: полный текст вопроса, названия и
/// пояснения всех вариантов (остров вытягивается вниз, до 420 px, дальше колесо). Раскрытие внутри того же острова; сворачивается, когда
/// курсор ушёл с карточки (М-009: открывать по наведению, раскрывать всё сразу).
/// </summary>
internal sealed class QuestionCard : Grid, IPromptCard
{
    public const double CardWidth = 520;
    private const double MinCardHeight = 150, MaxCardHeight = 300, MaxExpandedHeight = 420;
    private const double QuestionMaxHeight = 40, DescriptionMaxHeight = 32; // две строки

    private sealed record Option(string Label, string Description);
    private sealed record Question(string Text, string Header, bool Multi, List<Option> Options);

    private readonly ClaudeRequest _request;
    private readonly List<Question> _questions;
    private readonly Action<IReadOnlyList<KeyValuePair<string, string>>> _onDone;
    private readonly Action _onVsCode;
    private readonly List<KeyValuePair<string, string>> _answers = [];

    private int _index;
    private int _queued;
    private TextBlock _header = null!;
    private readonly HashSet<string> _selected = [];
    private TextBox? _customBox;
    private Button? _doneButton;

    // Раскрытие по наведению (М-009).
    private readonly DispatcherTimer _hoverOpen = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _hoverClose = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private TextBlock? _questionBlock;
    private ScrollViewer? _scroll;
    private readonly List<TextBlock> _labels = [], _descriptions = [];
    private bool _expanded;

    /// <summary>Сейчас раскрыто полностью (проверки).</summary>
    internal bool IsExpanded => _expanded;

    /// <summary>Высота острова под текущий экран вопроса (не больше ~300, раскрытого до 420: что не влезло, прокручивается колесом).</summary>
    public double PreferredHeight { get; private set; }

    /// <summary>Экран вопроса сменился и нужна другая высота острова.</summary>
    public event Action? ScreenChanged;

    private QuestionCard(ClaudeRequest request, List<Question> questions,
        Action<IReadOnlyList<KeyValuePair<string, string>>> onDone, Action onVsCode)
    {
        _request = request;
        _questions = questions;
        _onDone = onDone;
        _onVsCode = onVsCode;
        Background = Brushes.Transparent; // вся карточка «под мышью», а не только её содержимое: иначе щели между строками считались бы уходом
        _hoverOpen.Tick += (_, _) => { _hoverOpen.Stop(); SetExpanded(true); };
        _hoverClose.Tick += (_, _) => { _hoverClose.Stop(); if (_expanded && !IsMouseOver) SetExpanded(false); };
        MouseEnter += (_, _) => _hoverClose.Stop();
        MouseLeave += (_, _) => { _hoverOpen.Stop(); if (_expanded) { _hoverClose.Stop(); _hoverClose.Start(); } };
        Unloaded += (_, _) => { _hoverOpen.Stop(); _hoverClose.Stop(); };
        BuildScreen();
    }

    /// <summary>Создаёт карточку, если ввод похож на вопросы Claude; иначе null (тогда вопрос лучше отпустить окну VS Code).</summary>
    public static QuestionCard? TryCreate(ClaudeRequest request,
        Action<IReadOnlyList<KeyValuePair<string, string>>> onDone, Action onVsCode)
    {
        var questions = Parse(request.Input);
        return questions is { Count: > 0 } ? new QuestionCard(request, questions, onDone, onVsCode) : null;
    }

    public void SetQueued(int count)
    {
        _queued = count;
        UpdateHeader();
    }

    private static List<Question>? Parse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<Question>();
        foreach (var q in qs.EnumerateArray())
        {
            var text = Str(q, "question");
            if (string.IsNullOrWhiteSpace(text)) return null;
            var options = new List<Option>();
            if (q.ValueKind == JsonValueKind.Object && q.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array)
                foreach (var o in os.EnumerateArray())
                {
                    var label = Str(o, "label");
                    if (string.IsNullOrWhiteSpace(label)) return null;
                    options.Add(new Option(label, Str(o, "description") ?? ""));
                }
            if (options.Count == 0) return null;
            var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
            list.Add(new Question(text, Str(q, "header") ?? "", multi, options));
        }
        return list;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // ---------- экран одного вопроса ----------

    private void BuildScreen()
    {
        Children.Clear();
        RowDefinitions.Clear();
        _selected.Clear();
        _customBox = null;
        _doneButton = null;
        _expanded = false; // новый экран всегда обычный
        _hoverOpen.Stop();
        _hoverClose.Stop();
        _labels.Clear();
        _descriptions.Clear();
        // Снизу у мультивыбора побольше: кнопка «Готово» иначе прижата к нижнему краю острова.
        Margin = new Thickness(0, 16, 0, 16);

        var q = _questions[_index];
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // шапка
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // текст вопроса
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // варианты
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // «Готово» (мультивыбор)

        // Шапка: проект · подзаголовок · «1 из 3» · «ещё N», справа «В VS Code».
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header = new TextBlock
        {
            FontSize = 12.5,
            Foreground = Ui.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // «В VS Code» только на первом экране: окно VS Code открывает вопросы заново и ответы с острова туда не переносятся,
        // поэтому после первого ответа вместо неё «Назад» (решение пользователя).
        var first = _index == 0;
        var headerButton = Ui.Pill(first ? "В VS Code" : "Назад", padding: 10);
        headerButton.Height = 24;
        headerButton.FontSize = 12;
        headerButton.Margin = new Thickness(12, 0, 0, 0);
        headerButton.Click += (_, _) => { if (first) _onVsCode(); else GoBack(); };
        SetColumn(headerButton, 1);
        top.Children.Add(_header);
        top.Children.Add(headerButton);
        SetRow(top, 0);
        Children.Add(top);
        UpdateHeader();

        var question = new TextBlock
        {
            Text = q.Text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Hues.Ink,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 20,
            MaxHeight = QuestionMaxHeight
        };
        _questionBlock = question;
        question.MouseEnter += (_, _) => StartHover(IsClipped(question));
        question.MouseLeave += (_, _) => _hoverOpen.Stop();
        SetRow(question, 1);
        Children.Add(question);

        // Варианты. Колесо мыши прокручивает, если их много (полоса прокрутки не показывается).
        // Прокрутка шире текста на 10 px с каждой стороны, чтобы подсветка строки была со скруглёнными краями и не обрезалась.
        var list = new StackPanel();
        foreach (var option in q.Options)
            list.Children.Add(MakeOptionRow(q, option));
        var scroll = _scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(-10, 10, -10, 0),
            Focusable = false
        };
        SetRow(scroll, 2);
        Children.Add(scroll);

        // Нижняя строка: «Другое…» слева, у мультивыбора справа «Готово».
        var footer = new Grid { Margin = new Thickness(-10, 0, -10, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var other = MakeOtherRow(q);
        other.Margin = new Thickness(0);
        other.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(other);
        if (q.Multi)
        {
            _doneButton = Ui.Pill("Готово", accent: PermissionCard.Coral);
            _doneButton.Margin = new Thickness(12, 0, 10, 0);
            _doneButton.IsEnabled = false;
            _doneButton.Click += (_, _) => FinishMulti(q);
            SetColumn(_doneButton, 1);
            footer.Children.Add(_doneButton);
        }
        SetRow(footer, 3);
        Children.Add(footer);

        Measure(new Size(CardWidth - 48, double.PositiveInfinity)); // 48 — поля острова слева и справа
        PreferredHeight = Math.Clamp(DesiredSize.Height, MinCardHeight, MaxCardHeight);
    }

    private void UpdateHeader()
    {
        var q = _questions[_index];
        var parts = new List<string> { _request.Project };
        if (!string.IsNullOrWhiteSpace(q.Header)) parts.Add(q.Header);
        if (_questions.Count > 1) parts.Add($"{_index + 1} из {_questions.Count}");
        if (_queued > 0) parts.Add($"ещё {_queued}");
        _header.Text = string.Join(" · ", parts);
    }

    private OptionRow MakeOptionRow(Question q, Option option)
    {
        var row = new OptionRow { Key = option.Label };
        var texts = MakeLabel(option);
        row.Child = RowContent(q.Multi, texts, out var setMarker);
        // Наведение на строку, у которой название или пояснение обрезано, через треть секунды раскрывает всё сразу.
        row.MouseEnter += (_, _) => StartHover(texts.Children.OfType<TextBlock>().Any(IsClipped));
        row.MouseLeave += (_, _) => _hoverOpen.Stop();
        row.Clicked += () =>
        {
            if (!q.Multi) { Choose(q, option.Label); return; }
            if (!_selected.Remove(option.Label)) _selected.Add(option.Label);
            row.Selected = _selected.Contains(option.Label);
            setMarker(row.Selected);
            UpdateDone();
        };
        return row;
    }

    private StackPanel MakeLabel(Option option)
    {
        var stack = new StackPanel();
        var label = new TextBlock { Text = option.Label, FontSize = 14, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
        _labels.Add(label);
        stack.Children.Add(label);
        if (!string.IsNullOrWhiteSpace(option.Description))
        {
            var description = new TextBlock
            {
                Text = option.Description,
                FontSize = 12.5,
                Foreground = Ui.Gray,
                Margin = new Thickness(0, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                LineHeight = 16,
                MaxHeight = DescriptionMaxHeight
            };
            _descriptions.Add(description);
            stack.Children.Add(description);
        }
        return stack;
    }

    // ---------- раскрытие по наведению (М-009) ----------

    // Курсор встал на обрезанный вопрос или вариант: раскрываем через треть секунды (если он не ушёл раньше).
    private void StartHover(bool clipped)
    {
        _hoverClose.Stop();
        if (_expanded || !clipped) return;
        _hoverOpen.Stop();
        _hoverOpen.Start();
    }

    /// <summary>Текст обрезан по высоте или по ширине (виден «…»): полный размер больше того, что показано.</summary>
    private static bool IsClipped(TextBlock block)
    {
        var width = block.ActualWidth;
        if (width <= 0 || string.IsNullOrEmpty(block.Text)) return false;
        var text = new FormattedText(block.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip);
        if (block.TextWrapping == TextWrapping.NoWrap) return text.WidthIncludingTrailingWhitespace > width + 0.5;
        text.MaxTextWidth = width;
        if (block.LineHeight > 0 && block.LineStackingStrategy == LineStackingStrategy.BlockLineHeight) text.LineHeight = block.LineHeight;
        return text.Height > block.ActualHeight + 0.5;
    }

    private void SetExpanded(bool on)
    {
        if (_expanded == on || _questionBlock == null) return;
        _expanded = on;
        _questionBlock.MaxHeight = on ? double.PositiveInfinity : QuestionMaxHeight;
        foreach (var d in _descriptions) d.MaxHeight = on ? double.PositiveInfinity : DescriptionMaxHeight;
        foreach (var l in _labels) l.TextWrapping = on ? TextWrapping.Wrap : TextWrapping.NoWrap;
        // Нужную высоту берём из настоящей раскладки (после смены текста): сколько карточке не хватает (или остаётся лишним) до полной
        // высоты списка вариантов. Предварительный Measure тут недостоверен: у прокручиваемого списка он не учитывает изменение.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_expanded != on || _scroll == null) return;
            UpdateLayout();
            var needed = ActualHeight + Margin.Top + Margin.Bottom - _scroll.ViewportHeight + _scroll.ExtentHeight;
            PreferredHeight = Math.Clamp(Math.Ceiling(needed), MinCardHeight, on ? MaxExpandedHeight : MaxCardHeight);
            ScreenChanged?.Invoke(); // остров вытягивается (или возвращается) по высоте
        });
    }

    private static readonly Brush CoralBrush = new SolidColorBrush(PermissionCard.Coral);
    private const double MarkerBoxHeight = 20; // высота первой строки текста: метка выравнивается по ней

    // Содержимое строки: слева метка выбора (круг у одиночного выбора, квадрат с галочкой у мультивыбора), справа текст.
    // centerMarker: метка по центру строки (для однострочной «Другое…»), иначе по первой строке текста (у вариантов с пояснением).
    private static Grid RowContent(bool multi, UIElement text, out Action<bool> setMarker, bool centerMarker = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var host = new Grid { Height = MarkerBoxHeight, VerticalAlignment = centerMarker ? VerticalAlignment.Center : VerticalAlignment.Top };
        if (multi)
        {
            // Квадрат с галочкой: значки Segoe Fluent Icons CheckboxComposite (E73A) и Checkbox (E739).
            var glyph = new TextBlock { FontFamily = Ui.IconFont, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            host.Children.Add(glyph);
            setMarker = on =>
            {
                glyph.Text = ((char)(on ? 0xE73A : 0xE739)).ToString();
                glyph.Foreground = on ? CoralBrush : Ui.Gray;
            };
        }
        else
        {
            // Круг; у выбранного коралловое кольцо и точка внутри.
            var ring = new Ellipse { Width = 16, Height = 16, StrokeThickness = 1.5, Stroke = Ui.Gray, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = CoralBrush, Margin = new Thickness(4, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            host.Children.Add(ring);
            host.Children.Add(dot);
            setMarker = on =>
            {
                ring.Stroke = on ? CoralBrush : Ui.Gray;
                dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            };
        }
        setMarker(false);

        SetColumn(text, 1);
        grid.Children.Add(host);
        grid.Children.Add(text);
        return grid;
    }

    // «Другое…»: по нажатию превращается в поле ввода. Enter: у одиночного выбора отправляет ответ, у мультивыбора — как «Готово».
    private OptionRow MakeOtherRow(Question q)
    {
        var row = new OptionRow { Key = "Другое…" };
        var label = new TextBlock { Text = "Другое…", FontSize = 14, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center };
        var content = RowContent(q.Multi, label, out var setMarker, centerMarker: true);
        row.Child = content;
        var editing = false;

        row.Clicked += () =>
        {
            if (editing) return;
            editing = true;
            var box = Ui.BareTextBox();
            box.Foreground = Hues.Ink;
            box.FontSize = 14;
            _customBox = box;
            var frame = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 4, 8, 5),
                Child = box
            };
            // Вместо надписи «Другое…» появляется поле ввода (метка слева остаётся).
            content.Children.Remove(label);
            SetColumn(frame, 1);
            content.Children.Add(frame);

            box.TextChanged += (_, _) =>
            {
                var has = box.Text.Trim().Length > 0;
                row.Selected = q.Multi && has;
                setMarker(has); // у одиночного выбора круг тоже заполняется, пока что-то введено
                UpdateDone();
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    var text = box.Text.Trim();
                    if (text.Length == 0) return;
                    if (q.Multi) FinishMulti(q); else Choose(q, text);
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    BuildScreen(); // вернуть экран вопроса как был (введённое «Другое» сбрасывается)
                    ScreenChanged?.Invoke();
                }
            };

            // Окно острова не берёт фокус само; по клику оно уже активно, но клавиатурный фокус ставим явно.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (Window.GetWindow(this) is { IsActive: false } window) window.Activate();
                box.Focus();
                Keyboard.Focus(box);
            }));
        };
        return row;
    }

    private void UpdateDone()
    {
        if (_doneButton == null) return;
        _doneButton.IsEnabled = _selected.Count > 0 || (_customBox?.Text.Trim().Length ?? 0) > 0;
    }

    // ---------- ответы ----------

    private void FinishMulti(Question q)
    {
        // Подписи в порядке вариантов, свой текст в конце; всё через «, ».
        var picked = q.Options.Where(o => _selected.Contains(o.Label)).Select(o => o.Label).ToList();
        var custom = _customBox?.Text.Trim();
        if (!string.IsNullOrEmpty(custom)) picked.Add(custom);
        if (picked.Count == 0) return;
        Record(q, string.Join(", ", picked));
    }

    private void Choose(Question q, string answer) => Record(q, answer);

    // К предыдущему вопросу: его ответ сбрасывается, отвечаем заново.
    private void GoBack()
    {
        if (_index == 0) return;
        _index--;
        if (_answers.Count > _index) _answers.RemoveRange(_index, _answers.Count - _index);
        BuildScreen();
        ScreenChanged?.Invoke();
    }

    private void Record(Question q, string answer)
    {
        _answers.Add(new KeyValuePair<string, string>(q.Text, answer));
        _index++;
        if (_index >= _questions.Count)
        {
            _onDone(_answers);
            return;
        }
        BuildScreen();
        ScreenChanged?.Invoke();
    }
}

/// <summary>Строка варианта: подсвечивается при наведении, выбранная — коралловым оттенком.</summary>
internal sealed class OptionRow : Border
{
    private static readonly Brush HoverBrush = Frozen(0x26, 0xFF, 0xFF, 0xFF);
    private static readonly Brush SelectedBrush = Frozen(0x40, 0xD9, 0x77, 0x57);
    private static readonly Brush SelectedHoverBrush = Frozen(0x59, 0xD9, 0x77, 0x57);

    private bool _selected, _hover;

    public object? Key { get; init; }
    public event Action? Clicked;

    public OptionRow()
    {
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 3, 10, 3);
        Margin = new Thickness(0, 0, 0, 2);
        Background = Brushes.Transparent; // прозрачный, но кликабельный
        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => { _hover = true; Refresh(); };
        MouseLeave += (_, _) => { _hover = false; Refresh(); };
        MouseLeftButtonDown += (_, e) => { e.Handled = true; Click(); };
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Refresh(); }
    }

    public void Click() => Clicked?.Invoke();

    private void Refresh() =>
        Background = _selected ? (_hover ? SelectedHoverBrush : SelectedBrush) : _hover ? HoverBrush : Brushes.Transparent;

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
