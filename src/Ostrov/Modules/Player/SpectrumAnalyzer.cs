using System.Numerics;

namespace Ostrov.Modules.Player;

/// <summary>Текущие уровни частотных полос музыки (0..1) для волны в свёрнутом острове. Пишет захват звука, читают виды.</summary>
internal static class MusicSpectrum
{
    public const int Bands = 5;

    public static volatile double[] Levels = new double[Bands];

    public static void Reset() => Levels = new double[Bands];
}

/// <summary>
/// Превращает поток моно-отсчётов в уровни пяти полос: низкие, нижние средние, средние, верхние средние, высокие.
/// БПФ на 1024 отсчётов с окном Ханна; уровень каждой полосы нормируется по её собственному медленно «остывающему» максимуму,
/// поэтому волна живая и на тихой музыке, а в настоящей тишине остаётся пустой. Вызывается только с потока захвата.
/// </summary>
internal sealed class SpectrumAnalyzer
{
    private const int N = 1024;
    // Ниже этого средняя амплитуда полосы — тишина (≈ −86 дБ от полной шкалы). Проба: Apple Music играет тихо
    // (громкость около 0,002 от полной шкалы), так что порог низкий; настоящая цифровая тишина даёт ровно 0.
    private const double Floor = 0.00005;

    // Границы полос, Гц: бас, низ-середина, середина, верх-середина, верх.
    private static readonly double[] Edges = [30, 110, 330, 1000, 3200, 12000];

    private readonly float[] _ring = new float[N];
    private long _written;
    private readonly double[] _peak = new double[MusicSpectrum.Bands];
    private readonly Complex[] _buffer = new Complex[N];

    public SpectrumAnalyzer()
    {
        Array.Fill(_peak, Floor * 4);
    }

    public void Push(float[] samples, int count)
    {
        for (var i = 0; i < count; i++) _ring[_written++ % N] = samples[i];
    }

    /// <summary>Уровни полос по последним 1024 отсчётам; null, если отсчётов ещё мало.</summary>
    public double[]? Analyze()
    {
        if (_written < N) return null;
        for (var i = 0; i < N; i++)
        {
            var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
            _buffer[i] = _ring[(_written - N + i) % N] * window;
        }
        Fft(_buffer);

        var levels = new double[MusicSpectrum.Bands];
        for (var b = 0; b < levels.Length; b++)
        {
            var k0 = Math.Max(1, (int)(Edges[b] * N / ProcessLoopbackCapture.SampleRate));
            var k1 = Math.Max(k0 + 1, (int)(Edges[b + 1] * N / ProcessLoopbackCapture.SampleRate));
            double sum = 0;
            for (var k = k0; k < k1; k++) sum += _buffer[k].Magnitude;
            var amplitude = sum / (k1 - k0) / (N / 4.0); // 1.0 — синусоида на полной шкале

            _peak[b] = Math.Max(Math.Max(_peak[b] * 0.9985, amplitude), Floor * 4);
            levels[b] = amplitude < Floor ? 0 : Math.Pow(Math.Clamp(amplitude / _peak[b], 0, 1), 1.3);
        }
        return levels;
    }

    private static void Fft(Complex[] a)
    {
        var n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (var i = 0; i < n; i += len)
            {
                Complex wn = 1;
                for (var j = 0; j < len / 2; j++)
                {
                    var u = a[i + j];
                    var v = a[i + j + len / 2] * wn;
                    a[i + j] = u + v;
                    a[i + j + len / 2] = u - v;
                    wn *= w;
                }
            }
        }
    }
}
