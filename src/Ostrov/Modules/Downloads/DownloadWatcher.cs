using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Downloads;

/// <summary>
/// Загрузки браузера на пассивном индикаторе (М-021). Браузер скачивает во временный файл рядом с будущим («.crdownload» у Chrome, Edge, Brave, Vivaldi,
/// Яндекс Браузера, «.part» у Firefox, «.opdownload» у Opera, «.download»), а когда закончит, переименовывает его в настоящее имя.
/// По этому и работаем, для любого браузера, без прав администратора и без чтения его баз: появился такой файл в папке загрузок —
/// идёт загрузка (размер и скорость берём по росту файла, раз в секунду); временный файл переименовали в обычный — «Загрузка завершена» с именем
/// (удалили без переименования — отменили, молчим). <b>Процентов нет</b>: общий размер не записан ни в файле, ни доступен снаружи.
/// Размер читаем через открытый файл, а не из каталога: каталог для файла, который ещё пишется, отстаёт и показывает десятые доли мегабайта.
/// Папки: «Загрузки» Windows и папки из настроек Chrome, Edge, Brave, Vivaldi, Opera и Яндекс Браузера (если там указана своя).
/// Загрузка, которая не росла 8 секунд (пауза, обрыв), считается остановленной и с острова уходит. Знак «готово» держится 8 секунд или до клика.
/// Приоритет 30: ниже таймера и секундомера, выше музыки.
/// </summary>
internal sealed class DownloadWatcher : IDisposable
{
    private const string EntryId = "download";
    private static readonly string[] TempExtensions = [".crdownload", ".part", ".opdownload", ".download"];
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(8), DoneShown = TimeSpan.FromSeconds(8);

    private sealed class Tracked
    {
        public long Size;
        public DateTime GrewUtc, SeenUtc;
        public double Speed; // сглаженная, байт/с
    }

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly Dictionary<string, Tracked> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _doneTimer = new() { Interval = DoneShown };
    private readonly DispatcherTimer _foldersTimer = new() { Interval = TimeSpan.FromSeconds(60) };

    private readonly DownloadView _pill = new(wide: false), _wide = new(wide: true);
    private readonly StatusBubble _bubble = new(Hues.Ink, ""); // стрелка вниз в центре кольца
    private readonly CompactStrip _strip = new(Hues.Ink);

    private (string Name, string Size)? _done;
    private int _shownState; // 0 — ничего, 1 — идёт загрузка, 2 — «готово»

