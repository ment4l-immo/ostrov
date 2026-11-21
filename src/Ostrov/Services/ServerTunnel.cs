using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Ostrov.Services;

/// <summary>
/// Тоннель до собственного сервера пользователя, чтобы запросы к <c>api.openai.com</c> работали без VPN (решение пользователя 2026-11-16:
/// «SSH-тоннель с запертым ключом»). Остров запускает встроенный в Windows <c>ssh.exe</c> с пробросом одного порта
/// (<c>-L 127.0.0.1:порт:api.openai.com:443</c>); HTTP-клиент OpenAI подключается к этому порту, но сертификат по-прежнему проверяется
/// как для <c>api.openai.com</c>, поэтому шифрование идёт до самого OpenAI. На сервере нужен отдельный пользователь с ключом
/// <c>restrict,port-forwarding,permitopen="api.openai.com:443"</c>: больше этим ключом ничего сделать нельзя, новых открытых портов нет.
/// Приватный ключ лежит только на этом ПК (<c>%AppData%\Ostrov\tunnel_key</c>), в настройках хранятся адрес, пользователь и порт (не секреты).
/// Тоннель поднимается при первом запросе к OpenAI; не поднялся (нет сети, сервер не отвечает) — запрос идёт напрямую, как раньше
/// (с включённым VPN это работает). Свои ограничения: ключ-файл, <c>known_hosts</c> и <c>-F NUL</c>: чужие настройки <c>~/.ssh</c> не используются.
/// </summary>
internal static class ServerTunnel
{
    public const string Target = "api.openai.com";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _proc;
    private static int _port;
    private static string _lastError = "";
    private static DateTime _retryAfter = DateTime.MinValue;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov");
    public static string KeyPath => Path.Combine(Dir, "tunnel_key");
    public static string PublicKeyPath => KeyPath + ".pub";
    private static string KnownHosts => Path.Combine(Dir, "tunnel_known_hosts");

    /// <summary>Тоннель включён в настройках и заполнен.</summary>
    public static bool IsConfigured(AppSettings s) =>
        s.TunnelEnabled && !string.IsNullOrWhiteSpace(s.TunnelHost) && !string.IsNullOrWhiteSpace(s.TunnelUser) && File.Exists(KeyPath);

    private static volatile bool _ready; // локальный порт уже принимает подключения (а не только ssh запущен)
    public static bool IsUp => _ready && _proc is { HasExited: false } && _port != 0;

    /// <summary>Последняя ошибка ssh (одна строка) или пусто.</summary>
    public static string LastError => _lastError;

    /// <summary>Поднять тоннель, если он ещё не поднят, и дождаться готовности (до 9 с). false — не получилось, причина в <see cref="LastError"/>.</summary>
    public static async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        var s = AppSettings.Load();
        if (!IsConfigured(s)) return false;
        if (IsUp) return true;
        if (DateTime.UtcNow < _retryAfter) return false; // недавно не вышло: не долбим сервер на каждый запрос

        await Gate.WaitAsync(ct);
        try
        {
            if (IsUp) return true;
            Stop();
            _lastError = "";
            _port = FreePort();
            var psi = new ProcessStartInfo("ssh.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            // -F NUL: не читать чужой ~/.ssh/config; -T -N: ни терминала, ни команды, только проброс; BatchMode: не спрашивать пароль.
            foreach (var a in new[]
            {
                "-F", "NUL", "-N", "-T", "-i", KeyPath, "-p", Math.Clamp(s.TunnelPort <= 0 ? 22 : s.TunnelPort, 1, 65535).ToString(),
                "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes", "-o", "ConnectTimeout=8",
                "-o", "ServerAliveInterval=20", "-o", "ServerAliveCountMax=3", "-o", "StrictHostKeyChecking=accept-new",
                "-o", "UserKnownHostsFile=" + KnownHosts,
                "-L", $"127.0.0.1:{_port}:{Target}:443", "--", $"{s.TunnelUser!.Trim()}@{s.TunnelHost!.Trim()}"
            }) psi.ArgumentList.Add(a);

            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (Exception ex)
            {
                _lastError = "Не запустился ssh.exe: " + ex.Message;
                _retryAfter = DateTime.UtcNow.AddSeconds(30);
                return false;
            }
            _proc = proc;
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.TrimStart().StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)) _lastError = e.Data.Trim(); };
            proc.OutputDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            // Ждём, пока локальный порт откроется (это значит, что вход по ключу прошёл и проброс поднят).
            var deadline = DateTime.UtcNow.AddSeconds(9);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (proc.HasExited) break;
                if (await CanConnectAsync(_port)) { _ready = true; return true; }
                await Task.Delay(150, CancellationToken.None);
            }
            if (_lastError.Length == 0) _lastError = proc.HasExited ? "ssh завершился (код " + proc.ExitCode + ")" : "Сервер не ответил за 9 секунд";
            Stop();
            _retryAfter = DateTime.UtcNow.AddSeconds(30);
            return false;
        }
        finally { Gate.Release(); }
    }

    public static void Stop()
    {
        var p = _proc;
        _proc = null;
        _port = 0;
        _ready = false;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
        try { p.Dispose(); } catch { }
    }

    /// <summary>Сбросить паузу после неудачи (например, пользователь поменял настройки или нажал «Проверить»).</summary>
    public static void Reset()
    {
        _retryAfter = DateTime.MinValue;
        Stop();
    }

    /// <summary>Проверка из настроек: поднять тоннель и сделать настоящий запрос к OpenAI через него (любой ответ сервера, например 401 или 404, значит «дошло»).</summary>
    public static async Task<(bool Ok, string Message)> TestAsync()
    {
        Reset();
        var s = AppSettings.Load();
        if (!File.Exists(KeyPath)) return (false, "Нет ключа тоннеля");
        if (string.IsNullOrWhiteSpace(s.TunnelHost) || string.IsNullOrWhiteSpace(s.TunnelUser)) return (false, "Заполни адрес и пользователя");
        if (!s.TunnelEnabled) return (false, "Тоннель выключен");
        if (!await EnsureAsync()) return (false, _lastError.Length > 0 ? Shorten(_lastError) : "Тоннель не поднялся");
        try
        {
            using var handler = new SocketsHttpHandler { ConnectCallback = ConnectAsync };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var response = await client.GetAsync("https://" + Target + "/v1/models");
            return (true, "Работает: OpenAI ответил (" + (int)response.StatusCode + ")");
        }
        catch (Exception ex)
        {
            return (false, "Тоннель поднят, но OpenAI не ответил: " + Shorten(ex.GetBaseException().Message));
        }
    }

    /// <summary>
    /// Подключение для <see cref="SocketsHttpHandler.ConnectCallback"/>: к <c>api.openai.com</c> идём через тоннель (если он включён и поднялся),
    /// ко всему остальному и при неудаче тоннеля — напрямую, как обычно. TLS и проверка сертификата делаются поверх этого потока как всегда.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        if (endpoint.Host.Equals(Target, StringComparison.OrdinalIgnoreCase) && endpoint.Port == 443 && IsConfigured(AppSettings.Load()))
        {
            try
            {
                if (await EnsureAsync(ct)) return await ConnectToAsync(new IPEndPoint(IPAddress.Loopback, _port), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* тоннель оборвался на лету: пробуем напрямую */ }
        }
        return await ConnectToAsync(endpoint, ct);
    }

    private static async ValueTask<Stream> ConnectToAsync(EndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(400);
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    private static string Shorten(string text) => text.Length > 110 ? text[..110] + "…" : text;
}
