using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ostrov.Claude;

/// <summary>
/// Один запрос от хука Claude Code (<c>PermissionRequest</c>), ожидающий ответа.
/// Ответить можно один раз: «разрешить», «запретить» или «отпустить» (обычное окно VS Code решает само).
/// Если хук закрыл связь раньше (ответили в окне VS Code), приходит <see cref="Closed"/>.
/// </summary>
internal sealed class ClaudeRequest
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Action<string> _send;
    private int _finished; // 0 — ждёт ответа; 1 — ответили или связь закрыта

    public string Cwd { get; }
    public string Tool { get; }
    public JsonElement Input { get; }

    /// <summary>Имя папки проекта (последний кусок пути).</summary>
    public string Project { get; }

    /// <summary>Читаемое название инструмента: <c>Bash</c>, <c>Edit</c>; для MCP — «сервер · инструмент».</summary>
    public string ToolLabel { get; }

    /// <summary>Главное из запроса: команда, путь файла, адрес и т. п.</summary>
    public string Detail { get; }

    /// <summary>Файл переписки сессии (по нему узнаём, что ответили в окне VS Code).</summary>
    public string? TranscriptPath { get; }

    /// <summary>Хук закрыл связь до нашего ответа. Вызывается из любого потока.</summary>
    public event Action? Closed;

    /// <summary>Запрос завершён любым способом: ответили, отпустили или хук отключился. Вызывается из любого потока.</summary>
    public event Action? Finished;

    /// <summary>Запрос уже завершён любым способом.</summary>
    public bool IsFinished => Volatile.Read(ref _finished) != 0;

    /// <summary>Связь уже закрыта хуком (ответили в окне VS Code или хук завершился).</summary>
    public bool IsClosed { get; private set; }

    public ClaudeRequest(JsonElement hookInput, Action<string> sendReply)
    {
        _send = sendReply;
        Cwd = Str(hookInput, "cwd") ?? "";
        TranscriptPath = Str(hookInput, "transcript_path");
        Tool = Str(hookInput, "tool_name") ?? "?";
        Input = hookInput.TryGetProperty("tool_input", out var input) ? input.Clone() : default;
        Project = ProjectName(Cwd);
        ToolLabel = Tool.StartsWith("mcp__", StringComparison.Ordinal) ? string.Join(" · ", Tool[5..].Split("__")) : Tool;
        Detail = Describe(Tool, Input);
    }

    public void Allow() => Respond(Reply(w => w.WriteString("behavior", "allow")));

    public void Deny(string message) => Respond(Reply(w =>
    {
        w.WriteString("behavior", "deny");
        w.WriteString("message", message);
        w.WriteBoolean("interrupt", false);
    }));

    /// <summary>
    /// Ответить на вопросы Claude (AskUserQuestion): разрешить вызов, подставив в ввод поле <c>answers</c>
    /// (ключ — текст вопроса, значение — выбранная подпись; при мультивыборе подписи через «, »; свой текст — строка).
    /// Формат проверен опытом разведки этапа 3.
    /// </summary>
    public void AllowWithAnswers(IEnumerable<KeyValuePair<string, string>> answers) => Respond(Reply(w =>
    {
        w.WriteString("behavior", "allow");
        w.WriteStartObject("updatedInput");
        if (Input.ValueKind == JsonValueKind.Object)
            foreach (var property in Input.EnumerateObject())
                if (property.Name != "answers") property.WriteTo(w);
        w.WriteStartObject("answers");
        foreach (var (question, answer) in answers) w.WriteString(question, answer);
        w.WriteEndObject();
        w.WriteEndObject();
    }));

    /// <summary>Не отвечать: хук завершится без решения, и запрос решит обычное окно VS Code.</summary>
    public void Release() => Respond("");

    /// <summary>Вызывается мостом, когда хук отключился.</summary>
    internal void NotifyClosed()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        IsClosed = true;
        Closed?.Invoke();
        Finished?.Invoke();
    }

    private void Respond(string line)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        _send(line);
        Finished?.Invoke();
    }

    // Формат ответа проверен опытом этапа 3: hookSpecificOutput.decision.behavior.
    private static string Reply(Action<Utf8JsonWriter> decision)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteStartObject("hookSpecificOutput");
            w.WriteString("hookEventName", "PermissionRequest");
            w.WriteStartObject("decision");
            decision(w);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string ProjectName(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static string Describe(string tool, JsonElement input)
    {
        var main = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Edit" or "Write" or "Read" or "MultiEdit" => Str(input, "file_path"),
            "NotebookEdit" => Str(input, "notebook_path"),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "Glob" or "Grep" => Str(input, "pattern"),
            "Task" or "Agent" => Str(input, "description"),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(main) && input.ValueKind == JsonValueKind.Object)
        {
            // Незнакомый инструмент (например, из MCP): первое текстовое поле ввода, иначе сам ввод.
            foreach (var p in input.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                {
                    main = p.Value.GetString();
                    break;
                }
            main ??= input.GetRawText();
        }

        main = (main ?? "").Trim();
        // Длинное всё равно обрежется на карточке до трёх строк; не тащим в интерфейс мегабайты текста.
        return main.Length > 600 ? main[..600] : main;
    }
}
