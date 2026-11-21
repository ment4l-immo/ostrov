using System.Windows;
using Ostrov.Services;

namespace Ostrov.Modules.Weather;

/// <summary>
/// Вкладка «Погода»: сейчас, прогноз на 5 дней и подробности дня (Open-Meteo).
/// Город выбирается поиском во вкладке и запоминается.
/// </summary>
public sealed class WeatherModule : IIslandModule
{
    // Шире и выше прежних 520×290 / 300 (пожелание пользователя): больше воздуха между дугой солнца и плитками дней и до вкладок.
    private const double OverviewHeight = 338, DetailHeight = 340;
    private WeatherView? _view;
    private readonly IdleActivity _idle = new(); // плашка пустого острова: дуга дня и температура

    public string Id => "weather";
    public string Title => "Погода";
    public string Icon => "";

    /// <summary>В обзоре обычная высота; когда раскрыт день — остров вытягивается вниз.</summary>
    public double ExpandedHeight => _view?.IsDetailed == true ? DetailHeight : OverviewHeight;

    public double ExpandedWidth => 560;

    public FrameworkElement CreateView()
    {
        var settings = AppSettings.Load();
        City? city = string.IsNullOrWhiteSpace(settings.WeatherCity)
            ? null
            : new City(settings.WeatherCity, "", settings.WeatherLatitude, settings.WeatherLongitude);

        _view = new WeatherView(city, saved =>
        {
            // Читаем настройки заново, чтобы не затереть то, что могло измениться в других местах.
            var s = AppSettings.Load();
            s.WeatherCity = saved.Name;
            s.WeatherLatitude = saved.Latitude;
            s.WeatherLongitude = saved.Longitude;
            s.Save();
            _idle.Reload(); // выбрали другой город: плашка пустого острова берёт погоду заново
        });
        _view.DetailChanged += () => IslandBus.RequestHeight(Id);
        return _view;
    }
}
