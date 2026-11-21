using System.Windows;
using System.Windows.Media;
using Ostrov.Backdrop;

namespace Ostrov;

// Цветной фон по обложке во вкладке «Плеер» и цветная обводка острова (М-003, связь с М-001).
public partial class MainWindow
{
    // Обводка острова: белая, а во вкладке с цветным фоном берёт цвета краёв фона (слева, снизу, справа).
    private readonly LinearGradientBrush _outlineTint = new()
    {
        StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
        GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Colors.White, 0.5), new GradientStop(Colors.White, 1) }
    };

    private void InitGlow()
    {
        IslandBackdrop.Surface = Glow;
        Outline.BorderBrush = _outlineTint;
        IslandBackdrop.TintChanged += ApplyOutlineTint;
    }

    private void ApplyOutlineTint(EdgeTint tint)
    {
        _outlineTint.GradientStops[0].Color = Mix(tint.Left, tint.Amount);
        _outlineTint.GradientStops[1].Color = Mix(tint.Bottom, tint.Amount);
        _outlineTint.GradientStops[2].Color = Mix(tint.Right, tint.Amount);
    }

    // Плавный переход белая ↔ цвет края.
    private static Color Mix(Color edge, double amount) => Color.FromRgb(
        (byte)Math.Round(255 + (edge.R - 255) * amount), (byte)Math.Round(255 + (edge.G - 255) * amount), (byte)Math.Round(255 + (edge.B - 255) * amount));

    // Фон имеет размер раскрытого острова (как содержимое) и проявляется вместе с ним; свёрнутый остров остаётся чёрным.
    private void UpdateGlow(double progress, double width, double height)
    {
        Glow.Width = width;
        Glow.Height = height;
        Glow.Opacity = Math.Clamp((progress - 0.3) / 0.5, 0, 1) * NightGlow;
        Glow.Visibility = progress > 0.02 ? Visibility.Visible : Visibility.Collapsed;
        if (progress > 0.02) ApplyGlowClip(width);
    }

    // Фон обрезается по текущей скруглённой форме острова: ClipToBounds у Border режет по прямоугольнику,
    // и без этого прямоугольник фона торчал бы острыми углами за скруглением (верх у острова прямой, внизу радиус).
    private void ApplyGlowClip(double glowWidth)
    {
        double w = Island.Width, h = Island.Height;
        if (w < 2 || h < 2) return;
        var r = Math.Min(Island.CornerRadius.BottomLeft, Math.Min(w, h) / 2);
        var x0 = (glowWidth - w) / 2; // фон по центру, остров тоже
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(x0, 0), true, true);
            c.LineTo(new Point(x0 + w, 0), true, false);
            c.LineTo(new Point(x0 + w, h - r), true, false);
            c.ArcTo(new Point(x0 + w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x0 + r, h), true, false);
            c.ArcTo(new Point(x0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        Glow.Clip = geometry;
    }
}
