using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ostrov.Backdrop;
using Ostrov.Modules.Player.Lyrics;
using Ostrov.Services;
using Windows.Media.Control;

namespace Ostrov.Modules.Player;

/// <summary>
/// Содержимое вкладки «Плеер». Источник — GlobalSystemMediaTransportControls (SMTC):
/// предпочитаем сессию Apple Music, иначе берём текущую системную.
/// </summary>
internal sealed class PlayerView : Grid
{
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly Brush Dim = new SolidColorBrush(Hues.SurfaceHighColor);

    private readonly Border _cover = new();
    private readonly TextBlock _coverGlyph = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _artist = new();
    private readonly Button _playPause;
    private readonly Grid _progressHit = new();
    private readonly Border _track = new();
    private readonly Border _fill = new();
    private readonly TextBlock _posText = new();
    private readonly TextBlock _endText = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly VolumeControl _volume = new(); // громкость Apple Music справа от кнопок
    private readonly AdaptiveInk _ink = new(); // текст и значки темнеют там, где под ними светлое пятно цветного фона
    private Color[]? _palette;                  // главные цвета текущей обложки для фона

    // Режим текста песни (раскладка меняется плавно, Blend ведёт пружина).
    private readonly TextBlock _titleL = new(), _artistL = new(); // подписи под обложкой в режиме текста
    private readonly TrackInfoBlock _info = new();       // серый блок о треке слева в режиме текста
    private readonly TrackInfoService _infoService = new();
    private string? _infoSignature, _infoLoadedFor;       // какой трек сейчас в блоке и для какого трека данные уже запрошены
    private TrackKey _trackKey;                           // текущий трек (исполнитель и альбом уже разделены)
    private int _infoVersion;
    private readonly LyricsView _lyrics = new();
    private readonly LyricsService _lyricsService = new();
    private readonly Spring _mode = new(dampingRatio: 1.0, responseSeconds: 0.38); // 0 — обычный вид, 1 — режим текста
    private PlayerLayout _layout = null!;
    private Button _lyricsButton = null!;
    private enum LyricsState { None, Loading, Found }
    private LyricsState _lyricsState = LyricsState.None;
    private bool _lyricsOpen;
    private string? _lyricsKey;
    private int _lyricsVersion;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _initialized;
    private int _mediaVersion;

    // Последние известные данные о таймлайне; текущая позиция достраивается по времени.
    private bool _playing;
    private TimeSpan _start, _end, _pos;
    private DateTimeOffset _posUpdated = DateTimeOffset.UtcNow;

