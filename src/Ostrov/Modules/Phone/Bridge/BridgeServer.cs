using System.Buffers.Text;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Ostrov.Modules.Buffer;

namespace Ostrov.Modules.Phone.Bridge;

/// <summary>Одна строка в списке «С компьютера» на странице телефона: файл или текст из буфера.</summary>
internal sealed record BridgeItem(string Id, string Kind, string Name, long Size);

/// <summary>
/// Мост «телефон ⇄ ПК» (решения пользователя 2026-11-16): маленький HTTP-сервер в домашней сети, доступный только пока открыто окно моста.
/// Телефон открывает адрес из QR-кода в Safari: «На компьютер» (файлы, фото, текст) и «С компьютера» (файлы, которые бросили в окно моста,
/// файлы с полки и последние скопированные тексты). Без библиотек: свой разбор HTTP.
/// Безопасность: слушает только адрес домашней сети (не VPN, не 0.0.0.0), в адресе случайный пароль (22 знака), чужие адреса и неверный пароль
/// отбрасываются (после 30 неудач сервер молчит), принимаются только PUT файла и POST текста, имена файлов очищаются, файлы идут в одну папку
/// (<c>Загрузки\Остров</c>), размер файла не больше 4 ГБ, текста не больше 1 МБ. Трафик не шифруется (обычный HTTP внутри локальной сети).
/// </summary>
internal sealed class BridgeServer : IDisposable
{
    public const long MaxUpload = 4L * 1024 * 1024 * 1024;
    private const int MaxText = 1_000_000;

    private readonly List<string> _outbox = [];
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failures = new(); // неверные пароли по адресам: чужой перебор не глушит телефон владельца

    /// <summary>Случайный пароль в адресе.</summary>
    public string Token { get; } = NewToken();

    /// <summary>Адрес для QR-кода и ввода вручную; null, пока сервер не запущен.</summary>
    public string? Url { get; private set; }

    /// <summary>
    /// Предупреждение, если Windows отправляет трафик домашней сети не через выбранный адаптер, а, например, через VPN-тоннель
    /// (так бывает с Amnezia): тогда ответы телефону пропадают, и страница на телефоне не открывается.
    /// </summary>
    public string? Warning { get; private set; }

    /// <summary>Причина, почему сервер не запустился.</summary>
    public string? Error { get; private set; }

    /// <summary>Когда телефон последний раз обращался с верным паролем.</summary>
    public DateTime LastSeen { get; private set; } = DateTime.MinValue;

    /// <summary>Сколько соединений дошло до ПК (любых, даже с неверным адресом) и когда запущен сервер: по ним окно моста понимает, дошёл ли телефон вообще.</summary>
    public int Connections => Volatile.Read(ref _connections);
    public DateTime StartedAt { get; private set; } = DateTime.UtcNow;
    private int _connections;

    public bool Running => _listener != null;

    /// <summary>Показывать ли телефону последние тексты из истории буфера. По умолчанию выключено (в буфере бывают пароли и коды), включается кнопкой в окне моста и живёт, пока открыт мост.</summary>
    public volatile bool ShareClipboard;

    /// <summary>Только для проверок: другой адрес для прослушивания (loopback) и другая папка для файлов.</summary>
    internal static IPAddress? AddressOverride { get; set; }
    internal static string? ReceiveDirOverride { get; set; }

    /// <summary>Папка для файлов с телефона.</summary>
    public static string ReceiveDir => ReceiveDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Остров");

    /// <summary>С телефона пришёл файл (путь, размер). Вызывается не на потоке интерфейса.</summary>
    public event Action<string, long>? FileReceived;

    /// <summary>С телефона пришёл текст. Вызывается не на потоке интерфейса.</summary>
    public event Action<string>? TextReceived;

    /// <summary>Телефон обратился к серверу (для строки состояния). Не на потоке интерфейса.</summary>
    public event Action? Seen;

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    // ───── Запуск и остановка ─────

