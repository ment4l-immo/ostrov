using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Ostrov.Claude;

/// <summary>
/// Мост «хук Claude Code ↔ остров»: именованный канал Windows (без сети и портов, только для текущего пользователя).
/// Хук присылает одной строкой свой ввод (JSON) и ждёт одну строку ответа: готовый JSON для вывода хука
/// или пустую строку («не решать, пусть решит окно VS Code»). Если хук отключился раньше — запрос закрыт.
/// </summary>
internal sealed class ClaudeBridge : IDisposable
{
    /// <summary>Имя канала. Переменная окружения OSTROV_PIPE нужна только проверкам: чтобы они не задевали запущенный остров.</summary>
    public static string PipeName { get; } =
        Environment.GetEnvironmentVariable("OSTROV_PIPE") is { Length: > 0 } name ? name : "ostrov-claude-bridge";

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Пришёл новый запрос. Вызывается из рабочего потока.</summary>
    public event Action<ClaudeRequest>? RequestReceived;

    /// <summary>Пришло событие хука без ответа (начало или конец работы Claude). Вызывается из рабочего потока.</summary>
    public event Action<ClaudeEvent>? EventReceived;

    public void Start() => _ = Task.Run(AcceptLoop);

    public void Dispose() => _cts.Cancel();

    private async Task AcceptLoop()
    {
        var first = true;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Первый экземпляр канала должен быть единственным: если запущен второй остров, он не перехватывает хуки.
                var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
                if (first) options |= PipeOptions.FirstPipeInstance;
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
                first = false;

                await server.WaitForConnectionAsync(_cts.Token);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleAsync(connected));
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                // Канал уже занят другим островом или временный сбой: пробуем позже, не крутимся вхолостую.
                server?.Dispose();
                try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
            ClaudeRequest request;
            try
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (string.IsNullOrWhiteSpace(line)) return;
                using var doc = JsonDocument.Parse(line);
                // События начала и конца работы: ответа не ждут, канал закрывается сразу (хук выходит без вывода).
                if (ClaudeEvent.TryParse(doc.RootElement) is { } claudeEvent)
                {
                    EventReceived?.Invoke(claudeEvent);
                    return;
                }
                request = new ClaudeRequest(doc.RootElement, reply => SendAndClose(pipe, reply));
            }
            catch
            {
                return; // не наш формат или связь оборвалась
            }

            RequestReceived?.Invoke(request);

            // Хук после первой строки ничего не шлёт: чтение завершится, когда он отключится.
            try { await reader.ReadLineAsync(_cts.Token); } catch { }
            request.NotifyClosed();
        }
        finally
        {
            pipe.Dispose();
        }
    }

    // Ответ пишем в фоне (UI не ждёт), дожидаемся, пока хук его прочитает, и закрываем канал.
    private static void SendAndClose(NamedPipeServerStream pipe, string reply)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var bytes = Utf8.GetBytes(reply + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                pipe.WaitForPipeDrain();
            }
            catch { }
            finally
            {
                pipe.Dispose();
            }
        });
    }
}