    public PlayerView()
    {
        // Обложка (квадрат с заглушкой-иконкой). Положение задаёт PlayerLayout.
        _cover.Width = _cover.Height = 128;
        _cover.CornerRadius = new CornerRadius(12);
        _cover.Background = new SolidColorBrush(Hues.SurfaceColor);
        _cover.VerticalAlignment = VerticalAlignment.Center;
        _coverGlyph.Text = "";
        _coverGlyph.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        _coverGlyph.FontSize = 40;
        _coverGlyph.Foreground = Gray;
        _coverGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _coverGlyph.VerticalAlignment = VerticalAlignment.Center;
        _cover.Child = _coverGlyph;

        // Название и исполнитель.
        _title.FontSize = 17;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Hues.Ink;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _artist.FontSize = 13;
        _artist.Foreground = Gray;
        _artist.TextTrimming = TextTrimming.CharacterEllipsis;

        // Подписи для режима текста: слева под обложкой сначала название, ниже исполнитель (порядок пользователя), ещё ниже серый блок
        // о треке; в узкой колонке переносятся. Название берётся из основной подписи, исполнитель задаётся отдельно (без «— Альбом»).
        _titleL.FontSize = 17;
        _titleL.FontWeight = FontWeights.SemiBold;
        _titleL.Foreground = Hues.Ink;
        _titleL.TextWrapping = TextWrapping.Wrap;
        _titleL.TextTrimming = TextTrimming.CharacterEllipsis;
        _titleL.MaxHeight = 72;
        _artistL.FontSize = 13;
        _artistL.Foreground = Gray;
        _artistL.TextWrapping = TextWrapping.Wrap;
        _artistL.TextTrimming = TextTrimming.CharacterEllipsis;
        _artistL.MaxHeight = 54;
        _titleL.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = _title });

        // Кнопки.
        var prev = IconButton(PlayerIcon.Previous, 18, 40);
        _playPause = IconButton(PlayerIcon.Play, 22, 48);
        var next = IconButton(PlayerIcon.Next, 18, 40);
        prev.Click += async (_, _) => await Try(s => s.TrySkipPreviousAsync().AsTask());
        _playPause.Click += async (_, _) => await Try(s => s.TryTogglePlayPauseAsync().AsTask());
        next.Click += async (_, _) => await Try(s => s.TrySkipNextAsync().AsTask());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { prev, _playPause, next } };
        // Ряд управления: кнопки слева, справа ползунок громкости Apple Music, ещё правее значок текста песни.
        _lyricsButton = IconButton(PlayerIcon.Lyrics, 18, 34);
        _lyricsButton.Margin = new Thickness(8, 0, 0, 0);
        _lyricsButton.Click += (_, _) => ToggleLyrics();
        var controls = new Grid();
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(buttons, 0);
        SetColumn(_volume, 2);
        SetColumn(_lyricsButton, 3);
        controls.Children.Add(buttons);
        controls.Children.Add(_volume);
        controls.Children.Add(_lyricsButton);

        // Полоса прогресса: тонкая линия, зона клика выше неё.
        _track.Height = 4;
        _track.CornerRadius = new CornerRadius(2);
        _track.Background = Dim;
        _track.VerticalAlignment = VerticalAlignment.Center;
        _fill.Height = 4;
        _fill.CornerRadius = new CornerRadius(2);
        _fill.Background = Hues.Ink;
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
        _fill.VerticalAlignment = VerticalAlignment.Center;
        _fill.Width = 0;
        _progressHit.Height = 16;
        _progressHit.Background = Brushes.Transparent;
        _progressHit.Children.Add(_track);
        _progressHit.Children.Add(_fill);

        _posText.FontSize = 11;
        _posText.Foreground = Gray;
        _endText.FontSize = 11;
        _endText.Foreground = Gray;
        _endText.HorizontalAlignment = HorizontalAlignment.Right;
        var times = new Grid { Children = { _posText, _endText } };

        // Раскладка с плавным переходом в режим текста; Blend ведёт пружина.
        _layout = new PlayerLayout(_cover, _title, _artist, _titleL, _artistL, _info, controls, _progressHit, times, _lyrics);
        Children.Add(_layout);
        _mode.Changed += v => _layout.Blend = v;

        // «Чернила»: обычные цвета и тёмные для светлого фона. Значки меняют заливку своего рисунка.
        var darkWhite = Color.FromRgb(0x12, 0x12, 0x12);
        var darkGray = Color.FromRgb(0x2C, 0x2C, 0x2C);
        var gray = Color.FromRgb(0x9A, 0x9A, 0x9A);
        _ink.Add(_title, b => _title.Foreground = b, Colors.White, darkWhite);
        _ink.Add(_artist, b => _artist.Foreground = b, gray, darkGray);
        _ink.Add(_titleL, b => _titleL.Foreground = b, Colors.White, darkWhite);
        _ink.Add(_artistL, b => _artistL.Foreground = b, gray, darkGray);
        foreach (var text in _info.Texts)
        {
            var block = text;
            _ink.Add(block, b => block.Foreground = b, TrackInfoBlock.Ink, Color.FromRgb(0x3A, 0x3A, 0x3A));
        }
        _ink.Add(_posText, b => _posText.Foreground = b, gray, darkGray);
        _ink.Add(_endText, b => _endText.Foreground = b, gray, darkGray);
        _ink.Add(_fill, b => _fill.Background = b, Colors.White, darkWhite);
        _ink.Add(_volume.IconElement, b => ((TextBlock)_volume.IconElement).Foreground = b, gray, darkGray);
        _ink.Add(_volume.FillElement, b => _volume.FillElement.Background = b, Colors.White, darkWhite);
        foreach (var button in new[] { prev, _playPause, next, _lyricsButton })
        {
            var target = button;
            _ink.Add(target, b =>
            {
                if (target.Content is System.Windows.Shapes.Path icon) { icon.Fill = b; icon.Stroke = b; }
            }, Colors.White, darkWhite);
        }

        ShowEmpty();

        _timer.Tick += (_, _) => UpdateProgress();
        Loaded += async (_, _) =>
        {
            IslandBus.Expanding -= OnIslandExpanding;
            IslandBus.Expanding += OnIslandExpanding;
            IslandBackdrop.Frame -= OnBackdropFrame;
            IslandBackdrop.Frame += OnBackdropFrame;
            IslandBackdrop.Claim(this, _palette);
            _timer.Start();
            if (!_initialized) { _initialized = true; await InitAsync(); }
            else RefreshAll();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            IslandBus.Expanding -= OnIslandExpanding;
            IslandBackdrop.Frame -= OnBackdropFrame;
            IslandBackdrop.Release(this); // фон плавно уходит (если его уже не взяла другая вкладка)
            _ink.Reset();
            CloseLyricsInstantly();
        };
    }

    private void OnBackdropFrame(double dt) => _ink.Update(dt);

    // ---------- режим текста песни ----------

    // Остров раскрывают заново: если текст был открыт, обычный вид возвращается сразу (остров ещё свёрнут, скачка не видно).
    private void OnIslandExpanding() => CloseLyricsInstantly();

    private void ToggleLyrics()
    {
        if (_lyricsState != LyricsState.Found && !_lyricsOpen) return;
        SetLyricsOpen(!_lyricsOpen, fast: false);
    }

    private void SetLyricsOpen(bool open, bool fast)
    {
        if (_lyricsOpen == open) return;
        _lyricsOpen = open;
        PlayerState.LyricsOpen = open;
        _mode.SetResponse(fast ? 0.22 : 0.38); // песня без текста закрывает режим быстрее, чем обычный клик
        _mode.SetTarget(open ? 1 : 0);
        _timer.Interval = TimeSpan.FromMilliseconds(open ? 100 : 250); // подсветка строк точнее, пока текст открыт
        UpdateLyricsButton();
        IslandBus.SetTabsVisible(!open);
        IslandBus.SuspendAutoCollapse(open);
        IslandBus.RequestHeight("player");
        if (open)
        {
            _lyrics.UpdatePosition(CurrentPosition() - _start);
            LoadTrackInfo(); // данные о треке из сети нужны, только когда виден режим текста
        }
    }

    // Блок о треке: сразу альбом из Apple Music, год, жанр и номер приходят из сети (iTunes), когда открыт режим текста.
    private void ShowTrackInfo(string title, string artistField, string album)
    {
        var signature = $"{title}|{artistField}|{album}";
        if (signature == _infoSignature) return; // повторное уведомление Windows о том же треке: блок не сбрасываем
        _infoSignature = signature;
        _infoLoadedFor = null;
        _infoVersion++;
        _trackKey = TrackKey.FromMedia(title, artistField, album, _end - _start);
        _artistL.Text = _trackKey.Artist;
        _info.Show(new TrackInfo(string.IsNullOrWhiteSpace(_trackKey.Album) ? null : _trackKey.Album, null, null, null, null));
        LoadTrackInfo();
    }

    private async void LoadTrackInfo()
    {
        if (!_lyricsOpen || _trackKey.Title.Length == 0) return;
        if (_infoLoadedFor == _infoSignature) return;
        _infoLoadedFor = _infoSignature;
        var version = ++_infoVersion;
        try
        {
            await Task.Delay(600); // длительность нового трека приходит чуть позже названия; при быстром листании песен сеть не дёргаем
            if (version != _infoVersion) return;
            var key = _trackKey with { DurationSeconds = (int)Math.Round((_end - _start).TotalSeconds) };
            var info = await _infoService.GetAsync(key);
            if (version == _infoVersion) _info.Show(info);
        }
        catch
        {
            if (version == _infoVersion) _infoLoadedFor = null; // нет сети: останется альбом, при следующем открытии текста попробуем снова
        }
    }

    private void CloseLyricsInstantly()
    {
        if (!_lyricsOpen) return;
        _lyricsOpen = false;
        PlayerState.LyricsOpen = false;
        _mode.Snap(0);
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        UpdateLyricsButton();
        IslandBus.SetTabsVisible(true);
        IslandBus.SuspendAutoCollapse(false);
        IslandBus.RequestHeight("player");
    }

    private void SetLyricsState(LyricsState state)
    {
        _lyricsState = state;
        UpdateLyricsButton();
    }

    // Значок текста: серый и неактивный, если текста нет или он ещё ищется; белый, если есть; при открытом тексте на светлой подложке.
    private void UpdateLyricsButton()
    {
        var available = _lyricsState == LyricsState.Found || _lyricsOpen;
        _lyricsButton.IsEnabled = available;
        _lyricsButton.Opacity = available ? 1 : 0.3;
        _lyricsButton.Background = _lyricsOpen ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
    }

    // Текст ищется при смене трека (чтобы значок знал заранее), через полсекунды: при быстром листании песен сеть не дёргаем.
    private async void StartLyricsLookup(string title, string artistField, string album)
    {
        var signature = $"{title}|{artistField}|{album}";
        if (signature == _lyricsKey) return; // повторное уведомление Windows о том же треке
        _lyricsKey = signature;
        var version = ++_lyricsVersion;
        SetLyricsState(LyricsState.Loading);
        try
        {
            await Task.Delay(500);
            if (version != _lyricsVersion) return;
            var length = _end - _start;
            if (length <= TimeSpan.Zero && _session?.GetTimelineProperties() is { } timeline) length = timeline.EndTime - timeline.StartTime;
            var key = TrackKey.FromMedia(title, artistField, album, length);
            ApplyLyrics(version, key.Title.Length == 0 ? LyricsResult.None : await _lyricsService.GetAsync(key));
        }
        catch
        {
            ApplyLyrics(version, LyricsResult.None); // нет сети или ошибка базы: текста нет, при следующем треке попробуем снова
        }
    }

    private void ApplyLyrics(int version, LyricsResult result)
    {
        if (version != _lyricsVersion) return;
        if (result.Found)
        {
            _lyrics.SetLyrics(result.Lines, result.Synced);
            SetLyricsState(LyricsState.Found);
            if (_lyricsOpen) _lyrics.UpdatePosition(CurrentPosition() - _start);
        }
        else
        {
            SetLyricsState(LyricsState.None);
            SetLyricsOpen(false, fast: true); // после песни с открытым текстом пришла песня без текста: быстро сворачиваем до обычного размера
        }
    }

    // ---------- подключение к Windows ----------

    private async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(PickSession);
            _manager.SessionsChanged += (_, _) => Dispatcher.BeginInvoke(PickSession);
            PickSession();
        }
        catch
        {
            // Системные медиа-функции недоступны — остаёмся в состоянии «ничего не играет».
        }
    }

    private void PickSession()
    {
        if (_manager == null) return;
        var next = _manager.GetSessions()
                       .FirstOrDefault(s => s.SourceAppUserModelId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase))
                   ?? _manager.GetCurrentSession();
        if (next?.SourceAppUserModelId == _session?.SourceAppUserModelId && _session != null) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        _session = next;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        RefreshAll();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, object a) => Dispatcher.BeginInvoke(RefreshMedia);
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, object a) => Dispatcher.BeginInvoke(RefreshPlayback);
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, object a) => Dispatcher.BeginInvoke(RefreshTimeline);

    private void RefreshAll()
    {
        if (_session == null) { ShowEmpty(); return; }
        RefreshMedia();
        RefreshPlayback();
        RefreshTimeline();
    }

    // ---------- чтение данных ----------

    private async void RefreshMedia()
    {
        var session = _session;
        if (session == null) { ShowEmpty(); return; }
        var version = ++_mediaVersion;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _mediaVersion) return;
            _title.Text = string.IsNullOrWhiteSpace(props.Title) ? "Без названия" : props.Title;
            _artist.Text = props.Artist;
            StartLyricsLookup(props.Title ?? "", props.Artist ?? "", props.AlbumTitle ?? "");
            ShowTrackInfo(props.Title ?? "", props.Artist ?? "", props.AlbumTitle ?? "");

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
                bmp.DecodePixelWidth = 256;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            if (version != _mediaVersion) return;
            SetCover(image);
        }
        catch
        {
            if (version == _mediaVersion) SetCover(null);
        }
    }

    private void RefreshPlayback()
    {
        var info = _session?.GetPlaybackInfo();
        if (info == null) return;
        // Опираемся на «позицию прямо сейчас», чтобы после паузы полоса не прыгала.
        var now = CurrentPosition();
        _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        _pos = now;
        _posUpdated = DateTimeOffset.UtcNow;
        _lyrics.SetPlaying(_playing); // слова текста заливаются плавно между обновлениями позиции
        SetPlayPauseIcon(_playing);
    }

    private void RefreshTimeline()
    {
        var t = _session?.GetTimelineProperties();
        if (t == null) return;
        _start = t.StartTime;
        _end = t.EndTime;
        _pos = t.Position;
        _posUpdated = DateTimeOffset.UtcNow;
        UpdateProgress();
    }

    // ---------- отображение ----------

    private TimeSpan CurrentPosition()
    {
        var pos = _pos;
        if (_playing) pos += DateTimeOffset.UtcNow - _posUpdated;
        if (_end > _start && pos > _end) pos = _end;
        return pos < _start ? _start : pos;
    }

    private void UpdateProgress()
    {
        _lyrics.UpdatePosition(CurrentPosition() - _start); // подсветка строки и прокрутка идут за песней
        var length = _end - _start;
        if (_session == null || length <= TimeSpan.Zero)
        {
            _fill.Width = 0;
            _posText.Text = "";
            _endText.Text = "";
            return;
        }
        var pos = CurrentPosition() - _start;
        var fraction = Math.Clamp(pos / length, 0, 1);
        _fill.Width = _track.ActualWidth * fraction;
        _posText.Text = Format(pos);
        _endText.Text = Format(length);
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private void SetCover(ImageSource? image)
    {
        // Цвета обложки для фона острова (нет обложки — нет и фона, остров остаётся чёрным).
        _palette = CoverPalette.FromImage(image);
        IslandBackdrop.SetPalette(this, _palette);
        if (image == null)
        {
            _cover.Background = new SolidColorBrush(Hues.SurfaceColor);
            _coverGlyph.Visibility = Visibility.Visible;
        }
        else
        {
            _cover.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            _coverGlyph.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowEmpty()
    {
        _mediaVersion++;
        _playing = false;
        _start = _end = _pos = TimeSpan.Zero;
        _title.Text = "Ничего не играет";
        _artist.Text = "Запусти музыку в Apple Music";
        _artistL.Text = "";
        _trackKey = default;
        _infoSignature = _infoLoadedFor = null;
        _infoVersion++;
        _info.Show(TrackInfo.Empty);
        SetPlayPauseIcon(false);
        _lyricsKey = null;
        _lyricsVersion++;
        SetLyricsState(LyricsState.None);
        SetLyricsOpen(false, fast: true);
        SetCover(null);
        UpdateProgress();
    }

    // ---------- управление ----------

    private async Task Try(Func<GlobalSystemMediaTransportControlsSession, Task> action)
    {
        var session = _session;
        if (session == null) return;
        try { await action(session); } catch { /* приложение не приняло команду — игнорируем */ }
    }

    // ---------- кнопка-иконка ----------

    // Значок кнопки «играть/пауза» меняется только при смене состояния (не на каждом обновлении).
    private bool? _playPauseShown;

    private void SetPlayPauseIcon(bool playing)
    {
        if (_playPauseShown == playing) return;
        _playPauseShown = playing;
        _playPause.Content = PlayerIcons.Create(playing ? PlayerIcon.Pause : PlayerIcon.Play, 22);
        _ink.Invalidate(); // новый значок нарисован белым: «чернила» перекрасят его при следующем кадре
    }

    private static Button IconButton(PlayerIcon icon, double iconSize, double box)
    {
        var bg = new FrameworkElementFactory(typeof(Border), "Bg");
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(box / 2));
        bg.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty)); // подложка меняется снаружи (значок текста при открытом тексте)
        bg.SetValue(RenderTransformOriginProperty, new Point(0.5, 0.5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bg.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = bg };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), "Bg"));
        template.Triggers.Add(hover);
        // Реакция на нажатие мгновенная: значок чуть сжимается.
        var press = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(RenderTransformProperty, new ScaleTransform(0.9, 0.9), "Bg"));
        template.Triggers.Add(press);

        return new Button
        {
            Content = PlayerIcons.Create(icon, iconSize),
            Template = template,
            Width = box,
            Height = box,
            Background = Brushes.Transparent,
            Foreground = Hues.Ink,
            Cursor = Cursors.Hand,
            Focusable = false
        };
    }
}
