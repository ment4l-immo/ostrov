using System.IO;
using System.Text.Json;

namespace Ostrov.Claude;

/// <summary>
/// Событие хука Claude Code, на которое остров не отвечает: начало работы (<c>UserPromptSubmit</c>), конец (<c>Stop</c>)
/// или шаг работы (<c>PreToolUse</c> перед инструментом, <c>PostToolUse</c> после него).
/// Из ввода хука берутся только служебные поля (сессия, папка проекта, файл переписки) и для шага название инструмента с коротким именем файла;
/// текст запроса, ответ Claude и полные команды не читаются и нигде не хранятся.
/// </summary>
/// <param name="Tool">Название инструмента (только для шага).</param>
/// <param name="Step">Короткая подпись шага для плашки («читает ClaudeWork.cs», «выполняет команду»); только у <c>PreToolUse</c>.</param>
internal sealed record ClaudeEvent(string Name, string SessionId, string Cwd, string Project, string? TranscriptPath, string? Tool = null, string? Step = null)
{
    public const string PromptSubmit = "UserPromptSubmit", Stop = "Stop", PreTool = "PreToolUse", PostTool = "PostToolUse";

    public static bool IsKnown(string? name) => name is PromptSubmit or Stop or PreTool or PostTool;

    public static ClaudeEvent? TryParse(JsonElement hookInput)
    {
        var name = Str(hookInput, "hook_event_name");
        if (!IsKnown(name)) return null;
        var cwd = Str(hookInput, "cwd") ?? "";
        var trimmed = cwd.TrimEnd('\\', '/');
        var project = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(project)) project = trimmed;
        // Без номера сессии (старый формат ввода) считаем все события одной сессией этой папки.
        var session = Str(hookInput, "session_id") is { Length: > 0 } id ? id : "cwd:" + cwd;
        var tool = Str(hookInput, "tool_name");
        string? step = null;
        if (name == PreTool && tool != null)
            step = DescribeStep(tool, hookInput.TryGetProperty("tool_input", out var input) ? input : default);
        return new ClaudeEvent(name!, session, cwd, project, Str(hookInput, "transcript_path"), tool, step);
    }

    /// <summary>Что делает инструмент, по-русски и коротко. Команды и содержимое файлов не показываются: только название файла.</summary>
    public static string DescribeStep(string tool, JsonElement input)
    {
        string File() => ShortFile(Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path"));
        string With(string verb) => File() is { Length: > 0 } f ? $"{verb} {f}" : verb + " файл";
        return tool switch
        {
            "Read" => With("читает"),
            "Edit" or "MultiEdit" or "NotebookEdit" => With("правит"),
            "Write" => With("пишет"),
            "Bash" or "PowerShell" => "выполняет команду",
            "Grep" => "ищет в коде",
            "Glob" => "ищет файлы",
            "WebSearch" => "ищет в интернете",
            "WebFetch" => "читает страницу",
            "Task" or "Agent" => "запускает субагента",
            "AskUserQuestion" => "задаёт вопрос",
            "TodoWrite" => "обновляет план",
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "вызывает " + tool[(tool.LastIndexOf("__", StringComparison.Ordinal) + 2)..],
            _ => "использует " + tool
        };
    }

    private static string ShortFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 28 ? name[..27] + "…" : name;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
