using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Ostrov.Backdrop;
using Ostrov.Services;

namespace Ostrov.Modules.Settings;

/// <summary>
/// Образцы для выбора картинками в настройках. Всё в цветах острова (полуночный синий #191970, графит #212529, светло-серый #E9ECEF,
/// оранжево-красный #FB5607 только мелким пятном): никаких радужных обоев.
/// </summary>
internal static class SettingsPictures
{
    private static readonly byte[] GlassAlphas = [0xDB, 0xC7, 0xB0]; // те же значения, что у острова (MainWindow.GlassAlpha)

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Line = Frozen(Color.FromRgb(0x2B, 0x30, 0x35));

    // Плитка-основа графитовая с тонкой рамкой.
    private static Border Tile(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(9), Background = Hues.Surface, BorderBrush = Line, BorderThickness = new Thickness(1), Child = child, ClipToBounds = true
    };

    // ───── «Стекло»: остров на «обоях» из полуночного синего с оранжевым пятном (чтобы было видно, что просвечивает и размыто) ─────

    private static Brush Wallpaper()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x19, 0x19, 0x70), 0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x4A, 0xB4), 0.55));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xE9, 0xEC, 0xEF), 1));
        b.Freeze();
        return b;
    }

    public static FrameworkElement Glass(int i)
    {
        var wall = Wallpaper();
        var scene = new Grid { ClipToBounds = true };
        scene.Children.Add(new Border { Background = wall, CornerRadius = new CornerRadius(9) });
        // Небольшое оранжево-красное пятно: даёт острову что «просвечивать».
        scene.Children.Add(new Ellipse { Width = 24, Height = 24, Fill = Hues.Alert, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) });

        var island = new Grid { Width = 60, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        island.Clip = new RectangleGeometry(new Rect(0, -12, 60, 32 + 12), 10, 10);
        // Под плашкой те же обои, размытые (как за настоящим островом).
        var blurred = new Grid { Margin = new Thickness(-30, -20, -30, -20), Effect = new BlurEffect { Radius = 8 } };
        blurred.Children.Add(new Border { Background = wall });
        blurred.Children.Add(new Ellipse { Width = 24, Height = 24, Fill = Hues.Alert, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(48, 30, 0, 0) });
        island.Children.Add(blurred);
        island.Children.Add(new Border { Background = GlassBrush.Create(GlassAlphas[i]) });
        island.Children.Add(Dots(0xB0));
        scene.Children.Add(island);
        return scene;
    }

    private static FrameworkElement Dots(byte alpha)
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };
        foreach (var width in new[] { 14.0, 8.0, 4.0 })
            dots.Children.Add(new Border { Width = width, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(alpha, 0xE9, 0xEC, 0xEF)), Margin = new Thickness(2, 0, 2, 0) });
        return dots;
    }

    // ───── «Сворачивать сам»: кольцо-таймер, чем дольше, тем больше дуга; «никогда» — знак бесконечности ─────

    public static FrameworkElement Collapse(int i)
    {
        double[] fractions = [0.3, 0.5, 0.8, 0];
        var box = new Grid { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(new Ellipse { Stroke = Line, StrokeThickness = 3.5, Margin = new Thickness(2) });
        if (fractions[i] > 0)
        {
            var f = fractions[i];
            const double c = 15, r = 13;
            var end = new Point(c + r * Math.Sin(f * 2 * Math.PI), c - r * Math.Cos(f * 2 * Math.PI));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c, c - r), false, false);
                ctx.ArcTo(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            box.Children.Add(new Path { Data = g, Stroke = Hues.Ink, StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        else
        {
            box.Children.Add(new TextBlock { Text = "∞", FontSize = 18, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
        }
        return Tile(box);
    }

    // ───── Модель ответов: маленькая карточка с номером и классом модели («5» и «mini») ─────

    public static FrameworkElement Model(string label)
    {
        var parts = label.Split('-', 2);
        var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };
        text.Inlines.Add(new System.Windows.Documents.Run(parts[0]) { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink });
        if (parts.Length > 1) text.Inlines.Add(new System.Windows.Documents.Run(" " + parts[1]) { FontSize = 10.5, Foreground = Ui.Gray });
        return Tile(text);
    }

    // ───── Ночной режим: день и ночь ─────

    public static FrameworkElement Night(int i)
    {
        var night = i == 1;
        var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (night)
        {
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x19, 0x19, 0x70), 0));
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x0B, 0x0B, 0x38), 1));
        }
        else
        {
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xE9, 0xEC, 0xEF), 0));
            sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x9C, 0xA6, 0xB3), 1));
        }
        sky.Freeze();
        var scene = new Grid { ClipToBounds = true };
        scene.Children.Add(new Border { Background = sky, CornerRadius = new CornerRadius(9), BorderBrush = Line, BorderThickness = new Thickness(1) });
        var island = new Grid { Width = 40, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        island.Clip = new RectangleGeometry(new Rect(0, -10, 40, 30), 9, 9);
        island.Children.Add(new Border { Background = Brushes.Black, Opacity = night ? 0.8 : 1 });
        island.Children.Add(Dots(night ? (byte)0x70 : (byte)0xC8));
        scene.Children.Add(island);
        // Солнце (оранжево-красное) днём, луна ночью.
        scene.Children.Add(night
            ? new TextBlock { Text = "", FontFamily = Ui.IconFont, FontSize = 12, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 4) }
            : new Ellipse { Width = 10, Height = 10, Fill = Hues.Alert, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 7, 6) });
        return scene;
    }
}
