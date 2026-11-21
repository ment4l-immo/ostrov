using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Modules;
using Ostrov.Modules.Weather;
using Ostrov.Services;

namespace Ostrov.Dictation;

/// <summary>
/// Волна голоса: столбики-«история» громкости, самый новый справа, старые уходят влево и бледнеют (как в записи голосовых).
/// Кадры считаются, только пока элемент на экране. Только белый.
/// </summary>
internal sealed class MicWave : FrameworkElement
{
    private const int Bars = 20;
    private const double BarWidth = 2.5, Gap = 2.5, BarMin = 3, BarMax = 18, StepSeconds = 0.045;

    private readonly double[] _history = new double[Bars];
    private float _target;
    private double _shown, _accumulator;
    private TimeSpan _last;
    private bool _attached;
    private static readonly Brush[] Shades = Enumerable.Range(0, Bars).Select(i =>
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(70 + 185 * i / (Bars - 1)), 255, 255, 255));
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    public MicWave()
    {
        Width = Bars * BarWidth + (Bars - 1) * Gap;
        Height = BarMax;
        IsHitTestVisible = false;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Attach(); else Detach(); };
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Свежая громкость 0..1 (можно звать с любого потока).</summary>
    public void SetLevel(float level) => _target = level;

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_last == TimeSpan.Zero) { _last = now; return; }
        var dt = Math.Min((now - _last).TotalSeconds, 0.1);
        _last = now;
        if (dt <= 0) return;

        // Быстрая атака, плавный спад: волна «дышит» вместе с голосом.
        var tau = _target > _shown ? 0.03 : 0.11;
        _shown += (_target - _shown) * (1 - Math.Exp(-dt / tau));

        _accumulator += dt;
        var pushed = false;
        while (_accumulator >= StepSeconds)
        {
            _accumulator -= StepSeconds;
            Array.Copy(_history, 1, _history, 0, Bars - 1);
            _history[Bars - 1] = _shown;
            pushed = true;
        }
        if (pushed) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var cy = ActualHeight / 2;
        for (var i = 0; i < Bars; i++)
        {
            var h = BarMin + (BarMax - BarMin) * Math.Clamp(_history[i], 0, 1);
            dc.DrawRoundedRectangle(Shades[i], null, new Rect(i * (BarWidth + Gap), cy - h / 2, BarWidth, h), BarWidth / 2, BarWidth / 2);
        }
    }
}

/// <summary>Состояния плашки диктовки.</summary>
internal enum DictationPhase { Listening, Finishing, Error }

/// <summary>
/// Плашка диктовки в свёрнутом острове: микрофон, подпись состояния и волна голоса. «Слушаю» — пока зажата клавиша, «Печатаю» — пока дописывается
/// последний кусок после отпускания, при ошибке вместо волны короткая причина серым. Только белый и серый.
/// </summary>
internal sealed class DictationPill : Grid
{
    public const double ViewWidth = 230, ViewHeight = 36;

    private readonly TextBlock _icon = new()
    {
        Text = "", FontFamily = Ui.IconFont, FontSize = 16, Foreground = Hues.Ink,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 3)
    };
    private readonly TextBlock _label = new()
    {
        FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(42, 0, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly MicWave _wave = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 3) };

    public DictationPill()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Children.Add(new DaylightGlow()); // тот же «свет дня», что на плашке пустого острова (под всем остальным)
        Children.Add(_icon);
        Children.Add(_label);
        Children.Add(_wave);
        SetPhase(DictationPhase.Listening);
    }

    public void SetLevel(float level) => _wave.SetLevel(level);

    public void SetPhase(DictationPhase phase, string? message = null)
    {
        switch (phase)
        {
            case DictationPhase.Listening:
                _label.Text = "Слушаю";
                _label.Foreground = Hues.Ink;
                _label.MaxWidth = 90;
                _icon.Foreground = Hues.Ink;
                _wave.Visibility = Visibility.Visible;
                break;
            case DictationPhase.Finishing:
                _label.Text = "Печатаю";
                _label.Foreground = Hues.Ink;
                _label.MaxWidth = 90;
                _icon.Foreground = Ui.Gray;
                _wave.Visibility = Visibility.Visible;
                _wave.SetLevel(0);
                break;
            default:
                _label.Text = message ?? "Диктовка не удалась";
                _label.Foreground = Ui.Gray;
                _label.MaxWidth = ViewWidth - 42 - 14;
                _icon.Foreground = Ui.Gray;
                _wave.Visibility = Visibility.Collapsed;
                break;
        }
    }
}
