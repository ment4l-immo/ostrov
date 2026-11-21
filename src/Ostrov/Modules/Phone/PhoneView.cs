using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ostrov.Backdrop;
using Ostrov.Modules.Settings;
using Ostrov.Modules.Phone.Bridge;
using Ostrov.Services;
using Ostrov.Modules.Weather;

namespace Ostrov.Modules.Phone;

/// <summary>
/// Вкладка «Телефон» в духе экрана блокировки iOS (решения пользователя 2026-11-15): сверху дата и крупное время (цифры перелистываются),
/// справа название телефона, заряд с батарейкой и две маленькие кнопки («Приложения» и «Очистить»); ниже стопка скруглённых карточек-уведомлений
/// (значок приложения, приложение и время, отправитель; текста сообщений нет). Новая карточка падает сверху с лёгким перелётом, остальные
/// раздвигаются; при открытии вкладки карточки появляются каскадом; при новом уведомлении фон на секунду вспыхивает цветом значка приложения
/// и возвращается к обычному зелёно-серому. Без «дыхания» и покачиваний.
/// </summary>
internal sealed class PhoneView : Grid
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Soft = Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private const double CardHeight = 54, CardGap = 6;
    private static readonly Color CardColor = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);

    private sealed record Card(Border Wrap, Border Face, PhoneNotice Notice, TextBlock Time);

    private readonly IIslandModule _owner;
    // Крупная дата вместо часов (часы теперь в шапке острова; решение пользователя 2026-11-18).
    private readonly TextBlock _date = new() { FontSize = 30, FontWeight = FontWeights.Light, Foreground = Hues.Ink, Margin = new Thickness(2, 0, 0, 0) };
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { FontSize = 12, Foreground = Soft, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 };
    private readonly TextBlock _percent = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 1) };
    private readonly BatteryCapsule _capsule = new(30, 14) { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _batteryRow = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 0) };
    private readonly Button _bridgeButton = Ui.IconButton("?", 14, 28);
    private readonly Button _flashButton = Ui.IconButton("", 14, 28);
    private readonly Button _appsButton = Ui.IconButton("", 14, 28);
    private readonly Button _clearButton = Ui.IconButton("", 13, 28);
    private readonly StackPanel _list = new();
    private readonly Grid _header = new() { Margin = new Thickness(4, 0, 4, 0) };
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _flashBack = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly Dictionary<(uint, long), Card> _cards = [];

    // Мост с телефоном (QR): сервер живёт, только пока открыто окно моста.
    private readonly DispatcherTimer _bridgeWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private BridgeServer? _server;
    private BridgePanel? _bridge;
    private bool _bridgeOpen;

    /// <summary>Открыто окно моста: вкладка просит у острова больше высоты.</summary>
    public static bool BridgeActive { get; private set; }

    private bool _editing, _cardsMode, _introDone, _batteryShown;
    private DateTime _lastIntro = DateTime.MinValue;

    public PhoneView(IIslandModule owner)
    {
        _owner = owner;
        VerticalAlignment = VerticalAlignment.Top;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Шапка: слева крупная дата, справа название, заряд и кнопки.
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_date);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        nameRow.Children.Add(_dot);
        nameRow.Children.Add(_name);
        Typography.SetNumeralAlignment(_percent, FontNumeralAlignment.Tabular);
        _batteryRow.Children.Add(_percent);
        _batteryRow.Children.Add(_capsule);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, -4, 0) };
        _appsButton.ToolTip = "Приложения";
        _bridgeButton.Content = new QrIcon();
        _bridgeButton.ToolTip = "Мост с телефоном: файлы и текст по QR-коду";
        _bridgeButton.Click += (_, _) => OpenBridge();
        buttons.Children.Add(_bridgeButton);
        _flashButton.Click += (_, _) => ToggleFlashes();
        buttons.Children.Add(_flashButton);
        _clearButton.ToolTip = "Очистить";
        buttons.Children.Add(_appsButton);
        buttons.Children.Add(_clearButton);
        right.Children.Add(nameRow);
        right.Children.Add(_batteryRow);
        right.Children.Add(buttons);
        SetColumn(right, 1);
        _header.Children.Add(left);
        _header.Children.Add(right);
        Children.Add(_header);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list, Margin = new Thickness(0, 10, 0, 6) };
        Ui.SoftBottom(_scroll);
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        _appsButton.Click += (_, _) => { _editing = !_editing; Refresh(); };
        _clearButton.Click += (_, _) => Clear();
        _clock.Tick += (_, _) => Tick();
        _flashBack.Tick += (_, _) => { _flashBack.Stop(); RestoreBackdrop(); };
        _bridgeWatch.Tick += (_, _) =>
        {
            // Остров свернулся или ушли с вкладки: окно моста закрылось, сервер выключается.
            if (!IsLoaded || Window.GetWindow(this) is MainWindow { IsExpanded: false }) CloseBridge();
            else _bridge?.Refresh();
        };
        _onExpanding = () => { if (IsLoaded) Dispatcher.BeginInvoke(PlayIntro, DispatcherPriority.Loaded); };
        Services.IslandBus.Expanding += _onExpanding;
        Loaded += (_, _) =>
        {
            PhoneLink.Instance.Changed += Refresh;
            PhoneLink.Instance.Arrived += OnArrived;
            _clock.Start();
            _editing = false;
            Refresh();
            Tick(animate: false);
            Dispatcher.BeginInvoke(PlayIntro, DispatcherPriority.Loaded);
        };
        Unloaded += (_, _) =>
        {
            PhoneLink.Instance.Changed -= Refresh;
            PhoneLink.Instance.Arrived -= OnArrived;
            CloseBridge();
            _clock.Stop();
            _flashBack.Stop();
        };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Dispatcher.BeginInvoke(PlayIntro, DispatcherPriority.Loaded); };
        Tick(animate: false);
        Refresh();
    }

    private readonly Action _onExpanding;

    // ───── Время и шапка ─────

    private void Tick(bool animate = true)
    {
        var now = DateTime.Now;
        var date = now.ToString("dddd, d MMMM", Ru);
        _date.Text = char.ToUpper(date[0], Ru) + date[1..];
        foreach (var c in _cards.Values) c.Time.Text = Ago(c.Notice.Time);
    }

    private void UpdateHeader()
    {
        var link = PhoneLink.Instance;
        if (link.Name == null)
        {
            _name.Text = "iPhone не найден";
            _dot.Fill = Dim;
            _batteryRow.Visibility = Visibility.Collapsed;
            _batteryShown = false;
            return;
        }
        _name.Text = link.Connected ? link.Name : link.Name + " · не на связи";
        _dot.Fill = link.Connected ? Hues.Ink : Dim;
        if (link.Connected && link.Battery is { } b)
        {
            _percent.Text = b + " %";
            _capsule.Set(b / 100.0, animate: _batteryShown);
            _batteryRow.Visibility = Visibility.Visible;
            _batteryShown = true;
        }
        else
        {
            _batteryRow.Visibility = Visibility.Collapsed;
            _batteryShown = false;
        }
    }

    // ───── Список ─────

    // Выключатель вспышек на свёрнутом острове: колокольчик (вкл) или перечёркнутый серый колокольчик (выкл).
    private void UpdateFlashButton()
    {
        var on = Services.AppSettings.Load().PhoneFlashes;
        _flashButton.Content = on ? "" : "";
        _flashButton.Foreground = on ? Hues.Ink : Dim;
        _flashButton.ToolTip = on ? "Вспышки на острове: включены" : "Вспышки на острове: выключены";
    }

    private void ToggleFlashes()
    {
        var settings = Services.AppSettings.Load();
        settings.PhoneFlashes = !settings.PhoneFlashes;
        settings.Save();
        UpdateFlashButton();
    }

    private void Refresh()
    {
        UpdateHeader();
        UpdateFlashButton();
        _appsButton.Content = _editing ? "" : "";
        _clearButton.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;

        if (_editing)
        {
            _list.Children.Clear();
            _cards.Clear();
            _cardsMode = false;
            BuildApps();
            return;
        }

        var notices = VisibleNotices();
        if (!_cardsMode)
        {
            // Вернулись из редактора или первая сборка: строим список заново (без падения; каскад играет PlayIntro).
            _list.Children.Clear();
            _cards.Clear();
            _cardsMode = true;
        }

        if (notices.Count == 0)
        {
            _list.Children.Clear();
            _cards.Clear();
            var link = PhoneLink.Instance;
            _list.Children.Add(Empty(!link.NoticeAccess
                ? "Нет доступа к уведомлениям Windows"
                : link.Connected ? "Уведомлений нет" : "Уведомления приходят, пока iPhone на связи"));
            return;
        }
        if (_cards.Count == 0) _list.Children.Clear(); // убрать подпись «Уведомлений нет»

        var keys = notices.Select(Key).ToHashSet();
        foreach (var stale in _cards.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _list.Children.Remove(_cards[stale].Wrap);
            _cards.Remove(stale);
        }
        for (var i = 0; i < notices.Count; i++)
        {
            var key = Key(notices[i]);
            if (_cards.ContainsKey(key)) continue;
            var card = BuildCard(notices[i]);
            _cards[key] = card;
            _list.Children.Insert(Math.Min(i, _list.Children.Count), card.Wrap);
            if (_introDone && IsVisible) DropIn(card);
        }
    }

    private static (uint, long) Key(PhoneNotice n) => (n.Id, n.Time.Ticks);

    private static List<PhoneNotice> VisibleNotices()
    {
        var cleared = Services.AppSettings.Load().PhoneClearedAt;
        var limit = DateTime.Now.AddHours(-24);
        return PhoneLink.Instance.Notices.Where(n => n.Time > limit && (cleared == null || n.Time > cleared)).ToList();
    }

    private void Clear()
    {
        var settings = Services.AppSettings.Load();
        settings.PhoneClearedAt = DateTime.Now;
        settings.Save();
        Refresh();
    }

    private static UIElement Empty(string text)
    {
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        box.Children.Add(new TextBlock
        {
            Text = "", FontFamily = Ui.IconFont, FontSize = 22, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6)
        });
        box.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Soft, HorizontalAlignment = HorizontalAlignment.Center });
        return box;
    }

    private Card BuildCard(PhoneNotice n)
    {
        var logo = new AppLogo(34) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 11, 0) };
        logo.SetApp(n.App);
        var app = new TextBlock { Text = n.App, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Soft, TextTrimming = TextTrimming.CharacterEllipsis };
        var sender = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(n.Sender) ? " " : n.Sender, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0)
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(app);
        texts.Children.Add(sender);
        SetColumn(texts, 1);
        var time = new TextBlock { Text = Ago(n.Time), FontSize = 11.5, Foreground = Soft, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 10, 0, 0) };
        SetColumn(time, 2);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(logo);
        grid.Children.Add(texts);
        grid.Children.Add(time);
        if (n.Code is { } code)
        {
            // Уведомление с кодом подтверждения: рядом с временем кнопка «скопировать код» (один клик вместо набора).
            var copy = Ui.Pill(FormatCode(code), padding: 10);
            copy.Height = 26;
            copy.FontSize = 12.5;
            copy.VerticalAlignment = VerticalAlignment.Center;
            copy.Margin = new Thickness(8, 0, 0, 0);
            copy.ToolTip = "Скопировать код (он сам исчезнет из буфера через минуту)";
            copy.Click += (_, _) => CopyCode(code, copy);
            // Кнопка встаёт между названием и временем.
            SetColumn(copy, 2);
            time.Margin = new Thickness(10, 10, 0, 0);
            SetColumn(time, 3);
            grid.Children.Add(copy);
        }

        var face = new Border
        {
            CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(CardColor), Padding = new Thickness(10, 0, 14, 0), Height = CardHeight,
            Child = grid, RenderTransform = new TranslateTransform()
        };
        var wrap = new Border { Child = face, Padding = new Thickness(0, 0, 0, CardGap), Margin = new Thickness(2, 0, 2, 0) };
        return new Card(wrap, face, n, time);
    }

    private static string FormatCode(string code) => code.Length == 6 ? code[..3] + " " + code[3..] : code;

    // Копирует код и сразу убирает его из истории буфера (это секрет); через минуту очищает и сам буфер, если там всё ещё он.
    private void CopyCode(string code, Button button)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { Clipboard.SetText(code); break; }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                if (attempt == 3) { button.Content = "Не вышло"; return; }
                System.Threading.Thread.Sleep(60);
            }
        }
        button.Content = "✓ Скопировано";
        var forget = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        forget.Tick += (_, _) => { forget.Stop(); IslandBus.ForgetClipboardText(code); };
        forget.Start();
        var wipe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        wipe.Tick += (_, _) =>
        {
            wipe.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == code) Clipboard.Clear(); } catch { /* буфер занят: не страшно */ }
        };
        wipe.Start();
        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        back.Tick += (_, _) => { back.Stop(); button.Content = FormatCode(code); };
        back.Start();
    }

    // ───── Анимации ─────

    // Появление вкладки: шапка проявляется, карточки приезжают снизу каскадом (первые шесть, шаг 55 мс).
    private void PlayIntro()
    {
        if (!IsLoaded || !IsVisible) return;
        if ((DateTime.UtcNow - _lastIntro).TotalMilliseconds < 400) return;
        _lastIntro = DateTime.UtcNow;
        _introDone = true;
        Ui.FadeIn(_header);
        var i = 0;
        foreach (var child in _list.Children.OfType<Border>())
        {
            if (child.Child is not Border face) continue;
            var delay = TimeSpan.FromMilliseconds(40 + 55 * Math.Min(i++, 6));
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            // Начальные значения ставятся до анимации: пока идёт задержка, карточка не должна стоять на месте полностью видимой.
            face.BeginAnimation(OpacityProperty, null);
            face.Opacity = 0;
            face.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (face.RenderTransform is TranslateTransform t)
            {
                t.BeginAnimation(TranslateTransform.YProperty, null);
                t.Y = 16;
                t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = delay, EasingFunction = ease });
            }
        }
    }

    // Новая карточка: место под неё раскрывается, сама карточка падает сверху с лёгким перелётом и на секунду светлее обычной.
    private static void DropIn(Card card)
    {
        var wrap = card.Wrap;
        var full = CardHeight + CardGap;
        wrap.ClipToBounds = true;
        wrap.Height = 0;
        var grow = new DoubleAnimation(0, full, TimeSpan.FromMilliseconds(340)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        grow.Completed += (_, _) =>
        {
            wrap.BeginAnimation(HeightProperty, null);
            wrap.Height = double.NaN;
            wrap.ClipToBounds = false;
        };
        wrap.BeginAnimation(HeightProperty, grow);

        card.Face.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        if (card.Face.RenderTransform is TranslateTransform t)
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(480)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } });
        if (card.Face.Background is SolidColorBrush brush)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), CardColor, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // Новое уведомление, пока вкладка открыта: фон вспыхивает цветами значка приложения (с палитры вкладки на цвета приложения и обратно).
    private async void OnArrived(PhoneNotice notice)
    {
        try
        {
            if (!IsLoaded || !IsVisible || Window.GetWindow(this) is not MainWindow { IsExpanded: true }) return;
            var icon = await PhoneAppIcons.GetAsync(notice.App).ConfigureAwait(false);
            if (icon == null) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded || !IsVisible) return;
                var palette = CoverPalette.FromImage(icon);
                if (palette == null) return;
                IslandBackdrop.Claim(_owner, palette, 0.85, 1.15);
                _flashBack.Stop();
                _flashBack.Start();
            });
        }
        catch
        {
            // Вспышка фона — украшение: сбой её не должен мешать.
        }
    }

    private void RestoreBackdrop()
    {
        if (!IsLoaded || !IsVisible) return;
        if (_owner.BackdropPalette is { } palette) IslandBackdrop.Claim(_owner, palette, TabPalettes.Intensity, TabPalettes.Spread);
    }

    // ───── Мост с телефоном ─────

    private void OpenBridge()
    {
        if (_bridgeOpen) return;
        _server = new BridgeServer();
        _server.FileReceived += (path, size) => Dispatcher.BeginInvoke(() => OnBridgeFile(path, size));
        _server.TextReceived += text => Dispatcher.BeginInvoke(() => OnBridgeText(text));
        _server.Start(); // не вышло — причина покажется в окне моста
        _bridge = new BridgePanel(_server);
        _bridge.CloseRequested += CloseBridge;
        SetRow(_bridge, 0);
        SetRowSpan(_bridge, 2);
        _bridge.Margin = new Thickness(2, 0, 2, 6);
        Children.Add(_bridge);
        _header.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Collapsed;
        _bridgeOpen = true;
        BridgeActive = true;
        IslandBus.RequestHeight("phone");
        IslandBus.SuspendAutoCollapse(true); // QR должен висеть, пока наводят камеру
        IslandBus.BridgeOpen = true;
        IslandBus.Hold(true); // клик в другое окно не сворачивает остров и не рвёт передачу; закрывается кнопкой «назад», уходом с вкладки или из трея
        IslandBus.FileDropOverride = paths => { _bridge?.AddOutgoing(paths); return true; };
        _bridgeWatch.Start();
        _bridge.Refresh();
        Ui.FadeIn(_bridge);
    }

    private void CloseBridge()
    {
        if (!_bridgeOpen) return;
        _bridgeOpen = false;
        BridgeActive = false;
        IslandBus.RequestHeight("phone");
        _bridgeWatch.Stop();
        IslandBus.FileDropOverride = null;
        IslandBus.BridgeOpen = false;
        IslandBus.Hold(false);
        IslandBus.SuspendAutoCollapse(false);
        if (_bridge != null) Children.Remove(_bridge);
        _bridge = null;
        _server?.Dispose(); // сервер выключается вместе с окном моста; файлы на ПК остаются где были
        _server = null;
        _header.Visibility = Visibility.Visible;
        _scroll.Visibility = Visibility.Visible;
        Ui.FadeIn(_header);
        Refresh();
    }

    private void OnBridgeFile(string path, long size)
    {
        Modules.Buffer.ClipboardService.Instance.Shelf.Add([path]); // принятый файл сразу на полке
        _bridge?.AddIncoming(path, size);
        PhoneLink.Instance.NotifyReceived(System.IO.Path.GetFileName(path));
    }

    private void OnBridgeText(string text)
    {
        Modules.Buffer.ClipboardService.Instance.History.AddText(text); // текст с телефона — в историю буфера, сам буфер не трогаем
        _bridge?.AddIncomingText(text);
        PhoneLink.Instance.NotifyReceived("Текст: " + (text.Length > 40 ? text[..40] + "…" : text));
    }

    // ───── Выбор приложений ─────

    private void BuildApps()
    {
        _list.Children.Add(new TextBlock
        {
            Text = "Говорить «Новое сообщение» о:", FontSize = 12, Foreground = Soft, Margin = new Thickness(4, 0, 0, 6)
        });

        // Если у мессенджера выключены уведомления в самой Windows, остров их не увидит: говорим об этом и даём открыть настройки.
        var disabled = PhoneApps.DisabledInWindows();
        if (disabled.Count > 0)
        {
            var note = new Grid { Margin = new Thickness(2, 0, 2, 6) };
            note.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            note.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock
            {
                Text = $"{string.Join(", ", disabled)}: уведомления выключены в Windows, остров их не увидит",
                FontSize = 12, Foreground = Soft, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
            };
            var open = Ui.Pill("Открыть", padding: 12);
            open.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); }
                catch { /* Параметры не открылись: не критично */ }
            };
            SetColumn(open, 1);
            note.Children.Add(text);
            note.Children.Add(open);
            _list.Children.Add(note);
        }

        var names = PhoneLink.Instance.Notices.Select(n => n.App)
            .Concat(PhoneApps.KnownMessengers)
            .Concat(Services.AppSettings.Load().PhoneApps?.Select(r => r.Name) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(PhoneApps.IsMessenger).ThenBy(a => a, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var name in names)
        {
            var row = new Grid { Height = 36, Margin = new Thickness(2, 0, 2, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var logo = new AppLogo(26) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            logo.SetApp(name);
            var label = new TextBlock { Text = name, FontSize = 13, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(36, 0, 12, 1) };
            var toggle = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center };
            toggle.Set(PhoneApps.IsEnabled(name));
            var captured = name;
            toggle.Toggled += on => PhoneApps.SetEnabled(captured, on);
            SetColumn(toggle, 1);
            row.Children.Add(logo);
            row.Children.Add(label);
            row.Children.Add(toggle);
            _list.Children.Add(row);
        }
    }

    private static string Ago(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "сейчас";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} мин";
        if (span.TotalHours < 24 && t.Date == DateTime.Today) return $"{(int)span.TotalHours} ч";
        return t.Date == DateTime.Today.AddDays(-1) ? "вчера" : t.ToString("dd.MM");
    }
}
