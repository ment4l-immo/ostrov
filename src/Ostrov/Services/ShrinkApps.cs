namespace Ostrov.Services;

/// <summary>
/// Программы, над которыми свёрнутый остров сжимается в нотч, пока они в фокусе (М-006: «база приложений» в настройках, раздел «Программы»).
/// По умолчанию браузеры (у них полоса вкладок у верхнего края, остров её закрывал). Список хранится в настройках: включённые и выключенные
/// программы, а также добавленные самим пользователем.
/// </summary>
public static class ShrinkApps
{
    private static readonly (string Process, string Title)[] Defaults =
    [
        ("chrome", "Google Chrome"), ("msedge", "Microsoft Edge"), ("firefox", "Firefox"), ("opera", "Opera"), ("opera_gx", "Opera GX"),
        ("brave", "Brave"), ("vivaldi", "Vivaldi"), ("browser", "Яндекс Браузер"), ("arc", "Arc"), ("waterfox", "Waterfox"),
        ("librewolf", "LibreWolf"), ("chromium", "Chromium")
    ];

    private static HashSet<string> _enabled = Build(AppSettings.Load());

    /// <summary>Список для показа в настройках: сохранённый, а если его ещё не меняли, то стандартный (все включены).</summary>
    public static List<AppListEntry> Entries(AppSettings settings) =>
        settings.ShrinkApps ?? Defaults.Select(d => new AppListEntry { Process = d.Process, Title = d.Title, Enabled = true }).ToList();

    /// <summary>Стандартная ли это программа списка (её можно выключить, но не убрать).</summary>
    public static bool IsDefault(string process) => Defaults.Any(d => string.Equals(d.Process, process, StringComparison.OrdinalIgnoreCase));

    /// <summary>Включена ли программа (имя процесса без .exe, регистр не важен).</summary>
    public static bool IsEnabled(string processName) => _enabled.Contains(processName);

    /// <summary>Перечитать список из настроек (после изменения в разделе «Программы»).</summary>
    public static void Reload() => _enabled = Build(AppSettings.Load());

    private static HashSet<string> Build(AppSettings settings) =>
        new(Entries(settings).Where(e => e.Enabled).Select(e => e.Process), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Имена процессов программ, к которым относятся пассивные активности: пока пользователь в такой программе, активность не показывается (М-014).</summary>
public static class ActivityApps
{
    /// <summary>VS Code (в нём живёт Claude Code).</summary>
    public static readonly string[] VsCode = ["Code", "Code - Insiders"];

    /// <summary>After Effects (рендер).</summary>
    public static readonly string[] AfterEffects = ["AfterFX"];

    public static bool IsVsCode(string? processName) => processName != null && VsCode.Any(n => n.Equals(processName, StringComparison.OrdinalIgnoreCase));
}
