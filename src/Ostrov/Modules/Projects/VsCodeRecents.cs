using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Ostrov.Modules.Projects;

/// <summary>LastActive — когда в проекте в последний раз менялись файлы.</summary>
internal sealed record RecentProject(string Path, DateTime LastActive);

/// <summary>
/// Папки, которые открывали в VS Code. VS Code хранит по каталогу на каждую открытую папку:
/// <c>%AppData%\Code\User\workspaceStorage\&lt;хэш&gt;\workspace.json</c> с адресом папки. Только чтение, без библиотек.
/// Берём только существующие локальные папки (удалённые окна WSL/SSH/контейнеры и пропавшие папки пропускаем).
///
/// Порядок — по тому, когда в проекте последний раз менялись файлы. Время записи в самом хранилище VS Code для этого
/// не годится: оно обновляется, когда VS Code сбрасывает состояние окна (например, при перезапуске — сразу у всех
/// открытых окон), и давно забытый проект может оказаться выше свежего. Список «Open Recent» из VS Code в этой версии
/// на диске явно не хранится.
/// </summary>
internal static class VsCodeRecents
{
    // Не заходим в служебные и «тяжёлые» папки: свежие файлы там не означают работу над проектом.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "packages", "pods",
        "__pycache__", "venv", "env"
    };

    private const int MaxDepth = 3;
    private const int MaxEntriesPerProject = 4000;
    private static readonly TimeSpan MaxScanTimePerProject = TimeSpan.FromMilliseconds(400);

    public static IReadOnlyList<RecentProject> Load(string? storageRoot = null)
    {
        var root = storageRoot ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "workspaceStorage");
        if (!Directory.Exists(root)) return [];

        // 1) Какие локальные папки существуют; для каждой запоминаем время записи хранилища (на случай одинаковой активности).
        var stateWrite = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var file = System.IO.Path.Combine(dir, "workspace.json");
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("folder", out var folder) || folder.GetString() is not { } uri) continue;
                if (!uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) continue; // не локальная папка

                // VS Code пишет двоеточие диска как %3A (file:///c%3A/...), из-за чего Uri.LocalPath не узнаёт диск —
                // раскодируем адрес вручную (заодно %XX у кириллицы и пробелов) и приводим слэши к виндовым.
                var path = System.IO.Path.GetFullPath(Uri.UnescapeDataString(uri["file:///".Length..]));
                if (!Directory.Exists(path)) continue;

                var written = Directory.EnumerateFiles(dir).Select(File.GetLastWriteTime)
                    .DefaultIfEmpty(Directory.GetLastWriteTime(dir)).Max();
                if (!stateWrite.TryGetValue(path, out var known) || known < written) stateWrite[path] = written;
            }
            catch { /* повреждённый или недоступный каталог — пропускаем */ }
        }

        // 2) Активность файлов считаем параллельно по проектам.
        return stateWrite
            .AsParallel()
            .Select(p => (Path: p.Key, Activity: LastFileActivity(p.Key), State: p.Value))
            .OrderByDescending(x => x.Activity)
            .ThenByDescending(x => x.State)
            .Select(x => new RecentProject(x.Path, x.Activity))
            .ToList();
    }

    /// <summary>Самое позднее время изменения файла в проекте (неглубокий обход с ограничениями по глубине, числу файлов и времени).</summary>
    private static DateTime LastFileActivity(string root)
    {
        var newest = Directory.GetLastWriteTime(root);
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        var budget = MaxEntriesPerProject;
        var clock = Stopwatch.StartNew();

        while (stack.Count > 0 && budget > 0 && clock.Elapsed < MaxScanTimePerProject)
        {
            var (dir, depth) = stack.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    budget--;
                    if (entry is DirectoryInfo sub)
                    {
                        // Папки с точки (.git, .vs, .venv, .claude …) и тяжёлые пропускаем.
                        if (depth < MaxDepth && !sub.Name.StartsWith('.') && !SkipDirs.Contains(sub.Name))
                            stack.Push((sub.FullName, depth + 1));
                    }
                    else if (entry.LastWriteTime > newest)
                    {
                        newest = entry.LastWriteTime;
                    }
                }
            }
            catch { /* нет доступа к папке — пропускаем */ }
        }
        return newest;
    }
}
