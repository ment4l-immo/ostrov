using System.Windows;
using System.Windows.Media;

namespace Ostrov.Backdrop;

/// <summary>
/// «Умные чернила»: текст и значки остаются белыми или серыми, а там, где под ними светлое пятно фона и читать трудно,
/// плавно темнеют (идея как у маски difference в After Effects). Яркость фона берётся из <see cref="IslandBackdrop"/>.
/// </summary>
internal sealed class AdaptiveInk
{
    private sealed class Item
    {
        public required FrameworkElement Element { get; init; }
        public required Action<Brush> Apply { get; init; }
        public required Color Normal { get; init; }
        public required Color OnLight { get; init; }
        public double T = -1;
        public Color Applied;
    }

    // Яркость фона, при которой текст начинает темнеть, и при которой он полностью тёмный. Пороги высокие и переход короткий:
    // серая середина читается хуже и белого, и тёмного (проверено на жёлтой палитре), поэтому темнеть текст должен только на по-настоящему светлом.
    private const double From = 0.46, To = 0.58;

    private readonly List<Item> _items = [];

    public void Add(FrameworkElement element, Action<Brush> apply, Color normal, Color onLight) =>
        _items.Add(new Item { Element = element, Apply = apply, Normal = normal, OnLight = onLight, Applied = normal });

    public void Update(double dt)
    {
        var k = 1 - Math.Exp(-dt / 0.25);
        foreach (var item in _items)
        {
            var target = ColorMath.Smooth((IslandBackdrop.LuminanceUnder(item.Element) - From) / (To - From));
            item.T = item.T < 0 ? target : item.T + (target - item.T) * k;
            Set(item, Lerp(item.Normal, item.OnLight, item.T));
        }
    }

    /// <summary>Пометить цвета непримёнными: на следующем кадре они выставятся заново (например, значок заменили новым).</summary>
    public void Invalidate()
    {
        foreach (var item in _items) item.Applied = default;
    }

    /// <summary>Вернуть обычные цвета (вкладку закрыли).</summary>
    public void Reset()
    {
        foreach (var item in _items)
        {
            item.T = -1;
            Set(item, item.Normal);
        }
    }

    private static void Set(Item item, Color color)
    {
        if (color == item.Applied) return;
        item.Applied = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        item.Apply(brush);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
