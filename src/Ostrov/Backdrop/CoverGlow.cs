using System.Windows;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Backdrop;

/// <summary>
/// Цветной фон острова по обложке (вкладка «Плеер»): четыре больших мягких пятна главных цветов обложки, которые очень медленно плывут.
/// Рисуются радиальными градиентами (без тяжёлого размытия). Цвета при смене песни плавно перетекают, при уходе с вкладки пятна
/// растут и расходятся к краям, гаснут. Кадры идут, только пока фон виден. Яркость умеренная: заметно, но не бросается в глаза.
/// Положение окна/размер задаёт MainWindow (размер раскрытого острова); <see cref="Sample"/> нужен для яркости под текстом и цвета краёв.
/// </summary>
internal sealed class CoverGlow : FrameworkElement
{
    private const int Slots = CoverPalette.Slots;
    internal const double CenterAlpha = 0.44;   // непрозрачность в центре пятна (было 0,52; на 15 % прозрачнее по просьбе пользователя)
    private const double MidStop = 0.45, MidFactor = 0.6; // середина спада градиента: на 45 % радиуса остаётся 60 % непрозрачности

    // Пятна: положение (доля размеров), радиус (доля большей стороны), размах дрейфа, период (с) и фаза.
    private static readonly (double X, double Y, double R, double Ax, double Ay, double Period, double Phase)[] Blobs =
    [
        (0.12, 0.20, 0.60, 0.10, 0.14, 26, 0.0),
        (0.88, 0.15, 0.55, 0.09, 0.16, 31, 1.7),
        (0.22, 0.95, 0.58, 0.12, 0.10, 23, 3.1),
        (0.92, 0.90, 0.62, 0.10, 0.12, 29, 4.6),
    ];

    private readonly double[][] _current = new double[Slots][];
    private readonly double[][] _target = new double[Slots][];
    private bool _hasColors, _wanted, _attached;
    private double _amount, _time, _sinceTint;
    // Сила и размах фона владельца (вкладки «Время», «Проекты» и т. д. делают его тише и теснее, чем у плеера и погоды): плавно догоняют цель.
    private double _intensity = 1, _spread = 1, _intensityTarget = 1, _spreadTarget = 1;
    private TimeSpan _lastFrame;

    // Дождь и снег на фоне (вкладка «Погода»): очень тихие, поверх пятен. Рисуется то, что в _dropKind; смена вида: старое гаснет, новое проявляется.
    private PrecipKind _dropKind, _dropWanted;
    private double _dropIntensity, _dropAmount;
    private static readonly Particle[] Drops = MakeParticles(70, seed: 11), Flakes = MakeParticles(80, seed: 23);

    /// <summary>Частица: положение и фаза (0..1), «глубина» (0 дальняя .. 1 ближняя, от неё размер, яркость и скорость) и своя частота покачивания.</summary>
    private readonly record struct Particle(double X, double Phase, double Depth, double Sway);

    private static Particle[] MakeParticles(int count, int seed)
    {
        var rnd = new Random(seed);
        var list = new Particle[count];
        for (var i = 0; i < count; i++) list[i] = new Particle(rnd.NextDouble(), rnd.NextDouble(), rnd.NextDouble(), 0.5 + rnd.NextDouble());
        return list;
    }

    public CoverGlow()
    {
        for (var i = 0; i < Slots; i++) { _current[i] = new double[3]; _target[i] = new double[3]; }
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Wake();
        IslandBackdrop.StateChanged += Sync;
        Sync();
    }

    // Что должно быть: палитра есть, вкладка открыта, поверх вкладок нет карточки.
    private void Sync()
    {
        var palette = IslandBackdrop.Palette;
        if (palette is { Length: > 0 })
        {
            for (var i = 0; i < Slots; i++)
            {
                var c = palette[Math.Min(i, palette.Length - 1)];
                _target[i][0] = c.R; _target[i][1] = c.G; _target[i][2] = c.B;
            }
            if (!_hasColors)
            {
                for (var i = 0; i < Slots; i++) Array.Copy(_target[i], _current[i], 3); // первый раз: сразу, без перетекания из чёрного
                _hasColors = true;
            }
        }
        _intensityTarget = IslandBackdrop.Intensity;
        _spreadTarget = IslandBackdrop.Spread;
        if (!_hasColors) { _intensity = _intensityTarget; _spread = _spreadTarget; }
        _wanted = palette is { Length: > 0 } && IslandBackdrop.Active && !IslandBus.OverlayActive;
        _dropWanted = _wanted ? IslandBackdrop.Precip : PrecipKind.None;
        _dropIntensity = IslandBackdrop.PrecipIntensity;
        Wake();
    }

    private void Wake()
    {
        if (IsVisible && (_wanted || _amount > 0.002)) Attach();
        else Detach();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == TimeSpan.Zero) { _lastFrame = now; return; }
        var dt = (now - _lastFrame).TotalSeconds;
        // Не чаще ≈ 30 кадров в секунду, пока что-то меняется (переход цветов, появление, осадки); когда всё улеглось, пятна только медленно плывут
        // (оборот за 23–31 с), и хватает ≈ 12 кадров: перерисовка прозрачного окна дорогая, это главная постоянная нагрузка раскрытого острова.
        var settled = Settled();
        // Цвета улеглись, но идут осадки: 24 кадра в секунду достаточно для тихих штрихов и точек; пока цвета переходят: до 30.
        var interval = settled ? SettledInterval : ColorsCalm() ? 0.042 : 0.030;
        if (dt < interval) return;
        _lastFrame = now;
        dt = Math.Min(dt, 0.1);

