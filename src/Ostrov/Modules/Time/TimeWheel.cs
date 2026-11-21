using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Time;

/// <summary>
/// Колесо выбора числа 0..count−1 как в приложении «Часы» на iPhone: три строки, средняя крупная и белая, соседние бледные.
/// Колесо мыши, перетаскивание (с «броском» и доводкой до строки) и клик по соседней строке. Значения идут по кругу.
/// Положение — пружина (<see cref="Spring"/>): движение можно перехватить в любой момент, скорость жеста не теряется.
/// </summary>
internal sealed class TimeWheel : FrameworkElement
{
    public const double RowHeight = 34;
    private const double CenterSize = 30, SideSize = 19;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);

    private readonly int _count;
    private readonly Spring _spring = new(dampingRatio: 1.0, responseSeconds: 0.30);
    private int _lastRaised;

    private bool _dragging, _moved;
    private double _dragStartY, _dragStartPosition, _lastY, _velocity; // скорость в строках в секунду
    private long _lastMoveTicks;

    /// <summary>Значение изменил пользователь (колесом, перетаскиванием, кликом). Программные <see cref="SetValue"/> его не вызывают.</summary>
    public event Action? ValueChanged;

    public TimeWheel(int count, double width = 48)
    {
        _count = count;
        Width = width;
        Height = RowHeight * 3;
        ClipToBounds = true;
        Cursor = Cursors.SizeNS;
        _spring.Changed += _ => InvalidateVisual();
    }

    /// <summary>Выбранное значение (куда колесо едет или уже приехало).</summary>
    public int Value => Mod((int)Math.Round(_spring.Target), _count);

    private static int Mod(int a, int n) => (a % n + n) % n;

    /// <summary>
    /// Прокрутка при входе во вкладку: колесо сразу встаёт на <paramref name="rows"/> строк «назад» и через <paramref name="delayMs"/> мс
    /// пружиной докатывается до своего значения. Значение (а с ним и событие) не меняется.
    /// </summary>
    public void Spin(int rows, int delayMs)
    {
        var target = _spring.Target;
        _spring.Snap(target - rows);
        if (delayMs <= 0) { _spring.SetTarget(target); return; }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) => { timer.Stop(); _spring.SetTarget(target); };
        timer.Start();
    }

    /// <summary>Поставить значение программно (по кратчайшему пути по кругу). Событие не вызывается.</summary>
    public void SetValue(int value, bool animate)
    {
        value = Mod(value, _count);
        _lastRaised = value;
        var current = (int)Math.Round(_spring.Target);
        var diff = Mod(value - Mod(current, _count) + _count / 2, _count) - _count / 2;
        if (diff == 0) return;
        if (animate) _spring.SetTarget(current + diff);
        else _spring.Snap(current + diff);
    }

    private void RaiseIfChanged()
    {
        var v = Value;
        if (v == _lastRaised) return;
        _lastRaised = v;
        ValueChanged?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight)); // чтобы колесо ловило мышь на пустых местах
        var position = _spring.Value;
        var cy = ActualHeight / 2;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var first = (int)Math.Floor(position) - 2;
        for (var i = first; i <= first + 5; i++)
        {
            var d = i - position;
            var ad = Math.Abs(d);
            if (ad > 2) continue;
            // Прозрачность: 1 в центре, ~0.38 на соседней строке, к двум строкам гаснет.
            var opacity = ad <= 1 ? 1 - 0.62 * ad : 0.38 * (2 - ad);
            var size = CenterSize - (CenterSize - SideSize) * Math.Min(ad, 1);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 255, 255, 255));
            var text = new FormattedText(Mod(i, _count).ToString("00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, size, brush, pixelsPerDip);
            dc.DrawText(text, new Point((w - text.Width) / 2, cy + d * RowHeight - text.Height / 2));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Как в списке: колесо вниз — следующее значение, вверх — предыдущее.
        _spring.SetTarget(Math.Round(_spring.Target) + (e.Delta < 0 ? 1 : -1));
        RaiseIfChanged();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _moved = false;
        _dragStartY = _lastY = e.GetPosition(this).Y;
        _dragStartPosition = _spring.Value;
        _velocity = 0;
        _lastMoveTicks = Stopwatch.GetTimestamp();
        _spring.Snap(_dragStartPosition); // перехват: колесо останавливается там, где оно сейчас
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var y = e.GetPosition(this).Y;
        var dy = y - _dragStartY;
        if (!_moved && Math.Abs(dy) < 4) return; // небольшой порог, чтобы клик не считался перетаскиванием
        _moved = true;

        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastMoveTicks) / (double)Stopwatch.Frequency;
        if (dt > 0.002)
        {
            var instant = -(y - _lastY) / RowHeight / dt;
            _velocity = 0.5 * _velocity + 0.5 * instant;
            _lastY = y;
            _lastMoveTicks = now;
        }

        _spring.Snap(_dragStartPosition - dy / RowHeight); // 1:1 с мышью
        RaiseIfChanged();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        Finish(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) Finish(_lastY);
    }

    private void Finish(double y)
    {
        _dragging = false;
        ReleaseMouseCapture();

        if (!_moved)
        {
            // Клик: по верхней строке — на строку назад, по нижней — вперёд, по средней ничего.
            var row = (y - ActualHeight / 2) / RowHeight;
            if (row > 0.5) _spring.SetTarget(Math.Round(_spring.Target) + 1);
            else if (row < -0.5) _spring.SetTarget(Math.Round(_spring.Target) - 1);
            RaiseIfChanged();
            return;
        }

        // Бросок: куда колесо уехало бы по инерции, туда и доводим до целой строки; скорость жеста передаём пружине.
        var stale = (Stopwatch.GetTimestamp() - _lastMoveTicks) / (double)Stopwatch.Frequency > 0.08;
        var v = stale ? 0 : _velocity;
        var target = Math.Round(_spring.Value + v * 0.12);
        _spring.SetTarget(target);
        _spring.Kick(v);
        RaiseIfChanged();
    }
}
