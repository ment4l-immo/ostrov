using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ostrov.Modules.Player.Lyrics;

/// <summary>
/// Тексты песен из бесплатной базы LRCLIB (lrclib.net, без ключей). В сеть уходят название, исполнитель, альбом и длительность
/// трека, и больше ничего. Запросы идут строго по одному (просьба LRCLIB не делать параллельных). Приоритет записей:
/// с пословными таймингами (поле hasWordSync, слова в lyricsfile), затем с построчными, затем обычный текст; версия трека
/// должна совпадать по длительности (иначе тайминги съехали бы). Найденное (и «текста нет») запоминается на диске
/// в %AppData%\Ostrov\lyrics (последние 50 треков; «нет» живёт сутки), повторных запросов нет.
/// </summary>
internal sealed class LyricsService
{
    private const int CacheLimit = 50;
    private const double SyncedToleranceSeconds = 3, PlainToleranceSeconds = 10;
    private static readonly TimeSpan NegativeLife = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cacheDir;

    /// <summary>Каталог кэша можно подменить (для проверок).</summary>
    public LyricsService(string? cacheDir = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "lyrics");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ostrov/1.0 (desktop widget)"); // общий идентификатор программы, без личных данных
        return client;
    }

    /// <summary>Текст для трека. Сетевая ошибка — исключение (в кэш не пишется, при следующем треке попробуем снова).</summary>
    public async Task<LyricsResult> GetAsync(TrackKey key, CancellationToken cancellation = default)
    {
        if (TryReadCache(key, out var cached)) return cached;
        await _gate.WaitAsync(cancellation);
        try
        {
            if (TryReadCache(key, out cached)) return cached; // пока ждали очередь, мог появиться
            var result = await FetchAsync(key, cancellation);
            WriteCache(key, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- сеть ----------

    private static async Task<LyricsResult> FetchAsync(TrackKey key, CancellationToken ct)
    {
        // 1) поиск по названию и первому исполнителю: в ответе сразу тексты и признак пословной синхронизации
        var firstArtist = key.Artist.Split([", ", " & ", " feat. ", " ft. "], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? key.Artist;
        var search = $"api/search?track_name={Uri.EscapeDataString(key.Title)}&artist_name={Uri.EscapeDataString(firstArtist)}";
        using (var response = await Http.GetAsync(search, ct))
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (Pick(doc.RootElement, key) is { } picked)
                {
                    var result = ParseEntry(picked);
                    if (result.Found) return result;
                }
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw new HttpRequestException($"LRCLIB ответил {(int)response.StatusCode}");
            }
        }

        // 2) точное совпадение (название, исполнитель, альбом, длительность)
        var exact = $"api/get?track_name={Uri.EscapeDataString(key.Title)}&artist_name={Uri.EscapeDataString(key.Artist)}" +
                    $"&album_name={Uri.EscapeDataString(key.Album)}&duration={key.DurationSeconds}";
        using var exactResponse = await Http.GetAsync(exact, ct);
        if (exactResponse.StatusCode == HttpStatusCode.OK)
        {
            using var doc = JsonDocument.Parse(await exactResponse.Content.ReadAsStringAsync(ct));
            return ParseEntry(doc.RootElement.Clone());
        }
        return exactResponse.StatusCode == HttpStatusCode.NotFound
            ? LyricsResult.None
            : throw new HttpRequestException($"LRCLIB ответил {(int)exactResponse.StatusCode}");
    }

    // Лучшая запись: пословная → построчная → обычный текст; версия трека должна совпадать по длительности;
    // при равенстве ближе по длительности и с тем же альбомом.
    private static JsonElement? Pick(JsonElement items, TrackKey key)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        JsonElement? best = null;
        (int Rank, double Diff, int AlbumMiss) bestKey = (int.MaxValue, double.MaxValue, 1);
        foreach (var item in items.EnumerateArray())
        {
            if (Flag(item, "instrumental")) continue;
            var rank = Flag(item, "hasWordSync") ? 0 : HasText(item, "syncedLyrics") ? 1 : HasText(item, "plainLyrics") ? 2 : 3;
            if (rank == 3) continue;
            var duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            var diff = key.DurationSeconds > 0 && duration > 0 ? Math.Abs(duration - key.DurationSeconds) : 0;
            if (diff > (rank <= 1 ? SyncedToleranceSeconds : PlainToleranceSeconds)) continue;
            var albumMiss = item.TryGetProperty("albumName", out var a) && a.ValueKind == JsonValueKind.String &&
                            string.Equals(a.GetString(), key.Album, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            var candidate = (rank, Math.Round(diff, 1), albumMiss);
            if (candidate.CompareTo(bestKey) < 0) { bestKey = candidate; best = item.Clone(); }
        }
        return best;
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool HasText(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length > 0;

    private static LyricsResult ParseEntry(JsonElement entry)
    {
        if (Flag(entry, "instrumental")) return LyricsResult.None; // инструментал: текста нет

        // Пословные тайминги лежат в lyricsfile (YAML); если разобрать не вышло, идём дальше по построчным.
        if (Flag(entry, "hasWordSync") && entry.TryGetProperty("lyricsfile", out var file) && file.ValueKind == JsonValueKind.String)
        {
            var words = LyricsfileParser.Parse(file.GetString()!);
            if (words != null && words.Any(l => l.Words is { Count: > 0 })) return new LyricsResult(true, true, words);
        }
        if (entry.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String)
        {
            var lines = LrcParser.ParseSynced(synced.GetString()!);
            if (lines.Count > 0) return new LyricsResult(true, true, lines);
        }
        if (entry.TryGetProperty("plainLyrics", out var plain) && plain.ValueKind == JsonValueKind.String)
        {
            var lines = LrcParser.ParsePlain(plain.GetString()!);
            if (lines.Count > 0) return new LyricsResult(true, false, lines);
        }
        return LyricsResult.None;
    }

    // ---------- кэш ----------

    private string CachePath(TrackKey key) =>
        Path.Combine(_cacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key.Id))) + ".json");

    private bool TryReadCache(TrackKey key, out LyricsResult result)
    {
        result = LyricsResult.None;
        try
        {
            var path = CachePath(key);
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.GetProperty("found").GetBoolean())
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > NegativeLife) return false; // «нет» устарело: спросим ещё раз
                return true;
            }
            var lines = root.GetProperty("lines").EnumerateArray().Select(l =>
            {
                IReadOnlyList<LyricWord>? words = null;
                if (l.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.Array)
                    words = w.EnumerateArray().Select(x => new LyricWord(
                        TimeSpan.FromMilliseconds(x.GetProperty("s").GetInt64()), TimeSpan.FromMilliseconds(x.GetProperty("e").GetInt64()),
                        x.GetProperty("x").GetString() ?? "")).ToList();
                return new LyricLine(TimeSpan.FromMilliseconds(l.GetProperty("t").GetInt64()), l.GetProperty("x").GetString() ?? "", words);
            }).ToList();
            result = new LyricsResult(true, root.GetProperty("synced").GetBoolean(), lines);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch
        {
            return false; // битый кэш не мешает: спросим сеть
        }
    }

    private void WriteCache(TrackKey key, LyricsResult result)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var payload = new
            {
                found = result.Found,
                synced = result.Synced,
                lines = result.Lines.Select(l => new
                {
                    t = (long)l.Time.TotalMilliseconds,
                    x = l.Text,
                    w = l.Words?.Select(w => new { s = (long)w.Start.TotalMilliseconds, e = (long)w.End.TotalMilliseconds, x = w.Text })
                })
            };
            File.WriteAllText(CachePath(key), JsonSerializer.Serialize(payload));
            // Храним последние 50 (по времени записи).
            foreach (var old in new DirectoryInfo(_cacheDir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(CacheLimit))
                old.Delete();
        }
        catch
        {
            // кэш не обязателен
        }
    }
}