    public void Start()
    {
        _tick.Tick += (_, _) => Poll();
        _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); _done = null; Update(); };
        _foldersTimer.Tick += (_, _) => RefreshFolders();
        _foldersTimer.Start();
        RefreshFolders();
    }

    public void Dispose()
    {
        _tick.Stop();
        _doneTimer.Stop();
        _foldersTimer.Stop();
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
    }

    // ───── Папки и слежение ─────

    private void RefreshFolders()
    {
        foreach (var folder in Folders())
        {
            if (_watchers.ContainsKey(folder) || !Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName,
                    InternalBufferSize = 32 * 1024
                };
                watcher.Created += (_, e) => _ui.BeginInvoke(() => OnCreated(e.FullPath));
                watcher.Renamed += (_, e) => _ui.BeginInvoke(() => OnRenamed(e.OldFullPath, e.FullPath));
                watcher.Deleted += (_, e) => _ui.BeginInvoke(() => OnDeleted(e.FullPath));
                watcher.Error += (_, _) => _ui.BeginInvoke(() => Rescan(folder)); // события пропали: пересмотреть папку
                watcher.EnableRaisingEvents = true;
                _watchers[folder] = watcher;
                Rescan(folder); // загрузка могла начаться до запуска острова
            }
            catch { /* папка недоступна: пропускаем */ }
        }
    }

    private void Rescan(string folder)
    {
        try
        {
            // Недокачанные файлы, оставшиеся с прошлых раз, загрузкой не считаются, пока не начнут расти.
            foreach (var file in Directory.EnumerateFiles(folder))
                if (IsTemp(file)) Track(file, idle: true);
        }
        catch { /* папка недоступна */ }
    }

    private void OnCreated(string path)
    {
        if (IsTemp(path)) Track(path);
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        var wasTemp = IsTemp(oldPath);
        if (!wasTemp) { if (IsTemp(newPath)) Track(newPath); return; }

        _files.TryGetValue(oldPath, out var tracked);
        _files.Remove(oldPath);
        if (IsTemp(newPath))
        {
            // Браузер уточнил временное имя: следим дальше под новым.
            if (tracked != null) _files[newPath] = tracked; else Track(newPath);
            Update();
            return;
        }

        // Временный файл стал обычным: загрузка закончилась.
        long size = tracked?.Size ?? 0;
        try { size = new FileInfo(newPath).Length; } catch { /* файл уже убрали */ }
        _done = (Path.GetFileName(newPath), size > 0 ? ByteFormat.Size(size) : "");
        _doneTimer.Stop();
        _doneTimer.Start();
        Update();
    }

    private void OnDeleted(string path)
    {
        // Удалили без переименования: загрузку отменили.
        if (_files.Remove(path)) Update();
    }

    private void Track(string path, bool idle = false)
    {
        if (_files.ContainsKey(path)) return;
        var now = DateTime.UtcNow;
        _files[path] = new Tracked { Size = Math.Max(0, ReadSize(path)), GrewUtc = idle ? now - Stale - Stale : now, SeenUtc = now };
        if (!_tick.IsEnabled) _tick.Start();
        Update();
    }

    // ───── Опрос размера ─────

    private void Poll()
    {
        var now = DateTime.UtcNow;
        foreach (var (path, tracked) in _files.ToList())
        {
            var size = ReadSize(path);
            if (size < 0) continue; // не открылся (занят): оставляем прежнее
            var seconds = Math.Max(0.2, (now - tracked.SeenUtc).TotalSeconds);
            var instant = Math.Max(0, size - tracked.Size) / seconds;
            tracked.Speed = tracked.Speed <= 0 ? instant : tracked.Speed * 0.6 + instant * 0.4;
            if (size > tracked.Size) tracked.GrewUtc = now;
            else tracked.Speed *= 0.5; // не растёт: скорость быстро падает
            tracked.Size = size;
            tracked.SeenUtc = now;
        }
        if (_files.Count == 0) _tick.Stop();
        Update();
    }

    // Размер через открытый файл (с общим доступом): размер в каталоге у файла, который ещё пишется, отстаёт.
    private static long ReadSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch { return -1; }
    }

    private static bool IsTemp(string path)
    {
        var ext = Path.GetExtension(path);
        return TempExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    // ───── Показ ─────

    private void Update()
    {
        var now = DateTime.UtcNow;
        var active = _files.Values.Where(t => now - t.GrewUtc <= Stale).ToList();

        if (active.Count > 0)
        {
            var speed = active.Sum(t => t.Speed);
            var size = active.Sum(t => t.Size);
            _pill.ShowRunning(active.Count, ByteFormat.Speed(speed), ByteFormat.Size(size));
            _wide.ShowRunning(active.Count, ByteFormat.Speed(speed), ByteFormat.Size(size));
            _bubble.ShowWorking();
            if (_shownState != 1)
            {
                _strip.ShowRunning(paused: false);
                _shownState = 1;
                ActivityCenter.Set(Entry(done: false));
            }
        }
        else if (_done is { } done)
        {
            _pill.ShowDone(done.Name, done.Size);
            _wide.ShowDone(done.Name, done.Size);
            _bubble.ShowResult("", iconFont: true);
            if (_shownState != 2)
            {
                _strip.ShowSolid(0);
                _shownState = 2;
                ActivityCenter.Set(Entry(done: true));
            }
        }
        else if (_shownState != 0)
        {
            _shownState = 0;
            ActivityCenter.Remove(EntryId);
        }
    }

    private ActivityEntry Entry(bool done) => new()
    {
        Id = EntryId, Priority = 30, Pill = _pill, PillWidth = DownloadView.PillWidth, PillHeight = DownloadView.PillHeight,
        Bubble = _bubble, Compact = _strip,
        Wide = _wide, WideWidth = DownloadView.WideWidth, WideHeight = _wide.PreferredHeight,
        DismissOnClick = done, OnDismissed = done ? DismissDone : null
    };

    // Клик по знаку «готово»: убираем его (запись из центра активностей уберёт окно острова).
    private void DismissDone()
    {
        _doneTimer.Stop();
        _done = null;
        _shownState = 0;
        Update();
    }

    // ───── Где искать ─────

    private static IEnumerable<string> Folders()
    {
        var result = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(path.Trim()); } catch { return; }
            if (!result.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase))) result.Add(path);
        }

        Add(KnownDownloads());
        Add(Environment.GetEnvironmentVariable("OSTROV_DOWNLOADS_DIR")); // только для проверок
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var prefs in new[]
        {
            Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Preferences"),
            Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Preferences"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Preferences"),
            Path.Combine(local, "Vivaldi", "User Data", "Default", "Preferences"),
            Path.Combine(local, "Yandex", "YandexBrowser", "User Data", "Default", "Preferences"),
            Path.Combine(roaming, "Opera Software", "Opera Stable", "Preferences")
        })
            Add(DownloadFolderFromPreferences(prefs));
        return result;
    }

    // «download.default_directory» из настроек браузера на Chromium; у кого свою папку не задавали, поля нет.
    private static string? DownloadFolderFromPreferences(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("download", out var download) && download.TryGetProperty("default_directory", out var dir)
                ? dir.GetString() : null;
        }
        catch { return null; }
    }

    private static string? KnownDownloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) != 0) return null;
            try { return Marshal.PtrToStringUni(pointer); }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        catch { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
