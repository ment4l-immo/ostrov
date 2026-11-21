using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Player.Lyrics;

/// <summary>
/// Текст песни. С таймингами: текущая строка белая и чуть крупнее, остальные бледные; прокрутка плавно идёт за песней
/// (текущая строка держится на уровне нижней грани обложки). Колесом или перетаскиванием текст можно листать; если листание остановилось
/// на 3 секунды, текст возвращается к текущей строке. Без таймингов: ровный текст без подсветки, листается свободно.
/// Края плавно гаснут. Кадры идут, только пока вид виден.
/// </summary>
internal sealed class LyricsView : Grid
{
    // Текст начинается сверху: первая строка стоит у верхнего края и не двигается, пока подсвечена. Прокрутка начинается, только когда
    // подсвеченная строка доходит до линии фокуса (М-018, решение пользователя), и дальше держит её там. Линия фокуса стоит
    // на уровне нижней грани обложки (вид стоит справа от неё, верх у них общий): середина строки на FocusY = низ обложки − 8 px,
    // чтобы одна строка опиралась низом примерно на эту грань. Подкрутить можно одним числом.
    // Верхний край гаснет лишь у уже прокрученного текста (TopFadeDistance): иначе первая строка тонула бы в затемнении.
    // Бледные строки на 42 %: на сером фоне 34 % читались плохо.
    private const double FontPx = 19, LineGap = 14, TopInset = 2, TopFadeDistance = 30;
    private static readonly double FocusY = PlayerLayout.CoverSize - 8;

    // Линия фокуса в пикселях от верха вида (в совсем низком виде не ниже его середины).
    private double FocusLine => Math.Min(FocusY, ActualHeight * 0.5);
    private const double DimOpacity = 0.42, PlainOpacity = 0.9, UpNextLevel = 0.55;
    private static readonly TimeSpan ManualHold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Anticipation = TimeSpan.FromMilliseconds(200); // строка загорается чуть раньше: читать надо заранее
    // Apple Music отдаёт позицию с запаздыванием: текст шёл на 1–2 с позже звука на всех песнях (замечание пользователя).
    // Поэтому подсветка, слова и прокрутка всё время сдвинуты вперёд на это время. Если текст стал забегать, число уменьшить.
    private static readonly TimeSpan SyncLead = TimeSpan.FromMilliseconds(1500);

    private sealed class Line
    {
        public required TextBlock Block { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TimeSpan Time { get; init; }
        public IReadOnlyList<LyricWord>? Words { get; init; }   // слова с таймингами, если база их знает
        public Run[]? Runs { get; init; }
        public int[]? WordState { get; init; }                  // 0..Steps — доля заливки слова, −1 — ещё не выставлено
        public double Level;        // 0 — бледная, 1 — текущая
        public double Applied = -1;
    }

