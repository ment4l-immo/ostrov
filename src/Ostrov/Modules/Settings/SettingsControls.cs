using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ostrov.Services;

namespace Ostrov.Modules.Settings;

/// <summary>Переключатель вкл/выкл в духе Apple, но только в белом и сером: включённый белый с чёрной кнопкой, выключенный тёмно-серый.</summary>
internal sealed class ToggleSwitch : Border
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush OnTrack = Frozen(Color.FromRgb(0xF0, 0xF0, 0xF0)), OffTrack = Frozen(Color.FromArgb(Hues.GlassAlpha, 0x3A, 0x3A, 0x3A));
    private static readonly Brush OnKnob = Frozen(Color.FromRgb(0x10, 0x10, 0x10)), OffKnob = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));

    private const double TrackWidth = 46, TrackHeight = 26, KnobSize = 20, Pad = 3;

    private readonly Border _knob;
    private readonly TranslateTransform _shift = new();
    private bool _on;

    public event Action<bool>? Toggled;

    public ToggleSwitch()
    {
        Width = TrackWidth;
        Height = TrackHeight;
        CornerRadius = new CornerRadius(TrackHeight / 2);
        Cursor = Cursors.Hand;
        _knob = new Border
        {
            Width = KnobSize, Height = KnobSize, CornerRadius = new CornerRadius(KnobSize / 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Pad, 0, 0, 0), RenderTransform = _shift
        };
        Child = _knob;
        MouseLeftButtonUp += (_, _) => Set(!_on, notify: true, animate: true);
        Paint(animate: false);
    }

    public bool IsOn => _on;

    public void Set(bool on, bool notify = false, bool animate = false)
    {
        if (_on == on) { Paint(animate: false); return; }
        _on = on;
        Paint(animate);
        if (notify) Toggled?.Invoke(on);
    }

    private void Paint(bool animate)
    {
        Background = _on ? OnTrack : OffTrack;
        _knob.Background = _on ? OnKnob : OffKnob;
        var target = _on ? TrackWidth - KnobSize - 2 * Pad : 0;
        if (animate)
            _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(160)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.X = target;
        }
    }
}

/// <summary>Выбор одного из нескольких вариантов: таблетка с кнопками, выбранная светлее (как переключатель «Избранные | Недавние»).</summary>
internal sealed class SegmentedControl : Border
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Selected = Frozen(Color.FromArgb(Hues.GlassAlpha, 0x40, 0x40, 0x40));

    private readonly List<Button> _buttons = [];
    private int _selected = -1;

    public event Action<int>? Changed;

    public SegmentedControl(params string[] labels)
    {
        CornerRadius = new CornerRadius(15);
        Background = Frozen(Hues.GlassSurfaceColor);
        Padding = new Thickness(2);
        HorizontalAlignment = HorizontalAlignment.Right;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = Ui.Pill(labels[i], padding: 12);
            button.Margin = new Thickness(0);
            button.Height = 26;
            button.Background = Brushes.Transparent;
            button.Click += (_, _) => { Select(index); Changed?.Invoke(index); };
            _buttons.Add(button);
            panel.Children.Add(button);
        }
        Child = panel;
        PillSlider.Attach(this);
    }

    /// <summary>Выбранный вариант; −1 — ни один (значение вне списка).</summary>
    public void Select(int index)
    {
        _selected = index;
        for (var i = 0; i < _buttons.Count; i++) Ui.MarkSelected(_buttons[i], i == index);
    }

    public int SelectedIndex => _selected;
}
