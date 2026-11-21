using System.IO;
using System.Text;
using System.Text.Json;

namespace Ostrov.Claude;

/// <summary>
/// Узнаёт, что на запрос разрешения ответили не на острове, а в окне VS Code: в файл переписки сессии дописывается
/// результат именно этого вызова инструмента (<c>tool_result</c> с тем же <c>tool_use_id</c>).
/// В запросе хука идентификатора вызова нет, поэтому вызов ищется в файле по имени инструмента и вводу.
/// Любые другие записи (соседние вызовы из того же ответа, текст, чужие результаты) запрос не закрывают.
/// Если вызов в файле найти не удалось, ничего не закрываем: карточка остаётся, пока на неё не ответят на острове.
/// </summary>
internal sealed class TranscriptWatcher : IDisposable
{
    private const int PollMs = 300;
    private const int GiveUpMatchingMs = 15000;       // столько ждём, пока вызов появится в файле
    private const long InitialTailBytes = 4 * 1024 * 1024; // вызов ищем в последних мегабайтах файла

    private readonly string _path;
    private readonly string _tool;
    private readonly JsonElement _input;
    private readonly Action _onResolved;
    private readonly CancellationTokenSource _cts = new();

    private readonly List<string> _matches = [];   // id подходящих вызовов в порядке появления в файле
    private readonly HashSet<string> _results = []; // id вызовов, у которых уже есть результат
    private string? _targetId;
    private long _offset;
    private byte[] _leftover = [];                  // недописанная последняя строка

    /// <summary>Найденный вызов (для проверок).</summary>

    public TranscriptWatcher(string path, string tool, JsonElement input, Action onResolved)
    {
        _path = path;
        _tool = tool;
        _input = input;
        _onResolved = onResolved;
        _ = Task.Run(Loop);
    }

    public void Dispose() => _cts.Cancel();

    private async Task Loop()
    {
        try
        {
            var started = Environment.TickCount64;
            var first = true;
            while (!_cts.IsCancellationRequested)
            {
                if (!Poll(first)) return;
                first = false;

                if (_targetId != null && _results.Contains(_targetId))
                {
                    if (!_cts.IsCancellationRequested) _onResolved();
                    return;
                }
                if (_targetId == null && Environment.TickCount64 - started > GiveUpMatchingMs) return;

                await Task.Delay(PollMs, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* файл исчез или недоступен: просто перестаём следить */ }
    }

    // Читает новое с последнего раза. false — файла нет, следить нечем.
    private bool Poll(bool initial)
    {
        if (!File.Exists(_path)) return false;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;

        long start;
        if (initial)
        {
            start = Math.Max(0, length - InitialTailBytes);
            _offset = start;
        }
        else
        {
            if (length < _offset) { _offset = 0; _leftover = []; } // файл пересоздан
            start = _offset;
        }
        if (length == start && _leftover.Length == 0) return true;

        fs.Seek(start, SeekOrigin.Begin);
        var fresh = new byte[length - start];
        var read = 0;
        while (read < fresh.Length)
        {
            var n = fs.Read(fresh, read, fresh.Length - read);
            if (n <= 0) break;
            read += n;
        }
        _offset = start + read;

        var buffer = new byte[_leftover.Length + read];
        Buffer.BlockCopy(_leftover, 0, buffer, 0, _leftover.Length);
        Buffer.BlockCopy(fresh, 0, buffer, _leftover.Length, read);

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
        if (lastNewline < 0)
        {
            _leftover = buffer;
            return true;
        }
        _leftover = buffer[(lastNewline + 1)..];

        var from = 0;
        if (initial && start > 0)
        {
            // Начали с середины файла: первая строка может быть обрезана, пропускаем её.
            var firstNewline = Array.IndexOf(buffer, (byte)'\n');
            from = firstNewline + 1;
        }

        var text = Encoding.UTF8.GetString(buffer, from, lastNewline + 1 - from);
        foreach (var line in text.Split('\n'))
            if (line.Length > 0) ProcessLine(line);

        // Цель — первый подходящий вызов, на который ещё нет ответа. Выбрали один раз и дальше не меняем.
        if (_targetId == null)
            _targetId = _matches.FirstOrDefault(id => !_results.Contains(id));
        return true;
    }

    private void ProcessLine(string line)
    {
        if (!line.Contains("\"tool_use", StringComparison.Ordinal)) return; // и tool_use, и tool_use_id
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array) return;

            foreach (var item in content.EnumerateArray())
            {
                var type = Str(item, "type");
                if (type == "tool_result")
                {
                    if (Str(item, "tool_use_id") is { } resultFor) _results.Add(resultFor);
                }
                else if (type == "tool_use" && Str(item, "name") == _tool && Str(item, "id") is { } id &&
                         item.TryGetProperty("input", out var input) && InputMatches(input))
                {
                    _matches.Add(id);
                }
            }
        }
        catch (JsonException) { /* строка не JSON или недописана: пропускаем */ }
    }

    // Ввод из хука должен целиком содержаться во вводе вызова из файла (поля совпадают по значению).
    private bool InputMatches(JsonElement fileInput)
    {
        if (_input.ValueKind != JsonValueKind.Object || fileInput.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in _input.EnumerateObject())
        {
            if (!fileInput.TryGetProperty(property.Name, out var other)) return false;
            if (!SameValue(property.Value, other)) return false;
        }
        return true;
    }

    // Сравнение по смыслу, а не по тексту: порядок ключей и пробелы не важны (у вопросов Claude ввод вложенный).
    private static bool SameValue(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                using (var x = a.EnumerateArray().GetEnumerator())
                using (var y = b.EnumerateArray().GetEnumerator())
                    while (x.MoveNext() && y.MoveNext())
                        if (!SameValue(x.Current, y.Current)) return false;
                return true;
            case JsonValueKind.Object:
                var count = 0;
                foreach (var property in a.EnumerateObject())
                {
                    count++;
                    if (!b.TryGetProperty(property.Name, out var other) || !SameValue(property.Value, other)) return false;
                }
                return count == b.EnumerateObject().Count();
            case JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText();
            default:
                return true; // true / false / null: равенство видов уже проверено
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
