using System.Globalization;
using System.Text.RegularExpressions;
using Ostrov.Modules.Player;
using Ostrov.Services;

namespace Ostrov.Search;

/// <summary>Распознанная команда: подпись для строки («Таймер 25 мин») и действие (возвращает текст ошибки или null).</summary>
/// <param name="AfterClose">Выполнить после закрытия карточки (открытие вкладки: остров не должен сворачиваться под руками).</param>
internal sealed record CommandMatch(string Label, Func<Task<string?>> Run, bool AfterClose = false);

/// <summary>
/// Команды в строке Alt+Q (решение пользователя 2026-11-14): таймер и секундомер, музыка и громкость, проекты и вкладки.
/// Распознаются сами по точным формам фраз; всё остальное остаётся вопросом к ИИ. Чистая логика без интерфейса, действия берутся из <see cref="IslandCommands"/>.
/// </summary>
internal static class CommandParser
{
    private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Dictionary<string, string> TabIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["плеер"] = "player", ["плейер"] = "player", ["музыка"] = "player", ["музыку"] = "player",
        ["погода"] = "weather", ["погоду"] = "weather",
        ["время"] = "time", ["таймер"] = "time", ["таймеры"] = "time",
        ["проекты"] = "projects", ["проектов"] = "projects",
        ["буфер"] = "buffer", ["буфер обмена"] = "buffer", ["историю буфера"] = "buffer",
        ["телефон"] = "phone", ["телефона"] = "phone", ["айфон"] = "phone",
        ["главная"] = "hub", ["главную"] = "hub", ["главный экран"] = "hub", ["хаб"] = "hub",
        ["центр"] = "hub", ["центр управления"] = "hub",
        ["настройки"] = "settings"
    };

    private static readonly Dictionary<string, string> TabTitles = new()
    {
        ["hub"] = "Главная", ["player"] = "Плеер", ["weather"] = "Погода", ["time"] = "Время", ["projects"] = "Проекты",
        ["buffer"] = "Буфер", ["phone"] = "Телефон", ["settings"] = "Настройки"
    };

    /// <summary>Команда, если фраза на неё похожа; иначе null (тогда это вопрос к ИИ или пример для калькулятора).</summary>
    public static CommandMatch? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 80) return null;
        var text = Regex.Replace(input.Trim().TrimEnd('.', '!'), @"\s+", " ").ToLowerInvariant();

        // Таймер
        if (Regex.IsMatch(text, @"^(?:отмени|останови|сбрось|выключи|убери) таймер$|^таймер (?:стоп|сброс|отмена|выкл\w*|останови\w*)$"))
            return Sync("Таймер: отмена", () => Call(IslandCommands.CancelTimer));
        var timer = Regex.Match(text, @"^(?:поставь |запусти |включи )?таймер(?: на)? (.+)$", Opt);
        if (timer.Success && TryDuration(timer.Groups[1].Value, out var duration))
        {
            var span = duration;
            return Sync($"Таймер {FormatSpan(span)}", () => Call(() => IslandCommands.StartTimer?.Invoke(span), IslandCommands.StartTimer != null));
        }

        // Секундомер
        if (Regex.IsMatch(text, @"^(?:запусти |включи )?секундомер$"))
            return Sync("Секундомер: старт", () => Call(IslandCommands.StartStopwatch));
        if (Regex.IsMatch(text, @"^секундомер (?:стоп|пауза)$|^(?:стоп|останови|пауза) секундомер\w*$"))
            return Sync("Секундомер: стоп", () => Call(IslandCommands.StopStopwatch));

        // Музыка
        if (Regex.IsMatch(text, @"^(?:пауза|поставь на паузу|pause)$"))
            return Async("Музыка: пауза", () => MediaControl.RunAsync(MediaControl.Action.Pause));
        if (Regex.IsMatch(text, @"^(?:играй|плей|play|продолжи|продолжай|включи музыку)$"))
            return Async("Музыка: играть", () => MediaControl.RunAsync(MediaControl.Action.Play));
        if (Regex.IsMatch(text, @"^(?:дальше|далее|следующий|следующая|следующий трек|следующая песня|next|скип)$"))
            return Async("Музыка: следующий трек", () => MediaControl.RunAsync(MediaControl.Action.Next));
        if (Regex.IsMatch(text, @"^(?:назад|предыдущий|предыдущая|предыдущий трек|предыдущая песня|prev|previous)$"))
            return Async("Музыка: предыдущий трек", () => MediaControl.RunAsync(MediaControl.Action.Previous));
        var volume = Regex.Match(text, @"^громкость (\d{1,3}) ?%?$");
        if (volume.Success && int.Parse(volume.Groups[1].Value, CultureInfo.InvariantCulture) is var level and <= 100)
            return Sync($"Громкость музыки {level} %", () => MediaControl.SetVolume(level));

        // Вкладки и проекты
        var open = Regex.Match(text, @"^(?:открой|открыть|покажи|вкладка|вкладку) (?:вкладку )?(.+)$");
        if (open.Success)
        {
            var target = open.Groups[1].Value.Trim();
            if (TabIds.TryGetValue(target, out var tabId))
                return Sync($"Открыть «{TabTitles[tabId]}»", () => { IslandBus.RequestTab(tabId); return null; }, afterClose: true);
            if (IslandCommands.FindProject?.Invoke(target) is { } project)
                return Sync($"Открыть проект {project.Name}", () => IslandCommands.OpenProject is { } openProject ? openProject(project.Path) : "Проекты недоступны");
        }
        else if (TabIds.TryGetValue(text, out var bare) && text is "таймер" or "таймеры")
        {
            return Sync($"Открыть «{TabTitles[bare]}»", () => { IslandBus.RequestTab(bare); return null; }, afterClose: true);
        }
        return null;
    }

    private static string? Call(Action? action, bool available = true)
    {
        if (action == null || !available) return "Функция недоступна";
        action();
        return null;
    }

    private static CommandMatch Sync(string label, Func<string?> run, bool afterClose = false) => new(label, () => Task.FromResult(run()), afterClose);

    private static CommandMatch Async(string label, Func<Task<string?>> run) => new(label, run);

    /// <summary>Длительность из «25», «25 мин», «1:30», «1ч 30м», «90 сек», «1,5 часа»; одно число без единицы — минуты.</summary>
    internal static bool TryDuration(string input, out TimeSpan duration)
    {
        duration = default;
        var s = input.Trim();
        var clock = Regex.Match(s, @"^(\d{1,2}):(\d{2})(?::(\d{2}))?$");
        if (clock.Success)
        {
            var a = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
            duration = clock.Groups[3].Success
                ? new TimeSpan(a, b, int.Parse(clock.Groups[3].Value, CultureInfo.InvariantCulture))   // ч:мм:сс
                : new TimeSpan(0, a, b);                                                                // мм:сс
            return Valid(duration);
        }

        var all = Regex.Match(s, @"^(?:\s*(\d+(?:[.,]\d+)?)\s*([a-zа-яё]*))+$", Opt);
        if (!all.Success) return false;
        var numbers = all.Groups[1].Captures;
        var units = all.Groups[2].Captures;
        double seconds = 0;
        for (var i = 0; i < numbers.Count; i++)
        {
            var value = double.Parse(numbers[i].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var unit = units[i].Value.ToLowerInvariant();
            if (unit.Length == 0)
            {
                if (numbers.Count != 1) return false; // «1 30» неоднозначно
                seconds += value * 60;
            }
            else if (unit.StartsWith('ч') || unit.StartsWith('h')) seconds += value * 3600;
            else if (unit.StartsWith('м') || unit.StartsWith('m')) seconds += value * 60;
            else if (unit.StartsWith('с') || unit.StartsWith('s')) seconds += value;
            else return false;
        }
        duration = TimeSpan.FromSeconds(Math.Round(seconds));
        return Valid(duration);
    }

    private static bool Valid(TimeSpan d) => d >= TimeSpan.FromSeconds(1) && d <= TimeSpan.FromHours(99);

    internal static string FormatSpan(TimeSpan d)
    {
        var parts = new List<string>();
        if ((int)d.TotalHours > 0) parts.Add($"{(int)d.TotalHours} ч");
        if (d.Minutes > 0) parts.Add($"{d.Minutes} мин");
        if (d.Seconds > 0) parts.Add($"{d.Seconds} сек");
        return string.Join(" ", parts);
    }
}
