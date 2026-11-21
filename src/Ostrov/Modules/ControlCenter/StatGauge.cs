using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.ControlCenter;

/// <summary>
/// Кольцо нагрузки: серая дорожка и белая дуга от верхней точки по часовой стрелке (как кольцо таймера, но белое и тоньше).
/// Отдельный класс, чтобы не трогать <see cref="ArcRing"/>: у него цвет и толщина зашиты под таймер.
/// </summary>
internal sealed class LoadRing : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(LoadRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Доля круга (0..1).</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    /// <summary>Плавно довести кольцо до нового значения (≈0,6 с, замедляясь к концу): новые замеры приходят каждые 250 мс, и кольцо не прыгает, а непрерывно перетекает.</summary>
    public void AnimateTo(double value, int ms = 600)
    {
        value = Math.Clamp(value, 0, 1);
        // Кольцо почти там, где нужно (разница меньше 1,5 %): незачем снова гонять анимацию и перерисовывать прозрачное окно.
        if (IsLoaded && Math.Abs(value - Fraction) < 0.015 && (value > 0 || Fraction == 0)) return;
        if (!IsLoaded)
        {
            BeginAnimation(FractionProperty, null);
            Fraction = value;
            return;
        }
        var anim = new System.Windows.Media.Animation.DoubleAnimation(value, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, 30); // перерисовка прозрачного окна дорогая: кольцу хватает 30 кадров
        BeginAnimation(FractionProperty, anim);
    }

    /// <summary>Толщина дуги (у колец вкладки «Центр» 5, у мини-колец хаба тоньше).</summary>
    public double Stroke { get; init; } = 5;

    /// <summary>Цвет дуги (по умолчанию светло-серый; у предупреждения о диске оранжево-красный).</summary>
    public Color ArcColor { get; init; } = Hues.InkColor;

    private Pen? _track, _arc;

    private static Pen MakePen(Color color, bool round, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        if (round) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = (Math.Min(ActualWidth, ActualHeight) - Stroke) / 2;
        if (r <= 0) return;
        var track = _track ??= MakePen(Color.FromArgb(0x26, 0xE9, 0xEC, 0xEF), round: false, Stroke);
        var arc = _arc ??= MakePen(ArcColor, round: true, Stroke);

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
