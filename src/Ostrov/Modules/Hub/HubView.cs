using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ostrov.Modules.ControlCenter;
using Ostrov.Modules.Phone;
using Ostrov.Modules.Player;
using Ostrov.Modules.Time;
using Ostrov.Modules.Weather;
using Ostrov.Services;

namespace Ostrov.Modules.Hub;

/// <summary>
/// Содержимое «Главной»: ряд плиток (телефон, нагрузка), ряд круглых кнопок, ряд плиток (плеер, погода).
/// Только белое и серое; свой цвет только у того, у чего он уже есть (таймер идёт: оранжевый). Всё читается из уже работающих служб,
/// новых запросов в сеть вкладка не делает.
/// </summary>
internal sealed class HubView : Grid
{
    private const double RowGap = 10;

    private static readonly Brush Gray = Ui.Gray;

    public HubView(BluetoothSwitch bluetooth, TimerModel timer)
    {
        VerticalAlignment = VerticalAlignment.Top;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowGap) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(84) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowGap) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(84) });

        var phone = new PhoneCard();
        var load = new LoadCard();
        var player = new PlayerCard();
        var weather = new WeatherCard();
        var top = TwoColumns(phone, load, 1, 1);
        var middle = new ButtonRow(bluetooth, timer);
        var bottom = TwoColumns(player, weather, 1.55, 1);
        // Порядок появления: кнопки слева направо, затем плитки слева направо и сверху вниз.
        _cascade.AddRange(middle.Slots);
        _cascade.AddRange([phone, load, player, weather]);
        IslandBus.Expanding += () => Dispatcher.BeginInvoke(PlayCascade);
        SetRow(middle, 0);
        SetRow(top, 2);
        SetRow(bottom, 4);
        Children.Add(middle);
        Children.Add(top);
        Children.Add(bottom);
    }

    private static Grid TwoColumns(UIElement left, UIElement right, double leftStar, double rightStar)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftStar, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowGap) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightStar, GridUnitType.Star) });
        SetColumn(left, 0);
        SetColumn(right, 2);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    // ---------- каскад появления ----------

    private readonly List<FrameworkElement> _cascade = [];

    /// <summary>
    /// Элементы хаба проявляются по очереди (шаг 45 мс) и чуть поднимаются снизу, когда остров раскрывается: сначала кнопки слева направо,
    /// потом плитки. Начинается, когда остров уже набрал размер и содержимое начало проявляться.
    /// </summary>
    private void PlayCascade()
    {
        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var rise = new QuinticEase { EasingMode = EasingMode.EaseOut };
        for (var i = 0; i < _cascade.Count; i++)
        {
            var el = _cascade[i];
            if (el.RenderTransform is not TranslateTransform shift) el.RenderTransform = shift = new TranslateTransform();
            el.BeginAnimation(OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            el.Opacity = 0;
            shift.Y = 10;
            var delay = TimeSpan.FromMilliseconds(120 + i * 45);
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = delay, EasingFunction = fade });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = delay, EasingFunction = rise });
        }
    }

    private static TextBlock Txt(string text, double size, Brush fg, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = fg, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static bool IsOpen(FrameworkElement e) => Window.GetWindow(e) is not MainWindow { IsExpanded: false };

    // ---------- плитка-основа ----------

    /// <summary>Полупрозрачная белая плитка (сквозь неё виден цветной фон острова). Если задана вкладка, клик по плитке открывает её.</summary>
    private class Card : Border
    {
        private static readonly Color Base = Color.FromArgb(Hues.GlassAlpha, 0x21, 0x25, 0x29), Hot = Color.FromArgb((byte)(Hues.GlassAlpha + 6), 0x2B, 0x30, 0x35); // графит с общей прозрачностью карточек (Hues.GlassAlpha)
        private readonly SolidColorBrush _bg = new(Base);

        public Card(string? openTab)
        {
            CornerRadius = new CornerRadius(18);
            Background = _bg;
            Padding = new Thickness(14, 10, 14, 10);
            if (openTab == null) return;
            Cursor = Cursors.Hand;
            MouseEnter += (_, _) => Fade(Hot);
            MouseLeave += (_, _) => Fade(Base);
            // Кнопки внутри плитки гасят событие сами (Handled): тогда плитка не открывается.
            MouseLeftButtonUp += (_, e) => { if (!e.Handled) IslandBus.RequestTab(openTab); };
        }

        private void Fade(Color to) =>
            _bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------- телефон ----------

    private sealed class PhoneCard : Card
    {
        private readonly TextBlock _pct = Txt("", 20, Hues.Ink, FontWeights.Light);
        private readonly TextBlock _notice = Txt("", 11.5, Gray);
        private readonly BatteryCapsule _battery = new(26, 12) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 2, 0, 0) };
        private bool _shown;

        public PhoneCard() : base("phone")
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { _pct, _battery } };
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Txt("Телефон", 11.5, Gray, FontWeights.SemiBold), row, _notice } };
            Loaded += (_, _) => { PhoneLink.Instance.Changed += OnChanged; Refresh(); _shown = true; };
            Unloaded += (_, _) => { PhoneLink.Instance.Changed -= OnChanged; _shown = false; };
            Refresh();
        }

        private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

        private void Refresh()
        {
            var p = PhoneLink.Instance;
            if (p.Connected && p.Battery is { } level)
            {
                _pct.Text = $"{level} %";
                _pct.Foreground = Hues.Ink;
                _battery.Visibility = Visibility.Visible;
                _battery.Set(level / 100.0, animate: _shown);
            }
            else
            {
                _pct.Text = p.Connected ? "На связи" : "Не на связи";
                _pct.Foreground = p.Connected ? Hues.Ink : Gray;
                _battery.Visibility = Visibility.Collapsed;
            }

            var cleared = AppSettings.Load().PhoneClearedAt;
            var last = p.Notices
                .Where(n => n.Time > DateTime.Now.AddHours(-24) && (cleared == null || n.Time > cleared))
                .OrderByDescending(n => n.Time)
                .FirstOrDefault();
            _notice.Text = last != null
                ? $"{last.App} · {last.Sender} · {Ago(last.Time)}"
                : p.NoticeAccess ? "Нет новых уведомлений" : "Нет доступа к уведомлениям";
        }

        private static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            return d.TotalMinutes < 1 ? "сейчас" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} мин" : $"{(int)d.TotalHours} ч";
        }
    }

    // ---------- нагрузка ----------

    private sealed class LoadCard : Card
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        private sealed class Meter : StackPanel
        {
            private readonly LoadRing _ring = new() { Width = 46, Height = 46, Stroke = 4.5 };
            private readonly TextBlock _center = Txt("", 11.5, Hues.Ink, FontWeights.SemiBold);

            public Meter(string label)
            {
                _center.HorizontalAlignment = HorizontalAlignment.Center;
                _center.VerticalAlignment = VerticalAlignment.Center;
                var box = new Grid { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Center, Children = { _ring, _center } };
                var caption = Txt(label, 10.5, Gray);
                caption.HorizontalAlignment = HorizontalAlignment.Center;
                caption.Margin = new Thickness(0, 3, 0, 0);
                Children.Add(box);
                Children.Add(caption);
            }

            public void Set(double? fraction, string text)
            {
                _ring.AnimateTo(fraction ?? 0, 900);
                _center.Text = text;
                _center.Foreground = fraction == null ? Gray : Hues.Ink;
            }
        }

        private readonly SystemStats _stats = new(1000);
        private readonly Meter _cpu = new("Процессор"), _gpu = new("Видеокарта"), _mem = new("Память");
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) }; // раз в секунду: замер видеокарты и перерисовка колец дороги, а цифры чаще не нужны

        public LoadCard() : base(null)
        {
            Padding = new Thickness(8, 8, 8, 6);
            var g = new Grid();
            var meters = new[] { _cpu, _gpu, _mem };
            for (var i = 0; i < meters.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                SetColumn(meters[i], i);
                meters[i].VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(meters[i]);
            }
            Child = g;
            _timer.Tick += (_, _) => { if (IsOpen(this)) Update(); };
            Loaded += (_, _) => { Update(); _timer.Start(); };
            Unloaded += (_, _) => _timer.Stop();
        }

        private void Update()
        {
            var s = _stats.Sample();
            _cpu.Set(s.Cpu / 100, $"{s.Cpu:0}%");
            if (s.Gpu is { } gpu) _gpu.Set(gpu / 100, $"{gpu:0}%");
            else _gpu.Set(null, "—");
            _mem.Set(s.MemPercent / 100, $"{s.MemPercent:0}%");
            _gpu.ToolTip = s.GpuTemp is { } t ? $"Видеокарта · {t} °C" : "Видеокарта: нет данных";
            _mem.ToolTip = string.Format(Ru, "Память · {0:0.0} из {1:0.0} ГБ", s.MemUsedGb, s.MemTotalGb);
        }
    }

    // ---------- плеер ----------

    private sealed class PlayerCard : Card
    {
        private readonly Border _cover = new() { Width = 56, Height = 56, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)) };
        private readonly TextBlock _title = Txt("", 14, Hues.Ink, FontWeights.SemiBold);
        private readonly TextBlock _artist = Txt("", 12, Gray);
        private readonly Button _prev, _play, _next;
        private readonly Grid _controls = new();

        public PlayerCard() : base("player")
        {
            _prev = Flat(PlayerIcons.Create(PlayerIcon.Previous, 13), () => _ = MediaControl.RunAsync(MediaControl.Action.Previous));
            _play = Flat(PlayerIcons.Create(PlayerIcon.Play, 14), () => _ = MediaControl.RunAsync(MusicActivity.IsPlaying ? MediaControl.Action.Pause : MediaControl.Action.Play));
            _next = Flat(PlayerIcons.Create(PlayerIcon.Next, 13), () => _ = MediaControl.RunAsync(MediaControl.Action.Next));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 4, 0, 0), Children = { _prev, _play, _next } };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _artist, buttons } };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _cover.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(text, 2);
            g.Children.Add(_cover);
            g.Children.Add(text);
            Child = g;
            Loaded += (_, _) => { MusicActivity.NowChanged += OnChanged; Refresh(); };
            Unloaded += (_, _) => MusicActivity.NowChanged -= OnChanged;
            Refresh();
        }

        private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

        private void Refresh()
        {
            var track = MusicActivity.Track;
            _title.Text = track?.Title ?? "Музыка";
            _artist.Text = track is { } t ? t.Artist : MusicActivity.HasSession ? "Ничего не играет" : "Apple Music не запущена";
            _cover.Background = MusicActivity.Cover is { } img
                ? new ImageBrush(img) { Stretch = Stretch.UniformToFill }
                : new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            _play.Content = PlayerIcons.Create(MusicActivity.IsPlaying ? PlayerIcon.Pause : PlayerIcon.Play, 14);
            var active = MusicActivity.HasSession;
            foreach (var b in new[] { _prev, _play, _next }) { b.IsEnabled = active; b.Opacity = active ? 1 : 0.35; }
        }

        // Кнопка управления без фона: значок, чуть бледнеет при наведении.
        private static Button Flat(FrameworkElement icon, Action click)
        {
            var root = new FrameworkElementFactory(typeof(Border));
            root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(presenter);
            var b = new Button
            {
                Content = icon, Width = 34, Height = 24, Focusable = false, Cursor = Cursors.Hand,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = root }
            };
            b.MouseEnter += (_, _) => { if (b.IsEnabled) b.Opacity = 0.65; };
            b.MouseLeave += (_, _) => { if (b.IsEnabled) b.Opacity = 1; };
            b.Click += (_, _) => click();
            return b;
        }
    }

    // ---------- погода ----------

    private sealed class WeatherCard : Card
    {
        private readonly WeatherIcon _icon = new() { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _temp = Txt("", 26, Hues.Ink, FontWeights.Light);
        private readonly TextBlock _city = Txt("", 11, Gray);
        private readonly TextBlock _desc = Txt("", 12, Hues.Ink);
        private readonly TextBlock _summary = Txt("", 11, Gray);

        // Серая заглушка вместо текста, пока погода ещё грузится (первые секунды после запуска, город задан).
        private static readonly DateTime Started = DateTime.Now;
        private readonly StackPanel _content, _skeleton = new() { VerticalAlignment = VerticalAlignment.Center };

        private static Border Bar(double width, double height) => new()
        {
            Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(height / 2),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 0, 0, 7)
        };

        private void SetSkeleton(bool on)
        {
            _skeleton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _content.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            // Слабое «дыхание» заглушки, пока она на экране.
            // Дышит не дольше 12 секунд и не чаще 20 кадров в секунду: бесконечная анимация постоянно перерисовывала бы прозрачное окно.
            DoubleAnimation? pulse = null;
            if (on)
            {
                pulse = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(TimeSpan.FromSeconds(12)), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Timeline.SetDesiredFrameRate(pulse, 20);
            }
            _skeleton.BeginAnimation(OpacityProperty, pulse);
        }

        public WeatherCard() : base("weather")
        {
            _temp.Margin = new Thickness(6, 0, 0, 2);
            _temp.VerticalAlignment = VerticalAlignment.Center;
            _city.HorizontalAlignment = HorizontalAlignment.Right;
            _city.VerticalAlignment = VerticalAlignment.Top;
            _city.MaxWidth = 70;
            var row = new Grid { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, _temp } }, _city } };
            _content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { row, _desc, _summary } };
            _skeleton.Children.Add(Bar(104, 24));
            _skeleton.Children.Add(Bar(128, 10));
            _skeleton.Children.Add(Bar(96, 10));
            _skeleton.Visibility = Visibility.Collapsed;
            Child = new Grid { Children = { _content, _skeleton } };
            Loaded += (_, _) => { IdleActivity.LatestChanged += OnChanged; Refresh(); };
            Unloaded += (_, _) => { IdleActivity.LatestChanged -= OnChanged; _skeleton.BeginAnimation(OpacityProperty, null); };
            Refresh();
        }

        private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

        private void Refresh()
        {
            var city = AppSettings.Load().WeatherCity;
            var d = IdleActivity.Latest;
            var waiting = d == null && !string.IsNullOrWhiteSpace(city) && DateTime.Now - Started < TimeSpan.FromSeconds(25);
            if (waiting != (_skeleton.Visibility == Visibility.Visible)) SetSkeleton(waiting);
            if (d == null)
            {
                _icon.Visibility = Visibility.Collapsed;
                _temp.Text = "Погода";
                _temp.FontSize = 18;
                _temp.Margin = new Thickness(0, 0, 0, 2);
                _city.Text = "";
                _desc.Text = string.IsNullOrWhiteSpace(city) ? "Выбери город" : "Нет данных";
                _summary.Text = string.IsNullOrWhiteSpace(city) ? "во вкладке «Погода»" : "";
                return;
            }
            var (text, kind) = WeatherCodes.Describe(d.Code);
            _icon.Visibility = Visibility.Visible;
            _icon.Kind = kind;
            _icon.IsDay = d.IsDay;
            _temp.FontSize = 26;
            _temp.Margin = new Thickness(6, 0, 0, 2);
            _temp.Text = WeatherFormat.Temp(d.Temperature);
            _city.Text = city;
            _desc.Text = text;
            _summary.Text = WeatherSummary.Make(d, d.LocalNow + (DateTime.Now - d.Updated));
        }
    }

    // ---------- круглые кнопки ----------

    private enum Look { Off, On, Accent, Alert }

    private sealed class Round : Button
    {
        private readonly Border _disc = new() { Width = 48, Height = 48, CornerRadius = new CornerRadius(24) };
        private readonly TextBlock _glyph = new() { FontFamily = Ui.IconFont, FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly SolidColorBrush _bg = new(Hues.SurfaceColor);
        private readonly ScaleTransform _scale = new(1, 1);
        private Look _look = Look.Off;
        private bool _hover;

        public Round(string glyph)
        {
            _glyph.Text = glyph;
            _disc.Child = _glyph;
            _disc.Background = _bg;
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            Content = _disc;
            RenderTransform = _scale;
            RenderTransformOrigin = new Point(0.5, 0.5);
            Cursor = Cursors.Hand;
            Focusable = false;
            MouseEnter += (_, _) => { _hover = true; Apply(); };
            MouseLeave += (_, _) => { _hover = false; Apply(); Press(1, 160); };
            PreviewMouseLeftButtonDown += (_, _) => { if (IsEnabled) Press(0.93, 70); };
            PreviewMouseLeftButtonUp += (_, _) => Press(1, 160);
            IsEnabledChanged += (_, _) => { Cursor = IsEnabled ? Cursors.Hand : Cursors.Arrow; Opacity = IsEnabled ? 1 : 0.4; };
            Apply(animate: false);
        }

        public void SetLook(Look look, bool enabled = true)
        {
            if (look == _look && enabled == IsEnabled) return; // подпись таймера обновляется каждую секунду, а вид кнопки редко меняется
            _look = look;
            IsEnabled = enabled;
            Apply();
        }

        private void Press(double to, int ms)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }

        private void Apply(bool animate = true)
        {
            var hot = _hover && IsEnabled;
            var color = _look switch
            {
                Look.On => hot ? Color.FromRgb(0xD3, 0xD6, 0xD9) : Hues.InkColor,
                Look.Accent => hot ? Color.FromRgb(0xFF, 0xB3, 0x40) : Color.FromRgb(0xFF, 0x9F, 0x0A),
                Look.Alert => hot ? Color.FromRgb(0xFF, 0x6E, 0x2C) : Hues.AlertColor,
                _ => hot ? Hues.SurfaceHighColor : Hues.SurfaceColor
            };
            if (animate) _bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { _bg.BeginAnimation(SolidColorBrush.ColorProperty, null); _bg.Color = color; }
            _glyph.Foreground = _look == Look.Off ? Hues.Ink : _look == Look.Alert ? Brushes.White : Brushes.Black;
        }
    }

    /// <summary>Круглая кнопка с подписью под ней. Подпись можно на 3 секунды заменить коротким сообщением (например, «Не удалось»).</summary>
    private sealed class Slot : StackPanel
    {
        public Round Button { get; }
        private readonly TextBlock _label = Txt("", 11.5, Gray);
        private readonly DispatcherTimer _flash = new() { Interval = TimeSpan.FromSeconds(3) };
        private string _text = "";
        private string? _flashText;

        public Slot(string glyph)
        {
            Button = new Round(glyph) { HorizontalAlignment = HorizontalAlignment.Center };
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.Margin = new Thickness(0, 5, 0, 0);
            _label.MaxWidth = 92;
            VerticalAlignment = VerticalAlignment.Center;
            Children.Add(Button);
            Children.Add(_label);
            _flash.Tick += (_, _) => { _flash.Stop(); _flashText = null; _label.Text = _text; };
        }

        public string Text
        {
            set { _text = value; if (_flashText == null) _label.Text = value; }
        }

        public void Flash(string text)
        {
            _flashText = text;
            _label.Text = text;
            _flash.Stop();
            _flash.Start();
        }
    }

    private sealed class ButtonRow : Grid
    {
        private const string OffHint = "Выключить";

        private readonly BluetoothSwitch _bluetooth;
        private readonly TimerModel _timer;
        private readonly Slot _bt = new(""), _vpn = new(""), _clock = new(""), _sleep = new(""), _off = new("");
        private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) }; // VPN: чтение адаптеров ≈ 7 мс, чаще не нужно
        private readonly DispatcherTimer _second = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _confirmTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private bool _btBusy, _confirming;

        public IEnumerable<FrameworkElement> Slots => [_bt, _vpn, _clock, _sleep, _off];

        public ButtonRow(BluetoothSwitch bluetooth, TimerModel timer)
        {
            _bluetooth = bluetooth;
            _timer = timer;
            var slots = new[] { _bt, _vpn, _clock, _sleep, _off };
            for (var i = 0; i < slots.Length; i++)
            {
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                SetColumn(slots[i], i);
                Children.Add(slots[i]);
            }

            _sleep.Text = "Сон";
            _off.Text = OffHint;
            _bt.Button.Click += async (_, _) => await ToggleBluetoothAsync();
            _vpn.Button.Click += (_, _) => OpenVpnClient();
            _clock.Button.Click += (_, _) => OnTimerClick();
            _sleep.Button.Click += (_, _) => { IslandBus.RequestCollapse(); PowerActions.Sleep(); };
            _off.Button.Click += (_, _) => OnShutdownClick();

            _bluetooth.Changed += () => Dispatcher.BeginInvoke(RefreshBluetooth);
            _timer.Changed += () => Dispatcher.BeginInvoke(RefreshTimer);
            _poll.Tick += (_, _) => { if (IsOpen(this)) RefreshVpn(); };
            _second.Tick += (_, _) => { if (IsOpen(this)) RefreshTimer(); };
            _confirmTimer.Tick += (_, _) => CancelConfirm();

            Loaded += (_, _) =>
            {
                _ = _bluetooth.EnsureAsync();
                RefreshBluetooth();
                RefreshVpn();
                RefreshTimer();
                _poll.Start();
                _second.Start();
            };
            Unloaded += (_, _) => { _poll.Stop(); _second.Stop(); CancelConfirm(); };

            RefreshBluetooth();
            RefreshVpn();
            RefreshTimer();
        }

        private void RefreshBluetooth()
        {
            _bt.Text = "Bluetooth";
            if (!_bluetooth.Ready) { _bt.Button.SetLook(Look.Off, enabled: false); return; }
            if (!_bluetooth.Available) { _bt.Text = "Нет адаптера"; _bt.Button.SetLook(Look.Off, enabled: false); return; }
            _bt.Button.SetLook(_bluetooth.IsOn ? Look.On : Look.Off, enabled: !_btBusy);
        }

        private void RefreshVpn()
        {
            var state = VpnStatus.Read();
            _vpn.Text = state.Any ? state.Label : "VPN";
            _vpn.Button.SetLook(state.Any ? Look.On : Look.Off);
        }

        // Идёт отсчёт: оранжевая кнопка с оставшимся временем под ней; на паузе белая; иначе серая «25 мин».
        private void RefreshTimer()
        {
            switch (_timer.State)
            {
                case TimerState.Running:
                    _clock.Text = Format(_timer.Remaining);
                    _clock.Button.SetLook(Look.Accent);
                    break;
                case TimerState.Paused:
                    _clock.Text = "Пауза " + Format(_timer.Remaining);
                    _clock.Button.SetLook(Look.On);
                    break;
                default:
                    _clock.Text = "25 мин";
                    _clock.Button.SetLook(Look.Off);
                    break;
            }
        }

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        // Нет таймера: запускает 25 минут. Идёт: пауза. На паузе: продолжает. Отменить можно во вкладке «Время».
        private void OnTimerClick()
        {
            switch (_timer.State)
            {
                case TimerState.Running: _timer.Pause(); break;
                case TimerState.Paused: _timer.Start(); break;
                default: IslandCommands.StartTimer?.Invoke(TimeSpan.FromMinutes(25)); break;
            }
        }

        private async Task ToggleBluetoothAsync()
        {
            if (_btBusy || !_bluetooth.Available) return;
            _btBusy = true;
            RefreshBluetooth();
            var ok = await _bluetooth.SetAsync(!_bluetooth.IsOn);
            _btBusy = false;
            RefreshBluetooth();
            if (!ok) _bt.Flash("Не удалось");
        }

        private void OpenVpnClient()
        {
            if (VpnStatus.OpenClient(VpnStatus.Read())) IslandBus.RequestCollapse();
            else _vpn.Flash("Нет приложения");
        }

        // Выключение: два нажатия, как в «Центре». Первое меняет подпись на «Точно?», второе в течение 3 с выключает компьютер.
        private void OnShutdownClick()
        {
            if (!_confirming)
            {
                _confirming = true;
                _off.Text = "Точно?";
                _off.Button.SetLook(Look.Alert);
                _confirmTimer.Start();
                return;
            }
            CancelConfirm();
            IslandBus.RequestCollapse();
            PowerActions.Shutdown();
        }

        private void CancelConfirm()
        {
            _confirmTimer.Stop();
            _confirming = false;
            _off.Text = OffHint;
            _off.Button.SetLook(Look.Off);
        }
    }
}
