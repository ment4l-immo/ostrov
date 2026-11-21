using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Services;

namespace Ostrov.Modules;

/// <summary>Общие мелкие элементы интерфейса для вкладок (в одном стиле: белое и серое на чёрном).</summary>
internal static class Ui
{
    public static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static SolidColorBrush Argb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Круглая кнопка с иконкой из шрифта Segoe Fluent Icons.</summary>
    public static Button IconButton(string glyph, double fontSize, double box)
    {
        var button = new Button
        {
            Content = glyph,
            Template = LayeredTemplate(box / 2, null, Colors.White, new Thickness(0), HorizontalAlignment.Center),
            Width = box,
            Height = box,
            FontFamily = IconFont,
            FontSize = fontSize,
            Foreground = Hues.Ink,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false
        };
        Wire(button, 0.15);
        return button;
    }

    /// <summary>Кнопка-«таблетка» с текстом. Главная — белая с чёрным текстом, обычная — полупрозрачная.</summary>
    public static Button Pill(string text, bool primary = false, double padding = 14, Color? accent = null)
    {
        // Наведение: поверх кнопки плавно проступает светлая (у белой главной — тёмная) плёнка; нажатие чуть сжимает кнопку.
        var darkOverlay = primary && accent is null;
        var button = new Button
        {
            Content = text,
            Template = LayeredTemplate(15, Control.BackgroundProperty, darkOverlay ? Colors.Black : Colors.White, new Thickness(padding, 0, padding, 1), HorizontalAlignment.Center),
            Height = 30,
            MinWidth = 40,
            FontSize = 13,
            Margin = new Thickness(4, 0, 4, 0),
            Background = accent is { } c ? Argb(0xFF, c.R, c.G, c.B) : primary ? Hues.Ink : Argb(0xFF, Hues.SurfaceHighColor.R, Hues.SurfaceHighColor.G, Hues.SurfaceHighColor.B),
            Foreground = primary || accent is not null ? Brushes.Black : Hues.Ink,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false
        };
        Wire(button, darkOverlay ? 0.12 : 0.14);
        return button;
    }

