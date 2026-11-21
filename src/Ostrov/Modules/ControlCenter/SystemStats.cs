using System.Runtime.InteropServices;

namespace Ostrov.Modules.ControlCenter;

/// <summary>
/// Загрузка процессора, память и видеокарта NVIDIA. Только чтение, без прав администратора и без сторонних пакетов:
/// процессор и память — штатные функции Windows, видеокарта — <c>nvml.dll</c> из драйвера NVIDIA.
/// Температуры процессора нет (решение пользователя 2026-11-12: она потребовала бы права администратора и стороннюю библиотеку).
/// </summary>
internal sealed class SystemStats
{
    /// <param name="Cpu">Загрузка процессора, %.</param>
    /// <param name="Gpu">Загрузка видеокарты, % (null — видеокарты NVIDIA или драйвера нет).</param>
    /// <param name="GpuTemp">Температура видеокарты, °C (null — не удалось прочитать).</param>
    public readonly record struct Snapshot(double Cpu, double? Gpu, int? GpuTemp, double MemPercent, double MemUsedGb, double MemTotalGb);

    // Загрузка усредняется по последним замерам, чтобы кольца не дёргались: процессор около секунды, видеокарта около двух
    // (её значение на простое скачет 0 ↔ 9 %, см. Nvml). Размер окна считается от того, как часто зовут Sample().
    private readonly int CpuWindow, GpuWindow;

    private long _prevIdle, _prevBusy;
    private readonly Queue<double> _cpuWindow = new(), _gpuWindow = new();

    /// <param name="intervalMs">Как часто будут звать <see cref="Sample"/> (по умолчанию 250 мс; хабу хватает раза в секунду).</param>
    public SystemStats(int intervalMs = 250)
    {
        CpuWindow = Math.Max(1, 1000 / intervalMs);
        GpuWindow = Math.Max(1, 2000 / intervalMs);
        ReadCpuTimes(out _prevIdle, out _prevBusy);
    }

    private static double Push(Queue<double> window, double value, int size)
    {
        window.Enqueue(value);
        while (window.Count > size) window.Dequeue();
        return window.Average();
    }

    public Snapshot Sample()
    {
        // Загрузка процессора — доля «не простоя» между двумя замерами.
        ReadCpuTimes(out var idle, out var busy);
        var dIdle = idle - _prevIdle;
        var dTotal = dIdle + (busy - _prevBusy);
        var cpu = _cpuWindow.Count > 0 ? _cpuWindow.Average() : 0;
        if (dTotal > 0) cpu = Push(_cpuWindow, Math.Clamp((1.0 - (double)dIdle / dTotal) * 100, 0, 100), CpuWindow);
        _prevIdle = idle;
        _prevBusy = busy;

        var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        double total = 0, used = 0, percent = 0;
        if (GlobalMemoryStatusEx(ref mem))
        {
            const double gb = 1024.0 * 1024 * 1024;
            total = mem.TotalPhys / gb;
            used = (mem.TotalPhys - mem.AvailPhys) / gb;
            percent = mem.MemoryLoad;
        }

        double? gpu = null;
        int? temp = null;
        if (Nvml.TryRead(out var load, out var t))
        {
            gpu = Push(_gpuWindow, load, GpuWindow);
            if (t >= 0) temp = t;
        }
        else
        {
            _gpuWindow.Clear(); // нет данных (или драйвер ещё «разгоняется»): старые замеры не тянем в новое среднее
        }

        return new Snapshot(cpu, gpu, temp, percent, used, total);
    }

    // GetSystemTimes: «ядро» включает простой, поэтому занято = ядро + пользователь − простой.
    private static void ReadCpuTimes(out long idle, out long busy)
    {
        if (GetSystemTimes(out idle, out var kernel, out var user)) busy = kernel + user - idle;
        else { idle = 0; busy = 0; }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

/// <summary>Видеокарта NVIDIA через <c>nvml.dll</c> (ставится вместе с драйвером). Первая видеокарта; нет драйвера или карты — просто «нет данных».</summary>
internal static class Nvml
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    private const string Dll = "nvml.dll";

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlInit_v2();

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);

    [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temperature);

    private static readonly object Gate = new();
    private static bool _ready;
    private static IntPtr _device;
    private static DateTime _nextInit = DateTime.MinValue;
    private static DateTime _warmAt = DateTime.MaxValue;

    // Первые ~2 секунды после инициализации драйверный счётчик загрузки читает 0 и только потом «разгоняется»
    // (проверено замером 2026-11-12), поэтому в это время данных нет, а инициализируем библиотеку заранее, при запуске острова.
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(2.5);

    /// <summary>Инициализировать драйверную библиотеку заранее (в фоне), чтобы к первому открытию вкладки счётчик уже «прогрелся».</summary>
    public static void Warmup() => Task.Run(() => TryRead(out _, out _));

    /// <returns>false — данных нет. <paramref name="temp"/> = −1, если температуру прочитать не удалось.</returns>
    public static bool TryRead(out double load, out int temp)
    {
        load = 0;
        temp = -1;
        lock (Gate)
        {
            try
            {
                if (!_ready)
                {
                    // Драйвер мог ещё не подняться: повторяем не чаще раза в 30 секунд.
                    if (DateTime.UtcNow < _nextInit) return false;
                    _nextInit = DateTime.UtcNow.AddSeconds(30);
                    if (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0) return false;
                    _ready = true;
                    _warmAt = DateTime.UtcNow + WarmUp;
                }

                if (DateTime.UtcNow < _warmAt) return false;
                if (nvmlDeviceGetUtilizationRates(_device, out var u) != 0) return false;
                load = Math.Clamp(u.Gpu, 0, 100);
                if (nvmlDeviceGetTemperature(_device, 0, out var t) == 0) temp = (int)t; // 0 = датчик ядра видеокарты
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _nextInit = DateTime.UtcNow.AddMinutes(10); // библиотеки нет: не мучаем её каждую секунду
                return false;
            }
        }
    }
}
