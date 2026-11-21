using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Backdrop;
using Ostrov.Services;

namespace Ostrov.Modules.Weather;

/// <summary>
/// Содержимое вкладки «Погода».
/// Обзор: сверху поиск города и ближайшее событие солнца, слева «сейчас», справа плитки на 5 дней.
/// Подробности дня (клик по плитке): остров вытягивается вниз, на месте обзора график температуры и осадков по часам и ветер.
/// Источники данных передаются снаружи (по умолчанию — Open-Meteo), чтобы вкладку можно было проверять без сети.
/// </summary>
internal sealed class WeatherView : Grid
{
    private static readonly CultureInfo Ru = new("ru-RU");
    // Плитки и поле поиска — полупрозрачный белый: на чёрном выглядят как раньше (тёмно-серые), а над цветным фоном погоды
    // слегка подкрашиваются им. Значение прозрачности равно прежнему оттенку серого.
    private static readonly Brush FieldBg = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TileBg = Frozen(Color.FromArgb(Hues.GlassAlpha, 0x21, 0x25, 0x29)); // графит, общая прозрачность карточек
    private static readonly Brush TileHoverBg = Frozen(Color.FromArgb(0xF2, 0x2B, 0x30, 0x35));
    private static readonly Brush TileTodayBg = Frozen(Color.FromArgb(Hues.GlassAlpha, 0x2B, 0x30, 0x35));
    private static readonly Brush TileTodayHoverBg = Frozen(Color.FromArgb(0xF2, 0x34, 0x3A, 0x40));
    private static readonly Brush DropdownBg = Frozen(Color.FromRgb(0x11, 0x11, 0x11));
    private static readonly Brush DropdownBorder = Frozen(Hues.SurfaceHighColor);
    private static readonly Brush ChipSelected = Frozen(Color.FromRgb(0x40, 0x40, 0x40));

    // Второстепенный текст — белый 72 %: над цветным фоном он остаётся читаемым (обычный серый 9A9A9A на тёплом фоне слабоват), на чёрном почти как прежний серый.
    private static readonly Brush Soft = Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PhraseInk = Frozen(Color.FromRgb(0xE4, 0xE4, 0xE4));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly Action<City> _saveCity;
    private readonly Func<City, CancellationToken, Task<WeatherData>> _load;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<City>>> _search;

