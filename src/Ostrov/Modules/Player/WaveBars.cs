using System.Windows;
using System.Windows.Media;

namespace Ostrov.Modules.Player;

/// <summary>
/// Волна музыки: несколько столбиков, каждый отвечает за свою частотную полосу (низкие слева, высокие справа).
/// Уровни берёт из <see cref="MusicSpectrum"/>; быстро растёт и плавно опускается. Рисует кадры, только пока видна.
/// Нет звука — столбики остаются точками.
/// </summary>
internal sealed class WaveBars : FrameworkElement
{
    private readonly int _count;
    private readonly double _barWidth, _gap, _minHeight, _maxHeight;
    private readonly double[] _shown;
    private TimeSpan _lastFrame;
    private bool _attached;
    private Color _color = MusicAccent.Current; // показываемый цвет: плавно перетекает к акценту обложки новой песни

    public WaveBars(int count, double barWidth, double gap, double minHeight, double maxHeight)
    {
        _count = count;
        _barWidth = barWidth;
        _gap = gap;
        _minHeight = minHeight;
        _maxHeight = maxHeight;
        _shown = new double[count];
        Width = count * barWidth + (count - 1) * gap;
        Height = maxHeight;
        IsHitTestVisible = false;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Attach(); else Detach(); };
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _lastFrame = TimeSpan.Zero;
        _color = MusicAccent.Current;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    // Пять полос в нужное число столбиков (у кружка их три).
    private double Target(double[] levels, int i)
    {
        if (_count == levels.Length) return levels[i];
        return _count == 3
            ? i switch { 0 => Math.Max(levels[0], levels[1]), 1 => levels[2], _ => Math.Max(levels[3], levels[4]) }
            : levels[Math.Min(i * levels.Length / _count, levels.Length - 1)];
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == TimeSpan.Zero) { _lastFrame = now; return; }
        var dt = (now - _lastFrame).TotalSeconds;
        if (dt < 0.030) return; // не чаще ≈ 30 кадров в секунду: окно прозрачное, перерисовка программная, дорогая
        _lastFrame = now;
        dt = Math.Min(dt, 0.1);

        var levels = MusicSpectrum.Levels;
        var changed = false;
        // Цвет волны перетекает к акценту обложки (≈ 0,3 с).
        var accent = MusicAccent.Current;
        if (_color != accent)
        {
            var k = 1 - Math.Exp(-dt / 0.12);
            _color = Color.FromArgb(
                (byte)Math.Round(_color.A + (accent.A - _color.A) * k), (byte)Math.Round(_color.R + (accent.R - _color.R) * k),
                (byte)Math.Round(_color.G + (accent.G - _color.G) * k), (byte)Math.Round(_color.B + (accent.B - _color.B) * k));
            if (Math.Abs(_color.R - accent.R) + Math.Abs(_color.G - accent.G) + Math.Abs(_color.B - accent.B) + Math.Abs(_color.A - accent.A) < 3) _color = accent;
            changed = true;
        }
        for (var i = 0; i < _count; i++)
        {
            var target = Target(levels, i);
            // Быстрая атака (~35 мс) и плавный спад (~160 мс).
            var tau = target > _shown[i] ? 0.035 : 0.16;
            var next = _shown[i] + (target - _shown[i]) * (1 - Math.Exp(-dt / tau));
            if (Math.Abs(next - _shown[i]) > 0.002) changed = true;
            _shown[i] = next;
        }
        if (changed) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var cy = ActualHeight / 2;
        var brush = new SolidColorBrush(_color);
        for (var i = 0; i < _count; i++)
        {
            var h = _minHeight + (_maxHeight - _minHeight) * Math.Clamp(_shown[i], 0, 1);
            var x = i * (_barWidth + _gap);
            dc.DrawRoundedRectangle(brush, null, new Rect(x, cy - h / 2, _barWidth, h), _barWidth / 2, _barWidth / 2);
        }
    }
}
