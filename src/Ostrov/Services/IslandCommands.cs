namespace Ostrov.Services;

/// <summary>
/// Точки, через которые команды из строки Alt+Q («таймер 25», «открой ostrov») дотягиваются до функций острова.
/// Каждая функция сама подключает свои действия при запуске, строка Alt+Q ничего не знает об их устройстве.
/// </summary>
internal static class IslandCommands
{
    /// <summary>Запустить таймер на заданное время (запущенный сбрасывается).</summary>
    public static Action<TimeSpan>? StartTimer { get; set; }

    /// <summary>Остановить и сбросить таймер.</summary>
    public static Action? CancelTimer { get; set; }

    public static Action? StartStopwatch { get; set; }
    public static Action? StopStopwatch { get; set; }

    /// <summary>Найти проект из «Избранных» по части названия папки: имя для показа и путь.</summary>
    public static Func<string, (string Name, string Path)?>? FindProject { get; set; }

    /// <summary>Открыть папку проекта в VS Code; вернуть текст ошибки или null.</summary>
    public static Func<string, string?>? OpenProject { get; set; }
}