    public bool Start()
    {
        if (_listener != null) return true;
        var ip = AddressOverride ?? FindLanAddress();
        if (ip == null)
        {
            Error = "Не нашёл адрес домашней сети (Ethernet или Wi-Fi)";
            return false;
        }
        try
        {
            Directory.CreateDirectory(ReceiveDir);
            _listener = new TcpListener(ip, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://{ip}:{port}/{Token}/";
            Warning = AddressOverride == null ? CheckRouting() : null;
            _cts = new CancellationTokenSource();
            StartedAt = DateTime.UtcNow;
            _ = AcceptLoopAsync(_listener, _cts.Token);
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = "Не удалось запустить мост: " + ex.Message;
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        Url = null;
    }

    public void Dispose() => Stop();

    // ───── Файлы «на телефон» ─────

    /// <summary>Добавить файлы в список «С компьютера» (папки пропускаются). Возвращает, сколько добавлено.</summary>
    public int AddToOutbox(IEnumerable<string> paths)
    {
        var added = 0;
        lock (_lock)
            foreach (var p in paths)
            {
                if (!File.Exists(p) || _outbox.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                _outbox.Add(p);
                added++;
            }
        return added;
    }

    public void RemoveFromOutbox(string path)
    {
        lock (_lock) _outbox.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> Outbox()
    {
        lock (_lock) return _outbox.Where(File.Exists).ToList();
    }

    // Что показывать на телефоне: файлы из окна моста, файлы с полки и последние тексты из буфера. Читается на потоке интерфейса.
    private (List<(string Id, string Path, string Name, long Size)> Files, List<(string Id, string Text)> Texts) Snapshot()
    {
        return OnUi(() =>
        {
            var files = new List<(string, string, string, long)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Outbox().Concat(ClipboardService.Instance.Shelf.GetAll()))
            {
                try
                {
                    if (!File.Exists(path) || !seen.Add(path)) continue;
                    files.Add((FileId(path), path, Path.GetFileName(path), new FileInfo(path).Length));
                }
                catch { /* файл исчез или недоступен */ }
            }
            var texts = !ShareClipboard ? new List<(string, string)>() : ClipboardService.Instance.History.Entries
                .Where(e => e.Kind == ClipKind.Text && !string.IsNullOrWhiteSpace(e.Text))
                .Take(8).Select(e => (e.Id, e.Text!)).ToList();
            return (files, texts);
        });
    }

    private static string FileId(string path) => Base64Url.EncodeToString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..12];

    private static T OnUi<T>(Func<T> f) => Application.Current.Dispatcher.Invoke(f);

    // ───── Сеть ─────

    private static int _lanIndex;
    private static IPAddress? _lanGateway;

    private static IPAddress? FindLanAddress()
    {
        _lanIndex = 0;
        _lanGateway = null;
        string[] skip = ["amnezia", "wintun", "tap", "vpn", "virtual", "vmware", "vbox", "hyper-v", "vethernet", "bluetooth", "wsl", "docker", "tailscale", "zerotier", "loopback", "pseudo"];
        IPAddress? best = null;
        var bestRank = int.MaxValue;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
            var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (skip.Any(label.Contains)) continue;
            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(u.Address)) continue;
                // Лучше тот, у кого есть шлюз; среди равных проводной раньше беспроводного.
                var rank = (hasGateway ? 0 : 10) + (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1);
                if (rank < bestRank)
                {
                    best = u.Address;
                    bestRank = rank;
                    _lanIndex = props.GetIPv4Properties()?.Index ?? 0;
                    _lanGateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))?.Address;
                }
            }
        }
        return best;
    }

    // Куда Windows пошлёт пакет к шлюзу домашней сети: если не через адаптер, на котором мы слушаем, ответы телефону пойдут мимо.
    private static string? CheckRouting()
    {
        try
        {
            if (_lanGateway == null || _lanIndex == 0) return null;
            var sockaddr = new byte[16];
            sockaddr[0] = 2; // AF_INET
            Array.Copy(_lanGateway.GetAddressBytes(), 0, sockaddr, 4, 4);
            if (GetBestInterfaceEx(sockaddr, out var best) != 0 || best == _lanIndex) return null;
            var other = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            {
                try { return n.GetIPProperties().GetIPv4Properties()?.Index == best; } catch { return false; }
            });
            return "Windows отправляет трафик домашней сети через «" + (other?.Name ?? "другой адаптер") + "» (скорее всего VPN): телефон не достучится";
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern int GetBestInterfaceEx(byte[] destAddr, out int bestIfIndex);

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static bool IsLocalClient(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return IsPrivate(a) || (b[0] == 169 && b[1] == 254); // домашняя сеть или link-local
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    // ───── Разбор запроса ─────

    private sealed class Request
    {
        public string Method = "", Path = "", Query = "";
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Leftover = [];
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !(IsLocalClient(remote.Address) || (AddressOverride != null && IPAddress.IsLoopback(remote.Address)))) return;
                var who = remote.Address.ToString();
                if (_failures.TryGetValue(who, out var bad) && bad > 30) return; // с этого адреса перебирают пароль: молчим
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var req = await ReadRequestAsync(stream, cts.Token);
                if (req == null) { await Respond(stream, 400, "text/plain", "bad request"); return; }

                // Пароль — первый сегмент пути.
                var prefix = "/" + Token;
                if (req.Path != prefix && !req.Path.StartsWith(prefix + "/", StringComparison.Ordinal))
                {
                    _failures.AddOrUpdate(who, 1, (_, n) => n + 1);
                    await Respond(stream, 404, "text/plain", "not found");
                    return;
                }
                LastSeen = DateTime.UtcNow;
                Seen?.Invoke();
                var route = req.Path.Length > prefix.Length ? req.Path[(prefix.Length + 1)..] : "";

                await RouteAsync(stream, req, route, ct);
            }
            catch
            {
                // Обрыв связи, таймаут, закрытый сервер: тихо закрываем соединение.
            }
        }
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        int headerEnd;
        while (true)
        {
            if (total == buffer.Length) return null;
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) return null;
            total += n;
            headerEnd = IndexOf(buffer, total, "\r\n\r\n"u8);
            if (headerEnd >= 0) break;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var parts = head[0].Split(' ');
        if (parts.Length < 3) return null;
        var req = new Request { Method = parts[0] };
        var target = parts[1];
        var q = target.IndexOf('?');
        req.Path = q >= 0 ? target[..q] : target;
        req.Query = q >= 0 ? target[(q + 1)..] : "";
        for (var i = 1; i < head.Length; i++)
        {
            var c = head[i].IndexOf(':');
            if (c > 0) req.Headers[head[i][..c].Trim()] = head[i][(c + 1)..].Trim();
        }
        var bodyStart = headerEnd + 4;
        req.Leftover = buffer.AsSpan(bodyStart, total - bodyStart).ToArray();
        return req;
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) => data.AsSpan(0, length).IndexOf(pattern);

    private async Task RouteAsync(NetworkStream stream, Request req, string route, CancellationToken ct)
    {
        switch (req.Method, route)
        {
            case ("GET", ""):
                await Respond(stream, 200, "text/html; charset=utf-8", BridgePage.Html);
                return;

            case ("GET", "list"):
            {
                var (files, texts) = Snapshot();
                var items = files.Select(f => new BridgeItem(f.Id, "file", f.Name, f.Size))
                    .Concat(texts.Select(t => new BridgeItem(t.Id, "text", Preview(t.Text), t.Text.Length)));
                await Respond(stream, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(new { items }, JsonOptions));
                return;
            }

            case ("POST", "text"):
            {
                var body = await ReadBodyAsync(stream, req, MaxText, ct);
                if (body == null) { await Respond(stream, 413, "text/plain", "too large or no length"); return; }
                var text = Encoding.UTF8.GetString(body).Trim();
                if (text.Length == 0) { await Respond(stream, 400, "text/plain", "empty"); return; }
                TextReceived?.Invoke(text);
                await Respond(stream, 200, "application/json", "{\"ok\":true}");
                return;
            }

            case ("PUT", "up"):
                await ReceiveFileAsync(stream, req, ct);
                return;
        }

        if (req.Method == "GET" && route.StartsWith("file/", StringComparison.Ordinal))
        {
            var id = route[5..];
            var (files, _) = Snapshot();
            var found = files.FirstOrDefault(f => f.Id == id);
            if (found.Path == null) { await Respond(stream, 404, "text/plain", "not found"); return; }
            await SendFileAsync(stream, found.Path, found.Name, ct);
            return;
        }
        if (req.Method == "GET" && route.StartsWith("text/", StringComparison.Ordinal))
        {
            var id = route[5..];
            var (_, texts) = Snapshot();
            var found = texts.FirstOrDefault(t => t.Id == id);
            if (found.Text == null) { await Respond(stream, 404, "text/plain", "not found"); return; }
            await Respond(stream, 200, "text/plain; charset=utf-8", found.Text);
            return;
        }
        await Respond(stream, 404, "text/plain", "not found");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Preview(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    // ───── Приём ─────

    private static async Task<byte[]?> ReadBodyAsync(NetworkStream stream, Request req, int max, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("Content-Length", out var raw) || !long.TryParse(raw, out var length) || length < 0 || length > max) return null;
        var body = new byte[length];
        var have = Math.Min(req.Leftover.Length, (int)length);
        Array.Copy(req.Leftover, body, have);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (have < length)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var n = await stream.ReadAsync(body.AsMemory(have, (int)length - have), cts.Token);
            if (n == 0) return null;
            have += n;
        }
        return body;
    }

    private async Task ReceiveFileAsync(NetworkStream stream, Request req, CancellationToken ct)
    {
        if (!req.Headers.TryGetValue("Content-Length", out var raw) || !long.TryParse(raw, out var length) || length < 0)
        {
            await Respond(stream, 411, "text/plain", "length required");
            return;
        }
        if (length > MaxUpload) { await Respond(stream, 413, "text/plain", "too large"); return; }
        var name = SafeName(QueryValue(req.Query, "name"));
        Directory.CreateDirectory(ReceiveDir);
        var target = UniquePath(ReceiveDir, name);
        var temp = Path.Combine(ReceiveDir, ".ostrov-" + Guid.NewGuid().ToString("N")[..8] + ".part");
        var ok = false;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var remaining = length;
                var first = (int)Math.Min(req.Leftover.Length, remaining);
                if (first > 0) { await file.WriteAsync(req.Leftover.AsMemory(0, first), ct); remaining -= first; }
                var buffer = new byte[81920];
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (remaining > 0)
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(30)); // телефон замолчал на полминуты: обрываем
                    var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cts.Token);
                    if (n == 0) return; // оборвалось: недокачанное удалится в finally
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    remaining -= n;
                }
            }
            // Несколько файлов с одинаковым именем приходят одновременно (Safari часто зовёт все фото image.jpg): имя могли занять, пока шла передача.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, target); break; }
                catch (IOException) when (attempt < 20 && File.Exists(target)) { target = UniquePath(ReceiveDir, name); }
            }
            ok = true;
        }
        finally
        {
            if (!ok) { try { File.Delete(temp); } catch { } }
        }
        FileReceived?.Invoke(target, length);
        await Respond(stream, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(new { ok = true, name = Path.GetFileName(target) }, JsonOptions));
    }

    private static string QueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key) return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return "";
    }

    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>Безопасное имя файла: без пути и запрещённых знаков, без зарезервированных имён Windows, не длиннее 120 знаков.</summary>
    internal static string SafeName(string? raw)
    {
        var name = Path.GetFileName((raw ?? "").Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0) name = "файл";
        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            name = (ext.Length is > 0 and < 12 ? name[..(120 - ext.Length)] + ext : name[..120]);
        }
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(name).ToUpperInvariant())) name = "_" + name;
        return name;
    }

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 10_000; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid().ToString("N")[..6]}{ext}");
    }

    // ───── Отдача ─────

    private static async Task SendFileAsync(NetworkStream stream, string path, string name, CancellationToken ct)
    {
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true); }
        catch { await Respond(stream, 404, "text/plain", "not found"); return; }
        await using (file)
        {
            var ascii = new string(name.Select(c => c is < ' ' or > '~' or '"' or '\\' or '%' ? '_' : c).ToArray());
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {file.Length}\r\n" +
                       $"Content-Disposition: attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}\r\n" +
                       "X-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await file.CopyToAsync(stream, 81920, ct);
        }
    }

    private static async Task Respond(NetworkStream stream, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 411 => "Length Required", 413 => "Payload Too Large", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" +
                   "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; form-action 'none'; base-uri 'none'\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
    }
}
