namespace Ostrov.Modules;

/// <summary>Список функций, которые показываются вкладками в раскрытом острове (по порядку).</summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<IIslandModule> All { get; } =
    [
        new Hub.HubModule(), // главная: открывается при каждом раскрытии острова
        new Player.PlayerModule(),
        new Weather.WeatherModule(),
        new Time.TimeModule(),
        new Projects.ProjectsModule(),
        new Buffer.BufferModule(),
        new Phone.PhoneModule(), // «Новости» спрятаны (2026-11-15), код остался: вернуть строку new News.NewsModule()
        new Settings.SettingsModule(), // шестерёнка всегда последняя (самая правая)
    ];
}
