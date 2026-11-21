using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Weather;

/// <summary>
/// График одного дня по часам: сверху линия температуры (белая, с подписью максимума и минимума),
/// снизу серые столбики вероятности осадков (ярче — если ожидаются сами осадки), внизу подписи часов.
/// Для сегодняшнего дня отмечено текущее время. При наведении мыши рисуются вертикаль и точка, а значения часа показывает
/// всплывающая карточка <see cref="HourTip"/> (её ставит вкладка по событию <see cref="HoverChanged"/>).
/// </summary>
internal sealed class DayChart : FrameworkElement
{
    private static readonly CultureInfo Ru = new("ru-RU");
    private static readonly Typeface Face = new("Segoe UI");
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static readonly Brush LabelBrush = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BarDim = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush BarWet = Frozen(Color.FromRgb(0x8C, 0x8C, 0x8C));
    private static readonly Pen LinePen = MakePen(Hues.Ink, 2, round: true);
    private static readonly Pen GuidePen = MakePen(Frozen(Color.FromRgb(0x55, 0x55, 0x55)), 1, round: false);

    private static Pen MakePen(Brush b, double t, bool round)
    {
        var p = new Pen(b, t) { LineJoin = PenLineJoin.Round };
        if (round) p.StartLineCap = p.EndLineCap = PenLineCap.Round;
        p.Freeze();
        return p;
    }

    // Раскрытие слева направо при смене дня: линия и столбики «дорисовываются» (0..1; обычно 1).
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(DayChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    /// <summary>Нарисовать график заново слева направо (≈ 0,55 с).</summary>
    public void PlayReveal() =>
        BeginAnimation(RevealProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(550))
        {
            EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });

    private IReadOnlyList<HourPoint> _hours = [];
    private int _nowIndex = -1;
    private int _hover = -1;

    /// <summary>Наведение на час (индекс и точка температуры на графике, в координатах графика); −1 — мышь ушла.</summary>
    public event Action<int, Point>? HoverChanged;

    // Раскладка графика (общая для рисования и для точки наведения).
    private const double AxisH = 15, TopPad = 20;
    private readonly record struct Geo(double Slot, double LineTop, double LineBottom, double PlotBottom, double TMin, double TMax)
    {
        public double X(int i) => (i + 0.5) * Slot;
        public double Y(double t) => LineTop + (TMax - t) / (TMax - TMin) * (LineBottom - LineTop);
    }

    private bool TryGeo(out Geo g)
    {
        double w = ActualWidth, h = ActualHeight;
        g = default;
        var n = _hours.Count;
        if (n == 0 || w <= 0 || h <= 40) return false;
        var plotBottom = h - AxisH;
        var barsH = Math.Min(34, (plotBottom - TopPad) * 0.3);
        var tMin = _hours.Min(p => p.Temp);
        var tMax = _hours.Max(p => p.Temp);
        if (tMax - tMin < 3) { var mid = (tMax + tMin) / 2; tMin = mid - 1.5; tMax = mid + 1.5; }
        g = new Geo(w / n, TopPad, plotBottom - barsH - 10, plotBottom, tMin, tMax);
        return true;
    }

