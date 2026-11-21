using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Time;

/// <summary>
/// Таймер и секундомер как пассивные активности свёрнутого острова: слева иконка часов в кольце, справа время.
/// Приоритеты: таймер 50, секундомер 40 (выше музыки). Таймер виден, пока идёт или на паузе; секундомер — пока идёт
/// или остановлен с ненулевым временем (серым). Когда таймер дозвонил, остров раскрывается сам (это делает <see cref="TimerModel"/>).
/// </summary>
internal sealed class TimeActivity
{
    private const string TimerId = "timer", StopwatchId = "stopwatch";

    private readonly TimerModel _timer;
    private readonly StopwatchModel _stopwatch;
    private readonly ClockPill _timerPill = new(), _stopwatchPill = new();
    private readonly ClockBubble _timerBubble = new(), _stopwatchBubble = new();
    private readonly ClockWideView _timerWide = new(), _stopwatchWide = new(); // расширенный вид по правому клику
    // Нотч в полноэкранном видео: у таймера убывающая оранжевая полоска, у секундомера бегущая оранжевая.
    private readonly CompactStrip _timerStrip = new(ClockStyle.Orange), _stopwatchStrip = new(ClockStyle.Orange);
    private readonly TimeUpAlert _timerUpAlert = new(); // «Время вышло»: на конце таймера нотч вырастает в такое окошко и мигает до клика
    private DispatcherTimer? _tick; // создаётся при первой активности, уже на потоке интерфейса

    public TimeActivity(TimerModel timer, StopwatchModel stopwatch)
    {
        _timer = timer;
        _stopwatch = stopwatch;
        _timer.Changed += Refresh;
        _stopwatch.Changed += Refresh;
        IslandBus.CompactModeChanged += compact =>
        {
            // Вышли из видео, а таймер дозвонил, пока остров был нотчем: показываем обычное «готово» (остров раскрывается на вкладке).
            if (!compact && _timer.State == TimerState.Finished) IslandBus.RequestAttention("time");
            Refresh();
        };
    }

    private bool TimerActive => _timer.State is TimerState.Running or TimerState.Paused;
    private bool StopwatchActive => _stopwatch.IsRunning || _stopwatch.Elapsed > TimeSpan.Zero;

    // Таймер дозвонил, пока идёт полноэкранное видео: нотч мигает, пока не кликнут (мелодии нет, остров не раскрывается).
    private bool TimerFinishedInCompact => _timer.State == TimerState.Finished && IslandBus.CompactMode;

    private void Refresh()
    {
        if (TimerActive)
            ActivityCenter.Set(new ActivityEntry
            {
                Id = TimerId, Priority = 50, Pill = _timerPill, PillWidth = ClockPill.ViewWidth, PillHeight = ClockPill.ViewHeight,
                Bubble = _timerBubble, ModuleId = "time", Compact = _timerStrip,
                Wide = _timerWide, WideWidth = ClockWideView.ViewWidth, WideHeight = ClockWideView.ViewHeight
            });
        else if (TimerFinishedInCompact)
            ActivityCenter.Set(new ActivityEntry
            {
                Id = TimerId, Priority = 50, Pill = _timerPill, PillWidth = ClockPill.ViewWidth, PillHeight = ClockPill.ViewHeight,
                ModuleId = "time", Compact = _timerUpAlert, CompactWidth = ClockPill.ViewWidth, CompactHeight = ClockPill.ViewHeight,
                DismissOnClick = true, OnDismissed = _timer.Reset,
                // Клик по чипу «+5» продлевает таймер, а не закрывает окошко.
                OnPillClick = point =>
                {
                    if (point.X < TimeUpAlert.ChipLeft || point.X > TimeUpAlert.ChipRight) return false;
                    _timer.Extend(TimeSpan.FromMinutes(5));
                    return true;
                }
            });
        else ActivityCenter.Remove(TimerId);

        if (StopwatchActive)
            ActivityCenter.Set(new ActivityEntry
            {
                Id = StopwatchId, Priority = 40, Pill = _stopwatchPill, PillWidth = ClockPill.ViewWidth, PillHeight = ClockPill.ViewHeight,
                Bubble = _stopwatchBubble, ModuleId = "time", Compact = _stopwatchStrip,
                Wide = _stopwatchWide, WideWidth = ClockWideView.ViewWidth, WideHeight = ClockWideView.ViewHeight
            });
        else ActivityCenter.Remove(StopwatchId);

        UpdateViews();
        if (TimerActive || StopwatchActive)
        {
            _tick ??= CreateTick();
            _tick.Start();
        }
        else _tick?.Stop();
    }

    private DispatcherTimer CreateTick()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        t.Tick += (_, _) => UpdateViews();
        return t;
    }

    private void UpdateViews()
    {
        if (TimerActive)
        {
            var remaining = _timer.Remaining;
            var total = _timer.Duration.TotalSeconds;
            var fraction = total > 0 ? Math.Clamp(remaining.TotalSeconds / total, 0, 1) : 0;
            var paused = _timer.State == TimerState.Paused;
            _timerPill.Set(ClockKind.Timer, fraction, 0, FormatRemaining(remaining), paused);
            _timerWide.Set(ClockKind.Timer, fraction, 0, FormatRemaining(remaining), (paused ? "На паузе · " : "Таймер · ") + FormatDuration(_timer.Duration), paused);
            _timerBubble.Set(ClockKind.Timer, fraction, 0, paused);
            _timerStrip.ShowFraction(fraction, paused);
        }

        if (StopwatchActive)
        {
            var elapsed = _stopwatch.Elapsed;
            var angle = elapsed.TotalSeconds % 60 / 60 * 360;
            var paused = !_stopwatch.IsRunning;
            _stopwatchPill.Set(ClockKind.Stopwatch, 0, angle, FormatElapsed(elapsed), paused);
            _stopwatchWide.Set(ClockKind.Stopwatch, 0, angle, FormatElapsed(elapsed), StopwatchLabel(paused), paused);
            _stopwatchBubble.Set(ClockKind.Stopwatch, 0, angle, paused);
            _stopwatchStrip.ShowRunning(paused);
        }
    }

    // Подпись секундомера на расширенном виде: последний круг (его время, а не общее), иначе просто название или «Остановлен».
    private string StopwatchLabel(bool paused)
    {
        var laps = _stopwatch.Laps;
        if (laps.Count > 0)
        {
            var last = laps[^1] - (laps.Count > 1 ? laps[^2] : TimeSpan.Zero);
            return $"Круг {laps.Count} · {Format((int)last.TotalSeconds)}";
        }
        return paused ? "Остановлен" : "Секундомер";
    }

    // «25 мин», «1 ч 30 мин», «2 ч», «1:30» (если есть секунды).
    private static string FormatDuration(TimeSpan d)
    {
        var total = (int)Math.Round(d.TotalSeconds);
        if (total % 60 != 0) return Format(total);
        var minutes = total / 60;
        if (minutes < 60) return $"{minutes} мин";
        return minutes % 60 == 0 ? $"{minutes / 60} ч" : $"{minutes / 60} ч {minutes % 60} мин";
    }

    // Таймер показывает оставшиеся секунды с округлением вверх: 0:00 появляется только в самом конце.
    private static string FormatRemaining(TimeSpan t) => Format((int)Math.Ceiling(t.TotalSeconds));

    // Секундомер показывает прошедшие целые секунды.
    private static string FormatElapsed(TimeSpan t) => Format((int)t.TotalSeconds);

    private static string Format(int seconds)
    {
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}
