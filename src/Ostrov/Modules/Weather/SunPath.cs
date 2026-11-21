using System.Windows;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Weather;

/// <summary>
/// Путь солнца за сутки, как у Apple: волна на 24 часа (слева полночь, справа следующая полночь) и линия горизонта.
/// Над горизонтом солнце «стоит» (линия белая), под ним «ушло» (линия тёмная). Точка — где солнце сейчас.
/// Волна всегда одной высоты, а горизонт смещается: зимой он выше (больше пути под ним), летом ниже.
/// Время задаётся в часах (0..24) по местному времени города.
/// </summary>
internal sealed class SunPath : FrameworkElement
{
    private static DependencyProperty Hour(string name) => DependencyProperty.Register(
        name, typeof(double), typeof(SunPath),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RiseHourProperty = Hour(nameof(RiseHour));
    public static readonly DependencyProperty SetHourProperty = Hour(nameof(SetHour));
    public static readonly DependencyProperty NowHourProperty = Hour(nameof(NowHour));
    /// <summary>Когда ближайшее событие (на горизонте ставится колечко); NaN — оно не на этих сутках (завтрашний восход).</summary>
    public static readonly DependencyProperty EventHourProperty = Hour(nameof(EventHour));

    public double RiseHour { get => (double)GetValue(RiseHourProperty); set => SetValue(RiseHourProperty, value); }
    public double SetHour { get => (double)GetValue(SetHourProperty); set => SetValue(SetHourProperty, value); }
    public double NowHour { get => (double)GetValue(NowHourProperty); set => SetValue(NowHourProperty, value); }
    public double EventHour { get => (double)GetValue(EventHourProperty); set => SetValue(EventHourProperty, value); }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush DimDot = Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly Pen HorizonPen = MakePen(Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Pen DayPen = MakePen(Hues.Ink, 2);
    private static readonly Pen NightPen = MakePen(Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 2);
    private static readonly Pen RingPen = MakePen(Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)), 1.2);

    private static Pen MakePen(Brush brush, double thickness)
    {
        var p = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 16 || double.IsNaN(RiseHour) || double.IsNaN(SetHour) || double.IsNaN(NowHour)) return;

        var amp = (h - 8) / 2; // 4 px запаса сверху и снизу под точку
        var center = h / 2;
        var noon = (RiseHour + SetHour) / 2;
        var dayLength = Math.Clamp(SetHour - RiseHour, 0.5, 23.5);
        var horizon = center - amp * Math.Cos(Math.PI * dayLength / 24);

        double X(double hour) => hour / 24 * w;
        double Y(double hour) => center - amp * Math.Cos(2 * Math.PI * (hour - noon) / 24);

        dc.DrawLine(HorizonPen, new Point(0, horizon), new Point(w, horizon));

        var curve = new StreamGeometry();
        using (var ctx = curve.Open())
        {
            ctx.BeginFigure(new Point(0, Y(0)), false, false);
            for (var t = 0.25; t <= 24.001; t += 0.25)
                ctx.LineTo(new Point(X(t), Y(t)), true, true);
        }
        curve.Freeze();

        // Часть волны выше горизонта — белая, ниже — тёмная.
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, horizon)));
        dc.DrawGeometry(null, DayPen, curve);
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(0, horizon, w, h - horizon)));
        dc.DrawGeometry(null, NightPen, curve);
        dc.Pop();

        if (!double.IsNaN(EventHour) && EventHour is >= 0 and <= 24)
            dc.DrawEllipse(null, RingPen, new Point(X(EventHour), horizon), 3, 3);

        var now = Math.Clamp(NowHour, 0, 24);
        var sun = new Point(X(now), Y(now));
        dc.DrawEllipse(Y(now) <= horizon ? Hues.Ink : DimDot, null, sun, 3.8, 3.8);
    }
}
