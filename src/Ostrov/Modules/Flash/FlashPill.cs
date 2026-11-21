using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Services;

namespace Ostrov.Modules.Flash;

/// <summary>
/// Плашка короткой вспышки (М-020) размером с обычный свёрнутый остров, как у таймера: слева значок, справа значение.
/// Язык ввода («RU»), Caps Lock и Num Lock (замок и «CAPS»/«NUM»: белые при включении, серые при выключении),
/// громкость (динамик, полоска уровня и проценты), микрофон («вкл»/«выкл»). Только белый и серый. Один вид на все вспышки: его обновляют на месте.
/// </summary>
internal sealed class FlashPill : Grid
{
    public const double ViewWidth = 200, ViewHeight = 36;

    private const string Globe = "", Lock = "", Unlock = "", Speaker = "", Muted = "", Mic = "";
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static readonly Brush Track = Frozen(Color.FromRgb(0x33, 0x33, 0x33));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // Линия центров та же, что у плашек таймера и музыки: значок на 3 px, цифры на 4,8 px выше нижнего края (оба на 1,5 px выше центра острова).
    private readonly ScaleTransform _iconScale = new(1, 1);
    private readonly RotateTransform _iconTurn = new(0);
    private readonly TranslateTransform _textShift = new(0, 0);
    private readonly SolidColorBrush _ink = new(Colors.White); // цвет значка, текста и полоски: перетекает между белым и серым
    private string _kind = "";
    private string _glyph = "";

    private readonly TextBlock _icon = new()
    {
        FontFamily = Ui.IconFont, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16, 0, 0, 3), RenderTransformOrigin = new Point(0.5, 0.5)
    };
    private readonly TextBlock _text = new()
    {
        FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 16, 4.8)
    };
    private readonly Border _barTrack = new()
    {
        Width = 96, Height = 4, CornerRadius = new CornerRadius(2), Background = Track, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(48, 0, 0, 3), Visibility = Visibility.Collapsed
    };
    private readonly Border _barFill = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Left };

    public FlashPill()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Typography.SetNumeralAlignment(_text, FontNumeralAlignment.Tabular);
        _icon.RenderTransform = new TransformGroup { Children = { _iconScale, _iconTurn } };
        _text.RenderTransform = _textShift;
        _icon.Foreground = _ink;
        _text.Foreground = _ink;
        _barFill.Background = _ink;
        _barTrack.Child = _barFill;
        Children.Add(_icon);
        Children.Add(_barTrack);
        Children.Add(_text);
    }

    /// <summary>Язык ввода: «RU», «EN».</summary>
    public void ShowLanguage(string code) => Set("lang", Globe, code, on: true, bar: null);

    /// <summary>Caps Lock или Num Lock: замок и название; включён — белым и закрытый замок, выключен — серым и открытый.</summary>
    public void ShowLock(string name, bool on) => Set(name, on ? Lock : Unlock, name, on, bar: null);

    /// <summary>Громкость Windows: динамик (перечёркнутый, если звук выключен), полоска и проценты.</summary>
    public void ShowVolume(double level, bool muted)
    {
        var shown = muted ? 0 : Math.Clamp(level, 0, 1);
        Set("volume", muted ? Muted : Speaker, ((int)Math.Round(shown * 100)).ToString(), on: !muted, bar: shown);
    }

    /// <summary>Микрофон включён или выключен.</summary>
    public void ShowMicrophone(bool muted) => Set("mic", Mic, muted ? "выкл" : "вкл", on: !muted, bar: null);

    private void Set(string kind, string glyph, string text, bool on, double? bar)
    {
        // Значок и значение оживают, когда вспышка про другое или состояние сменилось; пока крутишь громкость, значок стоит спокойно, движется только полоска.
        var changed = kind != _kind || glyph != _glyph || kind != "volume";
        var wasKind = _kind;
        _kind = kind;
        _glyph = glyph;

        _icon.Text = glyph;
        _text.Text = text;
        var target = on ? Colors.White : Color.FromRgb(0x8A, 0x91, 0x99);
        if (changed) _ink.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, Ms(260)) { EasingFunction = new QuadraticEase() });
        else _ink.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, Ms(120)));

        _barTrack.Visibility = bar.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (bar.HasValue)
        {
            var width = _barTrack.Width * bar.Value;
            if (wasKind == "volume") _barFill.BeginAnimation(WidthProperty, new DoubleAnimation(width, Ms(110)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            else { _barFill.BeginAnimation(WidthProperty, null); _barFill.Width = width; }
        }
        if (changed) PlayEntrance(kind, on);
    }

    // Значок выскакивает пружиной и чуть поворачивается (замок закрывается с одной стороны, открывается с другой, глобус прокручивается),
    // значение выезжает из-под него. Начальные значения ставятся сразу, чтобы до старта анимации не мелькал готовый вид.
    private void PlayEntrance(string kind, bool on)
    {
        var turn = kind switch { "lang" => 80.0, "CAPS" or "NUM" => on ? -26.0 : 26.0, "mic" => on ? -14.0 : 14.0, _ => on ? -10.0 : 10.0 };
        var from = kind == "lang" ? 0.55 : 0.4;
        var begin = TimeSpan.FromMilliseconds(40);
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.9 };

        _iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _iconTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        _icon.BeginAnimation(OpacityProperty, null);
        _iconScale.ScaleX = _iconScale.ScaleY = from;
        _iconTurn.Angle = turn;
        _icon.Opacity = 0.25;
        var scale = new DoubleAnimation(from, 1, Ms(420)) { BeginTime = begin, EasingFunction = pop };
        _iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        _iconTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(turn, 0, Ms(460)) { BeginTime = begin, EasingFunction = pop });
        _icon.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, Ms(140)) { BeginTime = begin });

        // Значение: «включено» въезжает снизу, «выключено» опускается сверху.
        var drop = on ? 8.0 : -8.0;
        _textShift.BeginAnimation(TranslateTransform.YProperty, null);
        _text.BeginAnimation(OpacityProperty, null);
        _textShift.Y = drop;
        _text.Opacity = 0;
        var settle = new QuinticEase { EasingMode = EasingMode.EaseOut };
        _textShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(drop, 0, Ms(320)) { BeginTime = TimeSpan.FromMilliseconds(90), EasingFunction = settle });
        _text.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(220)) { BeginTime = TimeSpan.FromMilliseconds(90) });
    }

    private static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));
}
