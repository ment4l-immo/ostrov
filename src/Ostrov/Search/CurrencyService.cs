using System.Net.Http;
using System.Text.Json;

namespace Ostrov.Search;

/// <summary>Таблица курсов: сколько единиц каждой валюты за одну единицу базовой.</summary>
internal sealed record RateTable(string Base, IReadOnlyDictionary<string, double> Rates, DateTime UpdatedUtc)
{
    /// <summary>Сколько единиц <paramref name="to"/> в одной единице <paramref name="from"/> (через базу таблицы, поэтому годится любая пара).</summary>
    public bool TryCross(string from, string to, out double rate)
    {
        rate = 0;
        if (!Rates.TryGetValue(from, out var a) || !Rates.TryGetValue(to, out var b) || a <= 0) return false;
        rate = b / a;
        return true;
    }
}

/// <summary>
/// Курсы валют для пересчёта в ответе ИИ (решение пользователя 2026-11-15: open.er-api.com, без ключа, обновляется раз в сутки).
/// В сеть уходит только код валюты, и только когда пользователь попросил пересчёт; таблица помнится 6 часов. Условие бесплатного тарифа:
/// ссылка на источник (она есть в подписи карточки).
/// </summary>
internal static class CurrencyService
{
    /// <summary>Адрес API. Меняется только в проверках.</summary>
    internal static string BaseUrl { get; set; } = "https://open.er-api.com/v6/latest/";

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, (RateTable Table, DateTime Fetched)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OstrovIsland/1.0");
        return client;
    }

    // Одна повторная попытка: первое соединение с сервисом бывает медленным (DNS, VPN), второе обычно проходит сразу.
    private static async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        try { return await Http.GetStringAsync(url, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await Task.Delay(300, ct);
            return await Http.GetStringAsync(url, ct);
        }
    }

    public static async Task<RateTable> GetAsync(string baseCode, CancellationToken ct)
    {
        baseCode = baseCode.Trim().ToUpperInvariant();
        if (Cache.TryGetValue(baseCode, out var hit) && DateTime.UtcNow - hit.Fetched < Fresh) return hit.Table;

        using var doc = JsonDocument.Parse(await FetchAsync(BaseUrl + Uri.EscapeDataString(baseCode), ct));
        var root = doc.RootElement;
        if (!root.TryGetProperty("result", out var result) || result.GetString() != "success" || !root.TryGetProperty("rates", out var rates))
            throw new InvalidOperationException("Нет такой валюты: " + baseCode);

        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in rates.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Number) map[p.Name.ToUpperInvariant()] = p.Value.GetDouble();
        var updated = root.TryGetProperty("time_last_update_unix", out var u) && u.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : DateTime.UtcNow;
        var table = new RateTable(baseCode, map, updated);
        Cache[baseCode] = (table, DateTime.UtcNow);
        return table;
    }
}
