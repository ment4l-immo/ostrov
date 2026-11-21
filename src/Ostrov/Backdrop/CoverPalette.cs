using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ostrov.Backdrop;

/// <summary>
/// Главные цвета обложки для фона: картинка уменьшается до 24×24, пиксели собираются в четыре группы (k-средних, насыщенные
/// пиксели весят больше), группы упорядочены по размеру. Цвета подгоняются под фон: не слишком тёмные и не кричащие.
/// Серая обложка остаётся серой. Тёмная целиком или не картинка — null (фона не будет).
/// </summary>
internal static class CoverPalette
{
    public const int Slots = 4;
    private const int Size = 24;

    public static Color[]? FromImage(System.Windows.Media.ImageSource? image)
    {
        if (image is not BitmapSource source || source.PixelWidth < 2 || source.PixelHeight < 2) return null;
        try
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform((double)Size / source.PixelWidth, (double)Size / source.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            var points = new List<(double R, double G, double B, double W)>();
            for (var i = 0; i < w * h; i++)
            {
                if (px[i * 4 + 3] < 128) continue;
                double b = px[i * 4] / 255.0, g = px[i * 4 + 1] / 255.0, r = px[i * 4 + 2] / 255.0;
                var (_, s, l) = ColorMath.ToHsl(r, g, b);
                if (l < 0.08) continue; // почти чёрное (поля, тени) палитру не определяет
                points.Add((r, g, b, 0.25 + s * (1 - Math.Abs(2 * l - 1))));
            }
            return points.Count < 6 ? null : Cluster(points);
        }
        catch
        {
            return null;
        }
    }

    private static double Dist2((double R, double G, double B, double W) p, (double R, double G, double B) c) =>
        (p.R - c.R) * (p.R - c.R) + (p.G - c.G) * (p.G - c.G) + (p.B - c.B) * (p.B - c.B);

    private static Color[] Cluster(List<(double R, double G, double B, double W)> pts)
    {
        var k = Math.Min(Slots, pts.Count);
        var centers = new (double R, double G, double B)[k];
        var first = pts.MaxBy(p => p.W);
        centers[0] = (first.R, first.G, first.B);
        for (var c = 1; c < k; c++)
        {
            var best = -1.0;
            var pick = pts[0];
            foreach (var p in pts)
            {
                var d = double.MaxValue;
                for (var j = 0; j < c; j++) d = Math.Min(d, Dist2(p, centers[j]));
                var score = d * p.W;
                if (score > best) { best = score; pick = p; }
            }
            centers[c] = (pick.R, pick.G, pick.B);
        }

        var weight = new double[k];
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var sum = new (double R, double G, double B, double W)[k];
            foreach (var p in pts)
            {
                var nearest = 0;
                var nd = double.MaxValue;
                for (var j = 0; j < k; j++)
                {
                    var d = Dist2(p, centers[j]);
                    if (d < nd) { nd = d; nearest = j; }
                }
                sum[nearest] = (sum[nearest].R + p.R * p.W, sum[nearest].G + p.G * p.W, sum[nearest].B + p.B * p.W, sum[nearest].W + p.W);
            }
            for (var j = 0; j < k; j++)
            {
                weight[j] = sum[j].W;
                if (sum[j].W > 0) centers[j] = (sum[j].R / sum[j].W, sum[j].G / sum[j].W, sum[j].B / sum[j].W);
            }
        }

        var ordered = Enumerable.Range(0, k).OrderByDescending(j => weight[j]).Select(j => Normalize(centers[j])).ToList();
        // Мало разных цветов: добираем похожими, чуть светлее и темнее главного.
        for (var i = 0; ordered.Count < Slots; i++)
        {
            var (h, s, l) = ColorMath.ToHsl(ordered[0].R / 255.0, ordered[0].G / 255.0, ordered[0].B / 255.0);
            var shifted = ColorMath.FromHsl(h, s, Math.Clamp(l + (i % 2 == 0 ? 0.10 : -0.08), 0.2, 0.7));
            ordered.Add(Color.FromRgb((byte)Math.Round(shifted.R * 255), (byte)Math.Round(shifted.G * 255), (byte)Math.Round(shifted.B * 255)));
        }
        return ordered.ToArray();
    }

    // Подгонка под фон: серое остаётся серым; цветное чуть насыщеннее и в средней светлоте.
    private static Color Normalize((double R, double G, double B) c)
    {
        var (h, s, l) = ColorMath.ToHsl(c.R, c.G, c.B);
        if (s < 0.12) l = Math.Clamp(l, 0.28, 0.58);
        else
        {
            s = Math.Max(s, 0.5);
            l = Math.Clamp(l, 0.36, 0.58);
        }
        var (r, g, b) = ColorMath.FromHsl(h, s, l);
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}
