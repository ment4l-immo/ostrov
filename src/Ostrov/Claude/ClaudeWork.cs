using System.IO;
using System.Text;
using System.Windows.Threading;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Claude;

/// <summary>
/// «Claude работает» и «Claude закончил» на пассивном индикаторе острова. Источник — хуки Claude Code
/// <c>UserPromptSubmit</c> (начало) и <c>Stop</c> (конец), которые приходят по тому же каналу, что и запросы разрешений.
/// Несколько сессий (окон VS Code) считаются по номеру сессии: пока работает хоть одна — «работает» (с «×N», если их несколько);
/// каждое окончание — отдельный знак «закончил», он висит до клика и перекрывает «работает» (дальше показывается следующий знак или снова «работает»).
/// Если Claude остановили клавишей Esc, хук <c>Stop</c> не приходит: тогда по файлу переписки (последняя запись «Request interrupted…»)
/// сессия убирается без знака, а сессия, от которой нет ни хуков, ни записей в переписке больше 15 минут, тихо забывается.
/// В полноэкранном видео вместо плашки показывается нотч: бегущая коралловая полоска, а на конце окошко «Claude закончил».
/// Работает на потоке интерфейса. В журнал <c>%AppData%\Ostrov\claude-events.log</c> пишутся только событие, проект и время (без текста запросов и ответов).
/// </summary>
internal sealed class ClaudeWork : IDisposable
{
    private const string EntryId = "claude";
    private const int Priority = 110; // выше рендера After Effects (100): договорённость 2026-11-13
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleInTool = TimeSpan.FromHours(3);

    private sealed class Session
    {
        public required string Id { get; init; }
        public required string Project { get; init; }
        public string? Transcript { get; init; }
        public DateTime StartedUtc { get; init; }
        public DateTime LastSignUtc { get; set; }
        public string? Title { get; set; }      // короткое название сессии из переписки (ai-title)
        public string? Step { get; set; }       // что делает сейчас («читает файл.cs», «думает»)
        public DateTime StepUtc { get; set; }   // когда шаг сменился: показываем шаг самой «свежей» сессии
    }

    private sealed record Done(string SessionId, string Project, TimeSpan? Elapsed, string Cwd);

    private readonly ClaudeActivityView _view = new();
    private readonly ClaudeWideView _wide = new();                   // расширенный вид по правому клику
    private readonly StatusBubble _bubble = new(ClaudeStyle.Coral);   // кружок, когда Claude не главная активность
    private readonly CompactStrip _strip = new(ClaudeStyle.Coral);
    private readonly ClaudeDoneAlert _alert = new();
    private readonly Dictionary<string, Session> _working = [];
    private readonly List<Done> _done = [];
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _ticks;
    private bool _entryIsDone;
    private bool _expanded; // список сессий раскрыт (пользователь нажал стрелку)

    public ClaudeWork()
    {
        _tick.Tick += (_, _) => OnTick();
        _strip.ShowRunning(paused: false);
    }

    public void Dispose() => _tick.Stop();

    /// <summary>Событие хука (на потоке интерфейса).</summary>
    public void OnEvent(ClaudeEvent e)
    {
        Log($"{e.Name} | {e.Project} | {Short(e.SessionId)}" + (e.Tool != null ? " | " + e.Tool : ""));
        var now = DateTime.UtcNow;
        switch (e.Name)
        {
            case ClaudeEvent.PreTool:
            case ClaudeEvent.PostTool:
            {
                if (e.Name == ClaudeEvent.PreTool) Ostrov.Services.DayJournal.Current?.NoteClaudeStep();
                // Шаг работы: перед инструментом — что он делает, после — снова «думает». Незнакомую сессию заводит только начало шага
                // (остров мог запуститься посреди работы); конец шага без сессии игнорируем (запоздалое событие после Esc).
                var isNew = false;
                if (!_working.TryGetValue(e.SessionId, out var session))
                {
                    if (e.Name == ClaudeEvent.PostTool) return;
                    _done.RemoveAll(d => d.SessionId == e.SessionId);
                    _working[e.SessionId] = session = new Session
                    {
                        Id = e.SessionId, Project = e.Project, Transcript = e.TranscriptPath, StartedUtc = now, LastSignUtc = now
                    };
                    isNew = true;
                }
                session.LastSignUtc = now;
                session.Step = e.Name == ClaudeEvent.PreTool ? e.Step : Thinking;
                session.StepUtc = now;
                // Идёт «работает»: меняем только подпись, без пересоздания записи в центре активностей.
                if (!isNew && !_entryIsDone)
                {
                    var rows = BuildRows();
                    _view.ShowWorking(Format(OldestElapsed()), _working.Count, CurrentStep());
                    _view.UpdateRows(rows);
                    _wide.ShowWorking(Format(OldestElapsed()), _working.Count, CurrentStep(), FreshProject(), rows);
                }
                else Refresh();
                return;
            }
            case ClaudeEvent.PromptSubmit:
                Ostrov.Services.DayJournal.Current?.NoteClaudePrompt(e.SessionId);
                // Новый запрос в этой сессии: прежнее «закончил» от неё уже неактуально.
                _done.RemoveAll(d => d.SessionId == e.SessionId);
                _working[e.SessionId] = new Session
                {
                    Id = e.SessionId, Project = e.Project, Transcript = e.TranscriptPath, StartedUtc = now, LastSignUtc = now,
                    Step = Thinking, StepUtc = now
                };
                break;
            case ClaudeEvent.Stop:
                TimeSpan? elapsed = _working.TryGetValue(e.SessionId, out var s) ? now - s.StartedUtc : null; // начало могли не застать (остров запущен позже)
                _working.Remove(e.SessionId);
                if (elapsed is { } worked) Ostrov.Services.DayJournal.Current?.NoteClaudeDone(e.Project, worked);
                _done.RemoveAll(d => d.SessionId == e.SessionId);
                _done.Add(new Done(e.SessionId, e.Project, elapsed, e.Cwd));
                break;
        }
        Refresh();
    }

