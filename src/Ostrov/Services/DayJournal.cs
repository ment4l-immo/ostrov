using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Ostrov.Services;

/// <summary>Один день в журнале: время по программам, музыка, Claude. Только имена и числа, никаких заголовков окон, текстов и содержимого.</summary>
internal sealed class DayData
{
    public double Active { get; set; }                                       // секунд за компьютером (с вводом не реже раза в 3 минуты)
    public Dictionary<string, double> Apps { get; set; } = [];               // название программы → секунд в фокусе
    public Dictionary<string, MusicEntry> Music { get; set; } = [];          // «исполнитель|название» → прослушивание
    public ClaudeEntry Claude { get; set; } = new();
}

internal sealed class MusicEntry
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public double Seconds { get; set; }
    public int Plays { get; set; }
}

internal sealed class ClaudeEntry
{
    public double Seconds { get; set; }                                      // сколько Claude работал над запросами
    public int Steps { get; set; }                                           // вызовов инструментов
    public List<string> Sessions { get; set; } = [];                         // номера сессий (по ним считаем, сколько их было)
    public Dictionary<string, double> Projects { get; set; } = [];           // проект → секунд
}

/// <summary>
/// Локальный журнал дня для «итогов дня» (решение пользователя 2026-11-16): раз в 5 секунд смотрит, какая программа в фокусе и играет ли музыка, и
/// складывает секунды в файл <c>%AppData%\Ostrov\day-journal.json</c>. Хранится 30 дней, наружу ничего не уходит. Простой считается так: если
/// клавиатура и мышь не трогались 3 минуты, время никому не засчитывается. Работает на потоке интерфейса. Выключается в «Настройки → Общие».
/// </summary>
internal sealed class DayJournal : IDisposable
{
    public static DayJournal? Current { get; private set; }

    /// <summary>Файл журнала. Подменяется только в проверках.</summary>
    internal static string? PathOverride { get; set; }

