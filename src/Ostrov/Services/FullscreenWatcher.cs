using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Ostrov.Services;

/// <summary>Что сейчас на весь экран: ничего, видео (браузер или видеоплеер) или «игра» (любая другая программа).</summary>
public enum FullscreenKind { None, Video, Game }

/// <summary>
/// Следит, занят ли экран полноэкранным приложением (игра, видео, презентация), и определяет, видео это или игра.
/// Использует системный вызов SHQueryUserNotificationState и опрашивает его раз в секунду; если экран занят,
/// по имени программы окна переднего плана решает: браузеры и видеоплееры = видео, всё остальное = игра
/// (решение пользователя 2026-11-12; список программ потом можно вынести в настройки).
/// Для проверок: переменная окружения <c>OSTROV_FAKE_FULLSCREEN</c> = <c>video</c> или <c>game</c> держит нужный режим всегда.
/// </summary>
public sealed class FullscreenWatcher
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private static readonly string OwnProcess = Process.GetCurrentProcess().ProcessName;

    /// <summary>Нужно ли сжимать остров, когда в фокусе эта программа (имя процесса без .exe): список в настройках, раздел «Программы».</summary>
    public static bool IsShrinkApp(string? processName) => processName is { Length: > 0 } && ShrinkApps.IsEnabled(processName);

    /// <summary>Программа в фокусе (последняя, кроме самого острова: его окно активируется при раскрытии и состояние менять не должно).</summary>
    public string? Foreground { get; private set; }

    /// <summary>Сменилась программа в фокусе.</summary>
    public event Action<string?>? ForegroundChanged;

    // Имена процессов (без .exe, в нижнем регистре), которые считаем «видео»: браузеры и видеоплееры.
    private static readonly HashSet<string> VideoApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "browser", "yandex", "arc", "iexplore", "waterfox", "librewolf", "chromium",
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayer64", "potplayermini", "potplayermini64",
        "mpv", "wmplayer", "kmplayer", "smplayer", "video.ui", "movies"
    };

    public FullscreenKind Kind { get; private set; }

    /// <summary>Срабатывает при каждом изменении: что именно сейчас на весь экран.</summary>
    public event Action<FullscreenKind>? Changed;

    public FullscreenWatcher() => _timer.Tick += (_, _) => Check();

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Check()
    {
        var now = Query();
        if (now != Kind)
        {
            Kind = now;
            Changed?.Invoke(now);
        }

        var fake = Environment.GetEnvironmentVariable("OSTROV_FAKE_FOREGROUND"); // для проверок: например, chrome
        var foreground = fake ?? ForegroundProcessName();
        if (foreground != null && !foreground.Equals(OwnProcess, StringComparison.OrdinalIgnoreCase) && foreground != Foreground)
        {
            Foreground = foreground;
            ForegroundChanged?.Invoke(foreground);
        }
    }

    private static FullscreenKind Query()
    {
        var fake = Environment.GetEnvironmentVariable("OSTROV_FAKE_FULLSCREEN");
        if (fake is not null)
            return fake.Equals("video", StringComparison.OrdinalIgnoreCase) ? FullscreenKind.Video
                : fake.Equals("game", StringComparison.OrdinalIgnoreCase) ? FullscreenKind.Game : FullscreenKind.None;

        if (!QueryIsFullscreen()) return FullscreenKind.None;
        return Classify(ForegroundProcessName());
    }

    /// <summary>Браузеры и видеоплееры — видео; всё остальное (в том числе неизвестная программа) — игра: в играх остров молчит.</summary>
    public static FullscreenKind Classify(string? processName) =>
        processName is { Length: > 0 } && VideoApps.Contains(processName) ? FullscreenKind.Video : FullscreenKind.Game;

    /// <summary>Имя процесса окна переднего плана (без .exe) или null, если определить не удалось.</summary>
    public static string? ForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return null; }
    }

    private static bool QueryIsFullscreen()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        // BUSY — полноэкранное приложение, D3D_FULL_SCREEN — полноэкранная игра, PRESENTATION_MODE — показ презентации.
        // Режим «Не беспокоить» (QUIET_TIME) намеренно не считаем: остров в нём прятать не нужно.
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