        _time += dt;
        var targetAmount = _wanted ? 1.0 : 0.0;
        _amount += (targetAmount - _amount) * (1 - Math.Exp(-dt / (targetAmount > _amount ? 0.35 : 0.20)));
        // Осадки: другой вид (или их нет) — сначала гаснут, потом включается нужный.
        if (_dropKind != _dropWanted)
        {
            _dropAmount += (0 - _dropAmount) * (1 - Math.Exp(-dt / 0.25));
            if (_dropAmount < 0.01) { _dropKind = _dropWanted; _dropAmount = 0; }
        }
        else if (_dropKind != PrecipKind.None)
        {
            _dropAmount += (1 - _dropAmount) * (1 - Math.Exp(-dt / 0.6));
        }
        var kc = 1 - Math.Exp(-dt / 0.8); // цвета перетекают очень плавно
        _intensity += (_intensityTarget - _intensity) * kc;
        _spread += (_spreadTarget - _spread) * kc;
        for (var i = 0; i < Slots; i++)
            for (var c = 0; c < 3; c++)
                _current[i][c] += (_target[i][c] - _current[i][c]) * kc;

        InvalidateVisual();
        IslandBackdrop.RaiseFrame(dt);

        _sinceTint += dt;
        var finished = !_wanted && _amount < 0.002;
        if (_sinceTint >= 0.1 || finished)
        {
            _sinceTint = 0;
            PublishTint(finished ? 0 : _amount);
        }
        if (finished)
        {
            _amount = 0;
            Detach();
            InvalidateVisual();
        }
        else if (settled && _intensityTarget < QuietBelow && _dropKind == PrecipKind.None)
        {
            // Тихий фон вкладки (непрозрачность 12 %) улёгся: его дрейф глазом не виден, а каждая перерисовка прозрачного окна стоит процессора.
            // Оставляем последний кадр как есть и больше не рисуем, пока что-то не изменится (Sync → Wake включит снова).
            Detach();
        }
    }

    private const double SettledInterval = 0.25; // улёгшийся фон плывёт медленно (оборот за 23–31 с): 4 кадра в секунду хватает
    private const double QuietBelow = 0.5;       // у фона слабее этой силы (вкладки без обложки и погоды) дрейф после улёгшегося перехода не рисуем

    // Всё улеглось: появление и цвета дошли до цели, осадков нет. Тогда фон только плывёт.
    private bool Settled() => _dropKind == PrecipKind.None && _dropKind == _dropWanted && ColorsCalm();

    private bool ColorsCalm()
    {
        if (Math.Abs((_wanted ? 1.0 : 0.0) - _amount) > 0.01 || Math.Abs(_intensityTarget - _intensity) > 0.01 || Math.Abs(_spreadTarget - _spread) > 0.01) return false;
        for (var i = 0; i < Slots; i++)
            for (var c = 0; c < 3; c++)
                if (Math.Abs(_target[i][c] - _current[i][c]) > 1.5) return false;
        return true;
    }

    // Положение пятна: медленный дрейф; при уходе с вкладки (leave > 0) пятна растут и расходятся к краям.
    private void Layout(int i, out double cx, out double cy, out double r)
    {
        var b = Blobs[i];
        double w = ActualWidth, h = ActualHeight;
        var a = 2 * Math.PI * _time / b.Period + b.Phase;
        cx = (b.X + b.Ax * Math.Sin(a)) * w;
        cy = (b.Y + b.Ay * Math.Cos(a * 0.8 + 1.3)) * h;
        var leave = 1 - _amount;
        cx += (cx - w / 2) * 0.25 * leave;
        cy += (cy - h / 2) * 0.25 * leave;
        r = b.R * Math.Max(w, h) * (1 + 0.35 * leave) * _spread;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_hasColors || _amount < 0.003 || ActualWidth < 2 || ActualHeight < 2) return;
        for (var i = 0; i < Blobs.Length; i++)
        {
            Layout(i, out var cx, out var cy, out var r);
            var col = _current[i % Slots];
            byte R = (byte)Math.Round(col[0]), G = (byte)Math.Round(col[1]), B = (byte)Math.Round(col[2]);
            var alpha = CenterAlpha * _amount * _intensity;
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), R, G, B), 0),
                    new GradientStop(Color.FromArgb((byte)Math.Round(alpha * MidFactor * 255), R, G, B), MidStop),
                    new GradientStop(Color.FromArgb(0, R, G, B), 1)
                }
            };
            dc.DrawEllipse(brush, null, new Point(cx, cy), r, r);
        }
        DrawPrecip(dc);
    }

    // Дождь: тонкие наклонные штрихи; снег: мелкие точки, плавно покачиваются. Ближние крупнее, ярче и быстрее.
    // Яркость малая («не бросающийся в глаза»): рисуется поверх пятен, но под всем содержимым вкладки.
    // Кисти и перья — по четыре «ступени глубины» на кадр (а не по одной на частицу).
    private void DrawPrecip(DrawingContext dc)
    {
        var visible = _dropAmount * _amount;
        if (_dropKind == PrecipKind.None || visible < 0.01) return;
        double w = ActualWidth, h = ActualHeight;
        var t = _time;
        var rain = _dropKind == PrecipKind.Rain;
        var pool = rain ? Drops : Flakes;
        var count = (int)Math.Ceiling(pool.Length * (0.35 + 0.65 * _dropIntensity));

        var pens = new Pen[4];
        var brushes = new Brush[4];
        for (var k = 0; k < 4; k++)
        {
            var depth = (k + 0.5) / 4;
            if (rain)
            {
                var alpha = (0.09 + 0.14 * depth) * visible;
                pens[k] = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 200, 222, 255)), 0.8 + 0.5 * depth)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            }
            else
            {
                var alpha = (0.22 + 0.26 * depth) * visible;
                brushes[k] = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 238, 246, 255));
            }
        }

        const double slope = 0.2; // наклон дождя: на 1 px вниз 0,2 px вправо
        for (var i = 0; i < count; i++)
        {
            var p = pool[i];
            var k = Math.Min(3, (int)(p.Depth * 4));
            if (rain)
            {
                var len = 9 + 10 * p.Depth;
                var cycle = h + len * 2;
                var y = (p.Phase * cycle + (360 + 260 * p.Depth) * t) % cycle - len;
                var x = p.X * (w + h * slope) - h * slope + y * slope;
                dc.DrawLine(pens[k], new Point(x, y), new Point(x + len * slope, y + len));
            }
            else
            {
                var r = 1.0 + 1.5 * p.Depth;
                var cycle = h + r * 4;
                var y = (p.Phase * cycle + (18 + 24 * p.Depth) * t) % cycle - r * 2;
                var x = p.X * w + Math.Sin(t * 0.55 * p.Sway + p.Phase * 6.28) * (7 + 8 * p.Depth);
                dc.DrawEllipse(brushes[k], null, new Point(x, y), r, r);
            }
        }
    }

    /// <summary>Цвет фона в точке (в координатах этого элемента): пятна наложены на чёрный, как их рисует <see cref="OnRender"/>.</summary>
    public (double R, double G, double B) Sample(double x, double y)
    {
        double sr = 0, sg = 0, sb = 0;
        if (!_hasColors) return (0, 0, 0);
        for (var i = 0; i < Blobs.Length; i++)
        {
            Layout(i, out var cx, out var cy, out var r);
            var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / r;
            if (d >= 1) continue;
            var f = d < MidStop ? 1 - (1 - MidFactor) * (d / MidStop) : MidFactor * (1 - (d - MidStop) / (1 - MidStop));
            var a = CenterAlpha * _amount * _intensity * f;
            var col = _current[i % Slots];
            sr = sr * (1 - a) + col[0] * a;
            sg = sg * (1 - a) + col[1] * a;
            sb = sb * (1 - a) + col[2] * a;
        }
        return (sr, sg, sb);
    }

    /// <summary>Средняя яркость фона (0..1) под элементом (по пяти точкам внутри него).</summary>
    public double LuminanceUnder(FrameworkElement element)
    {
        if (_amount < 0.01 || !IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1) return 0;
        Rect rect;
        try { rect = element.TransformToVisual(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)); }
        catch (InvalidOperationException) { return 0; }

        double sum = 0;
        foreach (var (fx, fy) in new[] { (0.15, 0.5), (0.5, 0.5), (0.85, 0.5), (0.5, 0.15), (0.5, 0.85) })
        {
            var (r, g, b) = Sample(rect.X + rect.Width * fx, rect.Y + rect.Height * fy);
            sum += ColorMath.Luma(r, g, b) / 255.0;
        }
        return sum / 5;
    }

    // Цвета краёв для обводки острова: слева, снизу по центру, справа. Подняты до светлых (обводка не темнеет ниже заданной яркости).
    private void PublishTint(double amount)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        IslandBackdrop.PublishTint(new EdgeTint(
            OutlineColor(Sample(2, h * 0.5)), OutlineColor(Sample(w * 0.5, h - 2)), OutlineColor(Sample(w - 2, h * 0.5)),
            amount * Math.Min(1, _intensity * 1.6))); // у тихого фона и цветная обводка слабее
    }

    private static Color OutlineColor((double R, double G, double B) c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max < 1) return Colors.White;
        double r = c.R / max, g = c.G / max, b = c.B / max; // цвет края на полной яркости
        var (h, s, l) = ColorMath.ToHsl(r, g, b);
        var (nr, ng, nb) = ColorMath.FromHsl(h, Math.Min(s, 0.8), Math.Clamp(l, 0.66, 0.82));
        return Color.FromRgb((byte)Math.Round(nr * 255), (byte)Math.Round(ng * 255), (byte)Math.Round(nb * 255));
    }
}
