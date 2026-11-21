using System.Windows;
using Ostrov.Services;
using Ostrov.Tray;

namespace Ostrov;

public partial class App : Application
{
    private TrayIcon? _tray;
    private Claude.ClaudePrompts? _prompts;
    private Claude.ClaudeWork? _claudeWork;
    private Search.SearchService? _search;
    private AfterEffects.AeRenderService? _aeRender;
    private Modules.Flash.SystemHints? _hints;
    private Modules.Downloads.DownloadWatcher? _downloads;
    private Reminders.ReminderService? _reminders;
    private Modules.Disk.DiskWatcher? _disk;
    private Services.DayJournal? _journal;
    private Dictation.DictationService? _dictation;
    private readonly FullscreenWatcher _fullscreen = new();

    private static int _crashLogged;

    // Журнал сбоев: только тип, сообщение и стек, до 200 КБ; без пользовательских данных. Не больше 50 записей за запуск (чтобы ошибка на каждом кадре не раздувала файл).
    private static void LogCrash(string kind, Exception? ex)
    {
        try
        {
            if (System.Threading.Interlocked.Increment(ref _crashLogged) > 50) return;
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "crash.log");
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 200_000) System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{kind}] {ex}\r\n\r\n");
        }
        catch { }
    }

    // Второй запуск не нужен: он дал бы второй остров, второй перехватчик правого Alt, второй канал Claude и второй мост (предложение другого агента, принято).
    private static System.Threading.Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new System.Threading.Mutex(true, @"Local\Ostrov.SingleInstance.v1", out var firstInstance);
        if (!firstInstance) { Shutdown(); return; }

        // Страховка: ошибка в одной функции не должна убивать весь остров. Пишем коротко в журнал и продолжаем.
        DispatcherUnhandledException += (_, a) => { LogCrash("ui", a.Exception); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => { LogCrash("task", a.Exception); a.SetObserved(); };

        AutostartService.EnsureDefaultOn(AppSettings.Load());
        Search.Calculator.Degrees = AppSettings.Load().TrigInDegrees;
        AutostartService.RefreshPath();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _tray = new TrayIcon(window.ToggleVisibility, () => Shutdown());

        // Запросы разрешений от Claude Code (хук присылает их по именованному каналу).
        _prompts = new Claude.ClaudePrompts(Dispatcher);
        _prompts.Start();

        // «Claude работает» / «Claude закончил» на пассивном индикаторе (хуки UserPromptSubmit и Stop).
        _journal = new Services.DayJournal();
        _journal.Start();
        _claudeWork = new Claude.ClaudeWork();
        _prompts.EventReceived += _claudeWork.OnEvent;

        // Быстрый поиск по горячей клавише (карточка на острове).
        _search = new Search.SearchService();
        _search.Start();

        // Рендер After Effects: пассивный индикатор в свёрнутом острове (скрипт в AE пишет файл состояния).
        _aeRender = new AfterEffects.AeRenderService();
        _aeRender.Start();

        // Короткие подсказки: язык ввода, Caps/Num Lock, громкость, микрофон (М-020) и загрузки браузера (М-021).
        _hints = new Modules.Flash.SystemHints();
        _hints.Start();
        _downloads = new Modules.Downloads.DownloadWatcher();
        _downloads.Start();
        Modules.Phone.PhoneLink.Instance.Start(); // заряд и уведомления iPhone (вкладка «Телефон», вспышки)

        // Напоминания от ИИ, «после рендера…» и предупреждение о нехватке места на диске (решения 2026-11-15).
        _reminders = new Reminders.ReminderService();
        AfterEffects.AeRenderService.RenderFinished += AfterEffects.RenderAfterAction.OnRenderFinished;
        _disk = new Modules.Disk.DiskWatcher();
        _disk.Start();

        // Голосовая диктовка: зажал правый Alt — говоришь (решения пользователя 2026-11-16).
        _dictation = new Dictation.DictationService();
        _dictation.Start();

        // История буфера обмена и полка для файлов работают, даже если вкладку «Буфер» ни разу не открывали.
        Modules.Buffer.ClipboardService.Instance.Start();

        // В полноэкранных играх остров прячется сам; в полноэкранном видео сжимается в нотч (таймер, рендер AE).
        _fullscreen.Changed += window.SetFullscreenKind;
        _fullscreen.ForegroundChanged += window.SetForegroundApp; // в браузере остров сжимается в нотч
        _fullscreen.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _fullscreen.Stop();
        _claudeWork?.Dispose();
        _prompts?.Dispose();
        _search?.Dispose();
        _aeRender?.Dispose();
        _hints?.Dispose();
        _dictation?.Dispose();
        _journal?.Dispose();
        Services.ServerTunnel.Stop(); // ssh-тоннель до сервера не должен остаться висеть после выхода
        Modules.Phone.PhoneLink.Instance.Dispose();
        _downloads?.Dispose();
        Modules.Buffer.ClipboardService.Instance.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
