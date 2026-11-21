using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Backdrop;
using Ostrov.Services;

namespace Ostrov;

// Стекло острова и размытие фона под ним. Размытие делает одно окно BlurBehind прямо под островом (см. там, почему оно только прямоугольное).
// Прямоугольник вписан в остров так, чтобы его углы лежали внутри нижних скруглений: от боковых краёв и от низа он отступает на ≈ 0,3 радиуса.
// Узкую полосу без размытия у боков и у низа прячет заливка острова: к краям она плотнее (GlassBrush по бокам, BottomFade снизу).
// Раньше было два окна (верхнее и «нижнее между углами»), и нижняя полоса высотой в радиус выглядела отдельной плашкой, поэтому окно одно.
// Окно переставляется редко, не на каждом кадре анимации (см. UpdateBlurBounds): лаги от постоянной смены размера окна с размытием.
public partial class MainWindow
{
    private const double BlurInset = 2;            // зазор между окнами размытия и краем острова (сглаживание края)
    private const double BlurFadeHeight = 28;      // высота затемнения у нижнего края (скрывает полосу без размытия)
    private const double FadeBaseline = 0.9;       // плотность, до которой подтягивало бы затемнение; на деле берётся FadeStrength от неё (выбор пользователя 10 %)
    private const double FadeStrength = 0.1;
    private static readonly byte[] GlassAlpha = [0xDB, 0xC7, 0xB0]; // «слабо» 86 %, «средне» 78 % (выбор пользователя в стенде), «сильно» 69 % непрозрачности

    private const int BlurSteps = 8;                // полос «лесенки» по дуге скругления; всего окон BlurSteps + 1 (основное и полосы)
    private readonly BlurBehind?[] _blurs = new BlurBehind?[BlurSteps + 1];
    private (int X, int Y, int W, int H)[]? _blurApplied;
    private const double BlurSideSlack = 6;       // насколько окно размытия может выступать за боковой край, не считаясь «выглядывающим»
    private double _glassCenterAlpha = 0xCC / 255.0;

    private void InitBlur()
    {
        IsVisibleChanged += (_, _) => SyncBlurVisibility();
        ApplyGlass();
    }