    /// <summary>Часы дня и индекс текущего часа (−1, если это не сегодня).</summary>
    public void SetData(IReadOnlyList<HourPoint> hours, int nowIndex)
    {
        _hours = hours;
        _nowIndex = nowIndex;
        if (_hover >= 0) HoverChanged?.Invoke(-1, default);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_hours.Count == 0 || ActualWidth <= 0) return;
        var i = Math.Clamp((int)(e.GetPosition(this).X / ActualWidth * _hours.Count), 0, _hours.Count - 1);
        if (i == _hover) return;
        _hover = i;
        InvalidateVisual();
        if (TryGeo(out var g)) HoverChanged?.Invoke(i, new Point(g.X(i), g.Y(_hours[i].Temp)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
        HoverChanged?.Invoke(-1, default);
    }

    private FormattedText Text(string s, double size, Brush brush) =>
        new(s, Ru, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        // Пока график раскрывается (смена дня), виден только левый кусок: линия и столбики «дорисовываются» слева направо.
        var reveal = Math.Clamp(Reveal, 0, 1);
        if (reveal < 0.999) dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth * reveal, ActualHeight)));
        Draw(dc);
        if (reveal < 0.999) dc.Pop();
    }

    private void Draw(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // чтобы мышь «видела» весь график
        var n = _hours.Count;
        if (n == 0 || w <= 0 || h <= 40) return;

        if (!TryGeo(out var geo)) return;
        const double topPad = TopPad;
        var plotBottom = geo.PlotBottom;
        var barsH = Math.Min(34, (plotBottom - topPad) * 0.3);
        var slot = geo.Slot;
        var tMin = geo.TMin;
        var tMax = geo.TMax;
        double X(int i) => geo.X(i);
        double Y(double t) => geo.Y(t);

        // Осадки.
        var bw = Math.Max(2, slot - 3);
        for (var i = 0; i < n; i++)
        {
            var p = _hours[i];
            if (p.PrecipProb <= 0) continue;
            var bh = Math.Max(2, barsH * p.PrecipProb / 100.0);
            dc.DrawRoundedRectangle(p.Precip >= 0.05 ? BarWet : BarDim, null,
                new Rect(X(i) - bw / 2, plotBottom - bh, bw, bh), 1.5, 1.5);
        }

        // Метка «сейчас».
        if (_nowIndex >= 0 && _nowIndex < n)
            dc.DrawLine(GuidePen, new Point(X(_nowIndex), topPad - 2), new Point(X(_nowIndex), plotBottom));

        // Линия температуры (плавная, без «перелётов» за пределы соседних значений).
        var pts = Enumerable.Range(0, n).Select(i => new Point(X(i), Y(_hours[i].Temp))).ToArray();
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (var i = 0; i < n - 1; i++)
            {
                var p0 = pts[Math.Max(i - 1, 0)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(i + 2, n - 1)];
                var lo = Math.Min(p1.Y, p2.Y);
                var hi = Math.Max(p1.Y, p2.Y);
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, lo, hi));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, lo, hi));
                ctx.BezierTo(c1, c2, p2, true, true);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, LinePen, g);

        // Подписи максимума и минимума.
        var iMax = IndexOf(_hours, tMax: true);
        var iMin = IndexOf(_hours, tMax: false);
        Label(dc, WeatherFormat.Temp(_hours[iMax].Temp), pts[iMax], above: true, w);
        if (iMin != iMax) Label(dc, WeatherFormat.Temp(_hours[iMin].Temp), pts[iMin], above: false, w);

        // Точка «сейчас».
        if (_nowIndex >= 0 && _nowIndex < n)
            dc.DrawEllipse(Hues.Ink, null, pts[_nowIndex], 3.5, 3.5);

        // Подписи часов (каждые 3 часа).
        for (var i = 0; i < n; i++)
        {
            if (_hours[i].Time.Hour % 3 != 0) continue;
            var t = Text(_hours[i].Time.Hour.ToString("00"), 10, LabelBrush);
            dc.DrawText(t, new Point(X(i) - t.Width / 2, plotBottom + 2));
        }

        // Наведение: вертикаль и точка (значения часа показывает карточка HourTip).
        if (_hover >= 0)
        {
            dc.DrawLine(GuidePen, new Point(X(_hover), topPad - 2), new Point(X(_hover), plotBottom));
            dc.DrawEllipse(Hues.Ink, null, pts[_hover], 3.5, 3.5);
        }
    }

    private static int IndexOf(IReadOnlyList<HourPoint> hours, bool tMax)
    {
        var best = 0;
        for (var i = 1; i < hours.Count; i++)
            if (tMax ? hours[i].Temp > hours[best].Temp : hours[i].Temp < hours[best].Temp) best = i;
        return best;
    }

    private void Label(DrawingContext dc, string text, Point at, bool above, double width)
    {
        var t = Text(text, 11, LabelBrush);
        var x = Math.Clamp(at.X - t.Width / 2, 0, width - t.Width);
        dc.DrawText(t, new Point(x, above ? at.Y - t.Height - 3 : at.Y + 4));
    }
}
