using System.Globalization;
using System.Text.RegularExpressions;

namespace Ostrov.Modules.Player.Lyrics;

/// <summary>Слово со временем начала и конца (конец — начало следующего слова или конец строки). Текст может заканчиваться пробелом.</summary>
internal readonly record struct LyricWord(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Строка текста: время начала (для текста без таймингов — ноль), сама строка и (если база знает) слова с таймингами.</summary>
internal readonly record struct LyricLine(TimeSpan Time, string Text, IReadOnlyList<LyricWord>? Words = null);

/// <summary>Результат поиска текста: найден ли, есть ли тайминги (тогда строки подсвечиваются в такт) и строки.</summary>
internal sealed record LyricsResult(bool Found, bool Synced, IReadOnlyList<LyricLine> Lines)
{
    public static readonly LyricsResult None = new(false, false, []);

    /// <summary>Есть ли у строк пословные тайминги (слова подсвечиваются по очереди).</summary>
}

/// <summary>Что ищем: название, исполнитель, альбом и длительность трека в секундах (0 — неизвестна).</summary>
internal readonly record struct TrackKey(string Title, string Artist, string Album, int DurationSeconds)
{
    /// <summary>
    /// Из данных Apple Music: в поле «исполнитель» у неё приходит «Исполнитель — Альбом» (проверено на живых треках).
    /// Если альбом передан отдельно, берём его.
    /// </summary>
    public static TrackKey FromMedia(string title, string artistField, string albumTitle, TimeSpan duration)
    {
        var artist = artistField.Trim();
        var album = albumTitle.Trim();
        var split = artist.LastIndexOf(" — ", StringComparison.Ordinal);
        if (split > 0)
        {
            if (album.Length == 0) album = artist[(split + 3)..].Trim();
            artist = artist[..split].Trim();
        }
        return new TrackKey(title.Trim(), artist, album, (int)Math.Round(duration.TotalSeconds));
    }

    /// <summary>Ключ для кэша и для сравнения «тот же ли это трек».</summary>
    public string Id => $"{Title}|{Artist}|{Album}|{DurationSeconds}|v2".ToLowerInvariant(); // v2: кэш с пословными таймингами
}

/// <summary>
/// Разбор «Lyricsfile» из LRCLIB (YAML, у записей с hasWordSync): строки <c>lines[]</c> с <c>text</c>, <c>start_ms</c>, <c>end_ms</c>
/// и слова <c>words[]</c> с <c>text</c> и <c>start_ms</c>. Библиотеки YAML не нужны: формат плоский, разбор по отступам.
/// Строка без слов остаётся построчной. Null — не удалось разобрать ни одной строки.
/// </summary>
internal static class LyricsfileParser
{
    private sealed class RawWord { public string Text = ""; public long Start = -1; }
    private sealed class RawLine { public string Text = ""; public long Start = -1, End = -1; public List<RawWord> Words = []; }

    public static List<LyricLine>? Parse(string yaml)
    {
        var raws = new List<RawLine>();
        var inLines = false;
        var lineIndent = -1;
        var wordIndent = -1;
        RawLine? line = null;
        RawWord? word = null;

        foreach (var row0 in yaml.Split('\n'))
        {
            var row = row0.TrimEnd('\r');
            var body = row.TrimStart();
            if (body.Length == 0 || body[0] == '#') continue;
            var indent = row.Length - body.Length;

            if (!inLines)
            {
                if (body.StartsWith("lines:", StringComparison.Ordinal)) inLines = true;
                continue;
            }
            // Другой ключ верхнего уровня после списка строк — раздел lines закончился.
            if (indent == 0 && !body.StartsWith("- ", StringComparison.Ordinal) && body != "-") break;

            if (body.StartsWith("- text:", StringComparison.Ordinal))
            {
                var text = Scalar(body[7..]);
                if (lineIndent < 0) lineIndent = indent;
                if (indent <= lineIndent)
                {
                    line = new RawLine { Text = text };
                    raws.Add(line);
                    word = null;
                    wordIndent = -1;
                }
                else if (line != null)
                {
                    if (wordIndent < 0) wordIndent = indent;
                    word = new RawWord { Text = text };
                    line.Words.Add(word);
                }
                continue;
            }

            if (TryNumber(body, "start_ms:", out var start))
            {
                if (word != null && indent > wordIndent) word.Start = start;
                else if (line != null) line.Start = start;
            }
            else if (TryNumber(body, "end_ms:", out var end))
            {
                if (line != null && (word == null || indent <= wordIndent)) line.End = end;
            }
        }

        var result = new List<LyricLine>();
        foreach (var r in raws)
        {
            if (r.Start < 0 || r.Text.Trim().Length == 0) continue;
            IReadOnlyList<LyricWord>? words = null;
            if (r.Words.Count > 0 && r.Words.All(w => w.Start >= 0))
            {
                var list = new List<LyricWord>(r.Words.Count);
                for (var i = 0; i < r.Words.Count; i++)
                {
                    var start = r.Words[i].Start;
                    var end = i + 1 < r.Words.Count ? r.Words[i + 1].Start : (r.End > start ? r.End : start + 400);
                    list.Add(new LyricWord(TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(Math.Max(end, start + 1)), r.Words[i].Text));
                }
                words = list;
            }
            result.Add(new LyricLine(TimeSpan.FromMilliseconds(r.Start), r.Text, words));
        }
        return result.Count == 0 ? null : result.OrderBy(l => l.Time).ToList();
    }

    private static bool TryNumber(string body, string key, out long value)
    {
        value = 0;
        return body.StartsWith(key, StringComparison.Ordinal) &&
               long.TryParse(body[key.Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    // Значение YAML: в одинарных кавычках ('' — апостроф), в двойных (экранирование как в JSON) или без кавычек.
    private static string Scalar(string s)
    {
        s = s.TrimStart().TrimEnd('\r');
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s[1..^1].Replace("''", "'");
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(s) ?? ""; }
            catch (System.Text.Json.JsonException) { return s[1..^1]; }
        }
        return s.TrimEnd();
    }
}

/// <summary>Разбор текста песни: LRC (строки с таймкодами в квадратных скобках) и обычный текст.</summary>
internal static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    /// <summary>Строки LRC по времени. Пустые строки (паузы между куплетами) пропускаются; теги вида [ar:…] игнорируются.</summary>
    public static List<LyricLine> ParseSynced(string lrc)
    {
        var result = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = TimeTag().Matches(line);
            if (matches.Count == 0) continue;
            var text = TimeTag().Replace(line, "").Trim();
            if (text.Length == 0) continue;
            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = 0.0;
                if (m.Groups[3].Success)
                {
                    var digits = m.Groups[3].Value;
                    fraction = int.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, digits.Length);
                }
                result.Add(new LyricLine(TimeSpan.FromSeconds(minutes * 60 + seconds + fraction), text));
            }
        }
        // Стабильная сортировка по времени (строка с несколькими таймкодами встаёт в каждое своё место).
        return result.Select((l, i) => (l, i)).OrderBy(x => x.l.Time).ThenBy(x => x.i).Select(x => x.l).ToList();
    }

    /// <summary>Обычный текст без таймингов: непустые строки по порядку.</summary>
    public static List<LyricLine> ParsePlain(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => new LyricLine(TimeSpan.Zero, l)).ToList();
}
