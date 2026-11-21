using System.Text.RegularExpressions;

namespace Ostrov.Modules.News;

/// <summary>
/// Порядок в ленте и обучение по лайкам. Оценка новости = свежесть (спад вдвое примерно за сутки) + совпадения со словами включённых подтем
/// и своими словами + выученные веса слов заголовка и источника. Лайк поднимает слова и источник, дизлайк опускает и прячет новость.
/// Всё считается на компьютере; в сеть ничего не уходит.
/// </summary>
internal static partial class NewsRanker
{
    private const double HalfLifeHours = 30, TopicWeight = 1.5, CustomWeight = 2.2, MaxLearned = 3, WeightCap = 5;
    private const int MaxTerms = 600;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "with", "from", "this", "will", "have", "what", "your", "about", "after", "into", "their", "they", "just", "more", "than", "when", "over",
        "эта", "этот", "этих", "этой", "который", "которые", "будет", "чтобы", "также", "после", "через", "стал", "стала", "новый", "новая", "новое", "новые"
    };

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Splitter();

    public static IEnumerable<string> Tokens(string title) =>
        Splitter().Split(title.ToLowerInvariant()).Where(t => t.Length >= 4 && !Stop.Contains(t)).Distinct();

    /// <summary>Отсортированная лента: скрыты дизлайкнутые; <paramref name="sphere"/> — "games", "ai" или null (все).</summary>
    public static List<NewsItem> Rank(IEnumerable<NewsItem> items, NewsProfile profile, DateTimeOffset now, string? sphere)
    {
        var keywords = ActiveKeywords(profile);
        return items
            .Where(i => sphere == null || i.Sphere == sphere)
            .Where(i => !profile.Reactions.TryGetValue(i.Id, out var r) || r.Value >= 0)
            .Select(i => (Item: i, Score: Score(i, profile, keywords, now)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();
    }

    public static double Score(NewsItem item, NewsProfile profile, (string[] Builtin, string[] Custom) keywords, DateTimeOffset now)
    {
        var age = Math.Max(0, (now - item.Published).TotalHours);
        var score = 3 * Math.Pow(0.5, age / HalfLifeHours);
        var lower = item.Title.ToLowerInvariant();
        score += TopicWeight * Math.Min(3, keywords.Builtin.Count(k => lower.Contains(k, StringComparison.Ordinal)));
        score += CustomWeight * Math.Min(2, keywords.Custom.Count(k => lower.Contains(k, StringComparison.Ordinal)));
        var learned = Tokens(item.Title).Sum(t => profile.Terms.TryGetValue(t, out var w) ? w : 0);
        score += Math.Clamp(learned, -MaxLearned, MaxLearned);
        if (profile.Sources.TryGetValue(item.Source, out var s)) score += Math.Clamp(s, -2, 2);
        return score;
    }

    public static (string[] Builtin, string[] Custom) ActiveKeywords(NewsProfile profile) =>
    (
        NewsCatalog.Subtopics.Where(t => profile.ActiveSubtopics.Contains(t.Id)).SelectMany(t => t.Keywords).Select(k => k.ToLowerInvariant()).Distinct().ToArray(),
        profile.CustomKeywords.Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 1).Distinct().ToArray()
    );

    /// <summary>Записать реакцию (1 — интересно, −1 — нет, 0 — снять) и доучить веса слов и источника.</summary>
    public static void React(NewsProfile profile, NewsItem item, int value, DateTimeOffset now)
    {
        profile.Reactions.TryGetValue(item.Id, out var previous);
        var delta = value - (previous?.Value ?? 0); // повторный лайк не удваивает вес, смена мнения переучивает на разницу
        if (value == 0) profile.Reactions.Remove(item.Id);
        else profile.Reactions[item.Id] = new NewsReaction { Value = value, Title = item.Title, Source = item.Source, Sphere = item.Sphere, At = now };
        if (delta != 0)
        {
            foreach (var token in Tokens(item.Title)) Bump(profile.Terms, token, delta * 1.0);
            Bump(profile.Sources, item.Source, delta * 0.5);
        }
        Trim(profile, now);
    }

    private static void Bump(Dictionary<string, double> weights, string key, double delta)
    {
        var value = Math.Clamp((weights.TryGetValue(key, out var w) ? w : 0) + delta, -WeightCap, WeightCap);
        if (Math.Abs(value) < 0.01) weights.Remove(key);
        else weights[key] = value;
    }

    // Старые реакции (старше 60 дней) забываем, слов храним не больше MaxTerms самых весомых.
    private static void Trim(NewsProfile profile, DateTimeOffset now)
    {
        foreach (var id in profile.Reactions.Where(r => now - r.Value.At > TimeSpan.FromDays(60)).Select(r => r.Key).ToList()) profile.Reactions.Remove(id);
        if (profile.Terms.Count > MaxTerms)
            profile.Terms = profile.Terms.OrderByDescending(p => Math.Abs(p.Value)).Take(MaxTerms).ToDictionary(p => p.Key, p => p.Value);
    }
}
