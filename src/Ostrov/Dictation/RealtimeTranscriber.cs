using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ostrov.Search;
using Ostrov.Services;

namespace Ostrov.Dictation;

/// <summary>
/// Потоковое распознавание речи через Realtime API OpenAI (WebSocket, режим transcription): звук с микрофона уходит кусками по ≈ 100 мс,
/// готовый текст возвращается во время речи. Модель по умолчанию gpt-4o-mini-transcribe: сервер сам делит речь на фразы по паузам (server_vad),
/// и каждая фраза приходит сразу после паузы; у моделей gpt-live-* текст идёт прямо во время речи (клиент сам решает, когда закончить).
/// К OpenAI идём через тот же тоннель до своего сервера, что и остальные запросы (<see cref="ServerTunnel"/>), если он включён.
/// Звук нигде не записывается: он только пересылается. Ключ берётся из <see cref="OpenAiKeyStore"/>.
/// Все события приходят на потоке сети.
/// </summary>
internal sealed class RealtimeTranscriber : IDisposable
{
    /// <summary>Другой адрес сервера. Только для проверок (поддельный сервер на этом компьютере).</summary>
    internal static string? UrlOverride { get; set; }

    public const string DefaultModel = "gpt-4o-mini-transcribe";
    private const string Url = "wss://api.openai.com/v1/realtime?intent=transcription";
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(9);

    private readonly string _model;
    private readonly string? _words; // свои слова вместо настроек (для проверок); null — из настроек
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<byte[]> _pending = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly HashSet<string> _openItems = [];      // фразы, отправленные на распознавание и ещё не вернувшиеся
    private readonly Dictionary<string, string> _typed = []; // что уже напечатано по каждой фразе (только gpt-live-*: текст идёт кусками)
    private readonly List<string> _order = [];               // порядок фраз (как их выделил сервер): печатаем строго по порядку
    private readonly Dictionary<string, string> _done = [];  // готовые фразы, ждущие своей очереди
    private ClientWebSocket? _ws;
    private volatile bool _finish;
    private bool _failed;
    private bool _anyTyped;
    private long _lastCommitTicks;
    private long _finalSentTicks;       // когда ушёл последний commit при отпускании клавиши (0 — ещё нет)
    private volatile bool _finalAck;    // после него сервер что-то ответил (committed или «пустой остаток»)
    private Task? _run;

    /// <summary>Сколько фраз сервер вернул готовыми (для журнала диагностики).</summary>
    public int PhrasesDone { get; private set; }

    /// <summary>Новый кусок текста, который надо напечатать (первый кусок уже без пробела впереди).</summary>
    public event Action<string>? Text;

    /// <summary>Понятная причина сбоя (нет ключа, регион, обрыв…). Приходит не больше одного раза.</summary>
    public event Action<string>? Failed;

    public RealtimeTranscriber(string? model = null, string? words = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _words = words;
    }

    /// <summary>Есть ли ключ OpenAI (без него диктовка невозможна; проверять до начала записи).</summary>
    public static bool HasKey() => !string.IsNullOrEmpty(OpenAiKeyStore.Load());

    /// <summary>Подключиться в фоне. Звук можно подавать сразу: он накапливается и уходит, как только соединение готово.</summary>
    public void Start() => _run = Task.Run(RunAsync);

    /// <summary>Очередной блок звука с микрофона (PCM16, 24 кГц, моно). Можно звать с любого потока.</summary>
    public void Feed(byte[] pcm)
    {
        if (_finish || _failed) return;
        lock (_lock) _pending.Add(pcm);
        _signal.Release();
    }

    /// <summary>
    /// Запись закончена: досылаем звук, просим распознать остаток и ждём, пока вернутся все фразы (не дольше нескольких секунд).
    /// Завершается, когда всё допечатано, или после ошибки, или по тайм-ауту.
    /// </summary>
    public async Task FinishAsync()
    {
        _finish = true;
        _signal.Release();
        var run = _run;
        if (run == null) return;
        try { await run.WaitAsync(FinishTimeout + ConnectTimeout); }
        catch { /* ждали достаточно: закрываем как есть */ }
        Dispose();
    }

    /// <summary>Отмена: ничего больше не печатается, соединение закрывается.</summary>
    public void Abort()
    {
        _failed = true; // «закрыто»: события больше не доходят до подписчиков
        Dispose();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
    }

    private void Fail(string message)
    {
        if (_failed) return;
        _failed = true;
        Failed?.Invoke(message);
        Dispose();
    }

