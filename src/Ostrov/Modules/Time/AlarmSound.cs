using System.IO;
using System.Media;

namespace Ostrov.Modules.Time;

/// <summary>
/// Мелодия окончания таймера: тёплые «маримбовые» ноты, сгенерированные в коде (файлов и библиотек не нужно).
/// Играется системным <see cref="SoundPlayer"/>.
/// </summary>
internal static class AlarmSound
{
    private const int Rate = 44100;
    private static byte[]? _wav;

    public static void Play()
    {
        _wav ??= Build();
        try
        {
            // SoundPlayer играет в своём потоке, не блокируя интерфейс.
            new SoundPlayer(new MemoryStream(_wav)).Play();
        }
        catch
        {
            SystemSounds.Exclamation.Play(); // запасной вариант, если не вышло
        }
    }

    private static byte[] Build()
    {
        // Фраза из восьми нот (мажорная пентатоника), повторяется дважды.
        // Октавой ниже, чем была первая версия: мягче и глубже.
        double[] notes = [329.63, 392.00, 440.00, 493.88, 659.26, 493.88, 440.00, 392.00];
        const double step = 0.13;       // интервал между нотами, с
        const double noteLength = 0.9;  // длина звучания одной ноты, с
        const double phraseGap = 1.7;   // интервал между началами фраз, с
        const int phrases = 2;

        var total = (int)((phraseGap * (phrases - 1) + step * notes.Length + noteLength) * Rate);
        var mix = new double[total];

        for (var p = 0; p < phrases; p++)
        {
            for (var n = 0; n < notes.Length; n++)
            {
                var offset = (int)((p * phraseGap + n * step) * Rate);
                var f = notes[n];
                for (var i = 0; i < (int)(noteLength * Rate) && offset + i < total; i++)
                {
                    var t = i / (double)Rate;
                    var attack = 1 - Math.Exp(-t * 600);       // короткий «щелчок» вместо резкого старта
                    var body = Math.Exp(-t * 6.5);             // затухание основного тона
                    var overtone = Math.Exp(-t * 14) * 0.3;    // быстро гаснущий обертон — «деревянный» оттенок
                    var s = Math.Sin(2 * Math.PI * f * t) * body
                          + Math.Sin(2 * Math.PI * f * 4 * t) * overtone;
                    mix[offset + i] += s * attack;
                }
            }
        }

        var peak = mix.Max(Math.Abs);
        var gain = peak > 0 ? 0.55 / peak : 0;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = total * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // моно
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in mix) w.Write((short)(s * gain * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
