using System.Windows;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Weather;

/// <summary>Простые монохромные иконки погоды (белое и серое), нарисованные кодом в сетке 24×24.</summary>
internal sealed class WeatherIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(WeatherKind), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(WeatherKind.Clear, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(
        nameof(IsDay), typeof(bool), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public WeatherKind Kind { get => (WeatherKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool IsDay { get => (bool)GetValue(IsDayProperty); set => SetValue(IsDayProperty, value); }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush White = Hues.Ink;
    private static readonly Brush CloudGray = Frozen(Color.FromRgb(0xA8, 0xA8, 0xA8));
    private static readonly Pen Line = MakePen(Hues.Ink, 1.8);
    private static readonly Pen FogLine = MakePen(CloudGray, 1.8);

    private static Pen MakePen(Brush brush, double thickness)
    {
        var p = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static readonly Geometry CloudShape = BuildCloud();
    private static readonly Geometry MoonShape = BuildMoon();
    private static readonly Geometry PartlyHole = BuildPartlyHole(); // всё, кроме облака с каймой: по нему обрезается солнце или луна за облаком
    private static readonly Geometry BoltShape = Geometry.Parse("M13.4,14.2 L9.2,19.4 L12.2,19.4 L10.4,23.8 L15.4,18 L12.4,18 L14.6,14.2 Z");

    private static Geometry BuildCloud()
    {
        static Geometry Circle(double x, double y, double r) => new EllipseGeometry(new Point(x, y), r, r);
        static Geometry Union(Geometry a, Geometry b) => new CombinedGeometry(GeometryCombineMode.Union, a, b);
        var g = Union(Union(Union(Circle(7.5, 14.5, 4), Circle(12.5, 10.5, 5.5)), Circle(17.5, 14, 4.5)),
                      new RectangleGeometry(new Rect(7.5, 12, 10, 6.5)));
        g.Freeze();
        return g;
    }

    // Солнце за облаком «вырезается» настоящей геометрией (облако плюс зазор вокруг него), а не чёрным контуром:
    // на цветном фоне острова чёрная кайма была бы видна как тёмный ободок.
    private static Geometry BuildPartlyHole()
    {
        var cloud = CloudShape.Clone();
        cloud.Transform = new MatrixTransform(0.85, 0, 0, 0.85, 4.3, 5.6);
        var widened = cloud.GetWidenedPathGeometry(new Pen(Brushes.Black, 1.7));
        var covered = new CombinedGeometry(GeometryCombineMode.Union, cloud, widened);
        var hole = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-10, -10, 44, 44)), covered);
        hole.Freeze();
        return hole;
    }

    private static Geometry BuildMoon()
    {
        var g = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(12, 12), 7, 7),
            new EllipseGeometry(new Point(16.2, 8.6), 6.2, 6.2));
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var s = Math.Min(ActualWidth, ActualHeight) / 24;
        if (s <= 0) return;
        dc.PushTransform(new MatrixTransform(s, 0, 0, s, (ActualWidth - 24 * s) / 2, (ActualHeight - 24 * s) / 2));

        switch (Kind)
        {
            case WeatherKind.Clear:
                if (IsDay) Sun(dc, 12, 12, 4.6, 7.6, 10.6); else Moon(dc, 12, 12, 1);
                break;
            case WeatherKind.PartlyCloudy:
                dc.PushClip(PartlyHole);
                if (IsDay) Sun(dc, 8.5, 8.5, 3.3, 5.5, 7.6); else Moon(dc, 8.5, 8.5, 0.55);
                dc.Pop();
                Cloud(dc, 0.85, 4.3, 5.6, outline: false);
                break;
            case WeatherKind.Cloudy:
                Cloud(dc, 1.05, -1.4, -0.3, outline: false);
                break;
            case WeatherKind.Fog:
                dc.DrawLine(FogLine, new Point(5, 7), new Point(19, 7));
                dc.DrawLine(FogLine, new Point(3, 11.5), new Point(17, 11.5));
                dc.DrawLine(FogLine, new Point(7, 16), new Point(21, 16));
                dc.DrawLine(FogLine, new Point(5, 20.5), new Point(15, 20.5));
                break;
            case WeatherKind.Rain:
                Cloud(dc, 0.9, 1.3, -2.4, outline: false);
                foreach (var x in new[] { 8.5, 12.5, 16.5 })
                    dc.DrawLine(Line, new Point(x, 16.8), new Point(x - 1.6, 21.2));
                break;
            case WeatherKind.Snow:
                Cloud(dc, 0.9, 1.3, -2.4, outline: false);
                foreach (var (x, y) in new[] { (8.5, 17.6), (12.5, 17.6), (16.5, 17.6), (10.5, 21.4), (14.5, 21.4) })
                    dc.DrawEllipse(White, null, new Point(x, y), 1.1, 1.1);
                break;
            case WeatherKind.Thunder:
                Cloud(dc, 0.9, 1.3, -2.4, outline: false);
                dc.DrawGeometry(White, null, BoltShape);
                break;
        }
        dc.Pop();
    }

    private static void Sun(DrawingContext dc, double cx, double cy, double r, double rayFrom, double rayTo)
    {
        dc.DrawEllipse(White, null, new Point(cx, cy), r, r);
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            dc.DrawLine(Line,
                new Point(cx + Math.Cos(a) * rayFrom, cy + Math.Sin(a) * rayFrom),
                new Point(cx + Math.Cos(a) * rayTo, cy + Math.Sin(a) * rayTo));
        }
    }

    private static void Moon(DrawingContext dc, double cx, double cy, double k)
    {
        dc.PushTransform(new MatrixTransform(k, 0, 0, k, cx - 12 * k, cy - 12 * k));
        dc.DrawGeometry(White, null, MoonShape);
        dc.Pop();
    }

    private static void Cloud(DrawingContext dc, double scale, double tx, double ty, bool outline)
    {
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, tx, ty));
        dc.DrawGeometry(CloudGray, null, CloudShape);
        dc.Pop();
    }
}
