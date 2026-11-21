using Ostrov.Services;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ostrov.Search;

internal enum ChatEventKind { Text, Searching, Source, Incomplete, Done, Error, Action }

/// <param name="Text">Текст (для Text — кусок ответа, для Error — сообщение, для Incomplete — причина).</param>
/// <param name="AuthProblem">Ошибка ключа (401): остров должен попросить новый.</param>
/// <param name="Args">Для Action: параметры вызова функции (JSON); Text — имя функции.</param>
internal readonly record struct ChatEvent(ChatEventKind Kind, string Text = "", string? Url = null, string? Title = null, bool AuthProblem = false, string? Args = null);

internal sealed record ChatTurn(bool IsUser, string Text);

/// <summary>
/// Запрос к OpenAI (Responses API) с потоковым ответом. Модель сама решает, нужен ли поиск в интернете (встроенный инструмент web_search).
/// Диалог целиком уходит с каждым вопросом, на серверах OpenAI он не хранится (store: false).
/// </summary>
internal static class OpenAiChat
{
    public const string DefaultModel = "gpt-5-mini";

    /// <summary>Адрес API. Меняется только в проверках (поддельный сервер).</summary>
    internal static string BaseUrl { get; set; } = "https://api.openai.com/v1";

    // Подключение к api.openai.com идёт через тоннель до своего сервера, если он включён в настройках (ServerTunnel), иначе напрямую.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectCallback = ServerTunnel.ConnectAsync }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90); // модель с поиском может молчать долго, но не бесконечно
    private static readonly Regex ReasoningModel = new(@"^(gpt-5|gpt-6|o\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Instructions() =>
        "Ты — быстрый помощник в маленьком окне на рабочем столе. Сегодня " + DateTime.Now.ToString("d MMMM yyyy") + ". " +
        "Отвечай по-русски (если пользователь пишет на другом языке — на его языке), по существу и коротко: сначала прямой ответ, " +
        "потом при необходимости несколько пунктов. Не повторяй вопрос и не пиши вступлений. " +
        "Формат — простой Markdown: **жирный**, списки, `код`, блоки кода; без таблиц и без больших заголовков. " +
        "Если вопрос про актуальное (курсы, новости, погода, цены, версии программ, события, что-то, что могло измениться), " +
        "используй поиск в интернете; на общие знания и объяснения поиск не нужен. Если не уверен — скажи об этом.";

    private static string Body(IReadOnlyList<ChatTurn> history, string model, bool tools, bool reasoning, string? instructions, bool actions = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["instructions"] = (instructions ?? Instructions()) + (actions ? AiActions.InstructionsNote() : ""),
            ["input"] = history.Select(t => new Dictionary<string, object> { ["role"] = t.IsUser ? "user" : "assistant", ["content"] = t.Text }).ToList(),
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = 10000
        };
        if (tools || actions)
        {
            var list = new List<object>();
            if (tools) list.Add(new { type = "web_search" });
            if (actions) list.AddRange(AiActions.Tools()); // функции острова: напоминания, «после рендера», пересчёт валют
            body["tools"] = list;
            body["tool_choice"] = "auto";
        }
        // Поиск в интернете у моделей gpt-5 не работает с минимальными рассуждениями, поэтому «low».
        if (reasoning) body["reasoning"] = new { effort = "low" };
        return JsonSerializer.Serialize(body);
    }