    private void Refresh()
    {
        if (_done.Count > 0)
        {
            var d = _done[^1];
            var detail = d.Elapsed is { } t ? $"{d.Project} · {Format(t)}" : d.Project;
            _view.ShowDone(detail);
            _wide.ShowDone(_done.Count > 1 ? $"{detail} · ещё {_done.Count - 1}" : detail);
            _bubble.ShowResult("", iconFont: true);
            _entryIsDone = true;
            ActivityCenter.Set(new ActivityEntry
            {
                Id = EntryId, Priority = Priority, Pill = _view, PillWidth = ClaudeActivityView.ViewWidth, PillHeight = ClaudeActivityView.ViewHeight,
                DismissOnClick = true, OnDismissed = DismissDone,
                Compact = _alert, CompactWidth = ClaudeDoneAlert.ViewWidth, CompactHeight = ClaudeDoneAlert.ViewHeight,
                Bubble = _bubble, Wide = _wide, WideWidth = ClaudeWideView.ViewWidth, WideHeight = _wide.PreferredHeight,
                HideWhenForeground = ActivityApps.VsCode
            });
        }
        else if (_working.Count > 0)
        {
            if (_working.Count < 2) _expanded = false;
            var rows = BuildRows();
            _view.ShowWorking(Format(OldestElapsed()), _working.Count, CurrentStep());
            _view.SetSessions(rows, _expanded);
            _wide.ShowWorking(Format(OldestElapsed()), _working.Count, CurrentStep(), FreshProject(), rows);
            _bubble.ShowWorking();
            _entryIsDone = false;
            var several = _working.Count >= 2;
            ActivityCenter.Set(new ActivityEntry
            {
                Id = EntryId, Priority = Priority, Pill = _view, PillWidth = ClaudeActivityView.ViewWidth, PillHeight = _view.PreferredHeight,
                Compact = _strip,
                Bubble = _bubble, Wide = _wide, WideWidth = ClaudeWideView.ViewWidth, WideHeight = _wide.PreferredHeight,
                HideWhenForeground = ActivityApps.VsCode, // пока ты в VS Code, статус не нужен (М-014)
                // Две сессии и больше: при наведении стрелка, по клику на неё список раскрывается/сворачивается.
                OnHover = several ? _view.SetArrowHover : null,
                OnPillClick = several ? point => { if (!ClaudeActivityView.ArrowHit(point)) return false; _expanded = !_expanded; Refresh(); return true; } : null
            });
        }
        else
        {
            _entryIsDone = false;
            ActivityCenter.Remove(EntryId);
        }

        if (_working.Count > 0) _tick.Start();
        else _tick.Stop();
    }

    // Клик по знаку «закончил»: открывает проект в VS Code (окно этой папки выходит на передний план) и убирает знак;
    // дальше следующий знак или снова «работает».
    private void DismissDone()
    {
        if (_done.Count > 0)
        {
            var last = _done[^1];
            _done.RemoveAt(_done.Count - 1);
            try
            {
                if (!string.IsNullOrWhiteSpace(last.Cwd) && System.IO.Directory.Exists(last.Cwd))
                    Ostrov.Modules.Projects.VsCode.TryOpen(last.Cwd, out _);
            }
            catch { /* не открылось: знак всё равно убран */ }
        }
        Refresh();
    }

    private const string Thinking = "думает";

    // Шаг самой «свежей» сессии (при нескольких сессиях показываем ту, где что-то менялось последним).
    private string? CurrentStep() => _working.Values.OrderByDescending(s => s.StepUtc).FirstOrDefault()?.Step;

