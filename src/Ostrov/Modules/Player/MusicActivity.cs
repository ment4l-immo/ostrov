using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ostrov.Services;
using Windows.Media.Control;

namespace Ostrov.Modules.Player;

/// <summary>
/// Музыка как пассивная активность свёрнутого острова: слева мини-обложка, справа волна по частотам настоящего звука Apple Music.
/// Работает независимо от вкладки «Плеер». Показывается, пока Apple Music играет (после паузы держится ещё 2 с, чтобы смена трека не мигала).
/// Только сессия Apple Music: остальные источники звука здесь не показываются. Приоритет ниже таймера и секундомера.
/// Звук слушается только у процесса Apple Music (<see cref="ProcessLoopbackCapture"/>), нигде не сохраняется и в сеть не уходит.
/// </summary>
internal sealed class MusicActivity
{
    private const string ActivityId = "music";
    private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    // Что сейчас играет в Apple Music (для журнала дня): название и исполнитель, пока идёт воспроизведение.
    private static volatile string? s_title, s_artist;
    private static volatile bool s_playing;

    /// <summary>Играющий сейчас трек Apple Music или null.</summary>
    /// <summary>Текущий трек Apple Music (даже на паузе), обложка и состояние: для плитки плеера в хабе. Меняется на потоке интерфейса, о смене сообщает <see cref="NowChanged"/>.</summary>
    public static (string Title, string Artist)? Track => !string.IsNullOrEmpty(s_title) ? (s_title!, s_artist ?? "") : null;
    public static ImageSource? Cover { get; private set; }
    public static bool IsPlaying => s_playing;
    public static bool HasSession { get; private set; }
    public static event Action? NowChanged;

    public static (string Title, string Artist)? NowPlaying => s_playing && !string.IsNullOrEmpty(s_title) ? (s_title!, s_artist ?? "") : null;

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly MusicPill _pill = new();
    private readonly MusicBubble _bubble = new();
    private readonly MusicWideView _wide = new(); // расширенный вид по правому клику
    private readonly ProcessLoopbackCapture _capture = new();
    private readonly SpectrumAnalyzer _analyzer = new();
    private readonly DispatcherTimer _graceTimer;
    private readonly DispatcherTimer _retryTimer;
    private readonly string? _debugLog = Environment.GetEnvironmentVariable("OSTROV_DEBUG_AUDIO") == "1"
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "audio-debug.log")
        : null;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _shown;
    private int _coverVersion;
    private long _lastAnalysis;

    public MusicActivity()
    {
        _graceTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = PauseGrace };
        _graceTimer.Tick += (_, _) => { _graceTimer.Stop(); Hide(); };
        _retryTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = RetryEvery };
        _retryTimer.Tick += (_, _) => EnsureCapture();
        _capture.Block += OnAudioBlock;
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            _ = _ui.BeginInvoke(() =>
            {
                _manager = manager;
                manager.SessionsChanged += (_, _) => _ui.BeginInvoke(PickSession);
                manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(PickSession);
                PickSession();
            });
        }
        catch
        {
            // Системные медиа-функции недоступны: мини-острова музыки просто не будет.
        }
    }

    private void PickSession()
    {
        if (_manager == null) return;
        var next = _manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase));
        if (next?.SourceAppUserModelId == _session?.SourceAppUserModelId && (_session != null || next == null)) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }
        _session = next;
        HasSession = next != null;
        if (next == null) { s_title = s_artist = null; Cover = null; }
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            RefreshCover();
        }
        RefreshPlayback();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, object a) => _ui.BeginInvoke(RefreshCover);
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, object a) => _ui.BeginInvoke(RefreshPlayback);

    private void RefreshPlayback()
    {
        var playing = _session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        s_playing = playing;
        NowChanged?.Invoke();
        if (playing)
        {
            _graceTimer.Stop();
            if (!_shown) Show();
            EnsureCapture();
        }
        else if (_shown && !_graceTimer.IsEnabled)
        {
            _graceTimer.Start();
        }
    }

    private async void RefreshCover()
    {
        var session = _session;
        if (session == null) return;
        var version = ++_coverVersion;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            ImageSource? image = null;
            if (props.Thumbnail != null)
            {
                using var raw = await props.Thumbnail.OpenReadAsync();
                using var stream = raw.AsStreamForRead();
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 64;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            if (version != _coverVersion) return;
            // Apple Music кладёт в поле «исполнитель» строку «Исполнитель — Альбом»: ключ трека их разделяет.
            var key = Lyrics.TrackKey.FromMedia(props.Title ?? "", props.Artist ?? "", props.AlbumTitle ?? "", TimeSpan.Zero);
            _wide.SetTrack(key.Title, key.Artist);
            s_title = key.Title; s_artist = key.Artist;
            Cover = image;
            NowChanged?.Invoke();
            MusicAccent.Set(image);
            _pill.SetCover(image);
            _bubble.SetCover(image);
            _wide.SetCover(image);
        }
        catch
        {
            if (version == _coverVersion) { Cover = null; NowChanged?.Invoke(); MusicAccent.Set(null); _pill.SetCover(null); _bubble.SetCover(null); _wide.SetCover(null); }
        }
    }

    private void Show()
    {
        _shown = true;
        ActivityCenter.Set(new ActivityEntry
        {
            Id = ActivityId, Priority = 10, Pill = _pill, PillWidth = MusicPill.ViewWidth, PillHeight = MusicPill.ViewHeight,
            Bubble = _bubble, ModuleId = "player",
            Wide = _wide, WideWidth = MusicWideView.ViewWidth, WideHeight = MusicWideView.ViewHeight
        });
        _retryTimer.Start();
    }

    private void Hide()
    {
        _shown = false;
        _retryTimer.Stop();
        _capture.Stop();
        MusicSpectrum.Reset();
        ActivityCenter.Remove(ActivityId);
    }

    // ---------- звук Apple Music ----------

    // Захват живёт, пока музыка играет; если Apple Music перезапустили или захват упал, пробуем снова (раз в 5 с и при смене состояния).
    private void EnsureCapture()
    {
        if (!_shown) return;
        var pid = ProcessLoopbackCapture.FindAppleMusicAudio();
        if (pid == null) return;
        if (_capture.IsRunning && _capture.ProcessId == pid) return;
        _capture.Start(pid.Value);
        Log($"захват запущен для PID {pid}");
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_capture.IsRunning && _capture.Error is { } error) Log($"захват не запустился: {error}");
        });
    }

    private void OnAudioBlock(float[] samples, int count)
    {
        // Поток захвата. Считаем уровни не чаще ~30 раз в секунду.
        _analyzer.Push(samples, count);
        var now = Stopwatch.GetTimestamp();
        if (now - _lastAnalysis < Stopwatch.Frequency / 30) return;
        _lastAnalysis = now;
        if (_analyzer.Analyze() is { } levels) MusicSpectrum.Levels = levels;
    }

    private void Log(string text)
    {
        if (_debugLog is null) return;
        try { File.AppendAllText(_debugLog, $"{DateTime.Now:HH:mm:ss} {text}\n"); }
        catch { /* отладочный журнал не должен мешать работе */ }
    }
}
