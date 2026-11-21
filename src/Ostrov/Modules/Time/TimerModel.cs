using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Time;

internal enum TimerState { Idle, Running, Paused, Finished }

/// <summary>Таймер обратного отсчёта. Работает независимо от вкладки и при свёрнутом острове.</summary>
internal sealed class TimerModel
{
    private DispatcherTimer? _tick;
    private DateTime _endAt;
    private TimeSpan _pausedRemaining;

    public TimerState State { get; private set; } = TimerState.Idle;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(10);

    /// <summary>Меняется состояние или длительность (не вызывается на каждый тик).</summary>
    public event Action? Changed;

    public TimerModel()
    {
        // Нажатие «принять» (клик по острову после сигнала) сбрасывает завершённый таймер.
        IslandBus.Dismissed += () => { if (State == TimerState.Finished) Reset(); };
    }

    public TimeSpan Remaining => State switch
    {
        TimerState.Running => Max(_endAt - DateTime.UtcNow),
        TimerState.Paused => _pausedRemaining,
        TimerState.Finished => TimeSpan.Zero,
        _ => Duration
    };

    public void SetMinutes(int minutes)
    {
        if (State != TimerState.Idle || minutes < 1) return;
        Duration = TimeSpan.FromMinutes(minutes);
        Changed?.Invoke();
    }

    /// <summary>Своё время (колёса «час · мин · сек»): только пока таймер не запущен, не меньше секунды.</summary>
    public void SetDuration(TimeSpan duration)
    {
        if (State != TimerState.Idle || duration < TimeSpan.FromSeconds(1) || duration == Duration) return;
        Duration = duration;
        Changed?.Invoke();
    }

    public void Start()
    {
        if (State == TimerState.Idle) _endAt = DateTime.UtcNow + Duration;
        else if (State == TimerState.Paused) _endAt = DateTime.UtcNow + _pausedRemaining;
        else return;

        State = TimerState.Running;
        _tick ??= CreateTick();
        _tick.Start();
        Changed?.Invoke();
    }

    /// <summary>«+5 минут» после сигнала: таймер идёт снова ещё <paramref name="more"/>.</summary>
    public void Extend(TimeSpan more)
    {
        if (State != TimerState.Finished || more <= TimeSpan.Zero) return;
        Duration = more;
        _endAt = DateTime.UtcNow + more;
        State = TimerState.Running;
        _tick ??= CreateTick();
        _tick.Start();
        Changed?.Invoke();
    }

    public void Pause()
    {
        if (State != TimerState.Running) return;
        _pausedRemaining = Remaining;
        State = TimerState.Paused;
        _tick?.Stop();
        Changed?.Invoke();
    }

    public void Reset()
    {
        _tick?.Stop();
        State = TimerState.Idle;
        Changed?.Invoke();
    }

    private DispatcherTimer CreateTick()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        t.Tick += (_, _) =>
        {
            if (Remaining > TimeSpan.Zero) return;
            t.Stop();
            State = TimerState.Finished;
            // В полноэкранном видео остров показывает только нотч-знак: мелодия поверх видео не играет (решение пользователя).
            if (!IslandBus.CompactMode) AlarmSound.Play();
            IslandBus.RequestAttention("time");
            Changed?.Invoke();
        };
        return t;
    }

    private static TimeSpan Max(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
