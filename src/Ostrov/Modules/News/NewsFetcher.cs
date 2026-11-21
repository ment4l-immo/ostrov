using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Ostrov.Modules.News;

/// <summary>
/// Загрузка лент: обычные запросы RSS/Atom к публичным сайтам, только заголовки, ссылки и время. Никаких личных данных не отправляется
/// (User-Agent общий). Ленты грузятся параллельно по четыре, у каждой своё ожидание 12 секунд; сломавшаяся лента пропускается.
/// Кэш заголовков за 7 дней лежит на диске, чтобы вкладка открывалась сразу.
/// </summary>
internal static class NewsFetcher
{
    private const int MaxPerFeed = 40, MaxStored = 500, MaxBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ostrov/1.0 (desktop widget)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml");
        return client;
    }

    /// <summary>Подгрузить все ленты и слить с кэшем. Возвращает новые данные и сколько лент ответило.</summary>
    public static async Task<(List<NewsItem> Items, int Succeeded)> RefreshAsync(IReadOnlyList<NewsFeed> feeds, CancellationToken token)
    {
        var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(feeds.Select(async feed =>
        {
            await gate.WaitAsync(token);
            try { return (Items: await FetchOne(feed, token), Ok: true); }
            catch { return (Items: new List<NewsItem>(), Ok: false); }
            finally { gate.Release(); }
        }));

        var now = DateTimeOffset.Now;
        var merged = new Dictionary<string, NewsItem>();
        foreach (var cached in NewsStore.LoadCache()) merged[cached.Id] = cached;
        foreach (var item in results.SelectMany(r => r.Items)) merged[item.Id] = item;
        var all = merged.Values.Where(i => now - i.Published < KeepFor).OrderByDescending(i => i.Published).Take(MaxStored).ToList();
        if (results.Any(r => r.Ok)) NewsStore.SaveCache(all);
        return (all, results.Count(r => r.Ok));
    }

    private static async Task<List<NewsItem>> FetchOne(NewsFeed feed, CancellationToken token)
    {
        using var response = await Http.GetAsync(feed.Url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var network = await response.Content.ReadAsStreamAsync(token);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        var truncated = false;
        while ((read = await network.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes) { truncated = true; break; } // огромная лента (архив на мегабайты): нам нужны только свежие записи в начале
        }
        if (truncated)
        {
            // Документ оборван посередине: режем по последней целой записи и закрываем теги.
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            var cut = Math.Max(text.LastIndexOf("</item>", StringComparison.Ordinal), text.LastIndexOf("</entry>", StringComparison.Ordinal));
            if (cut < 0) return [];
            text = text[..(text.IndexOf('>', cut) + 1)] + (text.Contains("<rss", StringComparison.Ordinal) ? "</channel></rss>" : "</feed>");
            return Parse(text, feed, DateTimeOffset.Now);
        }
        buffer.Position = 0;
        return Parse(buffer, feed, DateTimeOffset.Now);
    }

    /// <summary>Разбор RSS 2.0 и Atom; без внешних сущностей и DTD. Берутся самые свежие записи.</summary>
    public static List<NewsItem> Parse(Stream stream, NewsFeed feed, DateTimeOffset now)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, CheckCharacters = false, XmlResolver = null };
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            // Оборванная (мы режем огромные ленты на 4 МБ) или кривая лента: берём то, что успели прочитать построчно.
            return [];
        }

        var list = new List<NewsItem>();
        foreach (var e in doc.Descendants().Where(x => x.Name.LocalName is "item" or "entry"))
        {
            var title = CleanTitle(Child(e, "title")?.Value);
            var link = LinkOf(e);
            if (title.Length == 0 || link == null) continue;
            var dateText = Child(e, "pubDate")?.Value ?? Child(e, "published")?.Value ?? Child(e, "updated")?.Value ?? Child(e, "date")?.Value;
            list.Add(new NewsItem
            {
                Id = HashId(link), Title = title, Link = link, Source = feed.Name, Sphere = feed.Sphere, Lang = feed.Lang,
                Published = ParseDate(dateText) ?? now, Image = ImageOf(e)
            });
        }
        return list.OrderByDescending(i => i.Published).Take(MaxPerFeed).ToList();
    }

    public static List<NewsItem> Parse(string xml, NewsFeed feed, DateTimeOffset now) =>
        Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), feed, now);

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    // RSS: текст <link>; Atom: <link href="…" rel="alternate">. Только http и https.
    private static string? LinkOf(XElement e)
    {
        foreach (var link in e.Elements().Where(x => x.Name.LocalName == "link"))
        {
            var rel = (string?)link.Attribute("rel");
            if (rel is not null and not "alternate") continue;
            var url = ((string?)link.Attribute("href") ?? link.Value).Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.ToString();
        }
        var guid = Child(e, "guid")?.Value.Trim();
        return guid != null && Uri.TryCreate(guid, UriKind.Absolute, out var g) && g.Scheme is "http" or "https" ? g.ToString() : null;
    }

    // Картинка записи: media:content / media:thumbnail / enclosure (только изображения), иначе первый <img> из описания или полного текста.
    private static string? ImageOf(XElement e)
    {
        foreach (var x in e.Descendants())
        {
            var name = x.Name.LocalName;
            if (name is not ("content" or "thumbnail" or "enclosure")) continue;
            var url = (string?)x.Attribute("url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            var type = (string?)x.Attribute("type") ?? (string?)x.Attribute("medium");
            if (name != "thumbnail" && type != null && !type.StartsWith("image", StringComparison.OrdinalIgnoreCase)) continue; // видео и аудио не нужны
            if (name == "enclosure" && type == null) continue;
            if (GoodImageUrl(url) is { } ok) return ok;
        }
        foreach (var x in e.Elements().Where(x => x.Name.LocalName is "encoded" or "description" or "summary" or "content"))
        {
            var m = ImgTag.Match(x.Value);
            if (!m.Success) continue;
            if (GoodImageUrl(WebUtility.HtmlDecode(m.Groups[1].Value)) is { } found) return found;
        }
        return null;
    }

    private static string? GoodImageUrl(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || url.Contains("pixel", StringComparison.OrdinalIgnoreCase)) return null; // счётчики
        return uri.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex ImgTag =
        new(@"<img\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = WebUtility.HtmlDecode(raw);
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 220 ? text[..219] + "…" : text;
    }

    private static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTimeOffset.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return d;
        // Часовые пояса вроде «EST»/«PST», которые .NET не знает: отбрасываем зону и считаем время всемирным.
        var cut = text.Trim();
        var space = cut.LastIndexOf(' ');
        if (space > 0 && DateTimeOffset.TryParse(cut[..space], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out d)) return d;
        return null;
    }

    private static string HashId(string link) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(link)))[..12];
}
