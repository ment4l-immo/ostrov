using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Player;

/// <summary>
/// Громкость Apple Music: значок динамика (клик выключает и включает звук у неё) и тонкий ползунок (клик, перетаскивание, колесо мыши).
/// Видим, только пока у Apple Music есть аудиосеансы; иначе место остаётся пустым. Общую громкость Windows не трогает.
/// Подхватывает изменения из «Микшера громкости» (опрос раз в 1,5 с, пока вкладка открыта).
/// </summary>
internal sealed class VolumeControl : StackPanel
{
    public const double TrackWidth = 64;

    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush Dim = new SolidColorBrush(Hues.SurfaceHighColor);
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly Border _fill = new() { Height = 4, CornerRadius = new CornerRadius(2), Background = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _hit = new() { Width = TrackWidth, Height = 22, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly TextBlock _icon = new()
    {
        FontFamily = Icons, FontSize = 16, Foreground = Gray, Width = 22, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border _iconHit = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand, Width = 26, Height = 26 };

    private double _level = 1;
    private bool _muted, _dragging, _polling;

    /// <summary>Элементы, цвет которых подстраивается под фон (см. AdaptiveInk).</summary>
    public FrameworkElement IconElement => _icon;
    public Border FillElement => _fill;

    public VolumeControl()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Hidden; // пока неизвестно, запущена ли Apple Music

        _iconHit.Child = _icon;
        _iconHit.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var next = !_muted;
            _muted = next;
            Render();
            _ = Task.Run(() => AppleMusicVolume.TrySetMuted(next));
        };

        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Dim, VerticalAlignment = VerticalAlignment.Center };
        _hit.Children.Add(track);
        _hit.Children.Add(_fill);
        _hit.MouseLeftButtonDown += (_, e) =>
        {
            _hit.CaptureMouse();
            _dragging = true;
            SetFromPointer(e.GetPosition(_hit).X);
            e.Handled = true;
        };
        _hit.MouseMove += (_, e) => { if (_dragging) SetFromPointer(e.GetPosition(_hit).X); };
        _hit.MouseLeftButtonUp += (_, _) => { _dragging = false; _hit.ReleaseMouseCapture(); };
        _hit.MouseWheel += (_, e) =>
        {
            Apply(_level + (e.Delta > 0 ? 0.05 : -0.05));
            e.Handled = true;
        };

        Children.Add(_iconHit);
        Children.Add(_hit);
        Render();

        _poll.Tick += (_, _) => Poll();
        Loaded += (_, _) => { _poll.Start(); Poll(); };
        Unloaded += (_, _) => _poll.Stop();
    }

    private void SetFromPointer(double x) => Apply(x / TrackWidth);

    private void Apply(double level)
    {
        _level = Math.Clamp(level, 0, 1);
        if (_muted && _level > 0) _muted = false; // сдвинули ползунок — звук включается
        Render();
        var value = _level;
        var unmute = !_muted;
        _ = Task.Run(() => { AppleMusicVolume.TrySetLevel(value); if (unmute) AppleMusicVolume.TrySetMuted(false); });
    }

    private void Render()
    {
        _fill.Width = TrackWidth * (_muted ? 0 : _level);
        _icon.Text = _muted || _level <= 0.001 ? "" /* Mute */ : "" /* Volume */;
    }

    private async void Poll()
    {
        if (_dragging || _polling) return;
        _polling = true;
        try
        {
            var (ok, level, muted) = await Task.Run(() =>
            {
                var found = AppleMusicVolume.TryGet(out var l, out var m);
                return (found, l, m);
            });
            if (_dragging) return;
            Visibility = ok ? Visibility.Visible : Visibility.Hidden;
            if (ok)
            {
                _level = level;
                _muted = muted;
                Render();
            }
        }
        finally
        {
            _polling = false;
        }
    }
}
