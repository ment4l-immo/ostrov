using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostrov.Services;

namespace Ostrov.Modules.Phone;

/// <summary>
/// Значки приложений телефона. Windows отдаёт из уведомления только текст, значка приложения там нет, поэтому значок ищется по названию
/// в iTunes Search API Apple (то же бесплатное API без ключей, что и для года и жанра трека; наружу уходит только название приложения).
/// Скачанное лежит на диске (<c>%AppData%\Ostrov\phone-icons</c>), «не нашли» помнится неделю; при ошибке сети следующая попытка через 5 минут.
/// Нет значка: буква в сером кружке (<see cref="AppLogo"/>).
/// </summary>
internal static class PhoneAppIcons
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> Tasks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> Failed = new(StringComparer.OrdinalIgnoreCase);

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "phone-icons");

    static PhoneAppIcons() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Ostrov/1.0 (desktop widget)");

    /// <summary>Значок приложения или null, если его нет (тогда рисуется буква). Повторные вызовы дешёвые.</summary>
    public static Task<BitmapSource?> GetAsync(string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return Task.FromResult<BitmapSource?>(null);
        if (Failed.TryGetValue(app, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(5)) return Task.FromResult<BitmapSource?>(null);
        Failed.TryRemove(app, out _);
        return Tasks.GetOrAdd(app, a => LoadAsync(a));
    }

    private static async Task<BitmapSource?> LoadAsync(string app)
    {
        try
        {
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(app.ToLowerInvariant())))[..16];
            var img = Path.Combine(Dir, key + ".img");
            var none = Path.Combine(Dir, key + ".none");
            if (File.Exists(img)) return Decode(await File.ReadAllBytesAsync(img));
            if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(7)) return null;

            var url = await FindArtworkAsync(app, "ru") ?? await FindArtworkAsync(app, "us");
            Directory.CreateDirectory(Dir);
            if (url == null)
            {
                await File.WriteAllTextAsync(none, "");
                return null;
            }
            var bytes = await Http.GetByteArrayAsync(url);
            var icon = Decode(bytes);
            if (icon != null) await File.WriteAllBytesAsync(img, bytes);
            return icon;
        }
        catch
        {
            // Нет сети или Apple не ответила: забываем и пробуем позже.
            Tasks.TryRemove(app, out _);
            Failed[app] = DateTime.UtcNow;
            return null;
        }
    }

    private static async Task<string?> FindArtworkAsync(string app, string country)
    {
        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(app)}&entity=software&country={country}&limit=6";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        string? best = null;
        var bestScore = 0;
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var name = r.TryGetProperty("trackName", out var n) ? n.GetString() : null;
            var art = r.TryGetProperty("artworkUrl100", out var a) ? a.GetString() : null;
            if (name == null || art == null) continue;
            var score = Score(app, name);
            if (score > bestScore) { bestScore = score; best = art; }
        }
        return best?.Replace("100x100bb", "200x200bb");
    }

    // 3 — название совпало целиком; 2 — название приложения начинается с искомого и дальше не буква («Telegram Messenger», «OZON: товары»).
    private static int Score(string app, string track)
    {
        if (string.Equals(app.Trim(), track.Trim(), StringComparison.OrdinalIgnoreCase)) return 3;
        if (track.StartsWith(app.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var rest = track[app.Trim().Length..];
            if (rest.Length == 0 || !char.IsLetterOrDigit(rest[0])) return 2;
        }
        return 0;
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}

/// <summary>Значок приложения телефона: скруглённый квадрат с картинкой, пока её нет (или совсем нет) — буква в сером кружке.</summary>
internal sealed class AppLogo : Border
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Base = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    private readonly TextBlock _letter = new() { Foreground = Hues.Ink, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private string? _app;

    public AppLogo(double size)
    {
        Width = Height = size;
        CornerRadius = new CornerRadius(size * 0.24);
        Background = Base;
        _letter.FontSize = size * 0.5;
        Child = _letter;
    }

    /// <summary>Показать значок этого приложения (картинка подгрузится сама, когда будет готова).</summary>
    public async void SetApp(string app)
    {
        _app = app;
        _letter.Text = app.Length > 0 ? char.ToUpperInvariant(app[0]).ToString() : "?";
        _letter.Visibility = Visibility.Visible;
        Background = Base;
        var icon = await PhoneAppIcons.GetAsync(app).ConfigureAwait(false);
        if (icon == null) return;
        // Возвращаемся на поток интерфейса явно: продолжение после загрузки может оказаться на другом потоке.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_app != app) return;
            Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill };
            _letter.Visibility = Visibility.Collapsed;
        });
    }
}
