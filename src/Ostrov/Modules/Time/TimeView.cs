using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Time;

/// <summary>
/// Содержимое вкладки «Время»: переключатель «Таймер | Секундомер» и две панели одной раскладки (три колонки 160 | центр | 160).
/// Таймер в покое: слева пресеты, в центре колёса «час · мин · сек», справа «Старт». После старта колёса сменяются кольцом,
/// слева «Отмена», справа «Пауза». Секундомер: слева «Круг/Сброс» и список кругов, в центре кольцо с точкой, справа «Старт/Стоп».
/// </summary>
internal sealed class TimeView : Grid
{
    private const double RingSize = 128, SideColumn = 160;

    private static readonly Brush Selected = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
    private static readonly Brush ChipOn = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ChipOff = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush WheelBand = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly int[] Presets = [5, 10, 25];

    private readonly TimerModel _timer;
    private readonly StopwatchModel _stopwatch;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(40) };

    // Переключатель
    private readonly Button _tabTimer = Ui.Pill("Таймер");
    private readonly Button _tabStopwatch = Ui.Pill("Секундомер");
    private readonly Grid _timerPanel = new();
    private readonly Grid _stopwatchPanel = new();

    // Таймер
    private readonly TimeWheel _hours = new(24), _minutes = new(60), _seconds = new(60);
    private readonly Grid _wheelsHost = new();
    private readonly Grid _ringHost = new() { Width = RingSize, Height = RingSize, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _ringScale = new(1, 1);
    private readonly ArcRing _timerRing = new() { Width = RingSize, Height = RingSize, RingThickness = 9 };
    private readonly TextBlock _timerDisplay = Display();
    private readonly StackPanel _presets = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Dictionary<int, Button> _chips = [];
    private readonly RoundButton _timerCancel = new("Отмена", RoundStyle.Gray);
    private readonly RoundButton _timerPrimary = new("Старт", RoundStyle.Orange);
    private TimerState? _faceState;

    // Секундомер
    private readonly OrbitRing _swRing = new() { Width = RingSize, Height = RingSize, RingThickness = 4, DotRadius = 6 };
    private readonly TextBlock _swDisplay = Display();
    private readonly Run _swMain = new() { FontSize = 30 };
    private readonly Run _swCentis = new() { FontSize = 15, Foreground = Ui.Gray };
    private readonly RoundButton _swLap = new("Круг", RoundStyle.Gray);
    private readonly RoundButton _swToggle = new("Старт", RoundStyle.Orange);
    private readonly StackPanel _lapList = new();

    public TimeView(TimerModel timer, StopwatchModel stopwatch)
    {
        _timer = timer;
        _stopwatch = stopwatch;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        BuildSwitch();
        BuildTimerPanel();
        BuildStopwatchPanel();
        SetRow(_timerPanel, 1);
        SetRow(_stopwatchPanel, 1);
        Children.Add(_timerPanel);
        Children.Add(_stopwatchPanel);
        ShowPanel(timerActive: true);

        _timer.Changed += OnTimerChanged;
        _stopwatch.Changed += OnStopwatchChanged;
        _refresh.Tick += (_, _) => UpdateDisplays();
        Loaded += (_, _) => { _refresh.Start(); OnTimerChanged(); OnStopwatchChanged(); Entrance(); };
        IslandBus.Expanding += () => { if (IsLoaded) Dispatcher.BeginInvoke(Entrance, DispatcherPriority.Loaded); };
        Unloaded += (_, _) => _refresh.Stop();
    }

    // Каждый раз, когда заходишь на таймер, колёса прокручиваются до своих значений (часы, минуты, секунды с небольшим сдвигом по времени).
    // Только когда таймер в покое и показаны колёса; повтор в пределах 400 мс (вход во вкладку и раскрытие острова) пропускается.
    private DateTime _lastEntrance;

    private void Entrance()
    {
        if (_timer.State != TimerState.Idle || _timerPanel.Visibility != Visibility.Visible) return;
        if ((DateTime.UtcNow - _lastEntrance).TotalMilliseconds < 400) return;
        _lastEntrance = DateTime.UtcNow;
        _hours.Spin(4, 0);
        _minutes.Spin(9, 70);
        _seconds.Spin(14, 140);
    }

    // Крупное лёгкое время в кольце. Цифры одной ширины и один размер всегда: ничего не «ездит».
    private static TextBlock Display()
    {
        var t = new TextBlock
        {
            FontWeight = FontWeights.Light,
            FontSize = 30,
            Foreground = Hues.Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Width = RingSize - 24
        };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    // ---------- переключатель ----------

    private void BuildSwitch()
    {
        foreach (var b in new[] { _tabTimer, _tabStopwatch })
        {
            b.Margin = new Thickness(0);
            b.Height = 26;
        }
        _tabTimer.Click += (_, _) => ShowPanel(timerActive: true);
        _tabStopwatch.Click += (_, _) => ShowPanel(timerActive: false);

        var holder = new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Hues.GlassSurfaceColor),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _tabTimer, _tabStopwatch } }
        };
        PillSlider.Attach(holder);
        SetRow(holder, 0);
        Children.Add(holder);
    }

    private void ShowPanel(bool timerActive)
    {
        _timerPanel.Visibility = timerActive ? Visibility.Visible : Visibility.Collapsed;
        _stopwatchPanel.Visibility = timerActive ? Visibility.Collapsed : Visibility.Visible;
        Ui.MarkSelected(_tabTimer, timerActive);
        Ui.MarkSelected(_tabStopwatch, !timerActive);
        UpdateDisplays();
        if (IsLoaded) Ui.FadeIn(timerActive ? _timerPanel : _stopwatchPanel);
        if (IsLoaded && timerActive) Entrance();
    }

    // Три колонки: боковые одинаковой ширины, чтобы центр стоял ровно по середине.
    private static void BuildGrid(Grid panel)
    {
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SideColumn) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SideColumn) });
    }

    // Плавно показать или спрятать элемент (невидимый не ловит мышь).
    private static void Fade(UIElement element, bool visible, bool animate)
    {
        element.IsHitTestVisible = visible;
        if (!animate)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = visible ? 1 : 0;
            return;
        }
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(visible ? 240 : 160)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // ---------- таймер ----------

    private void BuildTimerPanel()
    {
        BuildGrid(_timerPanel);

        // Левая колонка: пресеты в покое, «Отмена» когда таймер идёт.
        foreach (var minutes in Presets)
        {
            var chip = Ui.Pill($"{minutes} мин");
            chip.Width = 100;
            chip.Height = 24;
            chip.Margin = new Thickness(0, 2, 0, 2);
            var m = minutes;
            chip.Click += (_, _) => _timer.SetMinutes(m); // колёса подтянутся из OnTimerChanged
            _chips[minutes] = chip;
            _presets.Children.Add(chip);
        }
        _timerCancel.HorizontalAlignment = HorizontalAlignment.Center;
        _timerCancel.VerticalAlignment = VerticalAlignment.Center;
        _timerCancel.Click += (_, _) =>
        {
            if (_timer.State == TimerState.Finished)
            {
                _timer.Extend(TimeSpan.FromMinutes(5)); // «+5 мин» на месте «Отмены»: звонок прошёл, ещё немного
                IslandBus.Dismiss();
            }
            else _timer.Reset();
        };
        var left = new Grid();
        left.Children.Add(_presets);
        left.Children.Add(_timerCancel);
        SetColumn(left, 0);
        _timerPanel.Children.Add(left);

        // Центр: колёса (в покое) или кольцо (идёт).
        BuildWheels();
        _ringHost.RenderTransform = _ringScale;
        _ringHost.Children.Add(_timerRing);
        _ringHost.Children.Add(_timerDisplay);
        var center = new Grid();
        center.Children.Add(_wheelsHost);
        center.Children.Add(_ringHost);
        SetColumn(center, 1);
        _timerPanel.Children.Add(center);

        // Правая колонка: главное действие.
        _timerPrimary.HorizontalAlignment = HorizontalAlignment.Center;
        _timerPrimary.VerticalAlignment = VerticalAlignment.Center;
        _timerPrimary.Click += (_, _) => OnTimerPrimary();
        SetColumn(_timerPrimary, 2);
        _timerPanel.Children.Add(_timerPrimary);
    }

    private void BuildWheels()
    {
        const double wheelWidth = 48;
        _wheelsHost.Width = wheelWidth * 3;
        _wheelsHost.Height = TimeWheel.RowHeight * 3;
        _wheelsHost.HorizontalAlignment = HorizontalAlignment.Center;
        _wheelsHost.VerticalAlignment = VerticalAlignment.Center;

        // Полоса выбора за средней строкой.
        _wheelsHost.Children.Add(new Border
        {
            Height = TimeWheel.RowHeight, CornerRadius = new CornerRadius(10), Background = WheelBand,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (wheel, label) in new[] { (_hours, "ч"), (_minutes, "мин"), (_seconds, "сек") })
        {
            wheel.Width = wheelWidth;
            wheel.ValueChanged += OnWheelsChanged;
            row.Children.Add(new Grid
            {
                Width = wheelWidth,
                Children =
                {
                    wheel,
                    // Подпись над колесом (поле сверху отрицательное: над самими цифрами).
                    new TextBlock
                    {
                        Text = label, FontSize = 11, Foreground = Ui.Gray, IsHitTestVisible = false,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -18, 0, 0)
                    }
                }
            });
        }
        _wheelsHost.Children.Add(row);
    }

    private int WheelsTotalSeconds => _hours.Value * 3600 + _minutes.Value * 60 + _seconds.Value;

    private void OnWheelsChanged()
    {
        var total = WheelsTotalSeconds;
        if (total > 0) _timer.SetDuration(TimeSpan.FromSeconds(total));
        UpdateTimerPrimary();
    }

    // Колёса показывают длительность таймера (после пресета или сброса). Не вызывается при самих правках колёс: значения уже равны.
    private void SyncWheels(bool animate)
    {
        var d = _timer.Duration;
        _hours.SetValue((int)d.TotalHours, animate);
        _minutes.SetValue(d.Minutes, animate);
        _seconds.SetValue(d.Seconds, animate);
    }

    private void UpdateTimerPrimary() =>
        _timerPrimary.IsEnabled = _timer.State != TimerState.Idle || WheelsTotalSeconds > 0; // нулевое время запустить нельзя

    private void OnTimerPrimary()
    {
        switch (_timer.State)
        {
            case TimerState.Idle: _timer.Start(); break;
            case TimerState.Running: _timer.Pause(); break;
            case TimerState.Paused: _timer.Start(); break;
            case TimerState.Finished:
                _timer.Reset();
                IslandBus.Dismiss();
                break;
        }
    }

    private void OnTimerChanged()
    {
        foreach (var (minutes, chip) in _chips)
            chip.Background = _timer.Duration == TimeSpan.FromMinutes(minutes) ? ChipOn : ChipOff;

        // Сигнал таймера: показываем панель таймера, даже если была открыта панель секундомера.
        if (_timer.State == TimerState.Finished) ShowPanel(timerActive: true);

        if (_faceState != _timer.State)
        {
            var first = _faceState is null;
            _faceState = _timer.State;
            ApplyTimerFace(animate: !first && IsLoaded);
        }
        if (_timer.State == TimerState.Idle) SyncWheels(animate: IsLoaded);
        UpdateTimerPrimary();
        UpdateDisplays();
    }

    // Колёса и пресеты ↔ кольцо и «Отмена»: плавная смена, кольцо чуть «вырастает».
    private void ApplyTimerFace(bool animate)
    {
        var state = _timer.State;
        var idle = state == TimerState.Idle;
        var active = state is TimerState.Running or TimerState.Paused;

        Fade(_wheelsHost, idle, animate);
        Fade(_presets, idle, animate);
        Fade(_ringHost, !idle, animate);
        Fade(_timerCancel, active || state == TimerState.Finished, animate);
        _timerCancel.Content = state == TimerState.Finished ? "+5 мин" : "Отмена";

        var scale = idle ? 0.92 : 1.0;
        if (animate)
        {
            var grow = new DoubleAnimation(scale, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        else
        {
            _ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _ringScale.ScaleX = _ringScale.ScaleY = scale;
        }

        _timerPrimary.Content = state switch
        {
            TimerState.Idle => "Старт",
            TimerState.Running => "Пауза",
            TimerState.Paused => "Дальше",
            _ => "Ок"
        };
    }

    // ---------- секундомер ----------

    private void BuildStopwatchPanel()
    {
        BuildGrid(_stopwatchPanel);

        // Левая колонка: «Круг/Сброс» точно на уровне «Старт/Стоп» справа, а список кругов под ним (последние три, прокрутка).
        _swLap.HorizontalAlignment = HorizontalAlignment.Center;
        _swLap.VerticalAlignment = VerticalAlignment.Center;
        _swLap.Click += (_, _) => { if (_stopwatch.IsRunning) _stopwatch.Lap(); else _stopwatch.Reset(); };
        var laps = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = 150,
            MaxHeight = 44,
            Margin = new Thickness(0, 0, 0, 12), // зазор до иконок вкладок
            Content = _lapList
        };
        var left = new Grid();
        left.Children.Add(_swLap);
        left.Children.Add(laps);
        SetColumn(left, 0);
        _stopwatchPanel.Children.Add(left);

        // Центр: кольцо с точкой и время: крупно м:сс, сотые мелко и серым рядом.
        _swDisplay.Inlines.Add(_swMain);
        _swDisplay.Inlines.Add(_swCentis);
        var ring = new Grid { Width = RingSize, Height = RingSize, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(_swRing);
        ring.Children.Add(_swDisplay);
        SetColumn(ring, 1);
        _stopwatchPanel.Children.Add(ring);

        // Правая колонка.
        _swToggle.HorizontalAlignment = HorizontalAlignment.Center;
        _swToggle.VerticalAlignment = VerticalAlignment.Center;
        _swToggle.Click += (_, _) => { if (_stopwatch.IsRunning) _stopwatch.Pause(); else _stopwatch.Start(); };
        SetColumn(_swToggle, 2);
        _stopwatchPanel.Children.Add(_swToggle);
    }

    private void OnStopwatchChanged()
    {
        var running = _stopwatch.IsRunning;
        _swToggle.Content = running ? "Стоп" : "Старт";
        _swLap.Content = running || _stopwatch.Elapsed == TimeSpan.Zero ? "Круг" : "Сброс"; // «Сброс» только когда есть что сбрасывать
        _swLap.IsEnabled = running || _stopwatch.Elapsed > TimeSpan.Zero;

        _lapList.Children.Clear();
        var previous = TimeSpan.Zero;
        var rows = new List<TextBlock>();
        for (var i = 0; i < _stopwatch.Laps.Count; i++)
        {
            var total = _stopwatch.Laps[i];
            rows.Add(new TextBlock
            {
                Text = $"{i + 1,2}  +{FormatPrecise(total - previous)}  {FormatPrecise(total)}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Ui.Gray,
                Margin = new Thickness(0, 0, 0, 3)
            });
            previous = total;
        }
        rows.Reverse(); // новые круги сверху
        foreach (var r in rows) _lapList.Children.Add(r);
        UpdateDisplays();
    }

    // ---------- обновление цифр и колец ----------

    private void UpdateDisplays()
    {
        if (_timerPanel.Visibility == Visibility.Visible)
        {
            switch (_timer.State)
            {
                case TimerState.Finished:
                    _timerDisplay.Text = "Время\nвышло";
                    _timerDisplay.FontSize = 16;
                    _timerRing.Fraction = 0;
                    break;
                case TimerState.Running or TimerState.Paused:
                    _timerDisplay.Text = FormatSeconds(_timer.Remaining);
                    _timerDisplay.FontSize = 30;
                    // Дуга убывает; на паузе серая.
                    _timerRing.Fraction = _timer.Remaining.TotalSeconds / _timer.Duration.TotalSeconds;
                    _timerRing.ArcBrush = _timer.State == TimerState.Paused ? ClockStyle.Paused : ClockStyle.Orange;
                    break;
            }
        }
        if (_stopwatchPanel.Visibility == Visibility.Visible)
        {
            var elapsed = _stopwatch.Elapsed;
            var s = (int)elapsed.TotalSeconds;
            // Размер шрифта не меняется никогда. До часа «мм:сс» и сотые; после часа «ч:мм:сс» без сотых.
            _swMain.Text = s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60:00}:{s % 60:00}";
            _swCentis.Text = s >= 3600 ? "" : $".{(int)(elapsed.TotalMilliseconds / 10) % 100:00}";
            // Точка идёт по кольцу, оборот за минуту; когда секундомер стоит, она серая.
            _swRing.Angle = elapsed.TotalSeconds % 60 / 60 * 360;
            _swRing.DotBrush = _stopwatch.IsRunning ? ClockStyle.Orange : ClockStyle.Paused;
        }
    }

    private static string FormatSeconds(TimeSpan t)
    {
        var total = (int)Math.Ceiling(t.TotalSeconds); // округляем вверх: 0:00 показывается только в конце
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60}:{total % 60:00}";
    }

    private static string FormatPrecise(TimeSpan t)
    {
        var centis = (int)(t.TotalMilliseconds / 10) % 100;
        var s = (int)t.TotalSeconds;
        return s >= 3600
            ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}.{centis:00}"
            : $"{s / 60}:{s % 60:00}.{centis:00}";
    }
}
