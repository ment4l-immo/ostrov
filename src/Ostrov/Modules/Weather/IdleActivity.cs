using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Weather;

/// <summary>
/// Плашка пустого острова (решение пользователя 2026-11-14, правка: «время вместо дуги»): когда других активностей нет, на свёрнутом острове
/// слева время (цифры перелистываются, как в «Часах»), справа иконка погоды на сейчас и температура. Самый низкий приоритет: любая другая
/// активность её вытесняет, а когда уходит, плашка возвращается. Клик открывает вкладку «Время». Нет города или данных погоды:
/// остаётся одно время. Погода берётся тем же Open-Meteo, что и во вкладке (раз в 30 минут).
/// </summary>
internal sealed class IdleActivity
{
    private const string EntryId = "idle";

    /// <summary>Последняя загруженная погода (null — города нет или данных ещё нет): для плитки погоды в хабе.</summary>
    public static WeatherData? Latest { get; private set; }
    public static event Action? LatestChanged;

    private readonly IdlePill _pill = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _reload = new() { Interval = TimeSpan.FromMinutes(30) };
    private WeatherData? _data;
    private bool _loading;
    private bool _reloadAgain; // во время загрузки выбрали другой город: после неё грузим заново

    public IdleActivity()
    {
        _pill.SetTime(DateTime.Now, animate: false);
        ActivityCenter.Set(new ActivityEntry
        {
            Id = EntryId, Priority = 1,
            Pill = _pill, PillWidth = IdlePill.ViewWidth, PillHeight = IdlePill.ViewHeight,
            ModuleId = "hub", TabMark = false
        });
        _tick.Tick += (_, _) => _pill.SetTime(DateTime.Now, animate: true);
        _reload.Tick += (_, _) => Reload();
        _tick.Start();
        _reload.Start();
        Reload();
    }

    /// <summary>Загрузить погоду заново (при запуске, раз в 30 минут и когда в «Погоде» выбрали другой город).</summary>
    public async void Reload()
    {
        if (_loading) { _reloadAgain = true; return; }
        _loading = true;
        try
        {
            var settings = AppSettings.Load();
            if (string.IsNullOrWhiteSpace(settings.WeatherCity))
            {
                _data = null;
                _pill.SetWeather(null);
                Latest = null;
                LatestChanged?.Invoke();
                return;
            }
            var city = new City(settings.WeatherCity, "", settings.WeatherLatitude, settings.WeatherLongitude);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _data = await WeatherService.GetAsync(city, cts.Token);
            _pill.SetWeather(_data);
            Latest = _data;
            LatestChanged?.Invoke();
        }
        catch
        {
            // Нет сети: остаются прежние данные (если были); следующая попытка через 30 минут.
        }
        finally
        {
            _loading = false;
            if (_reloadAgain) { _reloadAgain = false; Reload(); }
        }
    }
}

/// <summary>Плашка 200×36: слева время «ЧЧ:ММ» с перелистывающимися цифрами, справа иконка погоды и температура. Всё на линии значков других плашек (на 1,5 px выше центра).</summary>
internal sealed class IdlePill : Grid
{
    public const double ViewWidth = 200, ViewHeight = 36;

    private readonly RollingDigit[] _digits = [new(), new(), new(), new()];
    private readonly WeatherIcon _icon = new() { Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) };
    private readonly TextBlock _temp = new()
    {
        FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly StackPanel _weather = new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 16, 3), Visibility = Visibility.Collapsed
    };
    private bool _timeShown;

    // «Свет дня» (выбор пользователя 2026-11-15; сначала 3 %, затем повышено до 7–10 %): еле заметное пятно света за сутки проходит по плашке слева направо,
    // как солнце по небу: утром слева и тёплое, днём по центру и светлое, вечером справа и янтарное; ночью остаётся ровный холодный отсвет.
    // Форма совпадает с островом (скруглён снизу), чтобы свет не выходил за чёрную плашку.
    private readonly DaylightGlow _glow = new();
    private int _glowMinute = -1;

    public IdlePill()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Children.Add(_glow); // под временем и погодой

        var colon = new TextBlock
        {
            Text = ":", FontSize = RollingDigit.FontSizeValue, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 2)
        };
        var time = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 3), Children = { _digits[0], _digits[1], colon, _digits[2], _digits[3] }
        };
        Children.Add(time);

        _weather.Children.Add(_icon);
        _weather.Children.Add(_temp);
        Children.Add(_weather);
    }

    /// <summary>Показать время; цифры, которые изменились, перелистываются (первый показ без анимации).</summary>
    public void SetTime(DateTime now, bool animate)
    {
        var text = now.ToString("HH:mm", CultureInfo.InvariantCulture); // «14:07»
        var chars = new[] { text[0], text[1], text[3], text[4] };
        for (var i = 0; i < 4; i++) _digits[i].Set(chars[i], animate && _timeShown);
        _timeShown = true;
        var minute = now.Hour * 60 + now.Minute;
        if (minute != _glowMinute) { _glowMinute = minute; _glow.Update(now); } // свет двигается раз в минуту: движения глазом не видно
    }

    /// <summary>Иконка погоды на сейчас и температура; null — данных нет, остаётся одно время.</summary>
    public void SetWeather(WeatherData? data)
    {
        if (data == null) { _weather.Visibility = Visibility.Collapsed; return; }
        _icon.Kind = WeatherCodes.Describe(data.Code).Kind;
        _icon.IsDay = data.IsDay;
        _temp.Text = WeatherFormat.Temp(data.Temperature);
        _weather.Visibility = Visibility.Visible;

        // Настоящие восход и закат города, если они есть в прогнозе на сегодня.
        var today = data.Days.FirstOrDefault(d => d.Date == DateOnly.FromDateTime(data.LocalNow));
        if (today?.Sunrise is { } rise && today.Sunset is { } set && set > rise)
        {
            DaylightGlow.SetSunTimes(rise.TimeOfDay.TotalHours, set.TimeOfDay.TotalHours);
        }
        _glow.Update(DateTime.Now);
    }
}