    // Прозрачность плашки (острова, кружков и перемычки обмена) и включение размытия из настроек.
    private void ApplyGlass()
    {
        var settings = AppSettings.Load();
        var alpha = GlassAlpha[Math.Clamp(settings.GlassLevel, 0, GlassAlpha.Length - 1)];
        _glassCenterAlpha = alpha / 255.0;
        var brush = new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0)); // кружки и перемычка принадлежат свёрнутому острову: прежние 88 %
        brush.Freeze();
        Island.Background = GlassBrush.Create(alpha); // у боковых краёв темнее: размытие там почти не видно
        Bubble.Background = brush;
        BubbleLeft.Background = brush;
        DropNeck.Fill = brush;
        BottomFade.Background = FadeBrush(_glassCenterAlpha);
        // Добавка чёрного, которая вместе с заливкой даёт прежние 88 % у свёрнутого острова.
        CollapsedFill.Opacity = 1 - _glassAmount;
        CollapsedFill.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * CollapsedExtra(_glassCenterAlpha)), 0, 0, 0));

        // OSTROV_NO_BLUR=1 — не создавать окна размытия (проверки в оболочке не должны показывать окна на экране).
        var want = settings.BlurBackdrop && Environment.GetEnvironmentVariable("OSTROV_NO_BLUR") != "1";
        if (want && _blurs[0] == null)
        {
            for (var i = 0; i < _blurs.Length; i++) _blurs[i] = new BlurBehind();
            _blurApplied = null;
            SyncBlurVisibility();
        }
        else if (!want && _blurs[0] != null)
        {
            for (var i = 0; i < _blurs.Length; i++) { _blurs[i]?.Close(); _blurs[i] = null; }
            _blurApplied = null;
        }
        BottomFade.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
    }

    // Затемнение у нижнего края: вместе с заливкой острова даёт плотность не меньше EdgeOpaque прямо у края и плавно сходит на нет вверх.
    private static Brush FadeBrush(double centerAlpha)
    {
        var need = centerAlpha >= FadeBaseline ? 0 : FadeStrength * (1 - (1 - FadeBaseline) / (1 - centerAlpha)); // добавка поверх заливки
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        (double Offset, double Mix)[] curve = [(0, 0), (0.35, 0.12), (0.6, 0.42), (0.8, 0.75), (1, 1)];
        foreach (var (offset, mix) in curve)
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(255 * need * mix), 0, 0, 0), offset));
        b.Freeze();
        return b;
    }

    private double _glassAmount;      // 0 — свёрнутый остров (ровный чёрный как раньше), 1 — раскрытый (стекло, кромка, блик, оттенок)
    private const double CollapsedAlpha = 0.88;

    private static double CollapsedExtra(double centerAlpha) => centerAlpha >= CollapsedAlpha ? 0 : 1 - (1 - CollapsedAlpha) / (1 - centerAlpha);

    // Стекло включается по мере раскрытия: в свёрнутом виде остров ровный чёрный 88 %, как до всех правок вида (решение пользователя 2026-11-20).
    private void ApplyGlassAmount(double progress)
    {
        var g = Math.Clamp(progress / 0.5, 0, 1);
        _glassAmount = g;
        CollapsedFill.Opacity = 1 - g;
        TintFill.Opacity = TintMax * g;
        GlassRim.Opacity = g;
        GlassSheen.Opacity = g * NightInk;
        BottomFade.Opacity = g;
    }
    private const double TintMax = 0.08;

    // Оттенок заливки по вкладке: цвет плавно перетекает при смене вкладки.
    private void ApplyTabTint(string moduleId)
    {
        var to = Backdrop.TabPalettes.TintFor(moduleId);
        if (TintFill.Background is not SolidColorBrush brush || brush.IsFrozen) { TintFill.Background = new SolidColorBrush(to); return; }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });
    }

    private IntPtr OwnHandle => new WindowInteropHelper(this).Handle;

    private void SyncBlurVisibility()
    {
        if (_blurs[0] == null) return;
        if (IsVisible)
        {
            foreach (var blur in _blurs) if (blur is { IsVisible: false }) blur.Show();
            UpdateBlurBounds();
            KeepBlurBelow();
        }
        else foreach (var blur in _blurs) if (blur is { IsVisible: true }) blur.Hide();
    }

    /// <summary>Снова поставить размытие прямо под остров (после того, как остров поднят поверх всех).</summary>
    private void KeepBlurBelow()
    {
        var own = OwnHandle;
        foreach (var blur in _blurs) blur?.PutBelow(own);
    }

    // Размеры раскрытого острова, к которым он идёт (их задаёт AnimateSize): окно размытия ставится по ним, а не по каждому кадру анимации.
    private double _goalWidth = DefaultExpandedWidth, _goalHeight = DefaultExpandedHeight;

    // Окна размытия для острова заданного размера (физические пиксели): основное до начала нижних скруглений и BlurSteps полос «лесенкой» по дуге.
    // Окно закрывает прямоугольник целиком (форму окна размытие игнорирует), поэтому каждая полоса вписана в остров по самой «широкой» точке своего участка дуги.
    // null — остров слишком мал.
    private (int X, int Y, int W, int H)[]? BlurRects(double w, double h, double r)
    {
        var k = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var left = (Left + (Width - w) / 2) * k;
        var top = Top * k;
        var inset = BlurInset * k;
        var body = (h - r) * k;
        if (body < 14 || (w - 2 * BlurInset) * k < 8) return null;
        var rects = new (int X, int Y, int W, int H)[BlurSteps + 1];
        var x = left + inset;
        rects[0] = ((int)Math.Round(x), (int)Math.Round(top), (int)Math.Round(w * k - 2 * inset), (int)Math.Round(body));
        var arc = Math.Max(1, (r - BlurInset)); // высота дуги, которую закрываем (до зазора внизу)
        for (var i = 0; i < BlurSteps; i++)
        {
            double dy0 = i * arc / BlurSteps, dy1 = (i + 1) * arc / BlurSteps;
            var off = (r - Math.Sqrt(Math.Max(0, r * r - dy1 * dy1))) * k; // насколько дуга отступает от бока к низу участка
            var side = Math.Ceiling(off);
            var y0 = top + body + dy0 * k;
            var y1 = top + body + dy1 * k;
            rects[i + 1] = ((int)Math.Round(x + side), (int)Math.Round(y0), Math.Max(1, (int)Math.Round(w * k - 2 * inset - 2 * side)), Math.Max(1, (int)Math.Round(y1) - (int)Math.Round(y0)));
        }
        return rects;
    }

    // Вмещаются ли окна размытия в остров (с запасом по бокам, чтобы баунс и округление не показали их за краем).
    private bool BlurFits((int X, int Y, int W, int H)[] wins, double w, double h)
    {
        var k = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var slack = (int)Math.Round(BlurSideSlack * k);
        var left = (int)Math.Round((Left + (Width - w) / 2) * k);
        var right = left + (int)Math.Round(w * k);
        var bottom = (int)Math.Round((Top + h) * k);
        foreach (var win in wins)
            if (win.X < left - slack || win.X + win.W > right + slack || win.Y + win.H > bottom + 2) return false;
        return true;
    }

    private static bool SameRects((int X, int Y, int W, int H)[]? a, (int X, int Y, int W, int H)[]? b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // Окна размытия переставляются редко, не на каждом кадре анимации (смена размера окна с размытием заставляет систему заново строить поверхность, и остров лагал):
    //  • остров дошёл до своего размера: окна встают по нему (один раз);
    //  • растёт (раскрытие, переход на вкладку повыше): окна остаются прежними, они меньше острова, это безопасно; на нужный размер встанут, когда остров дойдёт;
    //  • сжимается до вкладки пониже: окна сразу ставятся по итоговому размеру (он уже меньше острова), размытие не пропадает;
    //  • сворачивается: окна прячутся.
    private void UpdateBlurBounds()
    {
        // Затемнение у низа нужно только раскрытому острову (у маленькой плашки оно съело бы почти всю высоту).
        var fadeHeight = Math.Max(0, Math.Min(BlurFadeHeight, Island.Height / 2));
        if (Math.Abs(BottomFade.Height - fadeHeight) > 0.01) BottomFade.Height = fadeHeight;
        if (BottomFade.CornerRadius != Island.CornerRadius) BottomFade.CornerRadius = Island.CornerRadius;

        if (_blurs[0] is not { IsVisible: true }) return;
        double w = Island.Width, h = Island.Height; // без баунса: боковой запас больше его размаха

        var settled = Progress >= 0.995 && Math.Abs(w - _goalWidth) < 1 && Math.Abs(h - _goalHeight) < 1;
        if (settled)
        {
            var goal = BlurRects(_goalWidth, _goalHeight, ExpandedRadius);
            if (!SameRects(goal, _blurApplied)) ApplyBlurRects(goal);
            return;
        }
        if (_blurApplied is not { } cur) return; // остров ещё не дошёл до размера: размытия пока нет
        if (BlurFits(cur, w, h)) return;         // окна всё ещё внутри острова
        // Остров стал меньше окон. Если он сжимается до вкладки пониже, ставим окна по итоговому размеру, иначе прячем.
        var goalRects = BlurRects(_goalWidth, _goalHeight, ExpandedRadius);
        if (Progress > 0.5 && goalRects != null && BlurFits(goalRects, w, h)) ApplyBlurRects(goalRects);
        else ApplyBlurRects(null);
    }

    private void ApplyBlurRects((int X, int Y, int W, int H)[]? rects)
    {
        if (_blurs[0] is not { IsVisible: true }) return;
        if (rects == null)
        {
            if (_blurApplied != null) { foreach (var blur in _blurs) blur?.Park(); _blurApplied = null; }
            return;
        }
        if (SameRects(rects, _blurApplied)) return;
        for (var i = 0; i < _blurs.Length && i < rects.Length; i++) _blurs[i]?.SetBounds(rects[i].X, rects[i].Y, rects[i].W, rects[i].H);
        _blurApplied = rects;
    }
}
