using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ostrov.Backdrop;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov;

public partial class MainWindow : Window
{
    // Размеры острова (в независимых от DPI пикселях).
    private const double CollapsedWidth = 200, CollapsedHeight = 36, CollapsedRadius = 18;
    private const double DefaultExpandedWidth = 520, DefaultExpandedHeight = 220, ExpandedRadius = 38;

    // Время анимации и задержка автосворачивания.
    private static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(320));
    private static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(260));
    // Лёгкий «пружинный» перелёт в конце анимации (0 — без эффекта; ~0.3 — едва заметно).
    private const double ExpandBounce = 0.2, CollapseBounce = 0.15;
    // Автосворачивание (М-010): остров сворачивается, когда мышь полностью неподвижна заданное время (настройка, по умолчанию 3 с; 0 — не сворачивать).
    private TimeSpan _autoCollapseDelay = TimeSpan.FromSeconds(3);
    private POINT _lastCursor;
    private DateTime _lastMoveUtc;

    private readonly DispatcherTimer _autoCollapseTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public bool IsExpanded { get; private set; }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    /// <summary>0 — свёрнутая плашка, 1 — полностью раскрытый остров.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    // Высота раскрытого острова зависит от вкладки (задаёт модуль); при смене вкладки плавно меняется.
    public static readonly DependencyProperty ExpandedHeightValueProperty = DependencyProperty.Register(
        nameof(ExpandedHeightValue), typeof(double), typeof(MainWindow),
        new PropertyMetadata(DefaultExpandedHeight, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double ExpandedHeightValue
    {
        get => (double)GetValue(ExpandedHeightValueProperty);
        set => SetValue(ExpandedHeightValueProperty, value);
    }

    // Ширина раскрытого острова: у вкладок 520, у карточки запроса Claude меньше.
    public static readonly DependencyProperty ExpandedWidthValueProperty = DependencyProperty.Register(
        nameof(ExpandedWidthValue), typeof(double), typeof(MainWindow),
        new PropertyMetadata(DefaultExpandedWidth, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double ExpandedWidthValue
    {
        get => (double)GetValue(ExpandedWidthValueProperty);
        set => SetValue(ExpandedWidthValueProperty, value);
    }

    private void ApplyExpandedHeight(double target) => AnimateSize(ExpandedHeightValueProperty, target);

    private void ApplyExpandedWidth(double target) => AnimateSize(ExpandedWidthValueProperty, target);

    // Раскрытый остров меняет размер плавно; свёрнутый или раскрывающийся — сразу (его размер и так анимирует Progress).
    private void AnimateSize(DependencyProperty property, double target)
    {
        if (property == ExpandedHeightValueProperty) _goalHeight = target; else _goalWidth = target;
        if (IsExpanded && Progress > 0.99)
        {
            BeginAnimation(property, new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            BeginAnimation(property, null);
            SetValue(property, target);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        _bounce.Changed += v => BounceScale.ScaleX = BounceScale.ScaleY = 1 + v; // окно размытия баунс не повторяет: боковой запас больше его размаха
        // Вплотную к верху экрана, по центру.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
        // Сменили разрешение экрана: остров заново по центру (окно размытия пересчитывается вместе с ним).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            UpdateBlurBounds();
        });
        _autoCollapseTimer.Tick += (_, _) => PollMouseStillness();
        _outsideClickTimer.Tick += (_, _) => PollOutsideClick();
        // Колесо, клик и клавиши тоже считаются «пользуюсь»: листаешь список колесом без движения курсора, остров не должен закрыться.
        PreviewMouseWheel += (_, _) => _lastMoveUtc = DateTime.UtcNow;
        PreviewMouseDown += (_, _) => _lastMoveUtc = DateTime.UtcNow;
        PreviewKeyDown += (_, _) => _lastMoveUtc = DateTime.UtcNow;
        ApplyAutoCollapseSetting();
        IslandBus.SettingsChanged += () => Dispatcher.BeginInvoke(() =>
        {
            ApplyAutoCollapseSetting();
            RestartAutoCollapse();
            ShrinkApps.Reload();
            SetForegroundApp(_foregroundApp); // список «Программы» мог измениться
            ApplyGlass(); // стекло и размытие
        });
        BuildTabs();
        InitTabHeader();
        InitNight();

        // Функции могут попросить раскрыть остров (например, таймер дозвонил) или закрыть его.
        IslandBus.AttentionRequested += id => Dispatcher.BeginInvoke(() => ExpandPinned(id));
        IslandBus.TabRequested += id => Dispatcher.BeginInvoke(() => { if (!IsVisible || _compact) return; SelectTab(id); _keepTab = true; Expand(); });
        IslandBus.Dismissed += () => Dispatcher.BeginInvoke(DismissPinned);
        // Удержание ставится сразу, если вызов с потока окна: системный диалог (выбор файлов или папки) сообщает окну «фокус ушёл» раньше,
        // чем отложенный вызов успел бы сработать, и остров свернулся бы под диалогом.
        IslandBus.HoldChanged += on =>
        {
            if (Dispatcher.CheckAccess()) { _hold = on; RestartAutoCollapse(); }
            else Dispatcher.BeginInvoke(() => { _hold = on; RestartAutoCollapse(); });
        };
        IslandBus.CollapseRequested += () => Dispatcher.BeginInvoke(Collapse);
        // Функция просит другую высоту (например, в погоде раскрылись подробности дня).
        IslandBus.HeightChanged += id => Dispatcher.BeginInvoke(() =>
        {
            if (!_overlayActive && id == _currentModuleId && ModuleCatalog.All.FirstOrDefault(m => m.Id == id) is { } m)
            {
                ApplyExpandedWidth(m.ExpandedWidth);
                ApplyExpandedHeight(m.ExpandedHeight + HeaderExtra);
            }
        });
        // Карточка поверх вкладок (запрос Claude) и признак «остров виден» для функций.
        IslandBus.OverlayShown += (view, w, h) => Dispatcher.BeginInvoke(() => ShowOverlay(view, w, h));
        IslandBus.OverlayCleared += () => Dispatcher.BeginInvoke(ClearOverlay);
        // Пассивные активности в свёрнутом виде (таймер, рендер After Effects и т. п.): плашка и кружок.
        ActivityCenter.Changed += () => Dispatcher.BeginInvoke(ApplyActivities);
        IsVisibleChanged += (_, _) => { IslandBus.SetVisible(IsVisible && !_compact); UpdateOutlineTimer(); };
        _compactHideTimer.Tick += (_, _) =>
        {
            _compactHideTimer.Stop();
            if (_compact && ActivityCenter.MainCompact == null) { Collapse(); Hide(); } // нотчу нечего показывать: прячем окно
        };
        InitOutline();
        InitGlow();
        InitBlur();
        InitTabsToggle();
        InitWidened();
        _topTimer.Tick += (_, _) => KeepOnTop();
    }

    // Карточка поверх вкладок. Остров закреплён, пока она есть; после неё сворачивается (или показывает следующую).
    private bool _overlayActive;
    private bool _overlayClosing; // карточку сняли, остров сворачивается; вкладки вернём, когда свернётся
    private bool _overlayOverLyrics; // карточка стоит поверх раскрытого острова с открытым текстом песни
    private string? _pendingAttention; // функция просила внимания, пока была карточка

    private void ShowOverlay(FrameworkElement view, double width, double height)
    {
        if (!IsVisible || _compact) return; // в нотче (полноэкранное видео) карточки не показываем
        if (!_overlayActive) { _overlayOverLyrics = IsExpanded && LyricsHoldsIsland; _overlayOverBridge = IsExpanded && IslandBus.BridgeOpen; } // карточка пришла поверх текста песни или моста
        _overlayActive = true;
        IslandBus.OverlayActive = true;
        IslandBackdrop.NotifyChanged(); // под карточкой цветной фон не нужен
        _overlayClosing = false;
        OverlayHost.Content = view;
        OverlayHost.Visibility = Visibility.Visible;
        ModuleHost.Visibility = Visibility.Collapsed;
        TabStrip.Visibility = Visibility.Collapsed;
        ApplyExpandedWidth(width);
        ApplyExpandedHeight(height);
        _pinned = true;
        Expand(activate: false);
        RestartAutoCollapse(); // остров мог быть уже раскрыт, и таймер сворачивания шёл
    }

    private void ClearOverlay()
    {
        if (!_overlayActive) return;
        _pinned = false;
        // Карточка (запрос Claude, Alt+Q) пришла поверх открытого текста песни: после неё возвращаемся к тексту, а не сворачиваем остров
        // (с текстом остров закрывается только кликом мимо). Если карточку закрыл как раз клик вне острова, сворачиваем, как обычно.
        var outsideClick = AnyMouseButtonDown() && GetCursorPos(out var click) && !CursorOverIsland(click);
        if (_overlayOverLyrics && IsExpanded && LyricsHoldsIsland && !outsideClick)
        {
            FinishOverlay();
            if (!IsActive) StartOutsideClickWatch();
            RestartAutoCollapse();
            return;
        }
        if (_overlayOverBridge && IsExpanded && IslandBus.BridgeOpen)
        {
            // Мост открыт: после карточки остров возвращается к нему, иначе идущая передача оборвалась бы.
            FinishOverlay();
            RestartAutoCollapse();
            return;
        }
        _overlayClosing = true;
        if (IsExpanded) Collapse();
        else FinishOverlay();
    }

    private bool _overlayOverBridge;

    // Остров свернулся (или был скрыт): возвращаем вкладки и их размеры.
    private void FinishOverlay()
    {
        _overlayClosing = false;
        _overlayActive = false;
        _overlayOverLyrics = false;
        _overlayOverBridge = false;
        IslandBus.OverlayActive = false;
        IslandBackdrop.NotifyChanged();
        OverlayHost.Content = null;
        OverlayHost.Visibility = Visibility.Collapsed;
        ModuleHost.Visibility = Visibility.Visible;
        TabStrip.Visibility = _tabsHiddenByModule ? Visibility.Hidden : Visibility.Visible; // вкладка могла скрыть полосу (режим текста)
        var current = ModuleCatalog.All.FirstOrDefault(m => m.Id == _currentModuleId);
        ApplyExpandedWidth(current?.ExpandedWidth ?? DefaultExpandedWidth);
        if (current != null) ApplyExpandedHeight(current.ExpandedHeight + HeaderExtra);
        if (_pendingAttention is { } id)
        {
            _pendingAttention = null;
            ExpandPinned(id);
        }
    }

    // «Закреплённое» раскрытие по просьбе функции: не крадёт фокус у других окон,
    // не сворачивается само и от клика мимо; сворачивается по клику в остров.
    private bool _pinned;
    private bool _hold; // открыто системное окно (выбор папки и т. п.) — остров не сворачивается
    private string? _currentModuleId;

    private void ExpandPinned(string moduleId)
    {
        if (!IsVisible || _compact) return; // остров скрыт (трей, полноэкранная игра) или стал нотчем (полноэкранное видео) — не раскрываем
        if (_overlayActive) { _pendingAttention = moduleId; return; } // сейчас карточка запроса Claude: покажем после неё
        foreach (RadioButton tab in TabBar.Children)
            if (tab.Tag is IIslandModule m && m.Id == moduleId) tab.IsChecked = true;
        _keepTab = true;
        _pinned = true;
        Expand(activate: false);
    }

    private void DismissPinned()
    {
        _pinned = false;
        Collapse();
    }

    // Вкладки строятся по списку из ModuleCatalog. Содержимое вкладки создаётся при первом открытии.
    private readonly Dictionary<string, FrameworkElement> _views = [];

    private void BuildTabs()
    {
        var style = (Style)FindResource("TabButton");
        var sideStyle = (Style)FindResource("SideTabButton");
        foreach (var module in ModuleCatalog.All)
        {
            var tab = new RadioButton
            {
                Style = module.IsSecondary ? sideStyle : style,
                Content = module.Icon,
                ToolTip = module.Title,
                GroupName = "tabs",
                Tag = module,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) } }
            };
            tab.Checked += (_, _) => { ShowModule(module); UpdateTabMarks(); UpdateTabPoses(animate: IsExpanded && ContentAmount > 0.5); };
            tab.Unchecked += (_, _) => UpdateTabMarks();
            var label = module.Title.Split(' ')[0];
            TabMarks.SetLabel(tab, label);
            _labelWidths[tab] = MeasureLabel(label);
            System.Windows.Automation.AutomationProperties.SetName(tab, module.Title);
            (module.IsSecondary ? SideTabBar : TabBar).Children.Add(tab);
        }

        if (TabBar.Children.Count > 0)
            ((RadioButton)TabBar.Children[0]).IsChecked = true;
    }

    private void ShowModule(IIslandModule module)
    {
        if (!_views.TryGetValue(module.Id, out var view))
            _views[module.Id] = view = module.CreateView();
        SwapModule(view, ModuleCatalog.All.ToList().IndexOf(module));
        _currentModuleId = module.Id;
        _suspendAutoCollapse = false; // «не сворачивать» держит вкладка (поиск, статья); ушли с неё — флаг не должен залипнуть
        // Вкладки с палитрой в описании («Время», «Проекты», «Буфер», «Новости», «Центр», «Настройки») берут общий мягкий фон сами;
        // у плеера и погоды фоном управляют их виды (цвета обложки и погоды).
        if (module.BackdropPalette is { } palette) Backdrop.IslandBackdrop.Claim(module, palette, Backdrop.TabPalettes.Intensity, Backdrop.TabPalettes.Spread);
        ApplyTabTint(module.Id);
        if (!_overlayActive)
        {
            ApplyExpandedWidth(module.ExpandedWidth);
            ApplyExpandedHeight(module.ExpandedHeight + HeaderExtra);
        }
        RestartAutoCollapse();
    }

    // Смена вкладки «листанием»: старая страница и новая движутся одновременно в одну сторону, как страницы или карусель.
    // Старая (её снимок) уезжает медленнее и гаснет, новая приезжает быстрее с лёгким наклоном, который выравнивается к концу. Чем дальше вкладка по полосе, тем длиннее путь.
    // На свёрнутом или ещё раскрывающемся острове, под карточкой запроса и при первом показе меняем сразу.
    private int _moduleIndex = -1;
    private int _swapToken;

    private static BitmapSource? Snapshot(FrameworkElement e)
    {
        if (e.ActualWidth < 4 || e.ActualHeight < 4) return null;
        var dpi = VisualTreeHelper.GetDpi(e);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(e.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(e);
        rtb.Freeze();
        return rtb;
    }

    private void SwapModule(FrameworkElement view, int index)
    {
        var token = ++_swapToken;
        var previous = _moduleIndex;
        _moduleIndex = index;

        void Reset()
        {
            ModuleHost.BeginAnimation(OpacityProperty, null);
            ModuleShift.BeginAnimation(TranslateTransform.XProperty, null);
            ModuleSkew.BeginAnimation(SkewTransform.AngleXProperty, null);
            ModuleGhost.BeginAnimation(OpacityProperty, null);
            GhostShift.BeginAnimation(TranslateTransform.XProperty, null);
            GhostSkew.BeginAnimation(SkewTransform.AngleXProperty, null);
            ModuleHost.Opacity = 1;
            ModuleShift.X = 0;
            ModuleSkew.AngleX = 0;
            ModuleGhost.Visibility = Visibility.Collapsed;
            ModuleGhost.Source = null;
        }

        if (previous < 0 || previous == index || !IsExpanded || ContentAmount < 0.9 || _overlayActive || ModuleHost.Content == null || ModuleHost.Content == view)
        {
            Reset();
            ModuleHost.Content = view;
            return;
        }

        Reset();
        var snapshot = Snapshot(ModuleHost);
        var dir = index > previous ? 1 : -1;
        var dist = 46 + 18 * Math.Min(3, Math.Abs(index - previous)); // 64…100 px
        ModuleHost.Content = view;
        if (snapshot != null)
        {
            ModuleGhost.Source = snapshot;
            ModuleGhost.Width = snapshot.Width;
            ModuleGhost.Height = snapshot.Height;
            ModuleGhost.Opacity = 1;
            ModuleGhost.Visibility = Visibility.Visible;
        }

        var quint = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var cubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var cubicIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        // Новая страница: приезжает с края с наклоном и проявляется.
        ModuleHost.Opacity = 0;
        ModuleShift.X = dir * dist;
        ModuleSkew.AngleX = -dir * 5;
        ModuleHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(40), EasingFunction = cubicOut });
        ModuleShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * dist, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = quint });
        ModuleSkew.BeginAnimation(SkewTransform.AngleXProperty, new DoubleAnimation(-dir * 5, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = quint });
        if (snapshot == null) return;
        // Прежняя страница: уезжает в обратную сторону медленнее новой (параллакс) и гаснет.
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = cubicIn };
        fade.Completed += (_, _) =>
        {
            if (token != _swapToken) return;
            ModuleGhost.Visibility = Visibility.Collapsed;
            ModuleGhost.Source = null;
        };
        ModuleGhost.BeginAnimation(OpacityProperty, fade);
        GhostShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -dir * dist * 0.6, TimeSpan.FromMilliseconds(380)) { EasingFunction = quint });
        GhostSkew.BeginAnimation(SkewTransform.AngleXProperty, new DoubleAnimation(0, dir * 4, TimeSpan.FromMilliseconds(380)) { EasingFunction = quint });
    }

    private bool _keepTab; // перед Expand вкладка уже выбрана по причине раскрытия: «Главная» её не заменяет

    public void Expand(bool activate = true)
    {
        if (IsExpanded || _compact) { _keepTab = false; return; } // нотч в полноэкранном видео не раскрывается
        // Каждое обычное раскрытие начинается с «Главной»; если вкладку выбрала сама причина раскрытия (таймер, клик по плашке, файлы), её не трогаем.
        if (!_keepTab && !_overlayActive && _currentModuleId != "hub") SelectTab("hub");
        _keepTab = false;
        IslandBus.RaiseExpanding(); // вкладки могут вернуть обычный вид, пока остров ещё свёрнут
        ActivityCenter.SetWidened(false); // расширенная плашка (правый клик) не должна дожить до следующего сворачивания
        IsExpanded = true;
        ContentRoot.IsHitTestVisible = true;
        AnimateTo(1, ExpandDuration, ExpandBounce);
        if (activate) Activate(); // чтобы потеря фокуса (клик вне острова) могла свернуть его
        RestartAutoCollapse();
    }

    // Остров виден, только если его не скрыл пользователь (трей) и нет полноэкранной игры. В полноэкранном видео он не прячется,
    // а сжимается в нотч (см. SetCompact), пока есть что показать (таймер, секундомер, рендер AE).
    private bool _hiddenByUser;
    private FullscreenKind _fullscreenKind;
    private bool _compact;
    private bool _shrunk;                 // в браузере: остров сжат в нотч, но раскрывается кликом (в отличие от _compact — полноэкранного видео)
    private bool _foregroundShrinkApp;    // в фокусе программа из списка «сжимать» (браузер)
    private readonly DispatcherTimer _compactHideTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };

    /// <summary>Показать или скрыть остров целиком (из меню трея).</summary>
    public void ToggleVisibility()
    {
        _hiddenByUser = !_hiddenByUser;
        UpdateVisibility();
    }

    /// <summary>Вызывается, когда меняется программа в фокусе: в браузере остров сжимается в нотч.</summary>
    public void SetForegroundApp(string? processName)
    {
        _foregroundApp = processName;
        IslandBus.SetForegroundApp(processName); // активности и запросы Claude, которые прячутся в этой программе (М-014)
        _foregroundShrinkApp = FullscreenWatcher.IsShrinkApp(processName);
        UpdateVisibility();
        KeepOnTop();
    }

    private string? _foregroundApp;

    /// <summary>Вызывается, когда меняется то, что стоит на весь экран: ничего, видео (браузер, плеер) или игра.</summary>
    public void SetFullscreenKind(FullscreenKind kind)
    {
        _fullscreenKind = kind;
        UpdateVisibility();
        KeepOnTop();
    }

    private void UpdateVisibility()
    {
        // Полноэкранное видео: остров становится нотчем, пока есть активность с нотчем; игра и скрытие из трея прячут остров целиком.
        var compact = !_hiddenByUser && _fullscreenKind == FullscreenKind.Video;
        SetCompact(compact);
        SetShrunk(!_hiddenByUser && _fullscreenKind == FullscreenKind.None && _foregroundShrinkApp);
        var shouldShow = !_hiddenByUser && (_fullscreenKind == FullscreenKind.None || (compact && ActivityCenter.MainCompact != null));

        // Пока остров сжат (нотч над браузером или видео), раз в секунду заново закрепляем его поверх окон (М-015).
        if (shouldShow && (compact || _shrunk)) { if (!_topTimer.IsEnabled) _topTimer.Start(); }
        else _topTimer.Stop();

        if (shouldShow)
        {
            _compactHideTimer.Stop();
            if (!IsVisible) Show();
        }
        else if (IsVisible)
        {
            if (compact)
            {
                // Нотч ещё плавно гаснет: окно прячем, когда он погас (если активность за это время не вернулась).
                if (!_compactHideTimer.IsEnabled) _compactHideTimer.Start();
            }
            else
            {
                _pinned = false; // см. SetCompact: скрытый остров не держит «закреплённость»
                Collapse();
                Hide();
            }
        }
    }

    // Сжатый остров в браузере: то же нотч-оформление и те же активности (полоски), но раскрывается кликом и сам берёт фокус как обычно.
    // Раскрытый остров не трогаем: он сожмётся, когда свернётся (в фокус вернётся браузер).
    private void SetShrunk(bool shrunk)
    {
        if (_shrunk == shrunk) return;
        _shrunk = shrunk;
        BeginAnimation(ShrinkAmountProperty, new DoubleAnimation(shrunk ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        ApplyActivities(); // плашка <-> нотч
    }

    // Нотч: полноэкранное видео. Остров не раскрывается и не берёт фокус (иначе видео свернулось бы из полного экрана),
    // горячие клавиши и запросы Claude считают его скрытым (IslandBus.IsVisible = false), клик только закрывает висящий знак.
    private void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        if (compact)
        {
            _pinned = false; // закреплённое раскрытие (таймер, карточка) не должно пережить нотч: иначе первый клик после видео «ничего не делает»
            _autoCollapseTimer.Stop();
            Collapse();
        }
        SetNoActivate(compact);
        IslandBus.SetVisible(IsVisible && !compact);
        IslandBus.SetCompact(compact);
        ApplyActivities(); // плашка <-> нотч
        ApplyProgress();
    }

    public void Collapse()
    {
        if (!IsExpanded) return;
        IsExpanded = false;
        ContentRoot.IsHitTestVisible = false;
        _autoCollapseTimer.Stop();
        _outsideClickTimer.Stop();
        AnimateTo(0, CollapseDuration, CollapseBounce);
    }

    // Проявление содержимого раскрытого острова: 0 — скрыто, 1 — видно. Отдельно от Progress, чтобы не зависеть от его перелёта.
    public static readonly DependencyProperty ContentAmountProperty = DependencyProperty.Register(
        nameof(ContentAmount), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double ContentAmount
    {
        get => (double)GetValue(ContentAmountProperty);
        set => SetValue(ContentAmountProperty, value);
    }

    private void AnimateContent(bool show)
    {
        var animation = show
            ? new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(170))) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }
            : new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(110))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        BeginAnimation(ContentAmountProperty, animation);
    }

    private void AnimateTo(double target, Duration duration, double bounce)
    {
        AnimateContent(target > 0.5);
        var animation = new DoubleAnimation(target, duration)
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = bounce }
        };
        BeginAnimation(ProgressProperty, animation);
    }

    // Пассивный индикатор в свёрнутом виде: свёрнутый остров слегка вырастает до размера индикатора (ActivityAmount 0..1).
    public static readonly DependencyProperty ActivityAmountProperty = DependencyProperty.Register(
        nameof(ActivityAmount), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double ActivityAmount
    {
        get => (double)GetValue(ActivityAmountProperty);
        set => SetValue(ActivityAmountProperty, value);
    }

    // Размер плашки: когда одна активность сменила другую, остров плавно переходит от прежнего размера к новому.
    public static readonly DependencyProperty ActivityWidthProperty = DependencyProperty.Register(
        nameof(ActivityWidth), typeof(double), typeof(MainWindow),
        new PropertyMetadata(CollapsedWidth, (d, _) => ((MainWindow)d).ApplyProgress()));

    public static readonly DependencyProperty ActivityHeightProperty = DependencyProperty.Register(
        nameof(ActivityHeight), typeof(double), typeof(MainWindow),
        new PropertyMetadata(CollapsedHeight, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double ActivityWidth
    {
        get => (double)GetValue(ActivityWidthProperty);
        set => SetValue(ActivityWidthProperty, value);
    }

    public double ActivityHeight
    {
        get => (double)GetValue(ActivityHeightProperty);
        set => SetValue(ActivityHeightProperty, value);
    }

    private bool _activityDismissOnClick;

    // Проявление только что подставленной плашки (0..1); множитель к прозрачности ActivityHost.
    public static readonly DependencyProperty ActivitySwapProperty = DependencyProperty.Register(
        nameof(ActivitySwap), typeof(double), typeof(MainWindow),
        new PropertyMetadata(1.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double ActivitySwap
    {
        get => (double)GetValue(ActivitySwapProperty);
        set => SetValue(ActivitySwapProperty, value);
    }

    private void AnimateActivitySize(double width, double height)
    {
        foreach (var (property, target) in new[] { (ActivityWidthProperty, width), (ActivityHeightProperty, height) })
        {
            if (Math.Abs((double)GetValue(property) - target) < 0.5)
            {
                BeginAnimation(property, null);
                SetValue(property, target);
            }
            else
            {
                BeginAnimation(property, new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(260)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
        }
    }

    private void ShowActivity(FrameworkElement view, double width, double height, bool dismissOnClick)
    {
        _activityDismissOnClick = dismissOnClick;
        var alreadyFull = ActivityAmount > 0.999 && ActivityHost.Content != null;
        var replaced = alreadyFull && !ReferenceEquals(ActivityHost.Content, view) && _dropSide == 0; // при обмене проявление ведёт DropSwap
        ActivityHost.Content = view;
        // Другая плашка встала на место прежней (вспышка вместо таймера, расширение по правому клику и т. п.): новая проявляется плавно, а не сменой кадра.
        if (replaced)
            BeginAnimation(ActivitySwapProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        ActivityHost.Width = width;
        ActivityHost.Height = height;
        if (alreadyFull)
        {
            // Плашку заменили или обновили: остров уже расширен, размер плавно меняется, если он другой.
            AnimateActivitySize(width, height);
            ApplyProgress();
            return;
        }
        BeginAnimation(ActivityWidthProperty, null);
        BeginAnimation(ActivityHeightProperty, null);
        ActivityWidth = width;
        ActivityHeight = height;
        BeginAnimation(ActivityAmountProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(320)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ClearActivity()
    {
        _activityDismissOnClick = false;
        if (ActivityHost.Content == null) return;
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            // Если за это время показали новый индикатор, не трогаем его.
            if (!_activityDismissOnClick && ActivityAmount <= 0.001) ActivityHost.Content = null;
        };
        BeginAnimation(ActivityAmountProperty, fade);
    }

    private void ApplyProgress()
    {
        var p = Progress;
        var a = ActivityAmount;
        // В нотче (полноэкранное видео) без активности острова нет: он «выезжает» из верхнего края (ширина 60 %, высота 0) и уезжает обратно.
        // В браузере (ShrinkAmount) свёрнутый остров плавно уменьшается до нотча 150×14 и остаётся виден даже без активностей.
        var shrink = ShrinkAmount;
        var baseWidth = _compact ? ActivityCenter.CompactWidth * 0.6 : Lerp(CollapsedWidth, ActivityCenter.CompactWidth, shrink);
        var baseHeight = _compact ? 0 : Lerp(CollapsedHeight, ActivityCenter.CompactHeight, shrink);
        var baseRadius = _compact ? 0 : Lerp(CollapsedRadius, ActivityCenter.CompactRadius, shrink);
        var restWidth = Lerp(baseWidth, ActivityWidth, a);
        var restHeight = Lerp(baseHeight, ActivityHeight, a);
        // Нотч (высота 14) скругляем сильнее, чем половина высоты; остальные плашки как раньше.
        var activityRadius = ActivityHeight <= ActivityCenter.CompactHeight ? ActivityCenter.CompactRadius : Math.Min(ActivityHeight / 2, 24);
        var restRadius = Lerp(baseRadius, activityRadius, a);
        var expandedWidth = ExpandedWidthValue;
        // Перелёт пружины (BackEase) на сворачивании уводит p чуть ниже нуля; в нотче без активности база нулевая,
        // и отрицательные размеры бросали исключение анимации, которое убивало процесс. Размеры и радиус не отрицательные никогда.
        // У низкого нотча (14 px) просадка пружины заметна (он сжимался бы на четверть): там перелёт вниз отрезаем.
        var ps = restHeight < 20 ? Math.Max(0, p) : p;
        Island.Width = Math.Max(0, Lerp(restWidth, expandedWidth, ps));
        ContentRoot.Width = expandedWidth;
        var expandedHeight = ExpandedHeightValue;
        Island.Height = Math.Max(0, Lerp(restHeight, expandedHeight, ps));
        ContentRoot.Height = expandedHeight;
        var r = Math.Max(0, Lerp(restRadius, ExpandedRadius, ps));
        Island.CornerRadius = new CornerRadius(0, 0, r, r);
        NoteIslandLayoutChanged(Island.CornerRadius);
        // Содержимое проявляется в последней части раскрытия и гаснет в первой части сворачивания.
        // Проявление содержимого идёт отдельно от роста острова (ContentAmount): сначала остров вырастает, потом появляется текст;
        // при сворачивании содержимое гаснет первым.
        var ca = ContentAmount;
        ContentRoot.Opacity = ca * NightInk;
        ContentScale.ScaleX = ContentScale.ScaleY = 0.97 + 0.03 * ca;
        // Индикатор виден в свёрнутом виде и гаснет в самом начале раскрытия.
        ActivityHost.Visibility = a > 0.001 && ActivityHost.Content != null ? Visibility.Visible : Visibility.Collapsed;
        ActivityHost.Opacity = a * ActivitySwap * DropMainFade() * NightInk * (1 - Math.Clamp(p / 0.3, 0, 1));
        ApplyDropToMain();
        UpdateBubble(p);
        UpdateGlow(p, expandedWidth, expandedHeight);
        ApplyGlassAmount(p);
        UpdateBlurBounds();

        // Остров свернулся после карточки: возвращаем вкладки (не прямо здесь, а после текущего пересчёта).
        if (_overlayClosing && p <= 0.02)
            Dispatcher.BeginInvoke(() => { if (_overlayClosing) FinishOverlay(); });
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private void RestartAutoCollapse()
    {
        _autoCollapseTimer.Stop();
        if (IsExpanded && !_pinned && !_hold && !_dragHold && !_suspendAutoCollapse && _autoCollapseDelay > TimeSpan.Zero)
        {
            GetCursorPos(out _lastCursor);
            _lastMoveUtc = DateTime.UtcNow;
            _autoCollapseTimer.Start();
        }
    }

    private void ApplyAutoCollapseSetting() =>
        _autoCollapseDelay = TimeSpan.FromSeconds(Math.Max(0, AppSettings.Load().AutoCollapseSeconds));

    // Следим за положением курсора: любое движение сбрасывает отсчёт, полная неподвижность дольше заданного времени сворачивает остров.
    private void PollMouseStillness()
    {
        if (!GetCursorPos(out var p)) return;
        var now = DateTime.UtcNow;
        if (p.X != _lastCursor.X || p.Y != _lastCursor.Y)
        {
            _lastCursor = p;
            _lastMoveUtc = now;
            return;
        }
        if (now - _lastMoveUtc < _autoCollapseDelay) return;
        _autoCollapseTimer.Stop();
        Collapse();
    }

    // Файлы, перетащенные на остров (полка на вкладке «Буфер»). Перетяжка с самой полки наружу сюда не принимается.
    private bool _dragHold;

    private bool CanAcceptFiles(DragEventArgs e) =>
        !_overlayActive && !_compact && e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent("Ostrov.Shelf");

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanAcceptFiles(e)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy; // только «копировать»: исходные файлы остров не трогает и не удаляет
        _dragHold = true;
        // Остров раскрывается сам и открывает вкладку «Буфер», чтобы было куда бросить.
        if (IslandBus.FileDropOverride == null)
            foreach (RadioButton tab in TabBar.Children)
                if (tab.Tag is IIslandModule m && m.Id == Modules.Buffer.BufferModule.ModuleId) tab.IsChecked = true;
        _keepTab = IslandBus.FileDropOverride == null;
        Expand(activate: false);
        RestartAutoCollapse();
        IslandBus.SetFileDrag(true);
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAcceptFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Island_DragLeave(object sender, DragEventArgs e)
    {
        if (!_dragHold) return;
        _dragHold = false;
        IslandBus.SetFileDrag(false);
        RestartAutoCollapse();
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanAcceptFiles(e)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        _dragHold = false;
        IslandBus.SetFileDrag(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && IslandBus.FileDropOverride?.Invoke(files) != true)
            IslandBus.DropFiles(files);
        RestartAutoCollapse();
    }

    // Клик по острову раскрывает его; если раскрытие закреплено уведомлением — «принимает» его и сворачивает.
    private void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_overlayActive) return; // карточка запроса закрывается только кнопками
        if (_compact)
        {
            // Нотч в полноэкранном видео ничего не раскрывает; клик только закрывает висящий знак (конец таймера, «готово» у рендера).
            if (ActivityCenter.MainCompact is { OnPillClick: { } compactClick } && ActivityCenter.MainCompact.DismissOnClick
                && ActivityHost.Content is FrameworkElement && compactClick(e.GetPosition(ActivityHost))) return; // свой участок знака («+5»)
            if (_activityDismissOnClick && ActivityAmount > 0.5 && ActivityCenter.MainCompact is { DismissOnClick: true } shown)
            {
                ActivityCenter.Remove(shown.Id);
                // У знака с собственным обработчиком (таймер, Claude) общее событие не нужно: оно закрыло бы чужой знак (рендер AE).
                if (shown.OnDismissed != null) shown.OnDismissed();
                else IslandBus.NotifyActivityDismissed();
            }
            return;
        }
        // «Готово» / «ошибка» на пассивном индикаторе висят до клика: клик закрывает их, а не раскрывает остров.
        if (_activityDismissOnClick && !IsExpanded && ActivityAmount > 0.5)
        {
            // Убираем плашку из центра активностей (иначе она вернулась бы при следующем обновлении), остров плавно сжимается.
            if ((_shrunk ? ActivityCenter.MainCompact : ActivityCenter.Main) is { DismissOnClick: true } main)
            {
                ActivityCenter.Remove(main.Id);
                if (main.OnDismissed != null) main.OnDismissed();
                else IslandBus.NotifyActivityDismissed();
            }
            else
            {
                ClearActivity();
                IslandBus.NotifyActivityDismissed();
            }
            return;
        }
        if (_pinned) IslandBus.Dismiss();
        else
        {
            // Активность может сама обработать клик по своему месту на плашке (стрелка списка сессий Claude): остров тогда не раскрывается.
            if (!IsExpanded && !_shrunk && !ActivityCenter.Widened && ActivityCenter.Flash == null && ActivityAmount > 0.5 && ActivityHost.Content != null
                && ActivityCenter.Main is { OnPillClick: { } pillClick } && pillClick(e.GetPosition(ActivityHost)))
                return;
            // Клик по плашке активности открывает вкладку этой активности (таймер, секундомер → «Время»). Кружки обрабатывает Bubble_MouseLeftButtonDown.
            var clicked = _shrunk ? ActivityCenter.MainCompact : ActivityCenter.Main;
            if (!IsExpanded && clicked?.ModuleId is { } moduleId) { SelectTab(moduleId); _keepTab = true; }
            Expand();
        }
    }

    // Пока мышь над островом, автосворачивание не запускается. Свёрнутый остров при наведении слегка «подпрыгивает».
    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _widenedCloseTimer.Stop();
        Bounce();
        if (!_compact && !_shrunk) ActivityCenter.Main?.OnHover?.Invoke(true);
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ActivityCenter.Widened) _widenedCloseTimer.Start();
        RestartAutoCollapse();
        ActivityCenter.Main?.OnHover?.Invoke(false);
    }

    // Баунс: пружина (отклонение масштаба от 1) получает толчок, остров чуть вырастает и возвращается с лёгким перелётом.
    // Затухание 0.55 и отклик 0.38 с дают один подскок и едва видимый провал (расчётом: при 0.027 пик ≈ +2,5 %, провал ≈ −0,3 %).
    private const double BounceAmount = 0.027;      // на сколько вырастает (по просьбе пользователя 2–3 %; расчётный пик ≈ 93 % от этого числа)
    private static readonly TimeSpan BounceCooldown = TimeSpan.FromMilliseconds(700);
    private readonly Spring _bounce = new(dampingRatio: 0.55, responseSeconds: 0.38);
    private DateTime _lastBounce = DateTime.MinValue;

    private void Bounce()
    {
        // Только свёрнутый остров (раскрытый под курсором не «дышит»); без движения, если в Windows выключены анимации.
        if (_compact || IsExpanded || Progress > 0.05 || !SystemParameters.ClientAreaAnimation) return;
        var now = DateTime.UtcNow;
        if (now - _lastBounce < BounceCooldown) return; // курсор дрожит на краю: не дёргать остров
        _lastBounce = now;
        _bounce.KickToPeak(BounceAmount);
    }

    // Клик вне острова (или любое другое окно получило фокус) сворачивает остров.
    // Исключение: открыт текст песни. Там остров закрывается только настоящим кликом мышью (М-011), а фокус мог уйти и сам
    // (всплыло чужое окно, Alt+Tab, Win): тогда остров остаётся, а клик мимо ловит опрос _outsideClickTimer.
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_pinned || _hold || _dragHold) return;
        if (IsExpanded && LyricsHoldsIsland && !AnyMouseButtonDown())
        {
            StartOutsideClickWatch();
            return;
        }
        Collapse();
    }

    private bool LyricsHoldsIsland => Modules.Player.PlayerState.LyricsOpen && _currentModuleId == "player";

    private readonly DispatcherTimer _outsideClickTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _clickWasDown;

    private void StartOutsideClickWatch()
    {
        _clickWasDown = AnyMouseButtonDown();
        if (!_outsideClickTimer.IsEnabled) _outsideClickTimer.Start();
    }

    // Остров с открытым текстом остался без фокуса: следим за кнопками мыши. Нажали вне острова: сворачиваем; вернулся фокус: опрос не нужен.
    private void PollOutsideClick()
    {
        if (!IsExpanded || !LyricsHoldsIsland || IsActive || _pinned || _hold || _dragHold) { _outsideClickTimer.Stop(); return; }
        var down = AnyMouseButtonDown();
        if (down && !_clickWasDown && GetCursorPos(out var p) && !CursorOverIsland(p))
        {
            _outsideClickTimer.Stop();
            Collapse();
        }
        _clickWasDown = down;
    }

    private bool CursorOverIsland(POINT p)
    {
        var topLeft = Island.PointToScreen(new Point(0, 0));
        var bottomRight = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
        return p.X >= topLeft.X && p.X <= bottomRight.X && p.Y >= topLeft.Y && p.Y <= bottomRight.Y;
    }

    private static bool AnyMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        // Окно-инструмент: не показывается в Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
    }

    // М-015: нотч иногда оказывался под другим окном (браузер, полноэкранное видео), пока не нажмёшь Alt+Tab: окна с флагом «поверх всех»
    // выстраиваются по тому, какое поднято последним. Раз в секунду (и при каждой смене программы в фокусе) поднимаем остров заново, не активируя его.
    private readonly DispatcherTimer _topTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void KeepOnTop()
    {
        if (!IsVisible || IsExpanded) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        KeepBlurBelow();
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // Нотч не должен активироваться от клика: фокус остаётся у видео (иначе полный экран сбросился бы).
    private void SetNoActivate(bool on)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, on ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
