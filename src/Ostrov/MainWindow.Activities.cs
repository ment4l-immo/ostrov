using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov;

// Пассивные активности свёрнутого острова: главная плашка (ActivityHost, см. ShowActivity) и два кружка по бокам (как у Apple):
// справа вторая активность, слева третья. Что показывать, решает ActivityCenter (приоритеты и выбор пользователя); здесь только отрисовка.
// Клик по кружку делает его главным, правый клик по плашке расширяет её (М-016, М-017).
public partial class MainWindow
{
    private const double BubbleGap = 8; // зазор между островом и кружком

    // 0 — кружка нет, 1 — кружок на месте.
    public static readonly DependencyProperty BubbleAmountProperty = DependencyProperty.Register(
        nameof(BubbleAmount), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double BubbleAmount
    {
        get => (double)GetValue(BubbleAmountProperty);
        set => SetValue(BubbleAmountProperty, value);
    }

    public static readonly DependencyProperty BubbleLeftAmountProperty = DependencyProperty.Register(
        nameof(BubbleLeftAmount), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double BubbleLeftAmount
    {
        get => (double)GetValue(BubbleLeftAmountProperty);
        set => SetValue(BubbleLeftAmountProperty, value);
    }

    // Обмен местами (клик по кружку; решения пользователя 2026-11-14/15: быстрее, без растяжения самих фигур, но с перемычкой «капель»):
    // кружок едет к острову, между ними вырастает перемычка, и они сливаются (сам кружок и плашка гаснут), на слиянии меняется содержимое;
    // затем из острова с той же стороны быстро выезжает новый кружок, а перемычка сужается до разрыва. 0 — старт, 1 — всё на местах.
    public static readonly DependencyProperty DropSwapProperty = DependencyProperty.Register(
        nameof(DropSwap), typeof(double), typeof(MainWindow),
        new PropertyMetadata(1.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double DropSwap
    {
        get => (double)GetValue(DropSwapProperty);
        set => SetValue(DropSwapProperty, value);
    }

    private const int DropDuration = 450, DropSwapAtMs = 180; // содержимое меняется на 40 % хода: старое уже погасло
    private const double DropSplit = 0.4, DropSlide = 44;

    private int _dropPending;  // сторона кружка, по которому кликнули; ждёт ближайшего ApplyActivities
    private int _dropSide;     // сторона кружка, с которым идёт обмен прямо сейчас (0 — обмена нет)
    private double _dropFromX; // где был кружок (смещение от центра острова)

    private void StartDrop(int side)
    {
        _dropSide = side;
        BeginAnimation(DropSwapProperty, null);
        DropSwap = 0;
        var anim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(DropDuration)))
        {
        };
        anim.Completed += (_, _) => { _dropSide = 0; DropSwap = 1; BeginAnimation(DropSwapProperty, null); ApplyProgress(); };
        BeginAnimation(DropSwapProperty, anim);
    }

    // Перемычка между островом и кружком: две кривые образуют «талию», которая сначала тонкая нить, к слиянию толстеет до диаметра кружка
    // и после смены содержимого сужается до разрыва. Конец закруглён по кружку, поэтому, пока сам кружок погас, форма остаётся целой.
    private void UpdateDropNeck()
    {
        if (_dropSide == 0 || Slots.FirstOrDefault(s => s.Side == _dropSide) is not { } slot) { DropNeck.Visibility = Visibility.Collapsed; return; }
        var b = Math.Sin(Math.PI * Math.Clamp(DropSwap, 0, 1));
        var diameter = ActivityCenter.BubbleSize * (0.6 + 0.4 * (double)GetValue(slot.Amount));
        var thickness = diameter * 0.78 * Math.Clamp(b * 1.7, 0, 1); // тоньше кружка: перемычка не должна выходить за габариты острова и кружка
        if (thickness < 0.6) { DropNeck.Visibility = Visibility.Collapsed; return; }

        var cxWindow = ActualWidth / 2;
        var cy = 1.5 + ActivityCenter.BubbleSize / 2;               // линия центров кружка
        var x0 = cxWindow + _dropSide * (Island.Width / 2 - 8);     // чуть внутри острова
        var x1 = cxWindow + slot.Shift.X;                           // центр кружка
        var h0 = Math.Min(thickness * 1.1, Math.Max(8, Island.Height - 12));
        var h1 = thickness;
        var waist = thickness * (0.22 + 0.78 * Math.Clamp(b, 0, 1)) / 2; // половина толщины в самом тонком месте
        var midX = (x0 + x1) / 2;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x0, cy - h0 / 2), true, true);
            ctx.BezierTo(new Point(midX, cy - waist), new Point(midX, cy - waist), new Point(x1, cy - h1 / 2), true, false);
            // Закруглённый конец по форме кружка: у правого кружка обходим по часовой, у левого против.
            ctx.ArcTo(new Point(x1, cy + h1 / 2), new Size(h1 / 2, h1 / 2), 0, false,
                _dropSide > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            ctx.BezierTo(new Point(midX, cy + waist), new Point(midX, cy + waist), new Point(x0, cy + h0 / 2), true, false);
        }
        g.Freeze();
        DropNeck.Data = g;
        DropNeck.Visibility = Visibility.Visible;
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    // Главная плашка в обмене: старая уезжает в сторону кружка и гаснет, новая выезжает оттуда же и проявляется.
    private void ApplyDropToMain()
    {
        ActivityScale.ScaleX = ActivityScale.ScaleY = 1;
        if (_dropSide == 0) { ActivityShift.X = 0; return; }
        var sw = Math.Clamp(DropSwap, 0, 1);
        if (sw < DropSplit)
        {
            var t = sw / DropSplit;
            ActivityShift.X = _dropSide * DropSlide * t * t;
        }
        else
        {
            ActivityShift.X = _dropSide * DropSlide * (1 - EaseOut((sw - DropSplit) / (1 - DropSplit)));
        }
    }

    // Прозрачность главной плашки в обмене: до середины гаснет, дальше проявляется (1 — вне обмена).
    private double DropMainFade()
    {
        if (_dropSide == 0) return 1;
        var sw = Math.Clamp(DropSwap, 0, 1);
        return sw < DropSplit ? 1 - sw / DropSplit : EaseOut((sw - DropSplit) / (1 - DropSplit));
    }

    // Кружок: рамка, место для содержимого, масштаб и сдвиг от центра (Side: +1 справа, −1 слева), своя «степень появления» и то, что в нём сейчас должно быть.
    private sealed class BubbleSlot
    {
        public required Border Root { get; init; }
        public required ContentControl Host { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TranslateTransform Shift { get; init; }
        public required DependencyProperty Amount { get; init; }
        public required int Side { get; init; }
        public FrameworkElement? Desired { get; set; }
        public string? EntryId { get; set; }
    }

    private BubbleSlot[]? _bubbleSlots;

    private BubbleSlot[] Slots => _bubbleSlots ??=
    [
        new BubbleSlot { Root = Bubble, Host = BubbleHost, Scale = BubbleScale, Shift = BubbleShift, Amount = BubbleAmountProperty, Side = 1 },
        new BubbleSlot { Root = BubbleLeft, Host = BubbleLeftHost, Scale = BubbleLeftScale, Shift = BubbleLeftShift, Amount = BubbleLeftAmountProperty, Side = -1 }
    ];

    // 0 — обычный свёрнутый остров (200×36), 1 — сжат в нотч (150×14): в браузере, пока он в фокусе. Плавно, 260 мс.
    public static readonly DependencyProperty ShrinkAmountProperty = DependencyProperty.Register(
        nameof(ShrinkAmount), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).ApplyProgress()));

