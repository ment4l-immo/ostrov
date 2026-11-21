using System.IO;
using Ostrov.Services;

namespace Ostrov.Modules.Projects;

/// <summary>
/// Список проектов в настройках острова. Настройки читаются заново при каждом изменении, чтобы не затереть
/// то, что менялось в других местах (город погоды и т. п.). Источник можно подменить (для проверки без файлов).
/// </summary>
internal sealed class ProjectsStore
{
    private readonly Func<AppSettings> _load;
    private readonly Action<AppSettings> _save;

    public ProjectsStore(Func<AppSettings>? load = null, Action<AppSettings>? save = null)
    {
        _load = load ?? AppSettings.Load;
        _save = save ?? (s => s.Save());
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full == root ? full : full.TrimEnd('\\', '/');
    }

    private static DateTime SortKey(ProjectEntry p) => p.LastOpened is { } o && o > p.Added ? o : p.Added;

    /// <summary>Закреплённые — сверху, дальше недавно открытые (или недавно добавленные).</summary>
    public IReadOnlyList<ProjectEntry> GetAll() =>
        _load().Projects.OrderByDescending(p => p.Pinned).ThenByDescending(SortKey).ToList();

    public void SetPinned(string path, bool pinned)
    {
        var settings = _load();
        var entry = settings.Projects.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        entry.Pinned = pinned;
        _save(settings);
    }

    public void Add(string path)
    {
        path = Normalize(path);
        var settings = _load();
        var existing = settings.Projects.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.Added = DateTime.Now; // уже есть — просто поднимаем наверх
        else settings.Projects.Add(new ProjectEntry { Path = path });
        _save(settings);
    }

    public void Remove(string path)
    {
        var settings = _load();
        settings.Projects.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        _save(settings);
    }

    public void Touch(string path)
    {
        var settings = _load();
        var entry = settings.Projects.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        entry.LastOpened = DateTime.Now;
        _save(settings);
    }
}
