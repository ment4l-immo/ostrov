using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ostrov.Search;

/// <summary>
/// Шар-«персонаж» на время раздумий ИИ (порт аватара из примера пользователя: радиальный градиент, зерно, блик, глаза с морганием; цвет серебристо-белый).
/// Живёт по своей физике: взгляд скачет пружиной между «точками внимания» (надпись справа, вопрос выше, вверх, в сторону), тело запаздывает за глазами
/// и чуть наклоняется, лёгкое «дыхание», моргание в случайные моменты (иногда двойное), глаза при повороте сплющиваются, как на выпуклой поверхности.
/// Курсор над шаром притягивает взгляд (клик ничего не делает). Кадры считает CompositionTarget.Rendering, пока шар на экране.
/// <see cref="BeginFadeOut"/>: взгляд уходит вниз, на ответ, шар оседает и уменьшается (прозрачность гасит строка, <see cref="ThinkingRow"/>).
/// </summary>
internal sealed class ThinkingOrb : Grid
{
    public const double Size = 32;
    private const double GlowSpread = 8;    // насколько свечение выходит за край шара
    private const double FadeSeconds = 0.34;

    // Точки внимания в координатах шара (−1…1): x вправо, y вниз. Вес — как часто шар туда смотрит.
    private static readonly (double X, double Y, double Weight)[] Looks =
    [
        (1.00, 0.05, 0.34),   // на надпись справа
        (-0.15, -1.00, 0.22), // вверх, на вопрос
        (0.75, -0.75, 0.14),  // вверх-вправо: «думает»
        (-0.90, 0.10, 0.08),
        (0.10, 0.90, 0.08),
        (0.00, 0.00, 0.14)    // прямо
    ];

    private static readonly Brush Iris = Frozen(new LinearGradientBrush(Colors.White, Color.FromRgb(0xE3, 0xE3, 0xE8), new Point(0, 0), new Point(1, 1)));
    private static readonly ImageBrush Grain = MakeGrain();

    private readonly Grid _body = new() { Width = Size, Height = Size, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _bodyScale = new(1, 1);
    private readonly RotateTransform _bodyTilt = new();
    private readonly TranslateTransform _bodyMove = new();
    private readonly ScaleTransform _faceScale = new(1, 1);
    private readonly TranslateTransform _faceMove = new();
    private readonly ScaleTransform _lidLeft = new(1, 1), _lidRight = new(1, 1);

    private double _t, _last = -1;           // свои часы, секунды
    private double _gx, _gvx, _gy, _gvy;     // взгляд: положение (−1…1) и скорость
    private double _tx, _ty;                 // куда взгляд тянется
    private double _lx, _lvx, _ly, _lvy;     // сдвиг тела, px
    private double _ap, _apV;                // появление 0 → 1 (с лёгким перелётом)
    private double _nextGaze = 0.6, _nextBlink = 1.7, _blinkStart = -10, _fadeStart = -1;
    private int _look = -1;
    private bool _hover, _subscribed;

    public ThinkingOrb()
    {
        Width = Height = Size;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = false;

        _body.RenderTransform = new TransformGroup { Children = { _bodyScale, _bodyTilt, _bodyMove } };

        // Свечение: тёмная шапка не нужна, шар закрывает середину; видно только кольцо вокруг.
        var glow = new Ellipse
        {
            Margin = new Thickness(-GlowSpread), IsHitTestVisible = false,
            Fill = Radial(new Point(0.5, 0.5), 0.5, 0.5,
                (0.00, Color.FromArgb(62, 255, 255, 255)),
                (0.667, Color.FromArgb(62, 255, 255, 255)),
                (0.85, Color.FromArgb(12, 255, 255, 255)),
                (1.00, Color.FromArgb(0, 255, 255, 255)))
        };

        // Корпус: тёмный центр, светлый край (как в примере: radial-gradient(circle at 50% 45%, …)).
        var disk = new Ellipse
        {
            Fill = Radial(new Point(0.5, 0.45), 0.743, 0.743,
                (0.00, Color.FromRgb(0x2A, 0x2A, 0x2F)),
                (0.40, Color.FromRgb(0x72, 0x72, 0x79)),
                (0.68, Color.FromRgb(0xB6, 0xB6, 0xBC)),
                (1.00, Color.FromRgb(0xF0, 0xF0, 0xF2)))
        };
        var grain = new Ellipse { Fill = Grain, Opacity = 0.9, IsHitTestVisible = false };
        // Тень справа внизу и блик слева вверху.
        var shade = new Ellipse
        {
            IsHitTestVisible = false,
            Fill = Radial(new Point(0.62, 0.68), 0.877, 0.96,
                (0.00, Color.FromArgb(46, 0, 0, 0)), (0.55, Color.FromArgb(0, 0, 0, 0)), (1.00, Color.FromArgb(0, 0, 0, 0)))
        };
        var shine = new Ellipse
        {
            IsHitTestVisible = false,
            Fill = Radial(new Point(0.30, 0.24), 0.99, 1.07,
                (0.00, Color.FromArgb(150, 255, 255, 255)), (0.50, Color.FromArgb(22, 255, 255, 255)),
                (0.70, Color.FromArgb(0, 255, 255, 255)), (1.00, Color.FromArgb(0, 255, 255, 255)))
        };

        var face = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), IsHitTestVisible = false,
            RenderTransform = new TransformGroup { Children = { _faceScale, _faceMove } }
        };
        face.Children.Add(Eye(_lidLeft, 0));
        face.Children.Add(Eye(_lidRight, 6.5));

