using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Ostrov.Services;

namespace Ostrov.Modules.Player;

internal enum PlayerIcon { Play, Pause, Previous, Next, Lyrics }

/// <summary>
/// Значки кнопок плеера: залитые, минималистичные, с чуть скруглёнными углами (как play.fill, pause.fill, backward.end.fill у Apple).
/// Скругление делает обводка того же цвета с круглым соединением углов. Рисуются векторно под нужный размер.
/// </summary>
internal static class PlayerIcons
{
    // Значок «текст песни»: залитый пузырь с хвостиком, в нём вырезаны три строки (как text.bubble.fill у Apple).
    private static FrameworkElement CreateLyrics(double s)
    {
        var bubble = new CombinedGeometry(GeometryCombineMode.Union,
            new RectangleGeometry(new Rect(0.06 * s, 0.12 * s, 0.88 * s, 0.66 * s), 0.18 * s, 0.18 * s),
            Triangle(s, (0.20, 0.68), (0.20, 0.93), (0.46, 0.74)));
        var lines = new GeometryGroup();
        foreach (var (x, y, w) in new[] { (0.22, 0.28, 0.56), (0.22, 0.42, 0.56), (0.22, 0.56, 0.34) })
            lines.Children.Add(new RectangleGeometry(new Rect(x * s, y * s, w * s, 0.075 * s), 0.0375 * s, 0.0375 * s));
        var shape = new CombinedGeometry(GeometryCombineMode.Exclude, bubble, lines);
        shape.Freeze();
        return new Path { Data = shape, Fill = Hues.Ink, Width = s, Height = s, StrokeThickness = 0, IsHitTestVisible = false };
    }

    private static Geometry Triangle(double s, params (double X, double Y)[] p)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) Polygon(c, s, p);
        g.Freeze();
        return g;
    }

    public static FrameworkElement Create(PlayerIcon icon, double size)
    {
        if (icon == PlayerIcon.Lyrics) return CreateLyrics(size);
        var stroke = Math.Max(1.2, size * 0.09); // толщина обводки = радиус скругления × 2
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            switch (icon)
            {
                case PlayerIcon.Play:
                    Polygon(c, size, (0.30, 0.14), (0.30, 0.86), (0.88, 0.50));
                    break;
                case PlayerIcon.Pause:
                    Polygon(c, size, (0.20, 0.14), (0.20, 0.86), (0.40, 0.86), (0.40, 0.14));
                    Polygon(c, size, (0.60, 0.14), (0.60, 0.86), (0.80, 0.86), (0.80, 0.14));
                    break;
                case PlayerIcon.Previous:
                    Polygon(c, size, (0.10, 0.16), (0.10, 0.84), (0.22, 0.84), (0.22, 0.16));
                    Polygon(c, size, (0.90, 0.15), (0.90, 0.85), (0.42, 0.50));
                    break;
                case PlayerIcon.Next:
                    Polygon(c, size, (0.90, 0.16), (0.90, 0.84), (0.78, 0.84), (0.78, 0.16));
                    Polygon(c, size, (0.10, 0.15), (0.10, 0.85), (0.58, 0.50));
                    break;
            }
        }
        geometry.Freeze();

        return new Path
        {
            Data = geometry,
            Fill = Hues.Ink,
            Stroke = Hues.Ink,
            StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Round,
            Width = size,
            Height = size,
            IsHitTestVisible = false
        };
    }

    private static void Polygon(StreamGeometryContext c, double size, params (double X, double Y)[] points)
    {
        c.BeginFigure(new Point(points[0].X * size, points[0].Y * size), true, true);
        for (var i = 1; i < points.Length; i++)
            c.LineTo(new Point(points[i].X * size, points[i].Y * size), true, true);
    }
}
