using System.Diagnostics;

namespace Ostrov.Modules.Time;

/// <summary>Секундомер с кругами. Идёт независимо от вкладки.</summary>
internal sealed class StopwatchModel
{
    private readonly Stopwatch _sw = new();

    /// <summary>Общее время на момент каждого круга (по порядку).</summary>
    public List<TimeSpan> Laps { get; } = [];

    public bool IsRunning => _sw.IsRunning;
    public TimeSpan Elapsed => _sw.Elapsed;

    public event Action? Changed;

    public void Start() { _sw.Start(); Changed?.Invoke(); }
    public void Pause() { _sw.Stop(); Changed?.Invoke(); }

    public void Reset()
    {
        _sw.Reset();
        Laps.Clear();
        Changed?.Invoke();
    }

    public void Lap()
    {
        if (!_sw.IsRunning) return;
        Laps.Add(_sw.Elapsed);
        Changed?.Invoke();
    }
}