    // Шаблон кнопки: слой фона («Bg»), слой наведения («Hv», прозрачный, оживляет Wire) и содержимое по центру или слева.
    private static ControlTemplate LayeredTemplate(double radius, DependencyProperty? backgroundFrom, Color overlay, Thickness contentMargin, HorizontalAlignment align, bool disabledDims = true)
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        var bg = new FrameworkElementFactory(typeof(Border), "Bg");
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        if (backgroundFrom != null) bg.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(backgroundFrom));
        else bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var hv = new FrameworkElementFactory(typeof(Border), "Hv");
        hv.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        hv.SetValue(Border.BackgroundProperty, Argb(0xFF, overlay.R, overlay.G, overlay.B));
        hv.SetValue(UIElement.OpacityProperty, 0.0);
        hv.SetValue(UIElement.IsHitTestVisibleProperty, false);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, align);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(FrameworkElement.MarginProperty, contentMargin);
        root.AppendChild(bg);
        root.AppendChild(hv);
        root.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = root };
        if (disabledDims)
        {
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35, "Bg"));
            template.Triggers.Add(disabled);
        }
        return template;
    }

    // Живая обратная связь кнопки: плавное наведение (110 мс проступает, 160 мс гаснет) и нажатие со сжатием до 96 %.
    private static void Wire(Button b, double hoverOpacity)
    {
        var scale = new ScaleTransform(1, 1);
        b.RenderTransform = scale;
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        Border? hv = null;

        static DoubleAnimation Anim(double to, int ms) =>
            new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        void Hover(double to, int ms)
        {
            if (b.Resources.Contains("NoHover")) return;
            if (hv == null) { b.ApplyTemplate(); hv = b.Template.FindName("Hv", b) as Border; }
            hv?.BeginAnimation(UIElement.OpacityProperty, Anim(to, ms));
        }
        void Press(double to, int ms)
        {
            if (b.Resources.Contains("NoHover")) return; // кнопки переключателей с плашкой не сжимаем: плашка под ними не сжимается
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(to, ms));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(to, ms));
        }

        b.MouseEnter += (_, _) => { if (b.IsEnabled) Hover(hoverOpacity, 110); };
        b.MouseLeave += (_, _) => { Hover(0, 160); Press(1, 160); };
        b.PreviewMouseLeftButtonDown += (_, _) => { if (b.IsEnabled) Press(0.96, 70); };
        b.PreviewMouseLeftButtonUp += (_, _) => Press(1, 160);
        b.IsEnabledChanged += (_, _) => { if (!b.IsEnabled) { Hover(0, 100); Press(1, 100); } };
    }

    /// <summary>
    /// Выбранная кнопка переключателя («Избранные | Недавние» и подобные): подсветка плавно проступает и гаснет (180 мс),
    /// а не меняется скачком. Первый вызов у кнопки ставит состояние сразу, без анимации.
    /// </summary>
    public static void MarkSelected(Button b, bool selected)
    {
        // Кнопка входит в переключатель с «перекатывающейся» плашкой (PillSlider): выбор делает она.
        if (b.Resources.Contains("PillSlider")) { if (selected && b.Resources["PillSlider"] is PillSlider slider) slider.Select(b); return; }
        var target = Color.FromArgb(selected ? (byte)0xFF : (byte)0x00, 0x40, 0x40, 0x40);
        if (b.Background is SolidColorBrush { IsFrozen: false } brush)
        {
            if (brush.Color == target) return;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else b.Background = new SolidColorBrush(target);
    }

    /// <summary>
    /// Мягкая смена содержимого внутри вкладки (переключатель «История | Полка» и подобные): панель проявляется за 200 мс
    /// и чуть выезжает снизу (на 6 px). Если у панели уже есть свой RenderTransform, только проявляется.
    /// </summary>
    /// <summary>
    /// Мягкий нижний край у прокручиваемого списка: последние ~22 px плавно гаснут, поэтому строка, не поместившаяся целиком, не обрезается резкой линией
    /// (пользователь видел эту линию как «плашку» внизу острова поверх последнего уведомления).
    /// </summary>
    public static void SoftBottom(ScrollViewer scroll, double fade = 22)
    {
        void Update()
        {
            var h = scroll.ActualHeight;
            if (h < fade * 2) { scroll.OpacityMask = null; return; }
            var start = 1 - fade / h;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
            brush.GradientStops.Add(new GradientStop(Colors.Black, start));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            brush.Freeze();
            scroll.OpacityMask = brush;
        }
        scroll.SizeChanged += (_, e) => { if (e.HeightChanged) Update(); };
        Update();
    }

    public static void FadeIn(UIElement element)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        var shift = element.RenderTransform as TranslateTransform;
        if (shift == null && element.RenderTransform == Transform.Identity) element.RenderTransform = shift = new TranslateTransform();
        shift?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    /// <summary>
    /// Смена содержимого со сдвигом по горизонтали (смена дня в «Погоде»): элемент приезжает со стороны <paramref name="dx"/> и проявляется.
    /// </summary>
    public static void SlideIn(UIElement element, double dx)
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var shift = element.RenderTransform as TranslateTransform;
        if (shift == null && element.RenderTransform == Transform.Identity) element.RenderTransform = shift = new TranslateTransform();
        shift?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
    }

    /// <summary>
    /// Каскад снизу вверх (как на «Главной», но порядок обратный): строки по очереди проявляются и поднимаются на 10 px, начиная с нижней.
    /// Шаг 32 мс, не больше восьми ступеней (остальные строки идут вместе с последней).
    /// </summary>
    public static void CascadeUp(IReadOnlyList<FrameworkElement> items)
    {
        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var rise = new QuinticEase { EasingMode = EasingMode.EaseOut };
        for (var i = 0; i < items.Count; i++)
        {
            var el = items[i];
            if (el.RenderTransform is not TranslateTransform shift) el.RenderTransform = shift = new TranslateTransform();
            el.BeginAnimation(UIElement.OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            el.Opacity = 0;
            shift.Y = 10;
            var step = Math.Min(items.Count - 1 - i, 8);
            var delay = TimeSpan.FromMilliseconds(60 + step * 32);
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = delay, EasingFunction = fade });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = delay, EasingFunction = rise });
        }
    }

    /// <summary>Поле ввода без рамки, фона и отступов: размер по тексту. Оформление — у контейнера, в который его кладут.</summary>
    public static TextBox BareTextBox()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        return new TextBox
        {
            Template = new ControlTemplate(typeof(TextBox)) { VisualTree = host },
            MinWidth = 10,
            FontSize = 13,
            Foreground = Hues.Ink,
            CaretBrush = Hues.Ink,
            SelectionBrush = Gray,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>Строка списка во всю ширину: название белым и (необязательно) серая подпись рядом. При наведении подсвечивается.</summary>
    public static Button ListRow(string title, string? subtitle = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = title, Foreground = Hues.Ink, FontSize = 13 });
        if (!string.IsNullOrEmpty(subtitle))
            content.Children.Add(new TextBlock { Text = subtitle, Foreground = Gray, FontSize = 12, Margin = new Thickness(8, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });

        var button = new Button
        {
            Content = content,
            Template = LayeredTemplate(10, null, Colors.White, new Thickness(10, 0, 10, 1), HorizontalAlignment.Left, disabledDims: false),
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false
        };
        Wire(button, 0.15);
        return button;
    }
}