    // Слова текущей строки: спетые белые, будущие бледные, текущее заливается слева направо с мягким краем (шаг 1/Steps).
    private const int Steps = 32;
    // Ещё не спетые слова текущей строки чуть темнее (34 % вместо 42 %): граница между спетым и будущим заметнее (пожелание пользователя).
    private static readonly Color DimColor = Color.FromArgb(0x57, 0xFF, 0xFF, 0xFF);
    private static readonly Brush WhiteInk = Hues.Ink;
    private static readonly Brush DimInk = Freeze(new SolidColorBrush(DimColor));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static Brush Sweep(double p)
    {
        const double soft = 0.10; // край заливки чуть резче: слово «зажигается» отчётливее
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop(Colors.White, 0));
        brush.GradientStops.Add(new GradientStop(Colors.White, Math.Max(0, p - soft / 2)));
        brush.GradientStops.Add(new GradientStop(DimColor, Math.Min(1, p + soft / 2)));
        brush.GradientStops.Add(new GradientStop(DimColor, 1));
        brush.Freeze();
        return brush;
    }

    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false, CanContentScroll = false
    };
    private readonly StackPanel _stack = new();
    private readonly List<Line> _lines = [];
    private readonly Spring _spring = new(dampingRatio: 1.0, responseSeconds: 0.5);

    private bool _synced, _attached, _manual, _dragging;
    private int _current = -1;
    private DateTime _manualUntil;
    private double _dragStartY, _dragStartOffset;
    private TimeSpan _lastFrame;

    // Верхняя точка маски: непрозрачная, пока текст стоит в начале, и гаснет по мере прокрутки.
    private readonly GradientStop _topFade = new(Colors.Black, 0);
    private readonly LinearGradientBrush _mask;

    public LyricsView()
    {
        Background = Brushes.Transparent; // чтобы ловить колесо и перетаскивание
        _scroll.Content = _stack;
        // Маска по абсолютной высоте вида (а не «по границам содержимого»): в конце песни видна только верхняя часть текста,
        // и относительная маска сжималась бы до неё, затемняя последнюю строку прямо на середине.
        _mask = new LinearGradientBrush(
            [_topFade, new GradientStop(Colors.Black, 0.12), new GradientStop(Colors.Black, 0.86), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)],
            new Point(0, 0), new Point(0, 1)) { MappingMode = BrushMappingMode.Absolute };
        _scroll.OpacityMask = _mask;
        Children.Add(_scroll);

        _spring.Changed += v =>
        {
            _scroll.ScrollToVerticalOffset(v);
            UpdateTopFade(v);
        };
        SizeChanged += (_, _) =>
        {
            UpdatePadding();
            _mask.EndPoint = new Point(0, Math.Max(1, ActualHeight));
            // Высота вида меняется при раскрытии режима текста: линия фокуса могла сдвинуться (в низком виде), текущую строку надо поставить на неё заново.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Recenter);
        };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Attach(); else Detach(); };
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Заменить текст (новая песня). Прокрутка встаёт в начало.</summary>
    public void SetLyrics(IReadOnlyList<LyricLine> lines, bool synced)
    {
        _stack.Children.Clear();
        _lines.Clear();
        _synced = synced;
        _current = -1; // старый текст убран, сбрасывать слова у него не нужно
        _manual = false;
        foreach (var l in lines)
        {
            var scale = new ScaleTransform(1, 1);
            var block = new TextBlock
            {
                FontSize = FontPx, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, LineGap),
                RenderTransformOrigin = new Point(0, 0.5), RenderTransform = scale,
                Opacity = synced ? DimOpacity : PlainOpacity
            };
            Run[]? runs = null;
            int[]? states = null;
            if (synced && l.Words is { Count: > 0 })
            {
                // Строка из слов: каждое слово — свой Run с собственной заливкой (у спетой строки все белые).
                runs = l.Words.Select(w => new Run(w.Text) { Foreground = WhiteInk }).ToArray();
                states = Enumerable.Repeat(-1, runs.Length).ToArray();
                foreach (var run in runs) block.Inlines.Add(run);
            }
            else
            {
                block.Text = l.Text;
            }
            _lines.Add(new Line { Block = block, Scale = scale, Time = l.Time, Words = l.Words, Runs = runs, WordState = states });
            _stack.Children.Add(block);
        }
        UpdatePadding();
        _spring.Snap(0);
        _scroll.ScrollToVerticalOffset(0);
    }

    // Позиция песни: последнее известное значение и момент его получения; между обновлениями, пока песня играет, продолжается по часам.
    private TimeSpan _basePosition;
    private DateTime _baseStamp = DateTime.UtcNow;
    private bool _playing;

    private TimeSpan CurrentPosition() => _playing ? _basePosition + (DateTime.UtcNow - _baseStamp) : _basePosition;

    /// <summary>Играет ли песня (тогда позиция между обновлениями продолжается сама, и заливка слов идёт плавно).</summary>
    public void SetPlaying(bool playing)
    {
        _basePosition = CurrentPosition();
        _baseStamp = DateTime.UtcNow;
        _playing = playing;
    }

    /// <summary>Позиция в песне: подсветка и прокрутка идут за ней (только для текста с таймингами).</summary>
    public void UpdatePosition(TimeSpan position)
    {
        _basePosition = position;
        _baseStamp = DateTime.UtcNow;
        ApplyPosition(position);
    }

    private void ApplyPosition(TimeSpan position)
    {
        if (!_synced || _lines.Count == 0) return;
        position += SyncLead; // компенсация запаздывания позиции Apple Music
        // Последняя строка, время которой уже наступило (с небольшим упреждением); перед первой строкой подсвечено ничего.
        int lo = 0, hi = _lines.Count - 1, index = -1;
        var target = position + Anticipation;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_lines[mid].Time <= target) { index = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (index != _current)
        {
            ResetWords(_current);
            _current = index;
            if (!_manual) _spring.SetTarget(OffsetFor(_current));
        }
        UpdateWords(position);
    }

    // Слова текущей строки: доля заливки каждого по времени (для строк без пословных таймингов ничего не делает).
    private void UpdateWords(TimeSpan position)
    {
        if (_current < 0 || _current >= _lines.Count) return;
        var line = _lines[_current];
        if (line.Words is null || line.Runs is null || line.WordState is null) return;
        for (var j = 0; j < line.Words.Count; j++)
        {
            var w = line.Words[j];
            var span = (w.End - w.Start).TotalSeconds;
            var p = span <= 0 ? (position >= w.Start ? 1 : 0) : Math.Clamp((position - w.Start).TotalSeconds / span, 0, 1);
            var state = (int)Math.Round(p * Steps);
            if (state == line.WordState[j]) continue;
            line.WordState[j] = state;
            line.Runs[j].Foreground = state <= 0 ? DimInk : state >= Steps ? WhiteInk : Sweep(state / (double)Steps);
        }
    }

    // Строка перестала быть текущей: слова снова белые (бледной её делает прозрачность всей строки).
    private void ResetWords(int index)
    {
        if (index < 0 || index >= _lines.Count) return;
        var line = _lines[index];
        if (line.Runs is null || line.WordState is null) return;
        for (var j = 0; j < line.Runs.Length; j++)
        {
            line.Runs[j].Foreground = WhiteInk;
            line.WordState[j] = -1;
        }
    }

    // Отступы: сверху почти ничего (текст начинается у верхнего края), снизу место, чтобы последняя строка тоже дошла до линии фокуса;
    // у текста без таймингов снизу небольшой отступ. Пересчитываются при смене высоты.
    private void UpdatePadding()
    {
        var h = ActualHeight;
        _stack.Margin = new Thickness(0, TopInset, 0, _synced ? Math.Max(24, h - FocusLine) : 24);
    }

    // Верхний край гаснет, только когда текст уже прокручен: у начала песни первая строка полностью видна.
    private void UpdateTopFade(double offset)
    {
        var alpha = (byte)Math.Round(255 * (1 - Math.Clamp(offset / TopFadeDistance, 0, 1)));
        if (_topFade.Color.A != alpha) _topFade.Color = Color.FromArgb(alpha, 0, 0, 0);
    }

    private void Recenter()
    {
        if (_synced && !_manual && !_dragging && _lines.Count > 0) _spring.SetTarget(OffsetFor(_current));
    }

    // Смещение прокрутки, при котором середина строки index стоит на линии фокуса. Пока строка выше линии, оно 0: текст стоит.
    private double OffsetFor(int index)
    {
        if (index < 0 || index >= _lines.Count) return 0;
        try
        {
            var block = _lines[index].Block;
            var top = block.TransformToAncestor(_stack).Transform(new Point(0, 0)).Y + _stack.Margin.Top;
            var offset = top + block.ActualHeight / 2 - FocusLine;
            return Math.Clamp(offset, 0, Math.Max(0, _scroll.ScrollableHeight));
        }
        catch (InvalidOperationException)
        {
            return 0; // раскладка ещё не готова
        }
    }

    // ---------- кадры: уровни строк и возврат после ручной прокрутки ----------

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == TimeSpan.Zero) { _lastFrame = now; return; }
        var dt = Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        if (dt <= 0) return;
        _lastFrame = now;

        if (_manual && !_dragging && DateTime.UtcNow >= _manualUntil)
        {
            _manual = false; // листание остановилось на 3 секунды: возвращаемся к текущей строке
            if (_synced) _spring.SetTarget(OffsetFor(_current));
        }

        if (!_synced) return;
        if (_playing) ApplyPosition(CurrentPosition()); // плавная заливка слов между обновлениями позиции
        var k = 1 - Math.Exp(-dt / 0.16);
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            // До начала первой строки первая заметнее остальных («сейчас начнётся»), чтобы начало не тонуло в тёмной зоне.
            var target = i == _current ? 1.0 : (_current < 0 && i == 0 ? UpNextLevel : 0.0);
            line.Level += (target - line.Level) * k;
            if (Math.Abs(line.Level - line.Applied) < 0.004) continue;
            line.Applied = line.Level;
            line.Block.Opacity = DimOpacity + (1 - DimOpacity) * line.Level;
            line.Scale.ScaleX = line.Scale.ScaleY = 0.95 + 0.05 * line.Level;
        }
    }

    // ---------- ручная прокрутка ----------

    private void HoldManual()
    {
        if (!_synced) return; // без таймингов возвращаться некуда
        _manual = true;
        _manualUntil = DateTime.UtcNow + ManualHold;
    }

    private double Clamp(double offset) => Math.Clamp(offset, 0, Math.Max(0, _scroll.ScrollableHeight));

    // Preview: иначе ScrollViewer внутри перехватит колесо и прокрутит сам, мимо нашей пружины.
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        HoldManual();
        _spring.SetTarget(Clamp(_spring.Target - e.Delta * 0.6));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartOffset = _scroll.VerticalOffset;
        _spring.Snap(_dragStartOffset); // перехват: текст останавливается там, где он сейчас
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        HoldManual();
        _spring.Snap(Clamp(_dragStartOffset - (e.GetPosition(this).Y - _dragStartY)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        _manualUntil = DateTime.UtcNow + ManualHold; // три секунды отсчитываются от конца жеста
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }
}
