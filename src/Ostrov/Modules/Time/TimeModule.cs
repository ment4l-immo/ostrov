using System.Windows;

namespace Ostrov.Modules.Time;

/// <summary>Вкладка «Время»: таймер и секундомер. Состояние хранится в моделях, поэтому счёт идёт и без открытой вкладки.</summary>
public sealed class TimeModule : IIslandModule
{
    private readonly TimerModel _timer = new();
    private readonly StopwatchModel _stopwatch = new();
    private readonly TimeActivity _activity; // плашка и кружок таймера/секундомера в свёрнутом острове

    public TimeModule()
    {
        _activity = new TimeActivity(_timer, _stopwatch);
        // Команды из строки Alt+Q («таймер 25», «секундомер»).
        Ostrov.Services.IslandCommands.StartTimer = d => { _timer.Reset(); _timer.SetDuration(d); _timer.Start(); };
        Ostrov.Services.IslandCommands.CancelTimer = _timer.Reset;
        Ostrov.Services.IslandCommands.StartStopwatch = () => { if (!_stopwatch.IsRunning) _stopwatch.Start(); };
        Ostrov.Services.IslandCommands.StopStopwatch = () => { if (_stopwatch.IsRunning) _stopwatch.Pause(); };
    }

    /// <summary>Таймер для кнопки в хабе.</summary>
    internal TimerModel Timer => _timer;

    public string Id => "time";
    public string Title => "Время";
    public string Icon => "";
    public double ExpandedHeight => 238;
    public System.Windows.Media.Color[]? BackdropPalette => Ostrov.Backdrop.TabPalettes.Time;

    public FrameworkElement CreateView() => new TimeView(_timer, _stopwatch);
}