    private const int KeepDays = 30;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(3);
    private static readonly string[] Ignored = ["LockApp", "LogonUI"];

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saver;
    private readonly Dictionary<string, DayData> _days = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase); // путь к программе → её название
    private DateTime _last = DateTime.UtcNow;
    private string? _musicKey;
    private bool _dirty;

    private static string FilePath =>
        PathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "day-journal.json");

    public DayJournal()
    {
        Current = this;
        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => OnTick();
        _saver = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _saver.Tick += (_, _) => Save();
    }

    public void Start()
    {
        Load();
        _last = DateTime.UtcNow;
        _timer.Start();
        _saver.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _saver.Stop();
        Save();
        if (Current == this) Current = null;
    }

    /// <summary>Журнал включён в настройках (иначе ничего не собирается).</summary>
    private static bool Enabled => AppSettings.Load().DayJournalEnabled;

    private static string Key(DateTime day) => day.ToString("yyyy-MM-dd");

    private DayData Day(DateTime day)
    {
        var key = Key(day);
        if (!_days.TryGetValue(key, out var data)) _days[key] = data = new DayData();
        return data;
    }

    /// <summary>Данные за день (пустые, если журнал за него ничего не знает).</summary>
    public DayData Get(DateTime day) => _days.TryGetValue(Key(day), out var d) ? d : new DayData();

    /// <summary>Сложить данные за несколько дней подряд (включая оба края).</summary>
    public DayData Range(DateTime from, DateTime to)
    {
        var sum = new DayData();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var d = Get(day);
            sum.Active += d.Active;
            foreach (var (name, sec) in d.Apps) sum.Apps[name] = sum.Apps.GetValueOrDefault(name) + sec;
            foreach (var (key, m) in d.Music)
            {
                if (!sum.Music.TryGetValue(key, out var e)) sum.Music[key] = e = new MusicEntry { Title = m.Title, Artist = m.Artist };
                e.Seconds += m.Seconds; e.Plays += m.Plays;
            }
            sum.Claude.Seconds += d.Claude.Seconds;
            sum.Claude.Steps += d.Claude.Steps;
            foreach (var s in d.Claude.Sessions) if (!sum.Claude.Sessions.Contains(s)) sum.Claude.Sessions.Add(s);
            foreach (var (p, sec) in d.Claude.Projects) sum.Claude.Projects[p] = sum.Claude.Projects.GetValueOrDefault(p) + sec;
        }
        return sum;
    }

    /// <summary>Стереть весь журнал (кнопка в настройках).</summary>
    public void Clear()
    {
        _days.Clear();
        _musicKey = null;
        _dirty = true;
        Save();
    }

    // ───── Claude (события приходят из ClaudeWork) ─────

    public void NoteClaudeStep() { if (!Enabled) return; Day(DateTime.Today).Claude.Steps++; _dirty = true; }

    public void NoteClaudePrompt(string sessionId)
    {
        if (!Enabled) return;
        var c = Day(DateTime.Today).Claude;
        var id = sessionId.Length > 8 ? sessionId[..8] : sessionId;
        if (!c.Sessions.Contains(id)) c.Sessions.Add(id);
        _dirty = true;
    }

    public void NoteClaudeDone(string project, TimeSpan elapsed)
    {
        if (!Enabled || elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromHours(6)) return; // бессмысленные значения (остров запускался посреди работы) не берём
        var c = Day(DateTime.Today).Claude;
        c.Seconds += elapsed.TotalSeconds;
        c.Projects[project] = c.Projects.GetValueOrDefault(project) + elapsed.TotalSeconds;
        _dirty = true;
    }

    // ───── Раз в 5 секунд: программа в фокусе и музыка ─────

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min((now - _last).TotalSeconds, 15); // после сна компьютера большой скачок времени не засчитываем
        _last = now;
        if (!Enabled) { _musicKey = null; return; }

        // Звук играет сам по себе, пока ты отошёл: музыка считается и при простое, программы и «за компьютером» нет.
        var today = Day(DateTime.Today);
        if (Modules.Player.MusicActivity.NowPlaying is { } track)
        {
            var key = track.Artist + "|" + track.Title;
            if (!today.Music.TryGetValue(key, out var entry)) today.Music[key] = entry = new MusicEntry { Title = track.Title, Artist = track.Artist };
            if (_musicKey != key) { entry.Plays++; _musicKey = key; }
            entry.Seconds += dt;
            _dirty = true;
        }
        else _musicKey = null;

        if (IdleFor() >= IdleLimit) return;
        today.Active += dt;
        if (ForegroundApp() is { } app) today.Apps[app] = today.Apps.GetValueOrDefault(app) + dt;
        _dirty = true;
    }

    // ───── Программа в фокусе ─────

    private string? ForegroundApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return null; // сам остров
            var path = ImagePath(pid);
            if (path == null) return null;
            var file = Path.GetFileNameWithoutExtension(path);
            if (Ignored.Contains(file, StringComparer.OrdinalIgnoreCase)) return null;
            if (_names.TryGetValue(path, out var cached)) return cached;
            var name = file;
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
            }
            catch { /* название по имени файла */ }
            _names[path] = name;
            return name;
        }
        catch { return null; }
    }

    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private static TimeSpan IdleFor()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    // ───── Файл ─────

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DayData>>(File.ReadAllText(FilePath, Encoding.UTF8));
            if (loaded == null) return;
            var oldest = Key(DateTime.Today.AddDays(-KeepDays));
            foreach (var (key, data) in loaded)
                if (string.CompareOrdinal(key, oldest) >= 0) _days[key] = data;
        }
        catch { /* повреждённый файл: начинаем с чистого журнала */ }
    }

    private void Save()
    {
        if (!_dirty) return;
        try
        {
            var oldest = Key(DateTime.Today.AddDays(-KeepDays));
            foreach (var key in _days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) _days.Remove(key);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_days), Encoding.UTF8);
            File.Move(temp, FilePath, overwrite: true);
            _dirty = false;
        }
        catch { /* не вышло сейчас: попробуем через минуту */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint cbSize, dwTime; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
