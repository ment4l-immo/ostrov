using System.IO;
using Ostrov.Services;

namespace Ostrov.Modules.Buffer;

/// <summary>
/// Полка для файлов: список путей в настройках острова (сами файлы не копируются и не перемещаются).
/// Новые сверху. Настройки читаются заново при каждом изменении, чтобы не затереть чужие поля.
/// </summary>
internal sealed class ShelfStore
{
    private readonly Func<AppSettings> _load;
    private readonly Action<AppSettings> _save;

    public event Action? Changed;

    public ShelfStore(Func<AppSettings>? load = null, Action<AppSettings>? save = null)
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

    public IReadOnlyList<string> GetAll() => _load().Shelf.ToList();

    /// <summary>Положить на полку. Порядок пачки сохраняется, повторы поднимаются наверх.</summary>
    public void Add(IEnumerable<string> paths)
    {
        var settings = _load();
        var changed = false;
        foreach (var raw in paths.Reverse())
        {
            string path;
            try { path = Normalize(raw); }
            catch { continue; }
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            settings.Shelf.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            settings.Shelf.Insert(0, path);
            changed = true;
        }
        if (!changed) return;
        _save(settings);
        Changed?.Invoke();
    }

    public void Remove(string path)
    {
        var settings = _load();
        if (settings.Shelf.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        _save(settings);
        Changed?.Invoke();
    }

    public void Clear()
    {
        var settings = _load();
        if (settings.Shelf.Count == 0) return;
        settings.Shelf.Clear();
        _save(settings);
        Changed?.Invoke();
    }
}