        _body.Children.Add(glow);
        _body.Children.Add(disk);
        _body.Children.Add(grain);
        _body.Children.Add(shade);
        _body.Children.Add(shine);
        _body.Children.Add(face);
        Children.Add(_body);

        _body.Opacity = 0; // до первого кадра шар не виден: появится пружиной
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    /// <summary>Шар «досмотрел»: взгляд на ответ, шар оседает и уменьшается за ~0,34 с.</summary>
    public void BeginFadeOut()
    {
        if (_fadeStart >= 0) return;
        _fadeStart = _t;
        _hover = false;
        _tx = 0.10; _ty = 1.0;
        _nextGaze = double.MaxValue;
        Subscribe();
    }

    // ───── Кадры ─────

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _last = -1;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime.TotalSeconds;
        if (_last < 0) { _last = now; return; }
        var dt = now - _last;
        if (dt <= 0) return; // WPF иногда присылает кадр дважды с одним временем
        _last = now;
        dt = Math.Min(dt, 0.05);
        _t += dt;

        if (!_hover && _t >= _nextGaze) PickGaze();
        if (_t >= _nextBlink && _fadeStart < 0) StartBlink();

        // Взгляд быстрый, с небольшим перелётом; тело тянется за ним медленнее и покачивается.
        Integrate(ref _gx, ref _gvx, _tx, 0.20, 0.62, dt);
        Integrate(ref _gy, ref _gvy, _ty, 0.20, 0.62, dt);
        Integrate(ref _lx, ref _lvx, _tx * 1.5, 0.52, 0.45, dt);
        Integrate(ref _ly, ref _lvy, _ty * 1.2, 0.52, 0.45, dt);
        Integrate(ref _ap, ref _apV, 1, 0.40, 0.58, dt);

        var fade = _fadeStart < 0 ? 0 : Smooth(Math.Clamp((_t - _fadeStart) / FadeSeconds, 0, 1));

        // Дыхание и покачивание на месте.
        var bobPhase = _t * 2 * Math.PI / 2.7;
        var bob = Math.Sin(bobPhase) * 1.3;
        var bobV = Math.Cos(bobPhase) * 1.3 * 2 * Math.PI / 2.7;
        var breathe = 0.012 * Math.Sin(_t * 2 * Math.PI / 3.3);

        // Сжатие и растяжение вдоль вертикального движения (покачивание тела).
        var stretch = Math.Min(Math.Abs(_lvy + bobV) / 1100, 0.10);
        var s = (0.55 + 0.45 * _ap) * (1 - 0.22 * fade);
        _bodyScale.ScaleX = s * (1 - stretch * 0.7 + breathe);
        _bodyScale.ScaleY = s * (1 + stretch - breathe);
        _bodyTilt.Angle = _lx * 1.8;
        _bodyMove.X = _lx;
        _bodyMove.Y = bob + _ly + 5 * fade;
        _body.Opacity = Math.Clamp(_ap * 1.6, 0, 1);

        // Лицо скользит по шару; у края оно сплющивается по ширине, как на выпуклой поверхности.
        _faceMove.X = _gx * 3.4;
        _faceMove.Y = _gy * 3.0 - 2;
        _faceScale.ScaleX = 1 - 0.14 * Math.Abs(_gx);
        _faceScale.ScaleY = 1 - 0.06 * Math.Abs(_gy);

