using System.Net.Http;
using System.Text;
using System.Text.Json;
using Ostrov.Modules.Player.Lyrics;

namespace Ostrov.Modules.Player;

/// <summary>Что известно о треке сверх названия и исполнителя (любое поле может быть пустым).</summary>
internal sealed record TrackInfo(string? Album, int? Year, string? Genre, int? TrackNumber, int? TrackCount)
{
    public static readonly TrackInfo Empty = new(null, null, null, null, null);
}

/// <summary>
/// Данные о треке (год выхода, жанр, номер трека в альбоме) из iTunes Search API Apple: бесплатно, без ключей. Apple Music
/// сама отдаёт Windows только название и «Исполнитель — Альбом». В сеть уходят название, исполнитель и альбом (как и в LRCLIB),
/// и больше ничего. Ответ запоминается на время работы программы (последние 50 треков), запросы строго по одному.
/// Подходящая запись: то же название и исполнитель; в приоритете тот же альбом, потом ближе по длительности.
/// Если альбом не нашёлся, номер трека не показываем (он был бы от другого издания), а год берём самый ранний.
/// </summary>
internal sealed class TrackInfoService
{
    private const int CacheLimit = 50;
    private static readonly HttpClient Http = CreateClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TrackInfo> _cache = new();
    private readonly Queue<string> _order = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://itunes.apple.com/"), Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ostrov/1.0 (desktop widget)"); // общий идентификатор программы, без личных данных
        return client;
    }

    /// <summary>Сетевая ошибка — исключение (в память не пишется, при следующем открытии текста попробуем снова).</summary>
    public async Task<TrackInfo> GetAsync(TrackKey key, CancellationToken cancellation = default)
    {
        lock (_cache) { if (_cache.TryGetValue(key.Id, out var cached)) return cached; }
        await _gate.WaitAsync(cancellation);
        try
        {
            lock (_cache) { if (_cache.TryGetValue(key.Id, out var cached)) return cached; }
            var info = await FetchAsync(key, cancellation);
            lock (_cache)
            {
                _cache[key.Id] = info;
                _order.Enqueue(key.Id);
                while (_order.Count > CacheLimit) _cache.Remove(_order.Dequeue());
            }
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<TrackInfo> FetchAsync(TrackKey key, CancellationToken ct)
    {
        var firstArtist = key.Artist.Split([", ", " & ", " feat. ", " ft. "], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? key.Artist;
        var url = $"search?media=music&entity=song&limit=25&term={Uri.EscapeDataString(firstArtist + " " + key.Title)}";
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Pick(doc.RootElement, key);
    }

    internal static TrackInfo Pick(JsonElement root, TrackKey key)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return FromAlbumOnly(key);
        var title = Normalize(key.Title);
        var artist = Normalize(key.Artist.Split([", ", " & ", " feat. ", " ft. "], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? key.Artist);
        var album = Normalize(key.Album);

        JsonElement? best = null;
        (int AlbumMiss, double Diff) bestKey = (int.MaxValue, double.MaxValue);
        var earliest = int.MaxValue;
        string? earliestGenre = null;
        foreach (var item in results.EnumerateArray())
        {
            if (!SameText(Text(item, "trackName"), title) || !SameText(Text(item, "artistName"), artist, loose: true)) continue;
            var year = Year(item);
            if (year is { } y && y < earliest) { earliest = y; earliestGenre = Text(item, "primaryGenreName"); }
            // Тот же альбом: 0 — название совпало буква в букву, 1 — совпало без скобок и « - Single» (например «After Hours (Deluxe)»), 2 — другой.
            var collection = Text(item, "collectionName");
            var albumMiss = album.Length == 0 ? 2
                : string.Equals(Clean(collection), Clean(key.Album), StringComparison.OrdinalIgnoreCase) ? 0
                : Normalize(collection) == album ? 1 : 2;
            var duration = item.TryGetProperty("trackTimeMillis", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() / 1000 : 0;
            var diff = key.DurationSeconds > 0 && duration > 0 ? Math.Round(Math.Abs(duration - key.DurationSeconds), 1) : 0;
            if (key.DurationSeconds > 0 && duration > 0 && diff > 6) continue; // другая версия трека
            if ((albumMiss, diff).CompareTo(bestKey) < 0) { bestKey = (albumMiss, diff); best = item.Clone(); }
        }

        if (best is not { } found) return FromAlbumOnly(key);
        if (bestKey.AlbumMiss <= 1)
        {
            // Название альбома показываем так, как его пишет Apple Music у пользователя (в iTunes бывает «… (Deluxe)»).
            return new TrackInfo(NullIfEmpty(key.Album) ?? Clean(Text(found, "collectionName")), Year(found), Genre(Text(found, "primaryGenreName")),
                Number(found, "trackNumber"), Number(found, "trackCount"));
        }
        // Нашлась только запись из другого альбома (сборник, сингл): год берём самый ранний, номер трека не показываем.
        return new TrackInfo(NullIfEmpty(key.Album), earliest == int.MaxValue ? null : earliest, Genre(earliestGenre), null, null);
    }

    private static TrackInfo FromAlbumOnly(TrackKey key) => new(NullIfEmpty(key.Album), null, null, null, null);

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : null;

    private static int? Year(JsonElement e)
    {
        var s = Text(e, "releaseDate");
        return s.Length >= 4 && int.TryParse(s.AsSpan(0, 4), out var y) && y > 1900 ? y : null;
    }

    // Название альбома без хвоста вида « - Single» / « - EP» (так их пишет iTunes).
    private static string? Clean(string name)
    {
        name = name.Trim();
        foreach (var tail in new[] { " - Single", " - EP" })
            if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) name = name[..^tail.Length].TrimEnd();
        return name.Length == 0 ? null : name;
    }

    // Для сравнения: без регистра, «ё» как «е», без скобок («(feat. …)», «(edit)»), без « - Single» и знаков препинания.
    private static string Normalize(string s)
    {
        s = (Clean(s) ?? "").ToLowerInvariant().Replace('ё', 'е');
        var sb = new StringBuilder(s.Length);
        var depth = 0;
        foreach (var c in s)
        {
            if (c is '(' or '[') { depth++; continue; }
            if (c is ')' or ']') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0 && (char.IsLetterOrDigit(c) || c == ' ')) sb.Append(c);
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool SameText(string iTunes, string wanted, bool loose = false)
    {
        var a = Normalize(iTunes);
        if (a.Length == 0 || wanted.Length == 0) return false;
        return a == wanted || (loose && (a.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(a, StringComparison.Ordinal)));
    }

    // Жанры в iTunes английские; самые частые переводим так, как их пишет Apple Music по-русски. Остальные оставляем как есть.
    private static readonly Dictionary<string, string> GenreRu = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pop"] = "Поп", ["Rock"] = "Рок", ["Alternative"] = "Альтернатива", ["Hip-Hop/Rap"] = "Хип-хоп/рэп", ["R&B/Soul"] = "R&B/соул",
        ["Electronic"] = "Электронная", ["Dance"] = "Танцевальная", ["Indie Pop"] = "Инди-поп", ["Indie Rock"] = "Инди-рок", ["Metal"] = "Метал",
        ["Hard Rock"] = "Хард-рок", ["Punk"] = "Панк", ["Jazz"] = "Джаз", ["Classical"] = "Классика", ["Country"] = "Кантри",
        ["Latin"] = "Латиноамериканская", ["Reggae"] = "Регги", ["Blues"] = "Блюз", ["Folk"] = "Фолк", ["Soundtrack"] = "Саундтрек",
        ["Singer/Songwriter"] = "Авторская песня", ["K-Pop"] = "K-pop", ["J-Pop"] = "J-pop", ["Easy Listening"] = "Лёгкая музыка",
        ["Children's Music"] = "Детская музыка", ["World"] = "Мировая музыка", ["Christian"] = "Христианская", ["Gospel"] = "Госпел",
        ["New Age"] = "Нью-эйдж", ["Ambient"] = "Эмбиент", ["House"] = "Хаус", ["Techno"] = "Техно", ["Trance"] = "Транс",
        ["Russian Pop"] = "Русская поп-музыка", ["Russian Rock"] = "Русский рок", ["Russian Rap"] = "Русский рэп", ["Rap"] = "Рэп", ["Hip Hop"] = "Хип-хоп",
        ["Holiday"] = "Праздничная", ["Vocal"] = "Вокальная", ["Comedy"] = "Комедия", ["Spoken Word"] = "Разговорный жанр",
    };

    private static string? Genre(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : GenreRu.TryGetValue(name.Trim(), out var ru) ? ru : name.Trim();
}
