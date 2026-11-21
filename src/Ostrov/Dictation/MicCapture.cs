using System.Runtime.InteropServices;
using Ostrov.Modules.Player;
using Ostrov.Services;

namespace Ostrov.Dictation;

/// <summary>
/// Запись микрофона по умолчанию (WASAPI, общий режим): моно, 16 бит, 24 кГц — именно в этом виде OpenAI принимает звук для распознавания,
/// пересчёт делает сама Windows. Звук не пишется на диск: блоки сразу уходят подписчику (по сети на распознавание) и в измеритель громкости для волны.
/// Работает на собственном потоке; события приходят на нём.
/// </summary>
internal sealed class MicCapture
{
    public const int SampleRate = 24000;

    private Thread? _thread;
    private StopFlag _flag = new(); // у каждого запуска свой: старый поток, застрявший в активации, не оживёт от нового Start

    private sealed class StopFlag { public volatile bool Stop; }

    /// <summary>Очередной блок: PCM16, little-endian, моно, 24 кГц (массив новый на каждый блок).</summary>
    public event Action<byte[]>? Chunk;

    /// <summary>Громкость блока 0..1 (по децибелам: −55 дБ — тишина, −10 дБ — максимум) для волны.</summary>
    public event Action<float>? Level;

    /// <summary>Запись пошла (true) или не удалась, и тогда в <see cref="Error"/> понятная причина (false). Приходит один раз, на потоке записи.</summary>
    public event Action<bool>? Started;

    public bool IsRunning { get; private set; }

    /// <summary>Почему запись не удалась или оборвалась: текст для пользователя.</summary>
    public string? Error { get; private set; }

    public void Start()
    {
        Stop();
        var flag = _flag = new StopFlag();
        Error = null;
        _thread = new Thread(() => Run(flag)) { IsBackground = true, Name = "Ostrov mic" }; // по умолчанию MTA: так нужно активации
        _thread.Start();
    }

    public void Stop()
    {
        _flag.Stop = true;
        var t = _thread;
        _thread = null;
        if (t != null && t != Thread.CurrentThread) t.Join(1500);
    }

    private void Run(StopFlag flag)
    {
        ProcessLoopbackCapture.Native.IAudioClient? client = null;
        ProcessLoopbackCapture.Native.IAudioCaptureClient? capture = null;
        var reported = false;
        try
        {
            // Выбранный в настройках микрофон; пропал (отключён) или не открылся — микрофон по умолчанию в Windows.
            var chosen = AppSettings.Load().DictationMic;
            var defaultId = MicDevices.DefaultId();
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(chosen)) candidates.Add(chosen);
            if (!string.IsNullOrEmpty(defaultId) && defaultId != chosen) candidates.Add(defaultId);
            if (candidates.Count == 0) { Error = "Не найден микрофон"; return; }

            var iidClient = typeof(ProcessLoopbackCapture.Native.IAudioClient).GUID;
            foreach (var id in candidates)
            {
                var handler = new ProcessLoopbackCapture.Native.Handler();
                var hr0 = ProcessLoopbackCapture.Native.ActivateAudioInterfaceAsync(id, ref iidClient, IntPtr.Zero, handler, out _);
                if (hr0 != 0) { Error = Describe(hr0); continue; }
                if (!handler.Done.WaitOne(5000)) { Error = "Микрофон не ответил"; continue; }
                hr0 = handler.Op!.GetActivateResult(out var result, out var activated);
                if (hr0 != 0 || result != 0) { Error = Describe(hr0 != 0 ? hr0 : result); continue; }
                client = (ProcessLoopbackCapture.Native.IAudioClient)activated;
                Error = null;
                break;
            }
            if (client == null) return;
            int hr;

            var format = new ProcessLoopbackCapture.Native.WaveFormat
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2
            };
            // EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY: микрофон отдаёт любой формат, Windows приводит его к нужному.
            hr = client.Initialize(0, 0x00040000 | 0x80000000 | 0x08000000, 2_000_000, 0, ref format, IntPtr.Zero);
            if (hr != 0) { Error = Describe(hr); return; }

            var iidCapture = typeof(ProcessLoopbackCapture.Native.IAudioCaptureClient).GUID;
            client.GetService(ref iidCapture, out var service);
            capture = (ProcessLoopbackCapture.Native.IAudioCaptureClient)service;
            using var wake = new AutoResetEvent(false);
            client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());
            hr = client.Start();
            if (hr != 0) { Error = Describe(hr); return; }
            IsRunning = true;
            reported = true;
            Started?.Invoke(true);

            var samples = new short[4800];
            while (!flag.Stop)
            {
                wake.WaitOne(100);
                while (!flag.Stop && capture.GetNextPacketSize(out var next) == 0 && next > 0)
                {
                    if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) break;
                    var count = (int)frames;
                    var bytes = new byte[count * 2];
                    var silent = (flags & 2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                    if (!silent) Marshal.Copy(data, bytes, 0, bytes.Length);
                    capture.ReleaseBuffer(frames);

                    if (samples.Length < count) samples = new short[count];
                    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                    double sum = 0;
                    for (var i = 0; i < count; i++) sum += (double)samples[i] * samples[i];
                    var rms = count > 0 ? Math.Sqrt(sum / count) / 32768.0 : 0;
                    var db = rms > 1e-6 ? 20 * Math.Log10(rms) : -120;
                    Level?.Invoke((float)Math.Clamp((db + 55) / 45, 0, 1));
                    Chunk?.Invoke(bytes);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Error ??= "Микрофон недоступен: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            if (!reported) Started?.Invoke(false);
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    // E_ACCESSDENIED: у программы нет права на микрофон (Параметры → Конфиденциальность → Микрофон).
    private static string Describe(int hr) => hr == unchecked((int)0x80070005)
        ? "Нет доступа к микрофону: разреши его в Параметрах Windows"
        : $"Микрофон недоступен (0x{hr:X8})";
}
