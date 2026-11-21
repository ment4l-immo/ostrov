using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Search;

/// <summary>
/// Область с диалогом: вопросы серым, под каждым ответ ИИ (Markdown, дописывается по мере прихода), источники. Прокручивается колесом,
/// текст можно выделять и копировать (Ctrl+C). Один и тот же вид для первого ответа и для уточняющих вопросов.
/// </summary>
internal sealed class AnswerView : FlowDocumentScrollViewer
{
    private static readonly Brush Gray = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly FlowDocument _doc;
    private readonly Action<string> _openLink;
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    // Ответ «печатается»: пришедший текст показывается порциями по словам (см. AnswerTurn.Advance), а не кусками, как их прислал OpenAI.
    private readonly DispatcherTimer _typeTimer = new() { Interval = TimeSpan.FromMilliseconds(55) };
    private readonly HashSet<AnswerTurn> _dirty = [];
    private readonly HashSet<AnswerTurn> _typing = [];
    private readonly List<AnswerTurn> _turnList = [];
    private int _turns;
    private bool _measureQueued;

    /// <summary>Высота всего текста диалога изменилась: по ней карточка плавно растёт вниз (до своего предела), а не открывается сразу большой и пустой.</summary>
    public event Action<double>? ContentHeightChanged;

    public AnswerView(Action<string> openLink)
    {
        _openLink = openLink;
        _doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Foreground = Hues.Ink, Background = Brushes.Transparent,
            PagePadding = new Thickness(0, 0, 6, 0), LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Left
        };
        Document = _doc;
        IsToolBarVisible = false;
        IsSelectionEnabled = true;
        MinZoom = MaxZoom = 100; // колесо с Ctrl не меняет масштаб
        Zoom = 100;
        Background = Brushes.Transparent;
        Foreground = Hues.Ink;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), ThinScrollBarStyle());
        _renderTimer.Tick += (_, _) => FlushDirty();
        _typeTimer.Tick += (_, _) => TypeTick();
        Unloaded += (_, _) => { _renderTimer.Stop(); _typeTimer.Stop(); };
    }

    internal void StartTyping(AnswerTurn turn)
    {
        _typing.Add(turn);
        if (!_typeTimer.IsEnabled) _typeTimer.Start();
    }

    private void TypeTick()
    {
        foreach (var turn in _typing.ToList())
        {
            if (turn.Advance()) { turn.Render(); _dirty.Remove(turn); }
            if (!turn.HasBacklog) _typing.Remove(turn);
        }
        if (_typing.Count == 0) _typeTimer.Stop();
        QueueMeasure();
    }

    // Высоту читаем после раскладки (приоритет Loaded), раз за пачку перерисовок.
    private void QueueMeasure()
    {
        if (_measureQueued) return;
        _measureQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _measureQueued = false;
            if (Scroller is { } scroller && scroller.ExtentHeight > 0)
            {
                // Строка раздумий схлопывается и скоро освободит место: остров подстраивается под текст без неё, чтобы не вырасти зря.
                ContentHeightChanged?.Invoke(scroller.ExtentHeight - _turnList.Sum(t => t.CollapsingHeight));
                if (_anchor != null && scroller.ExtentHeight > scroller.ViewportHeight + 1) ScrollToTop(_anchor);
            }
        });
    }

    /// <summary>Начать новый вопрос: он появляется серой строкой, под ним будет ответ.</summary>
    public AnswerTurn BeginTurn(string question)
    {
        var turn = new AnswerTurn(this, question, first: _turns++ == 0);
        _turnList.Add(turn);
        _doc.Blocks.Add(turn.QuestionBlock);
        _doc.Blocks.Add(turn.AnswerSection);
        turn.Render();
        QueueMeasure();
        // Прокручиваем так, чтобы новый вопрос оказался вверху: ответ читают сверху вниз. Пока ответ растёт и читатель сам не листал,
        // вопрос остаётся вверху (карточка и текст вырастают позже первой прокрутки).
        _anchor = turn.QuestionBlock;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToTop(turn.QuestionBlock));
        return turn;
    }

    private Block? _anchor;

    protected override void OnPreviewMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        _anchor = null; // читатель листает сам
        base.OnPreviewMouseWheel(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        _anchor = null; // выделение текста или перетаскивание полосы прокрутки
        base.OnPreviewMouseLeftButtonDown(e);
    }

    private ScrollViewer? Scroller => Template?.FindName("PART_ContentHost", this) as ScrollViewer;

    private void ScrollToTop(Block block)
    {
        if (Scroller is not { } scroller) return;
        UpdateLayout();
        var rect = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty) return;
        scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + rect.Top));
    }

    internal void MarkDirty(AnswerTurn turn)
    {
        _dirty.Add(turn);
        if (!_renderTimer.IsEnabled) _renderTimer.Start();
    }

    private void FlushDirty()
    {
        _renderTimer.Stop();
        foreach (var t in _dirty.ToList()) t.Render();
        _dirty.Clear();
        QueueMeasure();
    }

    internal void OpenLink(string url) => _openLink(url);

    // Тонкая тёмная полоса прокрутки вместо стандартной светлой.
    private static Style ThinScrollBarStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
              <Setter Property="Background" Value="Transparent" />
              <Setter Property="Width" Value="8" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ScrollBar">
                    <Grid Background="Transparent">
                      <Track x:Name="PART_Track" IsDirectionReversed="True">
                        <Track.DecreaseRepeatButton>
                          <RepeatButton Command="ScrollBar.PageUpCommand" Focusable="False">
                            <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate></RepeatButton.Template>
                          </RepeatButton>
                        </Track.DecreaseRepeatButton>
                        <Track.Thumb>
                          <Thumb>
                            <Thumb.Template><ControlTemplate TargetType="Thumb"><Border Margin="2,0,2,0" CornerRadius="2" Background="#55FFFFFF" /></ControlTemplate></Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                        <Track.IncreaseRepeatButton>
                          <RepeatButton Command="ScrollBar.PageDownCommand" Focusable="False">
                            <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate></RepeatButton.Template>
                          </RepeatButton>
                        </Track.IncreaseRepeatButton>
                      </Track>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        return (Style)XamlReader.Parse(xaml);
    }

    /// <summary>Один вопрос и ответ на него. Ответ дописывается по мере прихода; перерисовка не чаще раза в ~70 мс.</summary>
    internal sealed class AnswerTurn
    {
        private readonly AnswerView _owner;
        private string _text = ""; // всё, что прислал OpenAI
        private int _shown;         // сколько из него уже показано («печать»)
        private readonly List<(string Url, string Title)> _sources = [];
        private string? _status, _error, _note;
        private bool _done;
        // Строка раздумий (шар и подпись) живёт один раз на вопрос и переживает перерисовки, иначе шар каждый раз начинал бы жизнь заново.
        private ThinkingRow? _row;
        private BlockUIContainer? _rowBlock;
        private bool _rowAtHead; // строка стоит до текста (раздумья до ответа) или после него («Ищу в интернете…» посреди ответа)

        public Paragraph QuestionBlock { get; }
        public Section AnswerSection { get; } = new();

        public AnswerTurn(AnswerView owner, string question, bool first)
        {
            _owner = owner;
            QuestionBlock = new Paragraph(new Run(question))
            {
                Foreground = Gray, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 0 : 12, 0, 4),
                LineHeight = 18
            };
        }

        /// <summary>Весь полученный текст (не только уже показанный).</summary>
        public string Text => _text;

        /// <summary>Сколько знаков уже показано.</summary>

        /// <summary>Есть полученный, но ещё не показанный текст.</summary>
        internal bool HasBacklog => _shown < _text.Length;

        /// <summary>
        /// Один шаг «печати»: показать следующую порцию, до конца слова. Порция растёт вместе с отставанием (чтобы длинный ответ не печатался минутами),
        /// но не меньше слова. true — показанное изменилось, надо перерисовать.
        /// </summary>
        internal bool Advance()
        {
            var backlog = _text.Length - _shown;
            if (backlog <= 0) return false;
            var next = Math.Min(_text.Length, _shown + Math.Clamp(backlog / 12, 1, 80));
            var limit = Math.Min(_text.Length, next + 14);
            while (next < limit && !char.IsWhiteSpace(_text[next])) next++; // слово показываем целиком
            if (next < _text.Length && char.IsLowSurrogate(_text[next])) next++;
            _shown = next;
            return true;
        }

        public void SetStatus(string? status) { _status = status; Touch(); }
        public void AppendText(string delta) { _text += delta; _status = null; _owner.StartTyping(this); }
        public void SetNote(string note) { _note = note; Touch(); }

        // Карточки действий ИИ (напоминание, пересчёт валют…): стоят под текстом, переживают перерисовки.
        private readonly List<BlockUIContainer> _widgets = [];

        public void AddWidget(FrameworkElement card)
        {
            _widgets.Add(new BlockUIContainer(card) { Margin = new Thickness(0, 2, 0, 4) });
            Touch();
        }
        public void SetError(string error) { _error = error; _status = null; _done = true; Touch(); }
        public void Finish() { _done = true; _status = null; Touch(); Render(); }

        public void AddSource(string url, string? title)
        {
            if (_sources.Any(s => s.Url == url)) return;
            _sources.Add((url, string.IsNullOrWhiteSpace(title) ? Host(url) : title!));
            Touch();
        }

        private void Touch() => _owner.MarkDirty(this);

        private static string Host(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host.StartsWith("www.") ? u.Host[4..] : u.Host) : url;

        /// <summary>Сколько места строка раздумий ещё занимает, пока схлопывается (остров под него не растёт).</summary>
        internal double CollapsingHeight => _row?.CollapsingHeight ?? 0;

        public void Render()
        {
            // Пока текста нет (или ИИ ищет в интернете): шар и подпись с бегущим бликом. Начался ответ: строка плавно гаснет и схлопывается.
            if (!_done && (_status != null || (_shown == 0 && _widgets.Count == 0)))
            {
                if (_row == null || _row.IsFading) NewRow();
                _row!.SetLabel(_status ?? "Думаю…");
            }
            else if (_row is { IsFading: false } fading) fading.BeginFadeOut();

            var blocks = AnswerSection.Blocks;
            blocks.Clear();
            if (_row != null && _rowAtHead) blocks.Add(_rowBlock!);
            if (_shown > 0)
                foreach (var b in MiniMarkdown.Parse(_shown >= _text.Length ? _text : _text[.._shown], _owner.OpenLink)) blocks.Add(b);

            foreach (var w in _widgets) blocks.Add(w);
            if (_error != null) blocks.Add(Small(_error, italic: false));
            if (_note != null) blocks.Add(Small(_note, italic: true));
            if (_row != null && !_rowAtHead) blocks.Add(_rowBlock!);

            if (_sources.Count > 0 && (_done || _shown > 0))
            {
                var p = new Paragraph { FontSize = 12, Foreground = Gray, Margin = new Thickness(0, 2, 0, 0), LineHeight = 17 };
                p.Inlines.Add(new Run("Источники: "));
                var shown = _sources.GroupBy(s => Host(s.Url)).Select(g => g.First()).Take(5).ToList();
                for (var i = 0; i < shown.Count; i++)
                {
                    if (i > 0) p.Inlines.Add(new Run("  ·  "));
                    var link = MiniMarkdown.Link(Host(shown[i].Url), shown[i].Url, _owner.OpenLink);
                    link.Foreground = Gray;
                    link.ToolTip = shown[i].Title + "\n" + shown[i].Url;
                    p.Inlines.Add(link);
                }
                blocks.Add(p);
            }
        }

        private void NewRow()
        {
            var row = new ThinkingRow(_status ?? "Думаю…");
            row.Finished += () =>
            {
                if (!ReferenceEquals(_row, row)) return;
                _row = null;
                _rowBlock = null;
                Touch(); // перерисовать уже без строки
            };
            _row = row;
            _rowBlock = new BlockUIContainer(row);
            _rowAtHead = _shown == 0;
        }

        private static Paragraph Small(string text, bool italic) => new(new Run(text))
        {
            Foreground = Gray, FontSize = 13, FontStyle = italic ? FontStyles.Italic : FontStyles.Normal, Margin = new Thickness(0, 0, 0, 4)
        };
    }
}
