using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostrov.Modules.Buffer;

internal enum ClipKind { Text, Image }

/// <summary>Одна запись истории буфера: текст или картинка (сам файл картинки лежит рядом, в папке clipboard).</summary>
internal sealed class ClipEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ClipKind Kind { get; set; }

    public string? Text { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Отпечаток пикселей картинки: по нему находим повтор.</summary>
    public string? Hash { get; set; }

    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>
/// История буфера обмена: до 50 записей (новые сверху), хранится на диске в %AppData%\Ostrov:
/// clipboard.json (тексты и сведения о картинках) и папка clipboard\*.png (сами картинки).
/// Все вызовы — из потока интерфейса.
/// </summary>
internal sealed class ClipboardHistory
{
    public const int MaxEntries = 50;
    /// <summary>Тексты длиннее не сохраняем (файл истории переписывается при каждом изменении).</summary>
    public const int MaxTextLength = 200_000;

    private readonly string _dir;
    private readonly List<ClipEntry> _entries = [];

    public event Action? Changed;

    public ClipboardHistory(string? dir = null)
    {
        _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov");
        Load();
    }

    private string JsonPath => Path.Combine(_dir, "clipboard.json");
    private string ImagesDir => Path.Combine(_dir, "clipboard");

    public string ImagePath(ClipEntry entry) => Path.Combine(ImagesDir, entry.Id + ".png");

    /// <summary>Новые сверху.</summary>
    public IReadOnlyList<ClipEntry> Entries => _entries;

    private void Load()
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(JsonPath)) ?? [];
            foreach (var e in loaded)
            {
                if (e.Kind == ClipKind.Text && string.IsNullOrEmpty(e.Text)) continue;
                if (e.Kind == ClipKind.Image && !File.Exists(ImagePath(e))) continue;
                _entries.Add(e);
            }
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        catch { /* файла нет или он повреждён — начинаем с пустой истории */ }

        // Картинки, которых нет в списке (например, после сбоя), удаляем.
        try
        {
            if (!Directory.Exists(ImagesDir)) return;
            var known = _entries.Where(e => e.Kind == ClipKind.Image).Select(e => e.Id).ToHashSet();
            foreach (var file in Directory.EnumerateFiles(ImagesDir, "*.png"))
                if (!known.Contains(Path.GetFileNameWithoutExtension(file))) TryDelete(file);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = JsonPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, JsonPath, overwrite: true);
        }
        catch { /* не удалось сохранить — история останется в памяти до перезапуска */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void Finish()
    {
        while (_entries.Count > MaxEntries) RemoveAt(_entries.Count - 1);
        Save();
        Changed?.Invoke();
    }

    private void RemoveAt(int index)
    {
        var e = _entries[index];
        _entries.RemoveAt(index);
        if (e.Kind == ClipKind.Image) TryDelete(ImagePath(e));
    }

    /// <summary>Добавить текст. Повтор не плодится: старая запись поднимается наверх.</summary>
    public void AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return;
        var index = _entries.FindIndex(e => e.Kind == ClipKind.Text && e.Text == text);
        if (index == 0) return; // уже наверху
        ClipEntry entry;
        if (index > 0)
        {
            entry = _entries[index];
            _entries.RemoveAt(index);
            entry.Time = DateTime.Now;
        }
        else entry = new ClipEntry { Kind = ClipKind.Text, Text = text };
        _entries.Insert(0, entry);
        Finish();
    }

    /// <summary>Если такая картинка уже есть — поднять её наверх и вернуть true.</summary>
    public bool BumpImage(string hash)
    {
        var index = _entries.FindIndex(e => e.Kind == ClipKind.Image && e.Hash == hash);
        if (index < 0) return false;
        if (index > 0)
        {
            var entry = _entries[index];
            _entries.RemoveAt(index);
            entry.Time = DateTime.Now;
            _entries.Insert(0, entry);
            Finish();
        }
        return true;
    }

    public void AddImage(string hash, int width, int height, byte[] png)
    {
        if (BumpImage(hash)) return;
        var entry = new ClipEntry { Kind = ClipKind.Image, Hash = hash, Width = width, Height = height };
        try
        {
            Directory.CreateDirectory(ImagesDir);
            File.WriteAllBytes(ImagePath(entry), png);
        }
        catch { return; }
        _entries.Insert(0, entry);
        Finish();
    }

    public void MoveToTop(ClipEntry entry)
    {
        var index = _entries.IndexOf(entry);
        if (index < 0) return;
        if (index > 0)
        {
            _entries.RemoveAt(index);
            _entries.Insert(0, entry);
        }
        entry.Time = DateTime.Now;
        Finish();
    }

    public void Remove(ClipEntry entry)
    {
        var index = _entries.IndexOf(entry);
        if (index < 0) return;
        RemoveAt(index);
        Finish();
    }

    /// <summary>Убрать из истории все записи с таким точным текстом (например, секрет, который вставили в остров).</summary>
    public void RemoveText(string text)
    {
        var removed = false;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Kind != ClipKind.Text || _entries[i].Text != text) continue;
            RemoveAt(i);
            removed = true;
        }
        if (removed) Finish();
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        while (_entries.Count > 0) RemoveAt(_entries.Count - 1);
        Finish();
    }
}