    private async Task RunAsync()
    {
        var key = OpenAiKeyStore.Load();
        if (string.IsNullOrEmpty(key)) { Fail("Нужен ключ OpenAI: вставь его в настройках"); return; }

        using var handler = new SocketsHttpHandler { ConnectCallback = ServerTunnel.ConnectAsync };
        using var invoker = new HttpMessageInvoker(handler);
        try
        {
            var ct = _cts.Token;
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeout);
                try { await _ws.ConnectAsync(new Uri(UrlOverride ?? Url), invoker, connectCts.Token); }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    Fail(ConnectFailure(_ws));
                    return;
                }
            }
            DebugLog($"connected, model {_model}");

            var input = new Dictionary<string, object?>
            {
                ["format"] = new { type = "audio/pcm", rate = MicCapture.SampleRate },
                ["transcription"] = TranscriptionConfig(),
                ["turn_detection"] = TurnDetection() // у gpt-live-* явный null
            };
            await SendJson(new { type = "session.update", session = new { type = "transcription", audio = new { input } } }, ct);

            var receive = Task.Run(() => ReceiveLoop(ct), ct);
            await SendLoop(ct);
            // Запись кончилась: ждём, пока вернутся все фразы.
            var deadline = DateTime.UtcNow + FinishTimeout;
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline && !receive.IsCompleted)
            {
                bool idle;
                lock (_lock) idle = _openItems.Count == 0;
                // Фразы, которые сервер ещё не успел объявить: ждём ответа на последний commit (на медленной связи он идёт дольше 0,7 с), но не дольше 3 с.
                var now = DateTime.UtcNow.Ticks;
                if (idle && (now - Interlocked.Read(ref _lastCommitTicks)) > TimeSpan.FromMilliseconds(700).Ticks
                    && (_finalAck || now - Interlocked.Read(ref _finalSentTicks) > TimeSpan.FromSeconds(3).Ticks)) break;
                await Task.Delay(60, ct);
            }
        }
        catch (OperationCanceledException) { /* отмена или конец */ }
        catch (Exception ex)
        {
            DebugLog("run error: " + ex.GetType().Name + " " + ex.Message);
            Fail("Диктовка прервалась: нет связи с OpenAI");
        }
        finally
        {
            try
            {
                if (_ws is { State: WebSocketState.Open } ws)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch { }
            _ws?.Dispose();
        }
    }

    private bool IsLive => _model.StartsWith("gpt-live", StringComparison.OrdinalIgnoreCase);

    // У gpt-live-* языки задаются списком; у остальных язык определяется сам (русский и английский вперемешку).
    private Dictionary<string, object?> TranscriptionConfig()
    {
        var config = new Dictionary<string, object?> { ["model"] = _model };
        if (IsLive) config["languages"] = new[] { "ru", "en" };
        // Свои слова пользователя (жаргон, названия) идут подсказкой: модель узнаёт их лучше. У gpt-live-* подсказка не проверена, там не передаём.
        else if ((_words ?? AppSettings.Load().DictationWords) is { Length: > 0 } words)
            config["prompt"] = "Слова и выражения, которые могут встретиться в речи: " + words + ".";
        return config;
    }

    // У gpt-live-* серверного деления на фразы нет (null), клиент сам решает, когда закончить; у остальных сервер делит по паузам.
    private object? TurnDetection() =>
        IsLive ? null : new { type = "server_vad", threshold = 0.5, prefix_padding_ms = 300, silence_duration_ms = 500 };

    private async Task SendLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(100), ct);
            List<byte[]> chunks;
            lock (_lock) { chunks = [.. _pending]; _pending.Clear(); }
            // Накопленное (в том числе пока шло подключение) уходит кусками не больше ≈ 1 с.
            var total = chunks.Sum(c => c.Length);
            if (total > 0)
            {
                var all = new byte[total];
                var offset = 0;
                foreach (var c in chunks) { Buffer.BlockCopy(c, 0, all, offset, c.Length); offset += c.Length; }
                for (var i = 0; i < all.Length; i += 48000)
                {
                    var len = Math.Min(48000, all.Length - i);
                    await SendJson(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(all, i, len) }, ct);
                }
            }
            if (_finish)
            {
                bool left;
                lock (_lock) left = _pending.Count > 0;
                if (left) continue; // успело добавиться ещё
                Interlocked.Exchange(ref _lastCommitTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _finalSentTicks, DateTime.UtcNow.Ticks);
                await SendJson(new { type = "input_audio_buffer.commit" }, ct); // остаток речи, который сервер ещё не успел выделить в фразу
                return;
            }
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Сервер закрыл связь, пока ещё говорили: без этого запись шла бы в пустоту и текст молча пропал бы.
                        if (!_finish) Fail("Связь с OpenAI прервалась");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                Handle(message.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLog("receive error: " + ex.GetType().Name);
            if (!_finish) Fail("Связь с OpenAI прервалась");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    // Разбор события сервера. Текст распознанного в журнал не пишется: только типы событий.
    private void Handle(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        DebugLog("event " + type);
        switch (type)
        {
            case "input_audio_buffer.committed":
                if (Str(root, "item_id") is { } committed)
                {
                    lock (_lock) { _openItems.Add(committed); if (!_order.Contains(committed)) _order.Add(committed); }
                    Interlocked.Exchange(ref _lastCommitTicks, DateTime.UtcNow.Ticks);
                    if (Interlocked.Read(ref _finalSentTicks) != 0) _finalAck = true;
                }
                break;
            case "conversation.item.input_audio_transcription.delta":
                // У обычных моделей дельты приходят пачкой уже после паузы и ничего не выигрывают, зато могут перемешать соседние фразы:
                // печатаем целую готовую фразу. Только у gpt-live-* текст идёт по ходу речи, и там печатаем куски сразу.
                if (IsLive && Str(root, "item_id") is { } id && Str(root, "delta") is { Length: > 0 } delta) Emit(id, delta);
                break;
            case "conversation.item.input_audio_transcription.completed":
                if (Str(root, "item_id") is { } doneId)
                {
                    var full = Str(root, "transcript") ?? "";
                    if (!IsLive)
                    {
                        lock (_lock) { if (!_order.Contains(doneId)) _order.Add(doneId); _done[doneId] = full; }
                        PhrasesDone++;
                        FlushPhrases();
                        lock (_lock) _openItems.Remove(doneId);
                        Interlocked.Exchange(ref _lastCommitTicks, DateTime.UtcNow.Ticks);
                        break;
                    }
                    PhrasesDone++;
                    // Если часть текста не пришла дельтами, допечатываем недостающий конец; исправлять уже напечатанное нельзя.
                    var have = _typed.TryGetValue(doneId, out var typed) ? typed : "";
                    full = full.TrimStart();
                    if (full.Length > have.Length && full.StartsWith(have, StringComparison.Ordinal)) Emit(doneId, full[have.Length..]);
                    lock (_lock) _openItems.Remove(doneId);
                    Interlocked.Exchange(ref _lastCommitTicks, DateTime.UtcNow.Ticks);
                }
                break;
            case "conversation.item.input_audio_transcription.failed":
                if (Str(root, "item_id") is { } failedId)
                {
                    lock (_lock) { _openItems.Remove(failedId); _order.Remove(failedId); }
                    FlushPhrases(); // не вернувшаяся фраза не должна держать очередь
                }
                break;
            case "error":
                HandleError(root);
                break;
        }
    }

    private void HandleError(JsonElement root)
    {
        var error = root.TryGetProperty("error", out var e) ? e : root;
        var code = Str(error, "code") ?? "";
        var message = Str(error, "message") ?? "";
        DebugLog($"error code={code}");
        // Пустой остаток при отпускании клавиши (вся речь уже ушла фразами): не ошибка.
        if (code.Contains("commit_empty", StringComparison.OrdinalIgnoreCase) || message.Contains("buffer too small", StringComparison.OrdinalIgnoreCase)
            || message.Contains("only 0.00ms", StringComparison.OrdinalIgnoreCase)) { _finalAck = true; return; }
        Fail(code == "invalid_api_key" ? "Ключ OpenAI не подошёл" : message.Length > 0 ? Short(message) : "OpenAI вернул ошибку");
    }

    // Печатаем готовые фразы по порядку: следующая ждёт, пока вернётся предыдущая. Между фразами пробел.
    private void FlushPhrases()
    {
        while (true)
        {
            string text;
            lock (_lock)
            {
                if (_order.Count == 0 || !_done.TryGetValue(_order[0], out var phrase)) return;
                _done.Remove(_order[0]);
                _order.RemoveAt(0);
                text = phrase.Trim();
            }
            if (text.Length == 0 || _failed) continue;
            var prefix = _anyTyped ? " " : "";
            _anyTyped = true;
            Text?.Invoke(prefix + text);
        }
    }

    private void Emit(string itemId, string text)
    {
        if (_failed) return;
        // Новая фраза: пробел между фразами (первая фраза без пробела впереди). В «напечатанном» пробел-разделитель не учитывается,
        // чтобы итоговый текст фразы можно было сверить с набранными дельтами.
        var prefix = "";
        if (!_typed.ContainsKey(itemId))
        {
            _typed[itemId] = "";
            text = text.TrimStart();
            if (_anyTyped) prefix = " ";
        }
        if (text.Length == 0) return;
        _typed[itemId] += text;
        _anyTyped = true;
        Text?.Invoke(prefix + text);
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string s) => s.Length <= 60 ? s : s[..57] + "…";

    private static string ConnectFailure(ClientWebSocket ws) => (int)ws.HttpStatusCode switch
    {
        401 => "Ключ OpenAI не подошёл",
        403 => "OpenAI не работает из твоего региона. Включи VPN",
        429 => "OpenAI: лимит запросов",
        >= 500 => "OpenAI временно недоступен",
        _ => "Нет связи с OpenAI. Проверь интернет и VPN"
    };

    private async Task SendJson(object payload, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    // Отладочный журнал: только если задана переменная окружения OSTROV_DEBUG_DICTATION=1. Тексты речи в него не попадают.
    private static readonly bool Debug = Environment.GetEnvironmentVariable("OSTROV_DEBUG_DICTATION") == "1";
    private static void DebugLog(string line)
    {
        if (!Debug) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "dictation-debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}\r\n", Encoding.UTF8);
        }
        catch { }
    }
}
