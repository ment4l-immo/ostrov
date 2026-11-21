using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ostrov.Modules.Player;

/// <summary>
/// Слушает звук одного процесса (и его дочерних) через «петлю» процесса Windows (WASAPI process loopback,
/// Windows 10 2004 и новее): остров получает те же отсчёты, что уходят в колонки от этой программы, и больше ничего.
/// Звук не записывается и не сохраняется: блоки сразу сворачиваются в частотные полосы и забываются. Сторонних библиотек нет.
/// Работает на собственном потоке; <see cref="Block"/> вызывается на нём.
/// </summary>
internal sealed class ProcessLoopbackCapture
{
    public const int SampleRate = 44100;

    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>Очередной блок моно-отсчётов −1..1 (буфер переиспользуется: данные только на время вызова).</summary>
    public event Action<float[], int>? Block;

    public bool IsRunning { get; private set; }
    public uint ProcessId { get; private set; }

    /// <summary>Почему захват не удался или остановился (для отладочного журнала).</summary>
    public string? Error { get; private set; }

    public void Start(uint processId)
    {
        Stop();
        _stop = false;
        Error = null;
        ProcessId = processId;
        _thread = new Thread(() => Run(processId)) { IsBackground = true, Name = "Ostrov audio tap" }; // по умолчанию MTA: так нужно активации
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        var t = _thread;
        _thread = null;
        t?.Join(1500);
    }

    private void Run(uint processId)
    {
        IntPtr pBlob = IntPtr.Zero, pVariant = IntPtr.Zero;
        Native.IAudioClient? client = null;
        Native.IAudioCaptureClient? capture = null;
        try
        {
            var p = new Native.ActivationParams { ActivationType = 1 /* PROCESS_LOOPBACK */, TargetProcessId = processId, ProcessLoopbackMode = 0 /* вместе с дочерними */ };
            pBlob = Marshal.AllocHGlobal(Marshal.SizeOf<Native.ActivationParams>());
            Marshal.StructureToPtr(p, pBlob, false);
            var variant = new Native.PropVariant { vt = 65 /* VT_BLOB */, cbSize = (uint)Marshal.SizeOf<Native.ActivationParams>(), pBlobData = pBlob };
            pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<Native.PropVariant>());
            Marshal.StructureToPtr(variant, pVariant, false);

            var iidClient = typeof(Native.IAudioClient).GUID;
            var handler = new Native.Handler();
            var hr = Native.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iidClient, pVariant, handler, out _);
            if (hr != 0) { Error = $"ActivateAudioInterfaceAsync 0x{hr:X8}"; return; }
            if (!handler.Done.WaitOne(5000)) { Error = "активация не ответила за 5 с"; return; }
            hr = handler.Op!.GetActivateResult(out var result, out var activated);
            if (hr != 0 || result != 0) { Error = $"GetActivateResult 0x{hr:X8} / 0x{result:X8}"; return; }
            client = (Native.IAudioClient)activated;

            var format = new Native.WaveFormat
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = SampleRate * 4
            };
            // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
            hr = client.Initialize(0, 0x00020000 | 0x00040000 | 0x80000000 | 0x08000000, 2_000_000, 0, ref format, IntPtr.Zero);
            if (hr != 0) { Error = $"Initialize 0x{hr:X8}"; return; }

            var iidCapture = typeof(Native.IAudioCaptureClient).GUID;
            client.GetService(ref iidCapture, out var service);
            capture = (Native.IAudioCaptureClient)service;
            using var wake = new AutoResetEvent(false);
            client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());
            hr = client.Start();
            if (hr != 0) { Error = $"Start 0x{hr:X8}"; return; }
            IsRunning = true;

            var raw = new short[8192];
            var mono = new float[4096];
            while (!_stop)
            {
                wake.WaitOne(100);
                while (!_stop && capture.GetNextPacketSize(out var next) == 0 && next > 0)
                {
                    if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) break;
                    var count = (int)frames;
                    if (count * 2 > raw.Length) { raw = new short[count * 2]; mono = new float[count]; }
                    var silent = (flags & 2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                    if (!silent) Marshal.Copy(data, raw, 0, count * 2);
                    for (var i = 0; i < count; i++)
                        mono[i] = silent ? 0 : (raw[i * 2] + raw[i * 2 + 1]) / 65536f;
                    capture.ReleaseBuffer(frames);
                    Block?.Invoke(mono, count);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsRunning = false;
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (pVariant != IntPtr.Zero) Marshal.FreeHGlobal(pVariant);
            if (pBlob != IntPtr.Zero) Marshal.FreeHGlobal(pBlob);
        }
    }

    /// <summary>
    /// Найти процесс, который играет звук Apple Music. Проба 2026-11-12: звук рождается не в главном AppleMusic.exe
    /// (у него нет аудиосеансов), а во вспомогательном AMPLibraryAgent.exe; AppleMusic.exe остаётся запасным вариантом.
    /// </summary>
    public static uint? FindAppleMusicAudio()
    {
        foreach (var name in new[] { "AMPLibraryAgent", "AppleMusic" })
        {
            try
            {
                var p = Process.GetProcessesByName(name).FirstOrDefault();
                if (p != null) return (uint)p.Id;
            }
            catch { /* процесс исчез между поиском и чтением: пробуем следующий */ }
        }
        return null;
    }

    // ---------- описания системных интерфейсов ----------

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WaveFormat
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ActivationParams { public uint ActivationType, TargetProcessId, ProcessLoopbackMode; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public uint cbSize;
            [FieldOffset(16)] public IntPtr pBlobData;
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WaveFormat format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormat format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAgileObject { }

        [ClassInterface(ClassInterfaceType.None)]
        public sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new(false);
            public IActivateAudioInterfaceAsyncOperation? Op;

            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                Op = operation;
                Done.Set();
                return 0;
            }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        public static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);
    }
}