    /// <param name="instructions">Свои указания вместо обычных (для служебных запросов вроде подбора тем новостей).</param>
    /// <param name="allowSearch">false — поиск в интернете модели не даётся.</param>
    public static async IAsyncEnumerable<ChatEvent> AskAsync(
        IReadOnlyList<ChatTurn> history, string apiKey, string model, [EnumeratorCancellation] CancellationToken ct,
        string? instructions = null, bool allowSearch = true, bool actions = false)
    {
        var tools = allowSearch;
        var reasoning = ReasoningModel.IsMatch(model);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + "/responses")
            {
                Content = new StringContent(Body(history, model, tools, reasoning, instructions, actions), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            HttpResponseMessage? response = null;
            string? failure = null;
            using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                headerCts.CancelAfter(IdleTimeout);
                try { response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "OpenAI не отвечает. Проверь интернет и VPN."; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException) { failure = "Нет связи с OpenAI. Проверь интернет и VPN."; }
            }
            if (failure != null) { yield return new ChatEvent(ChatEventKind.Error, failure); yield break; }

            using (response)
            {
                if (!response!.IsSuccessStatusCode)
                {
                    var text = await ReadBody(response, ct);
                    var message = ErrorMessage(text) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                    // Модель не принимает «рассуждения» или поиск — пробуем без них (один раз на каждое).
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && attempt < 2)
                    {
                        if (reasoning && message.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) { reasoning = false; continue; }
                        if ((tools || actions) && (message.Contains("web_search", StringComparison.OrdinalIgnoreCase) || message.Contains("tool", StringComparison.OrdinalIgnoreCase) || message.Contains("function", StringComparison.OrdinalIgnoreCase))) { tools = false; actions = false; continue; }
                    }
                    var auth = response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                    yield return new ChatEvent(ChatEventKind.Error, Friendly((int)response.StatusCode, message), AuthProblem: auth);
                    yield break;
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var finished = false;
                while (true)
                {
                    string? line = null;
                    string? readError = null;
                    // Если за IdleTimeout не пришло ни строки (обрыв сети без разрыва соединения), не крутим «Думаю…» вечно.
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(IdleTimeout);
                    try { line = await reader.ReadLineAsync(readCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { readError = "OpenAI давно не отвечает. Попробуй ещё раз."; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or HttpRequestException) { readError = "Связь с OpenAI прервалась."; }
                    if (readError != null) { yield return new ChatEvent(ChatEventKind.Error, readError); yield break; }
                    if (line == null) break; // поток закончился

                    // Строки «data: {...}»; тип события берём из самого JSON (поле type).
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line[5..].Trim();
                    if (data.Length == 0 || data == "[DONE]") continue;
                    foreach (var ev in Parse(data))
                    {
                        yield return ev;
                        if (ev.Kind is ChatEventKind.Done or ChatEventKind.Error) finished = true;
                    }
                    if (finished) yield break;
                }
                if (!finished) yield return new ChatEvent(ChatEventKind.Done);
                yield break;
            }
        }
    }

    private static async Task<string> ReadBody(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch { return ""; }
    }

    // Сообщение об ошибке из тела ответа ({"error":{"message":"…"}}); разные формы терпим.
    internal static string? ErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
                    if (e.ValueKind == JsonValueKind.String) return e.GetString();
                }
                if (root.TryGetProperty("message", out var mm) && mm.ValueKind == JsonValueKind.String) return mm.GetString();
            }
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }

    private static string Friendly(int status, string message) => status switch
    {
        401 => "Ключ OpenAI не подошёл.",
        // Блокировка по региону приходит как 403 «Country, region, or territory not supported»: говорим прямо, что нужен VPN.
        403 when message.Contains("country", StringComparison.OrdinalIgnoreCase) || message.Contains("region", StringComparison.OrdinalIgnoreCase)
            || message.Contains("territory", StringComparison.OrdinalIgnoreCase) => "OpenAI не работает из твоего региона. Включи VPN и повтори.",
        403 => "OpenAI отказал в доступе: " + message,
        404 => "Модель не найдена: " + message,
        429 => "Лимит запросов или закончились деньги на счёте OpenAI: " + message,
        >= 500 => "Сервис OpenAI сейчас недоступен (" + status + "). Попробуй позже.",
        _ => message
    };

    /// <summary>Разбор одного события потока. Незнакомые события пропускаем.</summary>
    internal static IEnumerable<ChatEvent> Parse(string json)
    {
        var events = new List<ChatEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                        events.Add(new ChatEvent(ChatEventKind.Text, d.GetString() ?? ""));
                    break;
                case "response.output_item.done":
                    // Модель вызвала функцию острова: имя и параметры; ответ на этом закончится (результат ей не возвращаем).
                    if (root.TryGetProperty("item", out var doneItem) && doneItem.TryGetProperty("type", out var dt) && dt.GetString() == "function_call"
                        && doneItem.TryGetProperty("name", out var fname) && fname.GetString() is { Length: > 0 } fn)
                        events.Add(new ChatEvent(ChatEventKind.Action, fn, Args: doneItem.TryGetProperty("arguments", out var fargs) && fargs.ValueKind == JsonValueKind.String ? fargs.GetString() : null));
                    break;
                case "response.output_item.added":
                    if (root.TryGetProperty("item", out var item) && item.TryGetProperty("type", out var it) && it.GetString() == "web_search_call")
                        events.Add(new ChatEvent(ChatEventKind.Searching));
                    break;
                case "response.output_text.annotation.added":
                    if (root.TryGetProperty("annotation", out var an)) AddSource(events, an);
                    break;
                case "response.completed":
                    // Источники из итогового ответа (на случай, если отдельных событий не было; повторы отсеет получатель).
                    if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                        foreach (var o in output.EnumerateArray())
                            if (o.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                                foreach (var c in content.EnumerateArray())
                                    if (c.TryGetProperty("annotations", out var anns) && anns.ValueKind == JsonValueKind.Array)
                                        foreach (var a in anns.EnumerateArray()) AddSource(events, a);
                    events.Add(new ChatEvent(ChatEventKind.Done));
                    break;
                case "response.incomplete":
                    var reason = root.TryGetProperty("response", out var ir) && ir.TryGetProperty("incomplete_details", out var det) && det.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                    events.Add(new ChatEvent(ChatEventKind.Incomplete, reason));
                    events.Add(new ChatEvent(ChatEventKind.Done));
                    break;
                case "response.failed":
                    var fm = root.TryGetProperty("response", out var fr) && fr.TryGetProperty("error", out var fe) && fe.TryGetProperty("message", out var fmm) ? fmm.GetString() : null;
                    events.Add(new ChatEvent(ChatEventKind.Error, fm ?? "OpenAI не смог ответить."));
                    break;
                case "error":
                    var em = root.TryGetProperty("message", out var emm) && emm.ValueKind == JsonValueKind.String ? emm.GetString()
                        : root.TryGetProperty("error", out var ee) && ee.TryGetProperty("message", out var eem) ? eem.GetString() : null;
                    events.Add(new ChatEvent(ChatEventKind.Error, em ?? "Ошибка OpenAI."));
                    break;
                default:
                    // Любые события поиска (response.web_search_call.in_progress / searching): показываем «Ищу в интернете».
                    if (type.StartsWith("response.web_search_call.", StringComparison.Ordinal) && !type.EndsWith(".completed", StringComparison.Ordinal))
                        events.Add(new ChatEvent(ChatEventKind.Searching));
                    break;
            }
        }
        catch (JsonException) { }
        return events;
    }

    private static void AddSource(List<ChatEvent> events, JsonElement annotation)
    {
        if (!annotation.TryGetProperty("type", out var type) || type.GetString() != "url_citation") return;
        if (!annotation.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) return;
        var title = annotation.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() : null;
        events.Add(new ChatEvent(ChatEventKind.Source, Url: url.GetString(), Title: title));
    }
}