/// <summary>
/// Одна цифра времени, которая при смене перелистывается вверх: старая уезжает вверх и гаснет, новая выезжает снизу
/// (как цифры в колёсах «Часов»). Ширина цифры постоянная, поэтому время не «ездит».
/// </summary>
internal sealed class RollingDigit : Grid
{
    public const double FontSizeValue = 14;
    private static readonly Dictionary<(double, int), double> Widths = [];

    private readonly double _fontSize;
    private readonly FontWeight _weight;
    private readonly double Height_;
    private readonly TextBlock _current;
    private readonly TextBlock _next;
    private readonly TranslateTransform _currentShift = new(), _nextShift = new();
    private char _shown = '\0';
    private int _token;

    /// <summary>Цифра заданного размера и начертания (по умолчанию как на плашке пустого острова: 14 px, полужирная).</summary>
    public RollingDigit(double fontSize = FontSizeValue, FontWeight? weight = null)
    {
        _fontSize = fontSize;
        _weight = weight ?? FontWeights.SemiBold;
        Height_ = fontSize == FontSizeValue ? 19 : Math.Ceiling(fontSize * 1.15);
        _current = Make();
        _next = Make();
        Width = MeasureDigit(fontSize, _weight);
        Height = Height_;
        ClipToBounds = true;
        _current.RenderTransform = _currentShift;
        _next.RenderTransform = _nextShift;
        _next.Opacity = 0;
        Children.Add(_current);
        Children.Add(_next);
    }

    private TextBlock Make() => new()
    {
        FontSize = _fontSize, FontWeight = _weight, Foreground = Hues.Ink,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private static double MeasureDigit(double size, FontWeight weight)
    {
        if (Widths.TryGetValue((size, weight.ToOpenTypeWeight()), out var cached)) return cached;
        double widest = 0;
        for (var d = '0'; d <= '9'; d++)
        {
            var t = new FormattedText(d.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, Hues.Ink, 1.0);
            widest = Math.Max(widest, t.WidthIncludingTrailingWhitespace);
        }
        var width = Math.Ceiling(widest) + 1;
        Widths[(size, weight.ToOpenTypeWeight())] = width;
        return width;
    }

    public void Set(char digit, bool animate)
    {
        if (digit == _shown) return;
        _shown = digit;
        var token = ++_token;
        if (!animate)
        {
            _current.BeginAnimation(OpacityProperty, null);
            _currentShift.BeginAnimation(TranslateTransform.YProperty, null);
            _next.BeginAnimation(OpacityProperty, null);
            _nextShift.BeginAnimation(TranslateTransform.YProperty, null);
            _current.Text = digit.ToString();
            _current.Opacity = 1;
            _currentShift.Y = 0;
            _next.Opacity = 0;
            return;
        }

        // Новая цифра выезжает снизу, прежняя уезжает вверх и гаснет (≈ 0,34 с, замедляясь к концу).
        _next.Text = digit.ToString();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(340);
        _nextShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Height_ * 0.9, 0, duration) { EasingFunction = ease });
        _next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _currentShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -Height_ * 0.9, duration) { EasingFunction = ease });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (token != _token) return; // за это время цифра сменилась ещё раз
            _current.BeginAnimation(OpacityProperty, null);
            _currentShift.BeginAnimation(TranslateTransform.YProperty, null);
            _next.BeginAnimation(OpacityProperty, null);
            _nextShift.BeginAnimation(TranslateTransform.YProperty, null);
            _current.Text = digit.ToString();
            _current.Opacity = 1;
            _currentShift.Y = 0;
            _next.Opacity = 0;
        };
        _current.BeginAnimation(OpacityProperty, fade);
    }
}
