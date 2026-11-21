using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Ostrov.Modules.News;

/// <summary>
/// Текст статьи для чтения прямо на острове (без браузера): страница скачивается обычным запросом, из неё эвристикой берутся абзацы
/// основного содержимого (<c>article</c>, иначе <c>main</c>, иначе вся страница): <c>p</c>, <c>li</c>, цитаты и подзаголовки <c>h2/h3</c>; скрипты, меню,
/// подвалы, формы и рекламные блоки отбрасываются. Картинки в тексте и вёрстка не показываются, берётся только обложка (<see cref="CoverUrl"/>). Без библиотек разбора HTML, поэтому на сайтах,
/// где текст подгружается скриптами или закрыт платной стеной, текста может не получиться: тогда возвращается null.
/// Подзаголовок помечается строкой «## …».
/// </summary>
internal static partial class ArticleReader
{
    private const int MaxBytes = 3 * 1024 * 1024, MaxChars = 14000, MinChars = 400;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // windows-1251 и другие старые кодировки
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Ostrov/1.0)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru,en;q=0.8");
        return client;
    }

    /// <summary>Что вышло из страницы: текст статьи (null — не вышло) и адрес обложки из тегов предпросмотра (og:image, twitter:image; null — нет).</summary>
    public sealed record Page(string? Text, string? ImageUrl);

    /// <summary>Скачать страницу и достать текст статьи и обложку; текст null — не вышло (сайт не пустил, текст грузится скриптами и т. п.).</summary>
    public static async Task<Page> FetchAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return new Page(null, null);
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return new Page(null, null);
            await using var network = await response.Content.ReadAsStreamAsync(token);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await network.ReadAsync(chunk, token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes) break;
            }
            var html = Decode(buffer.ToArray(), response.Content.Headers.ContentType?.CharSet);
            return new Page(Extract(html), CoverUrl(html, response.RequestMessage?.RequestUri ?? uri));
        }
        catch (OperationCanceledException) { throw; }
        catch { return new Page(null, null); }
    }

    /// <summary>Адрес обложки из <c>&lt;meta property="og:image"&gt;</c> (или twitter:image), относительный разрешается от адреса страницы; только http и https.</summary>
    public static string? CoverUrl(string html, Uri pageUri)
    {
        var head = html.Length > 200_000 ? html[..200_000] : html;
        string? fallback = null;
        foreach (Match tag in MetaTag().Matches(head))
        {
            string? key = null, content = null;
            foreach (Match a in Attr().Matches(tag.Value))
            {
                var name = a.Groups[1].Value.ToLowerInvariant();
                var value = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value;
                if (name is "property" or "name") key = value.Trim().ToLowerInvariant();
                else if (name == "content") content = value;
            }
            if (key == null || string.IsNullOrWhiteSpace(content)) continue;
            var og = key is "og:image" or "og:image:url" or "og:image:secure_url";
            if (!og && key is not ("twitter:image" or "twitter:image:src")) continue;
            if (!Uri.TryCreate(pageUri, WebUtility.HtmlDecode(content).Trim(), out var image) || image.Scheme is not ("http" or "https")) continue;
            if (og) return image.ToString();
            fallback ??= image.ToString();
        }
        return fallback;
    }

    private const int MaxImageBytes = 6 * 1024 * 1024, MinImageWidth = 300, MaxImageWidth = 1000;

    /// <summary>Скачать картинку и декодировать (не шире 1000 px, чтобы не держать лишнее в памяти); null — не вышло, не картинка или слишком мелкая (значок, логотип).</summary>
    public static async Task<System.Windows.Media.Imaging.BitmapSource?> FetchImageAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Просим привычные форматы: WebP и AVIF WPF сам не читает, а сайты отдают их тем, кто о них заявил.
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("image/jpeg,image/png,image/*;q=0.5");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentType?.MediaType is { } type && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            await using var network = await response.Content.ReadAsStreamAsync(token);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await network.ReadAsync(chunk, token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxImageBytes) return null;
            }
            var bytes = buffer.ToArray();
            return await Task.Run(() => DecodeImage(bytes), token);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static System.Windows.Media.Imaging.BitmapSource? DecodeImage(byte[] bytes)
    {
        using var probe = new MemoryStream(bytes);
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(probe, System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
            System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
        if (frame.PixelWidth < MinImageWidth) return null;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        if (frame.PixelWidth > MaxImageWidth) image.DecodePixelWidth = MaxImageWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string Decode(byte[] bytes, string? headerCharset)
    {
        var name = headerCharset?.Trim('"', '\'', ' ');
        if (string.IsNullOrEmpty(name))
        {
            var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 3000));
            var m = MetaCharset().Match(head);
            if (m.Success) name = m.Groups[1].Value;
        }
        try { if (!string.IsNullOrEmpty(name)) return Encoding.GetEncoding(name).GetString(bytes); }
        catch { /* незнакомая кодировка: пробуем UTF-8 */ }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Достать абзацы основного содержимого из HTML. null — слишком мало текста.</summary>
    public static string? Extract(string html)
    {
        html = Junk().Replace(html, "");
        html = Comments().Replace(html, "");
        var scope = Largest(html, "article") ?? Largest(html, "main") ?? html;

        var lines = new List<string>();
        foreach (Match m in Blocks().Matches(scope))
        {
            var tag = m.Groups[1].Value.ToLowerInvariant();
            var text = Clean(m.Groups[2].Value);
            if (text.Length == 0) continue;
            if (tag is "h2" or "h3")
            {
                if (text.Length is >= 3 and <= 120) lines.Add("## " + text);
                continue;
            }
            var min = tag == "li" ? 50 : 45;
            if (text.Length < min || IsBoilerplate(text)) continue;
            if (lines.Count > 0 && lines[^1] == text) continue;
            lines.Add(text);
        }
        // Подзаголовок без текста под ним не нужен.
        for (var i = lines.Count - 1; i >= 0; i--)
            if (lines[i].StartsWith("## ") && (i == lines.Count - 1 || lines[i + 1].StartsWith("## "))) lines.RemoveAt(i);

        var result = string.Join("\n\n", lines);
        if (result.Length < MinChars) return null;
        return result.Length > MaxChars ? result[..MaxChars].TrimEnd() + "…" : result;
    }

    // Самый длинный элемент данного тега (без учёта вложенности): для <article> и <main> этого достаточно.
    private static string? Largest(string html, string tag)
    {
        string? best = null;
        foreach (Match m in Regex.Matches(html, $@"<{tag}\b[^>]*>(.*?)</{tag}>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            if (best == null || m.Groups[1].Length > best.Length) best = m.Groups[1].Value;
        return best is { Length: > 600 } ? best : null;
    }

    private static string Clean(string inner)
    {
        // Переносы строк — пробел, остальные теги (ссылки, выделение) убираем без пробела: иначе «London , »
        var text = Tags().Replace(Br().Replace(inner, " "), "");
        text = WebUtility.HtmlDecode(text);
        return Spaces().Replace(text, " ").Trim();
    }

    private static readonly string[] BoilerplateWords =
    [
        "subscribe", "cookie", "newsletter", "sign up", "all rights reserved", "advertisement", "follow us", "privacy policy",
        "подписывайтесь", "подпишись", "реклама", "читайте также", "согласие на обработку", "все права защищены", "политика конфиденциальности"
    ];

    private static bool IsBoilerplate(string text)
    {
        var lower = text.ToLowerInvariant();
        return BoilerplateWords.Any(w => lower.Contains(w, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"<(script|style|noscript|svg|iframe|form|nav|header|footer|aside|template|button)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Junk();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<(h2|h3|p|li|blockquote)\b[^>]*>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Br();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"([\w:\-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')")]
    private static partial Regex Attr();

    [GeneratedRegex(@"charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();
}
