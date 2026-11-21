using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov;

// Полоса вкладок может скрываться по просьбе вкладки (режим текста песни в плеере): вкладка получает всю высоту.
public partial class MainWindow
{
    private bool _tabsHiddenByModule;
    private bool _suspendAutoCollapse;
    private readonly DispatcherTimer _tabsReturn = new() { Interval = TimeSpan.FromMilliseconds(260) };

    private void InitTabsToggle()
    {
        _tabsReturn.Tick += (_, _) => { _tabsReturn.Stop(); RestoreTabs(); };
        ActivityCenter.Changed += () => Dispatcher.BeginInvoke(UpdateTabMarks);
        IslandBus.TabsVisibleChanged += visible => Dispatcher.BeginInvoke(() => SetTabsVisible(visible));
        IslandBus.AutoCollapseSuspendedChanged += on => Dispatcher.BeginInvoke(() => { _suspendAutoCollapse = on; RestartAutoCollapse(); });
    }

    // Метки под значками вкладок: идёт таймер или секундомер («Время», оранжевая), играет музыка («Плеер», белая).
    // На открытой вкладке метки нет (там и так всё видно). Появляется и гаснет плавно.
    private static readonly Dictionary<string, System.Windows.Media.Brush> MarkBrushes = new()
    {
        ["time"] = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A))),
        ["player"] = Freeze(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White)),
    };

    private static System.Windows.Media.Brush Freeze(System.Windows.Media.Brush b) { b.Freeze(); return b; }

    private void UpdateTabMarks()
    {
        foreach (var panel in new[] { TabBar, SideTabBar })
            foreach (System.Windows.Controls.RadioButton tab in panel.Children)
            {
                if (tab.Tag is not IIslandModule m || !MarkBrushes.TryGetValue(m.Id, out var brush)) continue;
                var want = ActivityCenter.HasForModule(m.Id) && tab.IsChecked != true ? 1.0 : 0.0;
                if (want > 0) TabMarks.SetBrush(tab, brush);
                if (Math.Abs(TabMarks.GetAmount(tab) - want) < 0.01) continue;
                tab.BeginAnimation(TabMarks.AmountProperty, new DoubleAnimation(want, new Duration(TimeSpan.FromMilliseconds(220)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
    }

    // Шапка. Выбранная вкладка: под значком проявляется пластина, и рядом выезжает подпись (окошко подписи растёт от 0 до ширины текста);
    // остальные вкладки при этом плавно сдвигаются, потому что окошко подписи меняет ширину самой кнопки. Старая и новая вкладки движутся одним ходом,
    // поэтому ширина полосы в целом почти не меняется. Над содержимым вкладок шапка занимает HeaderExtra лишних пикселей к высоте вкладки.
    private const double HeaderExtra = -2;
    private readonly Dictionary<System.Windows.Controls.RadioButton, double> _labelWidths = [];
    private readonly DispatcherTimer _clockTimer = new();

    private void InitTabHeader()
    {
        IslandBus.Expanding += () => Dispatcher.BeginInvoke(PlayTabLift);
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
    }

    // Часы в шапке: показывают минуты, обновляются в начале каждой минуты.
    private void UpdateClock()
    {
        var now = DateTime.Now;
        HeaderClock.Text = now.ToString("HH:mm");
        _clockTimer.Stop();
        _clockTimer.Interval = TimeSpan.FromMilliseconds(60000 - (now.Second * 1000 + now.Millisecond) + 40);
        _clockTimer.Start();
    }

    private double MeasureLabel(string text)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
            14, Hues.Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 17; // справа запас до края пластины: вместе с левой частью (значок 32) даёт отступы 10 / 7 / 16 как в образце
    }

    // Открывается мягко (подпись выезжает), закрывается быстро: старая вкладка успевает свернуться, пока новая раскрывается.
    private static DoubleAnimation TabAnim(double to, bool opening, double delayMs = 0, double? openMs = null) => new(to, new Duration(TimeSpan.FromMilliseconds(opening ? openMs ?? 380 : 160)))
    {
        BeginTime = TimeSpan.FromMilliseconds(delayMs),
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
    };

    private IEnumerable<System.Windows.Controls.RadioButton> AllTabs()
    {
        foreach (var panel in new[] { TabBar, SideTabBar })
            foreach (System.Windows.Controls.RadioButton tab in panel.Children)
                yield return tab;
    }

    // Подпись и пластина у выбранной вкладки; у остальных скрыты.
    private void UpdateTabPoses(bool animate)
    {
        foreach (var tab in AllTabs())
        {
            var selected = tab.IsChecked == true;
            var width = selected && _labelWidths.TryGetValue(tab, out var w) ? w : 0;
            var amount = selected ? 1.0 : 0.0;
            tab.BeginAnimation(TabMarks.LabelWidthProperty, null);
            tab.BeginAnimation(TabMarks.SelectProperty, null);
            if (animate)
            {
                tab.BeginAnimation(TabMarks.LabelWidthProperty, TabAnim(width, selected));
                tab.BeginAnimation(TabMarks.SelectProperty, TabAnim(amount, selected, 0, 240));
            }
            else
            {
                TabMarks.SetLabelWidth(tab, width);
                TabMarks.SetSelect(tab, amount);
            }
        }
    }

    // При каждом раскрытии острова пластина и подпись выбранной вкладки выезжают заново, когда содержимое уже начало проявляться.
    private void PlayTabLift()
    {
        foreach (var tab in AllTabs())
        {
            tab.BeginAnimation(TabMarks.LabelWidthProperty, null);
            tab.BeginAnimation(TabMarks.SelectProperty, null);
            TabMarks.SetLabelWidth(tab, 0);
            TabMarks.SetSelect(tab, 0);
            if (tab.IsChecked != true || !_labelWidths.TryGetValue(tab, out var w)) continue;
            tab.BeginAnimation(TabMarks.LabelWidthProperty, TabAnim(w, true, 140));
            tab.BeginAnimation(TabMarks.SelectProperty, TabAnim(1, true, 140, 240));
        }
    }

    // Положение вкладки-содержимого: обычное (под шапкой) или на всю высоту, когда шапку скрыл сам режим (текст песни).
    private bool _hostTall;
    private const double HeaderRowHeight = 50, TallHostTop = 18; // высота шапки с полями и верхнее поле вкладки, когда шапки нет

    private void ApplyHostLayout(bool tabsHidden)
    {
        // Вкладка при смене раскладки перескакивает на высоту шапки (≈ 32 px): гасим скачок плавным сдвигом, чтобы всё содержимое (например, обложка
        // плеера, которая как раз переезжает на своё место) шло одним плавным движением, а не «телепортировалось».
        var changed = tabsHidden != _hostTall;
        _hostTall = tabsHidden;
        foreach (FrameworkElement e in new FrameworkElement[] { ModuleHost, ModuleGhost })
        {
            e.SetValue(Grid.RowProperty, tabsHidden ? 0 : 1);
            e.SetValue(Grid.RowSpanProperty, tabsHidden ? 2 : 1);
            e.Margin = tabsHidden ? new Thickness(24, 18, 24, 0) : new Thickness(24, 0, 24, 14);
        }
        if (!changed) return;
        var jump = HeaderRowHeight - TallHostTop; // вкладка ушла вверх (tabsHidden) или вниз (шапка вернулась) на эту величину
        ModuleShift.BeginAnimation(TranslateTransform.YProperty, null);
        ModuleShift.Y = tabsHidden ? jump : -jump;
        ModuleShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(380)))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void SetTabsVisible(bool visible)
    {
        _tabsReturn.Stop();
        if (!visible)
        {
            _tabsHiddenByModule = true;
            ApplyHostLayout(true); // вкладка занимает и место шапки
            var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(160)));
            fade.Completed += (_, _) => { if (_tabsHiddenByModule) TabStrip.Visibility = Visibility.Hidden; };
            TabStrip.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            _tabsHiddenByModule = false;
            // Шапка теперь сверху: вкладка опускается на её высоту сразу, плавным сдвигом (раньше ждали 260 мс, и содержимое прыгало вниз).
            RestoreTabs();
        }
    }

    private void RestoreTabs()
    {
        if (_tabsHiddenByModule) return;
        ApplyHostLayout(false);
        if (_overlayActive) return; // под карточкой запроса полоса остаётся скрытой, вернётся вместе с вкладками
        TabStrip.Visibility = Visibility.Visible;
        TabStrip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
