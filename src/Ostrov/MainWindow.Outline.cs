using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov;

// Светлая тонкая обводка острова (М-001): появляется, когда фон вокруг острова тёмный и чёрная плашка на нём теряется.
// Фон определяется по яркости узкого кольца экрана рядом с островом (по бокам и снизу; сверху край экрана).
public partial class MainWindow
{
    private const double OutlineMaxOpacity = 0.30;               // яркость обводки (белая, 0..1): спокойная, не бросается в глаза
    private const double DarkBelow = 0.26, LightAbove = 0.34;    // порог яркости фона с «гистерезисом»: туда-сюда не мигает
    private static readonly TimeSpan OutlinePoll = TimeSpan.FromMilliseconds(3000);
    private static readonly Duration OutlineFade = new(TimeSpan.FromMilliseconds(300));

    private DispatcherTimer? _outlineTimer;
    private bool _outlineOn;
    private DateTime _layoutChangedAt = DateTime.UtcNow;
    private readonly string? _outlineDebugLog = Environment.GetEnvironmentVariable("OSTROV_DEBUG_OUTLINE") == "1"
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "outline-debug.log")
        : null;

    private void InitOutline()
    {
        _outlineTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = OutlinePoll };
        _outlineTimer.Tick += (_, _) => SampleBackdrop();
        UpdateOutlineTimer();
    }

    // Опрашиваем экран, только пока остров виден.
    private void UpdateOutlineTimer()
    {
        if (_outlineTimer is null) return;
        if (IsVisible) _outlineTimer.Start();
        else _outlineTimer.Stop();
    }

    // Размер острова меняется (анимация): в это время кольцо вокруг него считать нельзя, оно задело бы сам остров.
    private void NoteIslandLayoutChanged(CornerRadius radius)
    {
        _layoutChangedAt = DateTime.UtcNow;
        Outline.CornerRadius = radius;
        GlassRim.CornerRadius = radius;
        TintFill.CornerRadius = radius;
        CollapsedFill.CornerRadius = radius;
    }

    private void SampleBackdrop()
    {
        if (!IsVisible || (DateTime.UtcNow - _layoutChangedAt).TotalMilliseconds < 250) return;
        if (Island.ActualWidth < 1 || Island.ActualHeight < 1) return;

        var dpi = VisualTreeHelper.GetDpi(Island);
        var origin = Island.PointToScreen(new Point(0, 0)); // физические пиксели
        var x = (int)Math.Round(origin.X);
        var y = Math.Max((int)Math.Round(origin.Y), 0);
        var w = (int)Math.Round(Island.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Round(Island.ActualHeight * dpi.DpiScaleY);
        var gap = (int)Math.Round(3 * dpi.DpiScaleX);   // отступ от края острова (сглаживание края, баунс)
        var band = (int)Math.Round(18 * dpi.DpiScaleX); // толщина кольца

        // Справа может стоять кружок второй активности (чёрный, как остров): кольцо берём за ним.
        var bubbleSpace = Bubble.Visibility == Visibility.Visible
            ? (int)Math.Round((BubbleGap + ActivityCenter.BubbleSize) * dpi.DpiScaleX)
            : 0;

        var rects = new List<(int X, int Y, int W, int H)>
        {
            (x - gap - band, y, band, h),          // слева
            (x + w + gap + bubbleSpace, y, band, h), // справа
            (x, y + h + gap, w, band),             // снизу
        };
        // Чтение экрана (BitBlt) стоит ≈ 17 мс независимо от размера участков: на потоке интерфейса это был бы пропущенный кадр каждые 0,8 с (теперь опрос раз в 1,5 с),
        // поэтому читаем в фоне, а результат применяем на потоке интерфейса.
        if (_sampling) return;
        _sampling = true;
        var startedAt = DateTime.UtcNow;
        Task.Run(() => BackdropSampler.MeanLuminance(rects)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _sampling = false;
            // Остров менял размер, пока шло чтение: участки задели бы сам остров, результат выбрасываем.
            if (t.IsCompletedSuccessfully && _layoutChangedAt <= startedAt) ApplyOutline(t.Result, x, y, w, h);
        }));
    }

    private bool _sampling;

    private void ApplyOutline(double lum, int x, int y, int w, int h)
    {
        if (lum < 0 || !IsVisible) return;
        var shouldBeOn = _outlineOn ? lum < LightAbove : lum < DarkBelow;
        if (_outlineDebugLog is { } log)
        {
            try { System.IO.File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} lum={lum:0.000} on={shouldBeOn} island={x},{y} {w}x{h}\n"); }
            catch { /* отладочный журнал не должен мешать работе */ }
        }
        if (shouldBeOn == _outlineOn) return;
        _outlineOn = shouldBeOn;
        Outline.BeginAnimation(OpacityProperty, new DoubleAnimation(shouldBeOn ? OutlineMaxOpacity : 0, OutlineFade)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}
