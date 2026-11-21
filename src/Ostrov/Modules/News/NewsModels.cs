using System.IO;
using System.Text.Json;

namespace Ostrov.Modules.News;

/// <summary>Лента новостей (RSS или Atom). <see cref="Sphere"/>: "games" или "ai".</summary>
internal sealed record NewsFeed(string Id, string Name, string Url, string Sphere, string Lang);

/// <summary>Подтема сферы: набор слов, которые поднимают заголовок в ленте (а не фильтруют: остальное тоже остаётся, но ниже).</summary>
internal sealed record NewsSubtopic(string Id, string Sphere, string Title, string[] Keywords);

/// <summary>Одна новость: только заголовок, ссылка, источник и время (текст статьи не загружается).</summary>
internal sealed class NewsItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Link { get; set; } = "";
    public string Source { get; set; } = "";
    public string Sphere { get; set; } = "";
    public string Lang { get; set; } = "";
    public DateTimeOffset Published { get; set; }

    /// <summary>Картинка записи из самой ленты (если лента её даёт): запасная обложка, когда страница статьи не отдаёт своей.</summary>
    public string? Image { get; set; }
}

/// <summary>Реакция пользователя на новость: 1 — интересно, −1 — неинтересно. Заголовок помним, чтобы учиться и (позже) отправлять недельную сводку.</summary>
internal sealed class NewsReaction
{
    public int Value { get; set; }
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string Sphere { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>Интересы пользователя и то, чему лента научилась по лайкам. Хранится в <c>%AppData%\Ostrov\news-profile.json</c>.</summary>
internal sealed class NewsProfile
{
    /// <summary>Включённые подтемы (Id из <see cref="NewsCatalog.Subtopics"/>).</summary>
    public List<string> ActiveSubtopics { get; set; } = [.. NewsCatalog.DefaultSubtopics];

    /// <summary>Свои слова пользователя (поднимают заголовки сильнее встроенных).</summary>
    public List<string> CustomKeywords { get; set; } = [];

    public Dictionary<string, NewsReaction> Reactions { get; set; } = [];

    /// <summary>Выученные веса слов и источников: лайк поднимает, дизлайк опускает. Учатся только на компьютере.</summary>
    public Dictionary<string, double> Terms { get; set; } = [];
    public Dictionary<string, double> Sources { get; set; } = [];

    /// <summary>Когда ИИ последний раз подбирал интересы (раз в неделю, см. <see cref="NewsReview"/>).</summary>
    public DateTimeOffset? LastAiReview { get; set; }

    /// <summary>Предложение ИИ, которое ждёт «Применить / Отклонить»; профиль без нажатия не меняется.</summary>
    public NewsSuggestion? Suggestion { get; set; }
}

/// <summary>Правки интересов от недельного подбора: какие подтемы включить и выключить, какие слова добавить.</summary>
internal sealed class NewsSuggestion
{
    public List<string> Enable { get; set; } = [];
    public List<string> Disable { get; set; } = [];
    public List<string> AddWords { get; set; } = [];
    public string Summary { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Встроенный список лент и подтем. Все ленты проверены живыми 2026-11-13.</summary>
internal static class NewsCatalog
{
    public const string Games = "games", Ai = "ai";

    public static IReadOnlyList<NewsFeed> Feeds { get; } =
    [
        new("pcgamer", "PC Gamer", "https://www.pcgamer.com/rss/", Games, "en"),
        new("rps", "Rock Paper Shotgun", "https://www.rockpapershotgun.com/feed", Games, "en"),
        new("ign", "IGN", "https://feeds.ign.com/ign/games-all", Games, "en"),
        new("eurogamer", "Eurogamer", "https://www.eurogamer.net/feed", Games, "en"),
        new("stopgame", "StopGame", "https://stopgame.ru/rss/rss_news.xml", Games, "ru"),
        new("igromania", "Игромания", "https://www.igromania.ru/rss/rss_news.xml", Games, "ru"),
        new("dtf", "DTF", "https://dtf.ru/rss", Games, "ru"),
        new("openai", "OpenAI", "https://openai.com/news/rss.xml", Ai, "en"),
        new("hf", "Hugging Face", "https://huggingface.co/blog/feed.xml", Ai, "en"),
        new("mit", "MIT Technology Review", "https://www.technologyreview.com/topic/artificial-intelligence/feed", Ai, "en"),
        new("verge", "The Verge", "https://www.theverge.com/rss/ai-artificial-intelligence/index.xml", Ai, "en"),
        new("ars", "Ars Technica", "https://arstechnica.com/ai/feed/", Ai, "en"),
        new("googleai", "Google AI", "https://blog.google/technology/ai/rss/", Ai, "en"),
        new("habr-ai", "Хабр · ИИ", "https://habr.com/ru/rss/hubs/artificial_intelligence/all/", Ai, "ru"),
        new("habr-ml", "Хабр · ML", "https://habr.com/ru/rss/hubs/machine_learning/all/", Ai, "ru"),
    ];

    public static IReadOnlyList<NewsSubtopic> Subtopics { get; } =
    [
        new("g-shooters", Games, "Шутеры и соревновательные", ["shooter", "шутер", "counter-strike", "cs2", "valorant", "apex", "overwatch", "battlefield", "call of duty", "fps", "esports", "киберспорт", "ranked", "tournament", "турнир"]),
        new("g-releases", Games, "Релизы, скидки и Steam", ["release", "релиз", "launch", "steam", "sale", "скидк", "распродаж", "discount", "free", "бесплатн", "update", "обновлени", "patch", "патч", "дата выхода", "release date"]),
        new("g-rpg", Games, "RPG и приключения", ["rpg", "рпг", "adventure", "приключен", "open world", "открытый мир", "quest", "witcher", "elden ring", "baldur", "skyrim"]),
        new("g-indie", Games, "Инди", ["indie", "инди", "roguelike", "рогалик", "pixel", "демо", "demo"]),
        new("a-models", Ai, "Новые модели", ["gpt", "claude", "gemini", "openai", "anthropic", "deepmind", "model", "модел", "llm", "benchmark", "бенчмарк", "grok", "o3", "sonnet", "opus"]),
        new("a-dev", Ai, "ИИ для разработки и вайбкодинга", ["coding", "code", "cursor", "copilot", "claude code", "codex", "agent", "агент", "vibe", "вайб", "кодинг", "разработ", "developer", "ide", "programming", "программир"]),
        new("a-open", Ai, "Открытые модели и локальный запуск", ["open source", "open-source", "opensource", "открыт", "llama", "qwen", "mistral", "deepseek", "ollama", "local", "локальн", "hugging face", "weights", "gguf", "on-device"]),
        new("a-media", Ai, "Картинки и видео", ["image", "video", "картинк", "изображен", "видео", "midjourney", "sora", "veo", "stable diffusion", "flux", "runway", "diffusion", "генерац"]),
    ];

    /// <summary>Включено по выбору пользователя (2026-11-13): шутеры, релизы/Steam, новые модели, вайбкодинг, открытые модели.</summary>
    public static IReadOnlyList<string> DefaultSubtopics { get; } = ["g-shooters", "g-releases", "a-models", "a-dev", "a-open"];

    public static string SphereTitle(string sphere) => sphere == Games ? "Игры" : "ИИ";
}

/// <summary>Файлы новостей: профиль и кэш заголовков. Каталог можно подменить (проверки не должны трогать настоящие данные).</summary>
internal static class NewsStore
{
    internal static string? DirectoryOverride { get; set; }

    private static string Dir => DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov");
    private static string ProfilePath => Path.Combine(Dir, "news-profile.json");
    private static string CachePath => Path.Combine(Dir, "news-cache.json");

    public static NewsProfile LoadProfile()
    {
        try { return JsonSerializer.Deserialize<NewsProfile>(File.ReadAllText(ProfilePath)) ?? new(); }
        catch { return new(); }
    }

    public static void SaveProfile(NewsProfile profile) => Write(ProfilePath, profile);

    public static List<NewsItem> LoadCache()
    {
        try { return JsonSerializer.Deserialize<List<NewsItem>>(File.ReadAllText(CachePath)) ?? []; }
        catch { return []; }
    }

    public static void SaveCache(List<NewsItem> items) => Write(CachePath, items);

    public static DateTime CacheWrittenUtc => File.Exists(CachePath) ? File.GetLastWriteTimeUtc(CachePath) : DateTime.MinValue;

    private static void Write(string path, object value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* не удалось сохранить — не критично */ }
    }
}
