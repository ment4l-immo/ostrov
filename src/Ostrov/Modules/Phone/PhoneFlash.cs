using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Backdrop;
using Ostrov.Services;

namespace Ostrov.Modules.Phone;

/// <summary>
/// Короткая вспышка про телефон поверх главной плашки (по образцу Dynamic Island, решения пользователя 2026-11-15): значок слева, две строки
/// (серая подпись и белое название), справа заряд и значок батарейки. «Подключён», «Низкий заряд» (на 20 % и 10 %) и «Новое сообщение в X».
/// Только белый и серый.
/// </summary>
internal sealed class PhoneFlash : Grid
{
    public const double ViewWidth = 290, ViewHeight = 48;

    internal const string PhoneGlyph = "", ChatGlyph = "";

    private readonly TextBlock _icon = new()
    {
        FontFamily = Ui.IconFont, FontSize = 19, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 2)
    };
    private readonly AppLogo _logo = new(30) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 2), Visibility = Visibility.Collapsed };
    // Свечение цвета значка приложения за содержимым: на новом сообщении вспыхивает и гаснет (решение пользователя: «вспышка в цвет приложения»).
    // Скругление снизу как у самого острова (его радиус для плашки высотой 48 — 24 px; сверху остров вплотную к краю экрана): иначе свечение торчит острыми углами.
    private readonly Border _glow = new() { IsHitTestVisible = false, Opacity = 0, CornerRadius = new CornerRadius(0, 0, 24, 24) };
    private int _glowToken;
    private readonly TextBlock _top = new() { FontSize = 11, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _bottom = new()
    {
        FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 2) };
    private readonly TextBlock _percent = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 1) };
    private readonly BatteryCapsule _capsule = new(26, 12);
    private readonly Grid _texts = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(48, 0, 16, 2) };

    public PhoneFlash()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Typography.SetNumeralAlignment(_percent, FontNumeralAlignment.Tabular);
        _texts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _texts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(_bottom, 1);
        _texts.Children.Add(_top);
        _texts.Children.Add(_bottom);
        _right.Children.Add(_percent);
        _right.Children.Add(_capsule);
        Children.Add(_glow);
        Children.Add(_texts);
        Children.Add(_icon);
        Children.Add(_logo);
        Children.Add(_right);
    }

    /// <summary>Телефон подключился: «Подключён», название и заряд, если он известен.</summary>
    public void ShowConnected(string name, int? battery) => Set(PhoneGlyph, "Подключён", name, battery);

    /// <summary>Заряд упал до порога: «Низкий заряд», название и проценты.</summary>
    public void ShowLow(string name, int battery) => Set(PhoneGlyph, "Низкий заряд на устройстве", name, battery);

    /// <summary>С телефона пришёл файл или текст (мост): «Получено на ПК» и его название.</summary>
    public void ShowReceived(string name) => Set("\uE896", "Получено на ПК", name, null);

    /// <summary>Новое сообщение в приложении телефона (без отправителя и текста: экран видят другие).</summary>
    public void ShowMessage(string app)
    {
        Set(ChatGlyph, "Телефон", $"Новое сообщение в {app}", null);
        _logo.SetApp(app); // значок приложения вместо общего значка сообщения
        PlayGlow(app);
        _logo.Visibility = Visibility.Visible;
        _texts.Margin = new Thickness(58, 0, 16, 2); // у значка приложения отступ больше: он шире значка телефона
        _icon.Visibility = Visibility.Collapsed;
    }

    // Вспышка цвета приложения: свечение слева (от значка) за 0,2 с набирает силу и за 1,5 с гаснет. Цвет берётся из значка приложения.
    private async void PlayGlow(string app)
    {
        var token = ++_glowToken;
        try
        {
            var icon = await PhoneAppIcons.GetAsync(app).ConfigureAwait(false);
            if (icon == null) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (token != _glowToken) return; // за это время пришло другое
                var palette = CoverPalette.FromImage(icon);
                if (palette == null || palette.Length == 0) return;
                var accent = Accent(palette);
                var brush = new RadialGradientBrush
                {
                    Center = new Point(0.12, 0.5), GradientOrigin = new Point(0.12, 0.5), RadiusX = 0.95, RadiusY = 1.7,
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x96, accent.R, accent.G, accent.B), 0),
                        new GradientStop(Color.FromArgb(0x38, accent.R, accent.G, accent.B), 0.5),
                        new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1)
                    }
                };
                _glow.Background = brush;
                var frames = new DoubleAnimationUsingKeyFrames();
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new CubicEase { EasingMode = EasingMode.EaseOut }));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(460))));
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1960)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
                _glow.BeginAnimation(OpacityProperty, frames);
            });
        }
        catch
        {
            // Свечение — украшение: сбой не должен мешать вспышке.
        }
    }

    // Самый выразительный цвет значка (насыщенный, не слишком тёмный и не слишком светлый), чуть осветлённый, чтобы читался на чёрном.
    private static Color Accent(Color[] palette)
    {
        var best = palette[0];
        var bestScore = -1.0;
        foreach (var c in palette)
        {
            var (_, sat, l) = ColorMath.ToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
            var score = sat * (1 - Math.Abs(2 * l - 1));
            if (score > bestScore) { bestScore = score; best = c; }
        }
        var (h, s, light) = ColorMath.ToHsl(best.R / 255.0, best.G / 255.0, best.B / 255.0);
        light = Math.Clamp(light, 0.5, 0.7);
        var (r, g, b) = ColorMath.FromHsl(h, s, light);
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private void Set(string glyph, string top, string bottom, int? battery)
    {
        _glowToken++; // другая вспышка (не про сообщение): свечение сообщения гасим
        _glow.BeginAnimation(OpacityProperty, null);
        _glow.Opacity = 0;
        _logo.Visibility = Visibility.Collapsed;
        _icon.Visibility = Visibility.Visible;
        _icon.Text = glyph;
        _top.Text = top;
        _bottom.Text = bottom;
        _right.Visibility = battery.HasValue ? Visibility.Visible : Visibility.Collapsed;
        _texts.Margin = new Thickness(54, 0, battery.HasValue ? 84 : 16, 2);
        if (battery is { } b)
        {
            _percent.Text = b + " %";
            _capsule.Set(b / 100.0);
        }
    }
}