    private City? _city;
    private WeatherData? _data;
    private Color[]? _palette; // цвета фона по текущей погоде; пока данных нет — фона нет (остров чёрный)
    private PrecipKind _precip;  // что тихо падает на фоне: дождь или снег (по текущему коду погоды)
    private double _precipIntensity;
    private bool _failed;
    private bool _started;
    private bool _syncingText;
    private int _selectedDay;
    private CancellationTokenSource? _refreshCts, _searchCts;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _sunTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };

    /// <summary>Открыт ли подробный вид дня (тогда острову нужна большая высота).</summary>
    public bool IsDetailed { get; private set; }

    /// <summary>Подробный вид открылся или закрылся — окно подстраивает высоту острова.</summary>
    public event Action? DetailChanged;

    // Обзор
    private readonly Grid _overview = new();
    private readonly TextBox _searchBox = Ui.BareTextBox();
    private readonly TextBlock _placeholder = new() { Text = "Город", Foreground = Ui.Gray, FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };
    private readonly TextBlock _offline = new() { Text = "Нет связи", Foreground = Soft, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 1), Visibility = Visibility.Collapsed };
    private readonly Button _refreshButton = Ui.IconButton("", 14, 28);
    private readonly Border _dropdown = new();
    private readonly StackPanel _dropdownList = new();
    private readonly SunPath _sunPath = new() { Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _sunLabel = new() { Foreground = Soft, FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 1) };
    private readonly TextBlock _sunTime = new() { Foreground = Hues.Ink, FontSize = 15, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel _sun = new() { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly Grid _current = new() { Margin = new Thickness(4, 0, 0, 0) };
    private readonly WeatherIcon _curIcon = new() { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _curTemp = new() { FontSize = 40, FontWeight = FontWeights.Light, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _curDesc = new() { FontSize = 14, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
    // Фраза-сводка (светло-серая, чуть заметнее мелких данных) и одна строка «ощущается · ветер».
    private readonly TextBlock _curPhrase = new() { FontSize = 12.5, Foreground = PhraseInk, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _curDetails = new() { FontSize = 11.5, Foreground = Soft, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
    private readonly UniformGrid _tiles = new() { Rows = 1, Columns = 5 };
    private readonly TextBlock _message = new() { Foreground = Ui.Gray, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    // Подробности дня
    private readonly Grid _detail = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _chipPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    // Светлая плашка выбранного дня перекатывается между днями (как в переключателях подразделов).
    private readonly ElasticBar _chipBar = new() { Height = 24, CornerRadius = new CornerRadius(12), Background = ChipSelected };
    private readonly Grid _chipHost = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private string _chipKey = "";
    private UniformGrid _stats = null!;
    private readonly TextBlock _detailDesc = new() { Foreground = Soft, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 1) };
    private readonly DayChart _chart = new() { VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Canvas _tipLayer = new() { IsHitTestVisible = false };   // поверх графика: карточка часа при наведении
    private readonly HourTip _tip = new() { Visibility = Visibility.Collapsed, Opacity = 0 };
    private readonly TranslateTransform _tipShift = new();
    private bool _tipShown;
    private readonly TextBlock _windValue = StatValue();
    private readonly TextBlock _windSub = StatSub();
    private readonly TextBlock _rainValue = StatValue();
    private readonly TextBlock _rainSub = StatSub();

    public WeatherView(
        City? city,
        Action<City> saveCity,
        Func<City, CancellationToken, Task<WeatherData>>? load = null,
        Func<string, CancellationToken, Task<IReadOnlyList<City>>>? search = null)
    {
        _city = city;
        _saveCity = saveCity;
        _load = load ?? WeatherService.GetAsync;
        _search = search ?? WeatherService.SearchAsync;

        _overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _overview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        BuildSearchRow();
        BuildContent();
        BuildDetail();
        Children.Add(_overview);
        Children.Add(_detail);

        SetBoxText(_city?.Name ?? "");
        Render();

        _refreshTimer.Tick += (_, _) => { if (_city != null) _ = RefreshAsync(); };
        _sunTimer.Tick += (_, _) => UpdateSun();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = SearchAsync(_searchBox.Text.Trim(), pickFirst: false);
        };
        Unloaded += (_, _) => IslandBackdrop.Release(this); // фон плавно уходит
        Loaded += (_, _) =>
        {
            IslandBackdrop.Claim(this, _palette);
            IslandBackdrop.SetPrecip(this, _precip, _precipIntensity);
            if (!_started) { _started = true; _refreshTimer.Start(); _sunTimer.Start(); }
            if (_city != null && (_data == null || DateTime.Now - _data.Updated > TimeSpan.FromMinutes(10)))
                _ = RefreshAsync();
            else
                UpdateSun();
            if (_city == null) _searchBox.Focus(); // город ещё не выбран — сразу можно печатать
        };
    }

    private static TextBlock StatValue() => new() { Foreground = Hues.Ink, FontSize = 14 };
    private static TextBlock StatSub() => new() { Foreground = Soft, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };

    // ---------- сборка интерфейса: обзор ----------

    private void BuildSearchRow()
    {
        var glyph = new TextBlock
        {
            Text = "", FontFamily = Ui.IconFont, FontSize = 13, Foreground = Soft,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumn(_searchBox, 1);
        SetColumn(_placeholder, 1);
        _searchBox.MaxLength = 60;
        _searchBox.VerticalAlignment = VerticalAlignment.Center;
        _searchBox.Margin = new Thickness(0, 0, 12, 1);
        inner.Children.Add(glyph);
        inner.Children.Add(_placeholder);
        inner.Children.Add(_searchBox);

        var pill = new Border
        {
            Width = 230, Height = 28, CornerRadius = new CornerRadius(14), Background = FieldBg,
            HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.IBeam, Child = inner
        };
        pill.MouseLeftButtonDown += (_, e) => { _searchBox.Focus(); e.Handled = true; };

        _searchBox.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_syncingText) return;
            _debounce.Stop();
            if (_searchBox.Text.Trim().Length < 2)
            {
                _searchCts?.Cancel();
                HideDropdown();
                return;
            }
            _debounce.Start();
        };
        _searchBox.GotKeyboardFocus += (_, _) => { Ostrov.Services.IslandBus.SuspendAutoCollapse(true); Dispatcher.BeginInvoke(_searchBox.SelectAll); }; // печатаешь город: остров не закрывать
        _searchBox.LostKeyboardFocus += (_, _) => { Ostrov.Services.IslandBus.SuspendAutoCollapse(false); CancelEdit(); };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _debounce.Stop();
                var q = _searchBox.Text.Trim();
                if (q.Length >= 2) _ = SearchAsync(q, pickFirst: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Keyboard.ClearFocus(); // LostKeyboardFocus вернёт название текущего города
            }
        };

        _refreshButton.ToolTip = "Обновить";
        _refreshButton.Click += (_, _) => { if (_city != null) _ = RefreshAsync(); };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _offline, _refreshButton }
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) }; // зазор до плиток дней (было 6: плитки почти упирались в поле)
        row.Children.Add(pill);
        row.Children.Add(right);
        SetRow(row, 0);
        _overview.Children.Add(row);
    }

    private void BuildContent()
    {
        // Зазор до иконок вкладок 14 px (был 6). Между левой колонкой (дуга солнца) и плитками дней — отдельная колонка-зазор.
        var content = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Слева сверху — погода сейчас, внизу — ближайшее событие солнца (подпись и путь солнца за сутки).
        _current.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _current.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _current.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel
        {
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { _curIcon, _curTemp } }, _curDesc, _curPhrase, _curDetails }
        };
        SetRow(top, 0);
        _sun.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _sunLabel, _sunTime } });
        _sun.Children.Add(_sunPath);
        SetRow(_sun, 2);
        _current.Children.Add(top);
        _current.Children.Add(_sun);
        SetColumn(_current, 0);
        SetColumn(_tiles, 2);
        SetColumnSpan(_message, 3);

        _dropdown.Background = DropdownBg;
        _dropdown.BorderBrush = DropdownBorder;
        _dropdown.BorderThickness = new Thickness(1);
        _dropdown.CornerRadius = new CornerRadius(14);
        _dropdown.Padding = new Thickness(4);
        _dropdown.VerticalAlignment = VerticalAlignment.Top;
        _dropdown.Visibility = Visibility.Collapsed;
        _dropdown.Child = _dropdownList;
        SetColumnSpan(_dropdown, 3);
        Panel.SetZIndex(_dropdown, 10);

        content.Children.Add(_current);
        content.Children.Add(_tiles);
        content.Children.Add(_message);
        content.Children.Add(_dropdown);
        SetRow(content, 1);
        _overview.Children.Add(content);
    }

    // ---------- сборка интерфейса: подробности дня ----------

    private void BuildDetail()
    {
        _detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var back = Ui.IconButton("", 14, 28);
        back.ToolTip = "Назад";
        back.Click += (_, _) => CloseDetail();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumn(back, 0);
        _chipHost.Children.Add(_chipBar);
        _chipHost.Children.Add(_chipPanel);
        SetColumn(_chipHost, 1);
        SetColumn(_detailDesc, 2);
        header.Children.Add(back);
        header.Children.Add(_chipHost);
        header.Children.Add(_detailDesc);
        SetRow(header, 0);
        _detail.Children.Add(header);

        // График и поверх него слой для карточки часа (того же размера).
        _tip.RenderTransform = _tipShift;
        _tipLayer.Children.Add(_tip);
        _chart.HoverChanged += OnChartHover;
        var chartHost = new Grid { Children = { _chart, _tipLayer } };
        SetRow(chartHost, 1);
        _detail.Children.Add(chartHost);

        var stats = _stats = new UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
        stats.Children.Add(StatBlock("Ветер", _windValue, _windSub, new Thickness(0, 0, 3, 0)));
        stats.Children.Add(StatBlock("Осадки", _rainValue, _rainSub, new Thickness(3, 0, 0, 0)));
        SetRow(stats, 2);
        _detail.Children.Add(stats);
    }

    private static Border StatBlock(string title, TextBlock value, TextBlock sub, Thickness margin) => new()
    {
        Background = TileBg, CornerRadius = new CornerRadius(14), Margin = margin, Padding = new Thickness(14, 7, 14, 8),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, Foreground = Soft, FontSize = 11 },
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0), Children = { value, sub } }
            }
        }
    };

    // ---------- отображение: обзор ----------

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Ru) + s[1..];

    private void Render()
    {
        var d = _data;
        if (d == null && IsDetailed) CloseDetail(); // данных нет (например, сменили город) — возвращаемся к обзору

        _current.Visibility = _tiles.Visibility = d == null ? Visibility.Collapsed : Visibility.Visible;
        _message.Visibility = d == null ? Visibility.Visible : Visibility.Collapsed;
        _offline.Visibility = d != null && _failed ? Visibility.Visible : Visibility.Collapsed;

        if (d == null)
        {
            _message.Text = _city == null ? "Введи город в поле выше"
                : _failed ? "Не удалось загрузить погоду.\nПроверь интернет или VPN и нажми «Обновить»"
                : "Загрузка…";
            _sun.Visibility = Visibility.Collapsed;
            _refreshButton.ToolTip = "Обновить";
            return;
        }

        var (text, kind) = WeatherCodes.Describe(d.Code);
        _curIcon.Kind = kind;
        _curIcon.IsDay = d.IsDay;
        _curTemp.Text = WeatherFormat.Temp(d.Temperature);
        _curDesc.Text = text;
        _curPhrase.Text = WeatherSummary.Make(d, CityNow(d));
        _curDetails.Text = $"Ощущается {WeatherFormat.Temp(d.FeelsLike)} · ветер {Math.Round(d.WindMs):0} м/с";
        _refreshButton.ToolTip = $"Обновлено в {d.Updated:HH:mm}. Нажми, чтобы обновить";
        _palette = WeatherSummary.Palette(kind, d.IsDay); // мягкий фон под погоду
        IslandBackdrop.SetPalette(this, _palette);
        (_precip, _precipIntensity) = WeatherSummary.Precipitation(d.Code); // дождь и снег на фоне, едва заметные
        IslandBackdrop.SetPrecip(this, _precip, _precipIntensity);
        UpdateSun();
        BuildTiles(d);
        if (IsDetailed) RenderDetail();
    }

    /// <summary>Время в городе сейчас: момент прогноза плюс сколько прошло с тех пор на этом компьютере.</summary>
    private static DateTime CityNow(WeatherData d) => d.LocalNow + (DateTime.Now - d.Updated);

    private static double Hours(DateTime t) => t.Hour + t.Minute / 60.0;

    /// <summary>Только ближайшее событие: до рассвета — восход, днём — закат, после заката — завтрашний восход.</summary>
    private void UpdateSun()
    {
        var d = _data;
        if (d == null) { _sun.Visibility = Visibility.Collapsed; return; }

        var now = CityNow(d);
        var idx = -1;
        for (var i = 0; i < d.Days.Count; i++)
            if (d.Days[i].Date == DateOnly.FromDateTime(now)) idx = i;
        if (idx < 0 || d.Days[idx].Sunrise is not { } rise || d.Days[idx].Sunset is not { } set)
        {
            _sun.Visibility = Visibility.Collapsed; // нет данных или полярный день/ночь
            return;
        }

        string label;
        DateTime time;
        double eventHour;
        if (now < rise)
        {
            (label, time, eventHour) = ("Восход", rise, Hours(rise));
        }
        else if (now < set)
        {
            (label, time, eventHour) = ("Закат", set, Hours(set));
        }
        else if (idx + 1 < d.Days.Count && d.Days[idx + 1].Sunrise is { } next)
        {
            (label, time, eventHour) = ("Восход", next, double.NaN); // завтра — на этой шкале его нет
        }
        else
        {
            _sun.Visibility = Visibility.Collapsed;
            return;
        }

        _sunLabel.Text = label;
        _sunTime.Text = time.ToString("HH:mm", Ru);
        _sunPath.RiseHour = Hours(rise);
        _sunPath.SetHour = Hours(set);
        _sunPath.NowHour = Hours(now);
        _sunPath.EventHour = eventHour;
        _sun.Visibility = Visibility.Visible;
    }

    private void BuildTiles(WeatherData d)
    {
        _tiles.Children.Clear();
        var weekMax = d.Days.Max(x => x.Max);
        var span = Math.Max(weekMax - d.Days.Min(x => x.Min), 1);
        for (var i = 0; i < d.Days.Count; i++)
        {
            var index = i;
            var day = d.Days[i];
            var today = i == 0;
            var name = today ? "Сегодня" : Capitalize(day.Date.ToString("ddd", Ru));
            var normal = today ? TileTodayBg : TileBg;
            var hover = today ? TileTodayHoverBg : TileHoverBg;

            // Содержимое плитки собирается ниже.
            var topPart = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            topPart.Children.Add(new TextBlock
            {
                Text = name, FontSize = today ? 10.5 : 11, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = today ? Hues.Ink : Soft
            });
            topPart.Children.Add(new WeatherIcon
            {
                Kind = WeatherCodes.Describe(day.Code).Kind, Width = 26, Height = 26, Margin = new Thickness(0, 8, 0, 0)
            });
            var maxText = new TextBlock { Text = WeatherFormat.Temp(day.Max), FontSize = 13, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            var minText = new TextBlock { Text = WeatherFormat.Temp(day.Min), FontSize = 12, Foreground = Soft, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) };
            var bar = new RangeBar
            {
                High = (weekMax - day.Max) / span, Low = (weekMax - day.Min) / span,
                MinHeight = 16, Margin = new Thickness(0, 6, 0, 6)
            };
            var chevron = new TextBlock
            {
                Text = "", FontFamily = Ui.IconFont, FontSize = 9, Foreground = Soft,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 4, 0, 0)
            };
            // Сверху вниз: день и иконка, максимум, шкала (растягивается), минимум, шеврон («раскрывается вниз»).
            var stack = new Grid();
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetRow(topPart, 0);
            SetRow(maxText, 1);
            SetRow(bar, 2);
            SetRow(minText, 3);
            SetRow(chevron, 4);
            stack.Children.Add(topPart);
            stack.Children.Add(maxText);
            stack.Children.Add(bar);
            stack.Children.Add(minText);
            stack.Children.Add(chevron);

            var tile = new Border
            {
                Background = normal, CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand,
                Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(0, 10, 0, 8), Child = stack
            };
            tile.MouseEnter += (_, _) => tile.Background = hover;
            tile.MouseLeave += (_, _) => tile.Background = normal;
            tile.MouseLeftButtonUp += (_, _) => ShowDetail(index);
            _tiles.Children.Add(tile);
        }
    }

    // ---------- отображение: подробности дня ----------

    public void ShowDetail(int dayIndex)
    {
        if (_data == null || _data.Days.Count == 0) return;
        _selectedDay = Math.Clamp(dayIndex, 0, _data.Days.Count - 1);
        RenderDetail();
        if (IsDetailed) return;
        IsDetailed = true;
        _overview.Visibility = Visibility.Collapsed;
        _detail.Visibility = Visibility.Visible;
        DetailChanged?.Invoke();
    }

    public void CloseDetail()
    {
        if (!IsDetailed) return;
        IsDetailed = false;
        _detail.Visibility = Visibility.Collapsed;
        _overview.Visibility = Visibility.Visible;
        DetailChanged?.Invoke();
    }

    /// <param name="dir">Куда листаем при смене дня: 1 — вперёд (новый день приезжает справа), −1 — назад; 0 — без перехода.</param>
    private void RenderDetail(int dir = 0)
    {
        var d = _data;
        if (d == null) return;
        var day = d.Days[_selectedDay];

        // Кнопки дней строятся только когда изменился набор дней; смена выбранного дня двигает плашку и красит подписи.
        var labels = d.Days.Select((dd, i) => i == 0 ? "Сегодня" : $"{Capitalize(dd.Date.ToString("ddd", Ru))} {dd.Date.Day}").ToList();
        var key = string.Join("|", labels);
        var rebuilt = key != _chipKey;
        if (rebuilt)
        {
            _chipKey = key;
            _chipPanel.Children.Clear();
            for (var i = 0; i < labels.Count; i++)
            {
                var index = i;
                var chip = Ui.Pill(labels[i], padding: 9);
                chip.Height = 24;
                chip.FontSize = 12;
                chip.Margin = new Thickness(0, 0, 2, 0);
                chip.MinWidth = 0;
                chip.Background = Brushes.Transparent;
                chip.Resources["NoHover"] = true; // подсветка выбранного — плашка, а не сама кнопка
                chip.Click += (_, _) =>
                {
                    var previous = _selectedDay;
                    if (previous == index) return;
                    _selectedDay = index;
                    RenderDetail(index > previous ? 1 : -1);
                };
                _chipPanel.Children.Add(chip);
            }
        }
        for (var i = 0; i < _chipPanel.Children.Count; i++)
            ((Button)_chipPanel.Children[i]).Foreground = i == _selectedDay ? Hues.Ink : Soft;
        MoveChipBar(animate: !rebuilt && IsLoaded);

        _detailDesc.Text = WeatherCodes.Describe(day.Code).Text;

        var now = CityNow(d);
        _chart.SetData(day.Hours, day.Date == DateOnly.FromDateTime(now) ? now.Hour : -1);

        _windValue.Text = $"{day.WindMax:0} м/с";
        _windSub.Text = $"порывы до {day.GustMax:0} · {WeatherFormat.Compass(day.WindDirDominant)}";
        _rainValue.Text = day.PrecipSum < 0.1 ? "Без осадков" : $"{day.PrecipSum.ToString("0.#", Ru)} мм";
        _rainSub.Text = $"вероятность до {day.PrecipProbMax}%";

        // Листание дней: график дорисовывается слева направо, подпись и показатели приезжают со стороны нового дня.
        if (dir != 0)
        {
            _chart.PlayReveal();
            Ui.SlideIn(_stats, dir * 18);
            Ui.SlideIn(_detailDesc, dir * 12);
        }
    }

    private void MoveChipBar(bool animate)
    {
        if (_selectedDay < 0 || _selectedDay >= _chipPanel.Children.Count) return;
        var chip = (FrameworkElement)_chipPanel.Children[_selectedDay];
        if (chip.ActualWidth <= 0)
        {
            // Кнопки ещё не разложены (только что построены): встанем на место сразу после раскладки.
            Dispatcher.BeginInvoke(() => MoveChipBar(false), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        var x = VisualTreeHelper.GetOffset(chip).X + VisualTreeHelper.GetOffset(_chipPanel).X;
        _chipBar.Place(x, x + chip.ActualWidth, animate);
    }

    // ---------- карточка часа при наведении на график ----------

    private void OnChartHover(int index, Point at)
    {
        var d = _data;
        if (index < 0 || d == null || _selectedDay >= d.Days.Count || index >= d.Days[_selectedDay].Hours.Count)
        {
            HideTip();
            return;
        }
        _tip.Show(d.Days[_selectedDay].Hours[index]);
        _tip.Visibility = Visibility.Visible; // скрытый элемент измеряется в ноль, поэтому показываем (он прозрачный) до измерения
        _tip.Measure(new Size(double.PositiveInfinity, HourTip.CardHeight));
        double w = _tipLayer.ActualWidth, h = _tipLayer.ActualHeight, cw = _tip.DesiredSize.Width, ch = HourTip.CardHeight;

        // Точка высоко (в верхней трети графика) — карточка под ней; низко или посередине — над ней. По горизонтали по центру точки, в пределах графика.
        const double gap = 14;
        var x = Math.Clamp(at.X - cw / 2, 0, Math.Max(0, w - cw));
        var y = at.Y < h / 3 ? at.Y + gap : at.Y - gap - ch;
        y = Math.Clamp(y, 0, Math.Max(0, h - ch));

        if (!_tipShown)
        {
            // Первое появление: сразу на месте, проявляется.
            _tipShift.BeginAnimation(TranslateTransform.XProperty, null);
            _tipShift.BeginAnimation(TranslateTransform.YProperty, null);
            _tipShift.X = x;
            _tipShift.Y = y;
            _tipShown = true;
            _tip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        }
        else
        {
            // Дальше плавно следует за точкой (анимация стартует с текущего положения: её можно прервать новым наведением).
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _tipShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
            _tipShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
            _tip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(80)));
        }
    }

    private void HideTip()
    {
        if (!_tipShown) return;
        _tipShown = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
        fade.Completed += (_, _) => { if (!_tipShown) _tip.Visibility = Visibility.Collapsed; };
        _tip.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- загрузка погоды ----------

    private async Task RefreshAsync()
    {
        var city = _city;
        if (city == null) return;
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        _failed = false;
        if (_data == null) Render(); // «Загрузка…», пока данных ещё нет

        try
        {
            var data = await _load(city, cts.Token);
            if (cts.IsCancellationRequested) return;
            _data = data;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        catch
        {
            if (cts.IsCancellationRequested) return;
            _failed = true; // старые данные (если есть) остаются на экране, с пометкой «нет связи»
        }
        Render();
    }

    // ---------- поиск города ----------

    private void SetBoxText(string text)
    {
        _syncingText = true;
        _searchBox.Text = text;
        _syncingText = false;
    }

    private void CancelEdit()
    {
        _debounce.Stop();
        _searchCts?.Cancel();
        HideDropdown();
        SetBoxText(_city?.Name ?? "");
    }

    private async Task SearchAsync(string query, bool pickFirst)
    {
        if (query.Length < 2) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            var found = await _search(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (pickFirst && found.Count > 0) { Pick(found[0]); return; }
            ShowDropdown(found);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch
        {
            if (!cts.IsCancellationRequested) ShowDropdownMessage("Не удалось выполнить поиск");
        }
    }

    private void Pick(City city)
    {
        _city = city;
        _data = null;
        _failed = false;
        _saveCity(city);
        HideDropdown();
        SetBoxText(city.Name);
        Keyboard.ClearFocus();
        Render();
        _ = RefreshAsync();
    }

    private void ShowDropdown(IReadOnlyList<City> cities)
    {
        if (cities.Count == 0) { ShowDropdownMessage("Ничего не найдено"); return; }
        _dropdownList.Children.Clear();
        foreach (var city in cities)
        {
            var row = Ui.ListRow(city.Name, city.Subtitle);
            row.Click += (_, _) => Pick(city);
            _dropdownList.Children.Add(row);
        }
        _dropdown.Visibility = Visibility.Visible;
    }

    private void ShowDropdownMessage(string text)
    {
        _dropdownList.Children.Clear();
        _dropdownList.Children.Add(new TextBlock { Text = text, Foreground = Ui.Gray, FontSize = 13, Margin = new Thickness(10, 5, 10, 6) });
        _dropdown.Visibility = Visibility.Visible;
    }

    private void HideDropdown() => _dropdown.Visibility = Visibility.Collapsed;
}
