using System.Windows;
using System.Windows.Media;

namespace Ostrov.Modules.Time;

/// <summary>
/// Кольцо секундомера: тонкая дорожка и яркая точка-«стрелка», которая плавно идёт по кругу (оборот за минуту).
/// Угол — в градусах от верхней точки по часовой стрелке. Без заполнения и обнуления: на стыке минуты нет рывка.
/// </summary>
internal sealed class OrbitRing : FrameworkElement
{
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(OrbitRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(OrbitRing),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DotRadiusProperty = DependencyProperty.Register(
        nameof(DotRadius), typeof(double), typeof(OrbitRing),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(OrbitRing),
        new FrameworkPropertyMetadata(ArcRing.DefaultArcBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(OrbitRing),
        new FrameworkPropertyMetadata(ArcRing.DefaultTrackBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
    public double DotRadius { get => (double)GetValue(DotRadiusProperty); set => SetValue(DotRadiusProperty, value); }
    public Brush DotBrush { get => (Brush)GetValue(DotBrushProperty); set => SetValue(DotBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var dot = DotRadius;
        // Точка не должна выходить за границы элемента: радиус кольца считаем с запасом под неё.
        var r = Math.Min(ActualWidth, ActualHeight) / 2 - Math.Max(RingThickness / 2, dot);
        if (r <= 0) return;

        dc.DrawEllipse(null, new Pen(TrackBrush, RingThickness), center, r, r);

        var a = Angle * Math.PI / 180;
        dc.DrawEllipse(DotBrush, null, new Point(center.X + r * Math.Sin(a), center.Y - r * Math.Cos(a)), dot, dot);
    }
}
