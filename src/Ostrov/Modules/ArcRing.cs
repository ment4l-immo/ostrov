using System.Windows;
using System.Windows.Media;

namespace Ostrov.Modules;

/// <summary>
/// Кольцо-индикатор в стиле таймера Apple: тёмная дорожка и оранжевая дуга от верхней точки по часовой стрелке.
/// <see cref="Fraction"/> — доля круга (0..1). Толщину и цвета можно менять (по умолчанию как у кольца на вкладке «Время»).
/// </summary>
internal sealed class ArcRing : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(ArcRing),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(ArcRing),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Оранжевый как у Apple — третий цвет острова для таймера и секундомера.
    public static readonly Brush DefaultArcBrush = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    public static readonly Brush DefaultTrackBrush = Frozen(Color.FromRgb(0x26, 0x26, 0x26));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(ArcRing),
        new FrameworkPropertyMetadata(DefaultArcBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ArcRing),
        new FrameworkPropertyMetadata(DefaultTrackBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public Brush ArcBrush
    {
        get => (Brush)GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var thickness = RingThickness;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = (Math.Min(ActualWidth, ActualHeight) - thickness) / 2;
        if (r <= 0) return;

        var track = new Pen(TrackBrush, thickness);
        var arc = new Pen(ArcBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        dc.DrawEllipse(null, track, center, r, r);

        var f = Math.Clamp(Fraction, 0, 1);
        if (f <= 0.0005) return;
        if (f >= 0.9995)
        {
            dc.DrawEllipse(null, arc, center, r, r);
            return;
        }

        var angle = f * 2 * Math.PI;
        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X + r * Math.Sin(angle), center.Y - r * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, arc, geometry);
    }
}
