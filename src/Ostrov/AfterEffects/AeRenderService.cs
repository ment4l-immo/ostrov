using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.AfterEffects;

/// <summary>
/// Рендер After Effects на пассивном индикаторе свёрнутого острова. Раз в секунду читает файл состояния, который пишет скрипт
/// <c>tools/ae/ostrov-render-notify.jsx</c> (раз в секунду, пока AE свободен), и смотрит, жив ли AfterFX.exe; решает <see cref="RenderTracker"/>.
/// Ни сети, ни портов: только файл в <c>%AppData%\Ostrov\ae-events</c> (для проверок папку заменяет переменная окружения <c>OSTROV_AE_DIR</c>).
/// Для просмотра вида без AE: файл <c>demo.txt</c> в той же папке (см. <c>tools/ae/ae-demo.ps1</c>).
/// </summary>
internal sealed class AeRenderService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RenderTracker _tracker = new();
    private readonly RenderActivityView _view = new();
    private readonly RenderWideView _wide = new();          // расширенный вид по правому клику
    private readonly StatusBubble _bubble = new(Hues.Ink); // кружок, когда рендер не главная активность
    private readonly CompactStrip _strip = new(Hues.Ink); // нотч в полноэкранном видео: бегущая белая полоска
    private readonly string _dir;
    private bool _shown, _shownDismissable;
    private RenderPhase? _lastPhase;

    /// <summary>Рендер успешно закончился (фаза сменилась на «готово»): на это ждёт «после рендера…» (<see cref="RenderAfterAction"/>).</summary>
    public static event Action? RenderFinished;

    public AeRenderService(string? directory = null)
    {
        _dir = directory
            ?? (Environment.GetEnvironmentVariable("OSTROV_AE_DIR") is { Length: > 0 } fromEnv ? fromEnv
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "ae-events"));
    }

    private string StatePath => Path.Combine(_dir, "state.txt");
    private string DemoPath => Path.Combine(_dir, "demo.txt");

    public void Start()
    {
        IslandBus.ActivityDismissed += OnDismissed;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        IslandBus.ActivityDismissed -= OnDismissed;
    }

    private void OnDismissed()
    {
        _tracker.Dismiss();
        _shown = false;
        try { File.Delete(DemoPath); } catch { /* демо-файла нет */ }
    }

    private void Tick()
    {
        try
        {
            Apply(ReadDemo() ?? Real());
        }
        catch
        {
            // Индикатор рендера не должен ронять остров из-за неожиданного файла или процесса.
        }
    }

    private RenderView? Real()
    {
        var aeStart = GetAeStartUtc();
        var snapshot = aeStart == null ? null : ReadSnapshot();
        return _tracker.Update(DateTime.UtcNow, aeStart, snapshot, OutputReady);
    }

    private void Apply(RenderView? view)
    {
        if (view is null)
        {
            _lastPhase = null;
            if (_shown) { IslandBus.ClearActivity(); _shown = false; }
            return;
        }
        if (_lastPhase != view.Phase)
        {
            var wasRendering = _lastPhase == RenderPhase.Rendering;
            _lastPhase = view.Phase;
            if (view.Phase == RenderPhase.Done && wasRendering) RenderFinished?.Invoke(); // только настоящий конец рендера, а не «готово» с прошлого раза
        }

        _view.Update(view);
        _wide.Update(view);
        switch (view.Phase)
        {
            case RenderPhase.Rendering: _bubble.ShowWorking(); break;
            case RenderPhase.Done: _bubble.ShowResult("", iconFont: true); break;
            default: _bubble.ShowResult("!", iconFont: false); break;
        }
        // Нотч: идёт рендер — бегущая полоска; готово — ровно горящая; ошибка — мигающая. Висят до клика, как и плашка.
        switch (view.Phase)
        {
            case RenderPhase.Rendering: _strip.ShowRunning(paused: false); break;
            case RenderPhase.Done: _strip.ShowSolid(0); break;
            default: _strip.ShowSolid(0.7); break;
        }
        var dismissable = view.Phase != RenderPhase.Rendering; // «готово» и «ошибка» висят до клика
        if (!_shown || dismissable != _shownDismissable)
        {
            IslandBus.ShowActivity(_view, RenderActivityView.ViewWidth, RenderActivityView.ViewHeight, dismissable, _strip,
                _bubble, _wide, RenderWideView.ViewWidth, _wide.Height);
            _shown = true;
            _shownDismissable = dismissable;
        }
    }

    // ---- Демо: «rendering <секунды Unix начала>» / «done» / «error»; файл старше 30 минут игнорируется ----
    private RenderView? ReadDemo()
    {
        try
        {
            var info = new FileInfo(DemoPath);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromMinutes(30)) return null;
            var parts = File.ReadAllText(DemoPath).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts.FirstOrDefault())
            {
                case "rendering":
                    var start = parts.Length > 1 && long.TryParse(parts[1], out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : info.LastWriteTimeUtc;
                    return new RenderView(RenderPhase.Rendering, DateTime.UtcNow - start, "After Effects · рендер", "");
                case "done":
                    return new RenderView(RenderPhase.Done, TimeSpan.FromSeconds(78), "Рендер завершён", "Comp 1 · " + RenderTracker.FormatElapsed(TimeSpan.FromSeconds(78)));
                case "error":
                    return new RenderView(RenderPhase.Error, TimeSpan.Zero, "Ошибка рендера", "Проверь After Effects");
            }
        }
        catch
        {
            // Демо-файл не читается: просто не показываем его.
        }
        return null;
    }

    // ---- Настоящие данные ----
    private static DateTime? GetAeStartUtc()
    {
        var processes = Process.GetProcessesByName("AfterFX");
        try
        {
            DateTime? earliest = null;
            foreach (var p in processes)
            {
                try
                {
                    var start = p.StartTime.ToUniversalTime();
                    if (earliest == null || start < earliest) earliest = start;
                }
                catch
                {
                    // Нет доступа к времени запуска этого процесса — пропускаем.
                }
            }
            return earliest;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>Файл состояния: строки <c>item=СТАТУС|секунды|композиция|путь</c> (остальные строки пропускаем).</summary>
    private RenderSnapshot? ReadSnapshot()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            string text;
            using (var fs = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return null; // скрипт как раз переписывает файл
            return Parse(text, File.GetLastWriteTimeUtc(StatePath));
        }
        catch
        {
            return null;
        }
    }

    internal static RenderSnapshot Parse(string text, DateTime beatUtc)
    {
        var items = new List<RenderItem>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("item=", StringComparison.Ordinal)) continue;
            var parts = line[5..].Split('|', 4);
            if (parts.Length < 4) continue;
            _ = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var elapsed);
            items.Add(new RenderItem(parts[0], elapsed, parts[2], parts[3]));
        }
        return new RenderSnapshot(beatUtc, items);
    }

    /// <summary>Готовый файл есть, он новее начала рендера и временного <c>.m4v</c> рядом нет (проверено: mp4 создаётся только в самом конце).</summary>
    private static bool OutputReady(RenderItem item, DateTime startedUtc)
    {
        try
        {
            var path = item.Output;
            if (string.IsNullOrEmpty(path) || path.Contains('#') || !File.Exists(path)) return false; // последовательности картинок не угадываем
            if (File.GetLastWriteTimeUtc(path) < startedUtc) return false;
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            return !Directory.EnumerateFiles(dir, name + ".*.m4v").Any();
        }
        catch
        {
            return false;
        }
    }
}