/// <summary>Значок батарейки: контур, закрашенная доля и «носик». Белая заливка на сером контуре.</summary>
internal sealed class BatteryCapsule : Grid
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Outline = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));

    private readonly Border _fill = new() { Background = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly double _inner;

    public BatteryCapsule(double width, double height)
    {
        var radius = height * 0.3;
        var nub = Math.Max(2, height * 0.18);
        var bodyWidth = width - nub - 1.5;
        Width = width;
        Height = height;
        _inner = bodyWidth - 4;
        _fill.Height = height - 4;
        _fill.CornerRadius = new CornerRadius(Math.Max(1, radius - 1.5));
        _fill.Margin = new Thickness(2, 0, 0, 0);
        _fill.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(new Border
        {
            Width = bodyWidth, Height = height, BorderBrush = Outline, BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(radius),
            HorizontalAlignment = HorizontalAlignment.Left
        });
        Children.Add(_fill);
        Children.Add(new Border
        {
            Width = nub, Height = height * 0.42, Background = Outline, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(0, nub / 2, nub / 2, 0)
        });
    }

    /// <summary>Доля заряда 0..1.</summary>
    public void Set(double level, bool animate = false)
    {
        var target = Math.Max(2, _inner * Math.Clamp(level, 0, 1));
        if (!animate || double.IsNaN(_fill.Width))
        {
            _fill.BeginAnimation(WidthProperty, null);
            _fill.Width = target;
            return;
        }
        _fill.BeginAnimation(WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(600))
        { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
    }
}
