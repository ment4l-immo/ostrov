using System.Text;
using System.Text.Json;
using Ostrov.Search;
using Ostrov.Services;

namespace Ostrov.Modules.News;

/// <summary>
/// Недельный подбор интересов ИИ: раз в семь дней (и по кнопке в «Темах») ИИ смотрит на заголовки, которые пользователь лайкал и дизлайкал,
/// и предлагает небольшие правки: какие подтемы включить или выключить и какие слова добавить. Это только предложение
/// (<see cref="NewsSuggestion"/>): профиль меняется, лишь когда пользователь нажал «Применить».
/// Приватность: на OpenAI уходят только эти заголовки с источником и отметкой (плюс общий каталог подтем, он не личный); включённые сейчас подтемы,
/// свои слова и всё остальное не отправляются. Ключ и модель те же, что у Alt+Q. Поиск в интернете модели не даётся.
/// </summary>
internal static class NewsReview
{
    public const int MinReactions = 10, MinLikes = 2;
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);
    private const int MaxHeadlines = 40;

    private const string Instructions =
        "Ты помогаешь настроить ленту новостей на компьютере по отметкам пользователя. " +
        "Отвечай ТОЛЬКО одним JSON-объектом, без Markdown, без обрамляющих кавычек-блоков и без пояснений.";

    /// <summary>null — всё готово к подбору; иначе причина по-русски (для кнопки).</summary>
    public static string? NotReadyReason(NewsProfile profile)
    {
        var reactions = Reactions(profile);
        var likes = reactions.Count(r => r.Value == 1);
        if (reactions.Count < MinReactions) return $"Нужно хотя бы {MinReactions} оценок: лайков и дизлайков (сейчас {reactions.Count})";
        if (likes < MinLikes) return "Нужна хотя бы пара лайков: пока почти одни дизлайки";
        return null;
    }

    /// <summary>Пора ли делать недельный подбор сам: не было или прошла неделя, нет висящего предложения, оценок хватает.</summary>
    public static bool IsDue(NewsProfile profile, DateTimeOffset now) =>
        profile.Suggestion == null
        && (profile.LastAiReview == null || now - profile.LastAiReview >= Interval)
        && NotReadyReason(profile) == null;

    private static List<NewsReaction> Reactions(NewsProfile profile) =>
        profile.Reactions.Values.Where(r => r.Value != 0 && r.Title.Length > 0).OrderByDescending(r => r.At).ToList();

    public static string BuildPrompt(NewsProfile profile)
    {
        var reactions = Reactions(profile);
        var sb = new StringBuilder();
        sb.AppendLine("Темы, которые в ленте можно включать и выключать (id — название):");
        foreach (var t in NewsCatalog.Subtopics) sb.AppendLine($"{t.Id} — {NewsCatalog.SphereTitle(t.Sphere)}: {t.Title}");
        sb.AppendLine();
        sb.AppendLine("Понравились пользователю:");
        foreach (var r in reactions.Where(r => r.Value == 1).Take(MaxHeadlines)) sb.AppendLine($"- [{r.Source}] {r.Title}");
        sb.AppendLine();
        sb.AppendLine("Не понравились:");
        foreach (var r in reactions.Where(r => r.Value == -1).Take(MaxHeadlines)) sb.AppendLine($"- [{r.Source}] {r.Title}");
        sb.AppendLine();
        sb.AppendLine("Предложи небольшие правки интересов:");
        sb.AppendLine("- \"enable\": id тем из списка, которые стоит включить (не больше 3);");
        sb.AppendLine("- \"disable\": id тем из списка, которые стоит выключить, потому что такие новости пользователю явно не нравятся (не больше 3);");
        sb.AppendLine("- \"addWords\": до 4 коротких слов или названий (2–25 знаков, строчными), по которым стоит поднимать новости; они должны часто встречаться в понравившихся заголовках и не встречаться в непонравившихся; на языке заголовков;");
        sb.AppendLine("- \"summary\": одна короткая фраза по-русски (до 110 знаков): что заметно в интересах.");
        sb.AppendLine("Предлагай правки только если они хорошо видны по отметкам. Если менять нечего, верни пустые списки.");
        sb.Append("Формат ответа: {\"enable\":[],\"disable\":[],\"addWords\":[],\"summary\":\"\"}");
        return sb.ToString();
    }

    /// <summary>Асинхронный запрос к OpenAI. Ok = получили понятный ответ (Suggestion может быть null: менять нечего); иначе Error по-русски.</summary>
    public static async Task<(bool Ok, NewsSuggestion? Suggestion, string? Error)> RunAsync(NewsProfile profile, DateTimeOffset now, CancellationToken token)
    {
        var key = OpenAiKeyStore.Load();
        if (key == null) return (false, null, "Нужен ключ OpenAI: вставь его в настройках");
        var model = AppSettings.Load().OpenAiModel is { Length: > 0 } m ? m : OpenAiChat.DefaultModel;
        var reply = new StringBuilder();
        try
        {
            await foreach (var ev in OpenAiChat.AskAsync([new ChatTurn(true, BuildPrompt(profile))], key, model, token, Instructions, allowSearch: false))
            {
                if (ev.Kind == ChatEventKind.Text) reply.Append(ev.Text);
                else if (ev.Kind == ChatEventKind.Error) return (false, null, ev.AuthProblem ? "Ключ OpenAI не подошёл: вставь новый в настройках" : ev.Text);
            }
        }
        catch (OperationCanceledException) { return (false, null, "ИИ не ответил вовремя"); }
        catch (Exception ex) { return (false, null, "Не удалось получить подбор: " + ex.Message); }
        return TryParse(reply.ToString(), profile, now, out var suggestion) ? (true, suggestion, null) : (false, null, "ИИ ответил непонятно, попробуй позже");
    }

    /// <summary>Разбор ответа: false — не JSON. true — разобрано; <paramref name="suggestion"/> null, если реальных правок нет.</summary>
    public static bool TryParse(string text, NewsProfile profile, DateTimeOffset now, out NewsSuggestion? suggestion)
    {
        suggestion = null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var known = NewsCatalog.Subtopics.Select(t => t.Id).ToHashSet();
            var enable = Strings(root, "enable").Where(id => known.Contains(id) && !profile.ActiveSubtopics.Contains(id)).Distinct().Take(3).ToList();
            var disable = Strings(root, "disable").Where(id => known.Contains(id) && profile.ActiveSubtopics.Contains(id) && !enable.Contains(id)).Distinct().Take(3).ToList();

            var builtin = NewsRanker.ActiveKeywords(profile).Builtin.ToHashSet();
            var words = new List<string>();
            foreach (var raw in Strings(root, "addWords"))
            {
                var word = raw.Trim().ToLowerInvariant();
                if (word.Length is < 2 or > 25) continue;
                if (profile.CustomKeywords.Contains(word, StringComparer.OrdinalIgnoreCase) || builtin.Contains(word) || words.Contains(word)) continue;
                words.Add(word);
                if (words.Count == 4) break;
            }

            if (enable.Count == 0 && disable.Count == 0 && words.Count == 0) return true;
            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.Trim() : "";
            if (summary.Length > 140) summary = summary[..139] + "…";
            suggestion = new NewsSuggestion { Enable = enable, Disable = disable, AddWords = words, Summary = summary, CreatedAt = now };
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static IEnumerable<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in array.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } value) yield return value;
    }

    /// <summary>Применить предложение к профилю и убрать его. Сохранение на диск — на вызывающем.</summary>
    public static void Apply(NewsProfile profile, NewsSuggestion suggestion)
    {
        foreach (var id in suggestion.Enable) if (!profile.ActiveSubtopics.Contains(id)) profile.ActiveSubtopics.Add(id);
        foreach (var id in suggestion.Disable) profile.ActiveSubtopics.Remove(id);
        foreach (var word in suggestion.AddWords) if (!profile.CustomKeywords.Contains(word, StringComparer.OrdinalIgnoreCase)) profile.CustomKeywords.Add(word);
        profile.Suggestion = null;
    }

    /// <summary>Подписи для карточки: «Включить: …», «Выключить: …», «Добавить слова: …».</summary>
    public static IEnumerable<string> Describe(NewsSuggestion suggestion)
    {
        string Title(string id) => NewsCatalog.Subtopics.FirstOrDefault(t => t.Id == id)?.Title ?? id;
        if (suggestion.Enable.Count > 0) yield return "Включить: " + string.Join(", ", suggestion.Enable.Select(Title));
        if (suggestion.Disable.Count > 0) yield return "Выключить: " + string.Join(", ", suggestion.Disable.Select(Title));
        if (suggestion.AddWords.Count > 0) yield return "Добавить слова: " + string.Join(", ", suggestion.AddWords);
    }
}