    public double ShrinkAmount
    {
        get => (double)GetValue(ShrinkAmountProperty);
        set => SetValue(ShrinkAmountProperty, value);
    }

    // Переключить вкладку по Id модуля (в основной полосе и в полосе «второго плана»).
    private void SelectTab(string moduleId)
    {
        foreach (var bar in new[] { TabBar, SideTabBar })
            foreach (RadioButton tab in bar.Children)
                if (tab.Tag is IIslandModule m && m.Id == moduleId) tab.IsChecked = true;
    }

    private void ApplyActivities()
    {
        // Обмен каплями: сначала кружок тянется к острову и они сливаются; содержимое меняем, когда форма уже единая.
        if (_dropPending != 0 && !(_compact || _shrunk))
        {
            StartDrop(_dropPending);
            _dropPending = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DropSwapAtMs) };
            timer.Tick += (_, _) => { timer.Stop(); ApplyActivitiesNow(); };
            timer.Start();
            return;
        }
        ApplyActivitiesNow();
    }

    private void ApplyActivitiesNow()
    {
        // В полноэкранном видео вместо плашки показывается нотч (Compact) самой важной активности, у которой он есть; кружков нет.
        // То же в браузере (_shrunk): там нотч, но его можно раскрыть кликом.
        var notch = _compact || _shrunk;
        var main = notch ? ActivityCenter.MainCompact : ActivityCenter.Main;
        // Вспышка (язык, Caps Lock, громкость…) показывается только свёрнутым островом: в нотче, играх, видео и пока остров раскрыт её нет (М-020).
        // Не перекрывает важное: идущую диктовку (130) и обратный отсчёт сна/выключения (120).
        var flash = !notch && !IsExpanded && (ActivityCenter.Main?.Priority ?? 0) < 120 ? ActivityCenter.Flash : null;
        if (flash != null) ShowActivity(flash.View, flash.Width, flash.Height, false);
        else if (main is null) ClearActivity();
        else if (notch) ShowActivity(main.Compact!, main.CompactWidth, main.CompactHeight, main.DismissOnClick);
        else if (ActivityCenter.Widened && main.Wide != null) ShowActivity(main.Wide, main.WideWidth, main.WideHeight, main.DismissOnClick);
        else ShowActivity(main.Pill, main.PillWidth, main.PillHeight, main.DismissOnClick);
        ApplyBubble(Slots[0], notch ? null : ActivityCenter.Secondary);
        ApplyBubble(Slots[1], notch ? null : ActivityCenter.Tertiary);
        if (_compact) UpdateVisibility(); // нотч нужен, пока есть что показать; иначе окно прячется
        if (!ActivityCenter.Widened) _widenedCloseTimer.Stop();
    }

    private void ApplyBubble(BubbleSlot slot, ActivityEntry? entry)
    {
        var view = entry?.Bubble;
        slot.EntryId = entry?.Id;
        slot.Desired = view;
        if (view is null)
        {
            if (slot.Host.Content is null) return;
            var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) =>
            {
                // Если за это время кружку нашли новое содержимое, не трогаем его.
                if ((double)GetValue(slot.Amount) <= 0.001 && slot.Desired is null) slot.Host.Content = null;
            };
            BeginAnimation(slot.Amount, fade);
            return;
        }

        // Вид мог стоять в соседнем кружке (кружки поменялись местами): один и тот же элемент нельзя показывать в двух местах.
        foreach (var other in Slots)
            if (!ReferenceEquals(other, slot) && ReferenceEquals(other.Host.Content, view)) other.Host.Content = null;
        var sameView = ReferenceEquals(slot.Host.Content, view);
        slot.Host.Content = view;
        if (sameView && (double)GetValue(slot.Amount) > 0.999) return;
        BeginAnimation(slot.Amount, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // Положение, прозрачность и размер кружков; вызывается из ApplyProgress. Кружки гаснут в самом начале раскрытия острова.
    private void UpdateBubble(double progress)
    {
        foreach (var slot in Slots)
        {
            var a = (double)GetValue(slot.Amount);
            slot.Root.Visibility = a > 0.001 && slot.Host.Content != null ? Visibility.Visible : Visibility.Collapsed;
            slot.Root.Opacity = a * NightInk * (1 - Math.Clamp(progress / 0.3, 0, 1));
            slot.Root.IsHitTestVisible = progress < 0.05 && a > 0.5; // невидимый кружок не должен ловить клики рядом с раскрытым островом
            slot.Scale.ScaleX = slot.Scale.ScaleY = 0.6 + 0.4 * a;
            var baseX = slot.Side * ((Island.Width + ActivityCenter.BubbleSize) / 2 + BubbleGap);
            slot.Shift.X = baseX;
            if (_dropSide == slot.Side)
            {
                // Кружок уезжает к острову и гаснет, потом проявляется на своём месте, выезжая из-под острова.
                var sw = Math.Clamp(DropSwap, 0, 1);
                var touch = slot.Side * (Island.Width / 2 + 4);
                if (sw < DropSplit)
                {
                    var t = sw / DropSplit;
                    slot.Shift.X = baseX + (touch - baseX) * t * t;
                    slot.Root.Opacity *= 1 - t;
                }
                else
                {
                    var e = EaseOut((sw - DropSplit) / (1 - DropSplit));
                    slot.Shift.X = touch + (baseX - touch) * e;
                    slot.Root.Opacity *= e;
                }
            }
        }
        UpdateDropNeck();
    }

    // Клик по кружку: активность становится главной, прежняя главная встаёт на её место (М-016).
    private void Bubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_overlayActive || _compact || _shrunk || IsExpanded) return;
        var slot = Slots.FirstOrDefault(s => ReferenceEquals(s.Root, sender));
        if (slot?.EntryId is not { } id) return;
        e.Handled = true;
        _dropFromX = slot.Side * ((Island.Width + ActivityCenter.BubbleSize) / 2 + BubbleGap);
        _dropPending = slot.Side;
        ActivityCenter.Promote(id);
        // Если обмена не получилось (кружок уже главный), отложенный запрос не должен дожить до чужого обновления.
        Dispatcher.BeginInvoke(() => _dropPending = 0, System.Windows.Threading.DispatcherPriority.Background);
    }

    // Правый клик по плашке: расширенный вид (то же, но полнее); повторный правый клик или уход курсора возвращают обычную (М-017).
    private void Island_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_overlayActive || _compact || _shrunk || IsExpanded || ActivityAmount < 0.5) return;
        if (ActivityCenter.Main?.Wide == null || ActivityCenter.Flash != null) return;
        e.Handled = true;
        ActivityCenter.SetWidened(!ActivityCenter.Widened);
    }

    // Курсор ушёл с расширенной плашки: через полсекунды она плавно возвращается (если курсор не вернулся).
    private readonly System.Windows.Threading.DispatcherTimer _widenedCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void InitWidened()
    {
        _widenedCloseTimer.Tick += (_, _) =>
        {
            _widenedCloseTimer.Stop();
            if (!Island.IsMouseOver) ActivityCenter.SetWidened(false);
        };
    }
}