        var blink = _t - _blinkStart;
        _lidLeft.ScaleY = 1 - 0.93 * Lid(blink);
        _lidRight.ScaleY = 1 - 0.93 * Lid(blink - 0.04);

        if (fade >= 1) Unsubscribe();
    }

    private void PickGaze()
    {
        int index;
        var guard = 0;
        do
        {
            var r = Random.Shared.NextDouble();
            index = 0;
            foreach (var (_, _, w) in Looks)
            {
                if (r < w) break;
                r -= w;
                index++;
            }
            index = Math.Min(index, Looks.Length - 1);
        } while (index == _look && ++guard < 4);
        _look = index;

        var (x, y, _) = Looks[index];
        var nx = Math.Clamp(x + (Random.Shared.NextDouble() - 0.5) * 0.24, -1, 1);
        var ny = Math.Clamp(y + (Random.Shared.NextDouble() - 0.5) * 0.24, -1, 1);
        var far = Math.Abs(nx - _tx) + Math.Abs(ny - _ty);
        _tx = nx; _ty = ny;
        _nextGaze = _t + (index == 5 ? Rand(0.5, 1.1) : Rand(1.0, 2.7));
        if (far > 1.4 && Random.Shared.NextDouble() < 0.3) StartBlink(); // человек часто моргает, когда отводит взгляд
    }

    private void StartBlink()
    {
        _blinkStart = _t;
        _nextBlink = Random.Shared.NextDouble() < 0.2 ? _t + 0.30 : _t + Rand(2.2, 5.0); // иногда подряд два раза
    }

    private static double Rand(double a, double b) => a + Random.Shared.NextDouble() * (b - a);

    /// <summary>Закрытость века: 0 — глаз открыт, 1 — закрыт (закрывается за 65 мс, открывается за 120 мс).</summary>
    private static double Lid(double t)
    {
        if (t < 0 || t > 0.22) return 0;
        if (t < 0.065) return Smooth(t / 0.065);
        if (t < 0.10) return 1;
        return 1 - Smooth((t - 0.10) / 0.12);
    }

    private static double Smooth(double x) => x * x * (3 - 2 * x);

    /// <summary>Пружина: значение тянется к цели с заданным откликом и затуханием; скорость сохраняется (как <see cref="Ostrov.Services.Spring"/>).</summary>
    private static void Integrate(ref double x, ref double v, double target, double response, double zeta, double dt)
    {
        var omega = 2 * Math.PI / response;
        var d = x - target;
        while (dt > 0)
        {
            var h = Math.Min(dt, 1.0 / 240);
            v += (-omega * omega * d - 2 * zeta * omega * v) * h;
            d += v * h;
            dt -= h;
        }
        x = target + d;
    }

    // ───── Мышь ─────

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_fadeStart >= 0) return;
        _hover = true;
        LookAt(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_hover) LookAt(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _nextGaze = _t + 0.5;
    }

    private void LookAt(Point p)
    {
        var dx = (p.X - Size / 2) / (Size / 2) * 1.6;
        var dy = (p.Y - Size / 2) / (Size / 2) * 1.6;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 1) { dx /= len; dy /= len; }
        _tx = dx; _ty = dy;
    }

    // ───── Кисти ─────

    private static Ellipse Eye(ScaleTransform lid, double left) => new()
    {
        Width = 4.5, Height = 7, Fill = Iris, Margin = new Thickness(left, 0, 0, 0),
        RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = lid
    };

    private static Brush Radial(Point center, double rx, double ry, params (double Offset, Color Color)[] stops)
    {
        var b = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry };
        foreach (var (offset, color) in stops) b.GradientStops.Add(new GradientStop(color, offset));
        b.Freeze();
        return b;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    // Мелкое зерно по шару (в примере — шум feTurbulence с наложением): плитка из случайных серых точек с малой прозрачностью.
    private static ImageBrush MakeGrain()
    {
        const int n = 48;
        var px = new byte[n * n * 4];
        var rnd = new Random(7);
        for (var i = 0; i < n * n; i++)
        {
            const int a = 14;
            var v = (byte)(rnd.Next(256) * a / 255);
            px[4 * i] = px[4 * i + 1] = px[4 * i + 2] = v;
            px[4 * i + 3] = a;
        }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Pbgra32, null, px, n * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, n, n), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
