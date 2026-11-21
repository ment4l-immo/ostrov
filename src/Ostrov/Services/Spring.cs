using System.Windows.Media;

namespace Ostrov.Services;

/// <summary>
/// Пружина без фиксированной длительности (параметры как у Apple: коэффициент затухания и «отклик»).
/// Значение тянется к цели; новая цель или толчок не обрывают движение, скорость сохраняется (прерываемость).
/// Кадры считает CompositionTarget.Rendering и только пока пружина не успокоилась. Работает на потоке интерфейса.
/// </summary>
public sealed class Spring
{
    private double _omega; // собственная частота, рад/с
    private readonly double _zeta;  // коэффициент затухания: 1 = без перелёта, < 1 = с перелётом
    private TimeSpan _lastFrame;
    private bool _running;

    public double Value { get; private set; }
    public double Target { get; private set; }
    public double Velocity { get; private set; }

    /// <summary>Значение изменилось (на каждом кадре движения и при остановке).</summary>
    public event Action<double>? Changed;

    /// <param name="dampingRatio">1 — плавно без отскока; ~0.8 — лёгкий отскок; ~0.5 — заметный.</param>
    /// <param name="responseSeconds">Как быстро пружина доходит до цели, секунды (не длительность анимации).</param>
    public Spring(double dampingRatio, double responseSeconds, double initial = 0)
    {
        _zeta = Math.Clamp(dampingRatio, 0.05, 1.0);
        _omega = 2 * Math.PI / Math.Max(responseSeconds, 0.05);
        Value = Target = initial;
    }

    /// <summary>Сменить «отклик» на лету (например, быстрее закрыть, чем открыть); текущее движение и скорость не прерываются.</summary>
    public void SetResponse(double responseSeconds) => _omega = 2 * Math.PI / Math.Max(responseSeconds, 0.05);

    /// <summary>Максимум отклонения после толчка, если пружина стояла на месте: peak = v0 / ω · k.</summary>
    public double PeakPerVelocity
    {
        get
        {
            if (_zeta >= 1) return 1 / (_omega * Math.E); // критическое затухание: максимум v0 / (ω·e)
            var s = Math.Sqrt(1 - _zeta * _zeta);
            return Math.Exp(-_zeta / s * Math.Atan(s / _zeta)) / _omega;
        }
    }

    /// <summary>Толчок: прибавляет скорость. Из покоя значение отклонится от цели и вернётся с перелётом.</summary>
    public void Kick(double velocity)
    {
        Velocity += velocity;
        Start();
    }

    /// <summary>Толчок такой силы, чтобы из покоя отклонение достигло amount (в единицах значения).</summary>
    public void KickToPeak(double amount) => Kick(amount / PeakPerVelocity);

    public void SetTarget(double target)
    {
        Target = target;
        Start();
    }

    /// <summary>Мгновенно поставить значение и остановить пружину.</summary>
    public void Snap(double value)
    {
        Stop();
        Value = Target = value;
        Velocity = 0;
        Changed?.Invoke(Value);
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == TimeSpan.Zero) { _lastFrame = now; return; }
        var dt = (now - _lastFrame).TotalSeconds;
        if (dt <= 0) return; // WPF иногда присылает кадр дважды с одним временем
        _lastFrame = now;
        dt = Math.Min(dt, 0.05); // после остановки показа не прыгаем

        // Полунеявный Эйлер с мелким шагом: устойчив при любой частоте кадров.
        var x = Value - Target;
        var v = Velocity;
        while (dt > 0)
        {
            var h = Math.Min(dt, 1.0 / 240);
            v += (-_omega * _omega * x - 2 * _zeta * _omega * v) * h;
            x += v * h;
            dt -= h;
        }

        if (Math.Abs(x) < 0.0003 && Math.Abs(v) < 0.003)
        {
            Stop();
            Value = Target;
            Velocity = 0;
        }
        else
        {
            Value = Target + x;
            Velocity = v;
        }
        Changed?.Invoke(Value);
    }
}