    private string FreshProject() => _working.Values.OrderByDescending(s => s.StepUtc).FirstOrDefault()?.Project ?? "";

    // Строки списка: старейшая сессия сверху; название — из переписки, пока его нет — проект.
    private List<ClaudeSessionRow> BuildRows()
    {
        var now = DateTime.UtcNow;
        return _working.Values.OrderBy(s => s.StartedUtc)
            .Select(s => new ClaudeSessionRow(s.Title is { Length: > 0 } t ? t : s.Project, s.Project, s.Step ?? "работает", Format(now - s.StartedUtc)))
            .ToList();
    }

    private TimeSpan OldestElapsed()
    {
        var oldest = _working.Values.Min(s => s.StartedUtc);
        return DateTime.UtcNow - oldest;
    }

    private void OnTick()
    {
        if (_working.Count == 0) { _tick.Stop(); return; }
        if (!_entryIsDone)
        {
            _view.SetElapsed(Format(OldestElapsed()));
            _wide.SetElapsed(Format(OldestElapsed()));
            if (_working.Count >= 2)
            {
                var rows = BuildRows();
                _view.UpdateRows(rows);
                _wide.UpdateRows(rows);
            }
        }

        // Раз в 2 секунды проверяем, не остановили ли Claude клавишей Esc (хук Stop тогда не приходит) и не пропала ли сессия совсем.
        if (++_ticks % 2 != 0) return;
        var now = DateTime.UtcNow;
        var gone = new List<string>();
        foreach (var s in _working.Values)
        {
            if (s.Transcript is { Length: > 0 } path)
            {
                if (_working.Count >= 2 && TranscriptTail.LastAiTitle(path) is { } title) s.Title = title;
                var info = new FileInfo(path);
                if (info.Exists && info.LastWriteTimeUtc > s.LastSignUtc) s.LastSignUtc = info.LastWriteTimeUtc;
                if (TranscriptTail.EndsWithInterruption(path)) { gone.Add(s.Id); Log($"interrupted | {s.Project} | {Short(s.Id)}"); continue; }
            }
            // Пока идёт долгий инструмент (команда, субагент), хуки и переписка молчат: терпим дольше. Esc ловится по переписке выше.
            var limit = s.Step is { } step && step != Thinking ? StaleInTool : Stale;
            if (now - s.LastSignUtc > limit) { gone.Add(s.Id); Log($"stale | {s.Project} | {Short(s.Id)}"); }
        }
        if (gone.Count == 0) return;
        foreach (var id in gone) _working.Remove(id);
        Refresh();
    }

    private static string Format(TimeSpan t)
    {
        var seconds = Math.Max(0, (int)t.TotalSeconds);
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    // ---- Журнал: только событие, проект и время ----
    private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "claude-events.log");

    private static void Log(string text)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 200_000) File.Delete(path); // журнал не должен расти бесконечно
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {text}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { /* журнал не обязателен */ }
    }
}

/// <summary>Читает хвост файла переписки (JSONL): остановили ли Claude клавишей Esc («[Request interrupted by user…]» последней записью) и как названа сессия.</summary>
internal static class TranscriptTail
{
    private const int TailBytes = 4096;
    private const int TitleTailBytes = 256 * 1024;

    /// <summary>Короткое название сессии из последней записи <c>ai-title</c> (то, что видно в списке чатов VS Code); null, если его ещё нет. Другие поля не читаются.</summary>
    public static string? LastAiTitle(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = (int)Math.Min(TitleTailBytes, stream.Length);
            if (read == 0) return null;
            stream.Seek(-read, SeekOrigin.End);
            var buffer = new byte[read];
            var got = 0;
            while (got < read)
            {
                var n = stream.Read(buffer, got, read - got);
                if (n <= 0) break;
                got += n;
            }
            var lines = Encoding.UTF8.GetString(buffer, 0, got).Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"ai-title\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(lines[i]);
                    if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "ai-title"
                        && doc.RootElement.TryGetProperty("aiTitle", out var title) && title.GetString() is { Length: > 0 } text)
                        return text;
                }
                catch { /* обрезанная первая строка хвоста */ }
            }
            return null;
        }
        catch { return null; }
    }

    public static bool EndsWithInterruption(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length == 0) return false;
            var read = (int)Math.Min(TailBytes, length);
            stream.Seek(-read, SeekOrigin.End);
            var buffer = new byte[read];
            var got = 0;
            while (got < read)
            {
                var n = stream.Read(buffer, got, read - got);
                if (n <= 0) break;
                got += n;
            }
            var lines = Encoding.UTF8.GetString(buffer, 0, got).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length > 0 && lines[^1].Contains("[Request interrupted by user", StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
