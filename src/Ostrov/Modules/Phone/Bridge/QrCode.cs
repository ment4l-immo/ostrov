using System.Text;

namespace Ostrov.Modules.Phone.Bridge;

/// <summary>
/// Кодировщик QR-кода без сторонних библиотек (версии 1–10, уровень коррекции M, режим «байты» UTF-8; для адреса вида
/// <c>http://192.168.1.5:54321/Abc…/</c> хватает версии 4–5). Устройство по спецификации ISO 18004: данные → блоки Рида—Соломона →
/// чередование → размещение змейкой → выбор маски по штрафам → информация о формате (и версии с 7-й). Результат — матрица модулей
/// (<c>true</c> = тёмный), без «тихой зоны»: её добавляет тот, кто рисует.
/// </summary>
internal static class QrCode
{
    // Для уровня M: всего кодовых слов, слов коррекции на блок, число блоков (короткие первыми, потом на слово длиннее).
    private static readonly int[] TotalWords = [0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346];
    private static readonly int[] EccPerBlock = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] Blocks = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];
    private static readonly int[][] AlignPositions =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50]
    ];

    public const int MaxVersion = 10;

    /// <summary>Максимум байт текста, которые влезают в версию (режим «байты», уровень M).</summary>
    public static int Capacity(int version)
    {
        var data = TotalWords[version] - EccPerBlock[version] * Blocks[version];
        var header = 4 + (version >= 10 ? 16 : 8);
        return (data * 8 - header) / 8;
    }

    /// <summary>Закодировать текст. Бросает <see cref="ArgumentException"/>, если текст длиннее, чем влезает в версию 10.</summary>
    public static bool[,] Encode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var version = 1;
        while (version <= MaxVersion && bytes.Length > Capacity(version)) version++;
        if (version > MaxVersion) throw new ArgumentException("Текст слишком длинный для QR-кода");

        var dataWords = TotalWords[version] - EccPerBlock[version] * Blocks[version];
        var bits = new List<bool>();
        void Put(int value, int count) { for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Put(0b0100, 4);
        Put(bytes.Length, version >= 10 ? 16 : 8);
        foreach (var b in bytes) Put(b, 8);
        Put(0, Math.Min(4, dataWords * 8 - bits.Count)); // завершающие нули
        while (bits.Count % 8 != 0) bits.Add(false);
        var data = new List<byte>();
        for (var i = 0; i < bits.Count; i += 8)
        {
            var v = 0;
            for (var j = 0; j < 8; j++) v = (v << 1) | (bits[i + j] ? 1 : 0);
            data.Add((byte)v);
        }
        for (var pad = 0xEC; data.Count < dataWords; pad ^= 0xEC ^ 0x11) data.Add((byte)pad);

        var codewords = AddEccAndInterleave(data.ToArray(), version);
        return BuildMatrix(codewords, version);
    }

    // ───── Рид—Соломон и чередование ─────

    private static byte[] AddEccAndInterleave(byte[] data, int version)
    {
        var blocks = Blocks[version];
        var ecc = EccPerBlock[version];
        var raw = TotalWords[version];
        var shortLen = raw / blocks;
        var shortCount = blocks - raw % blocks;
        var divisor = ComputeDivisor(ecc);

        var dataBlocks = new List<byte[]>();
        var eccBlocks = new List<byte[]>();
        var offset = 0;
        for (var i = 0; i < blocks; i++)
        {
            var len = shortLen - ecc + (i < shortCount ? 0 : 1);
            var block = data.AsSpan(offset, len).ToArray();
            offset += len;
            dataBlocks.Add(block);
            eccBlocks.Add(ComputeRemainder(block, divisor));
        }

        var result = new List<byte>();
        var maxData = dataBlocks.Max(b => b.Length);
        for (var i = 0; i < maxData; i++)
            foreach (var b in dataBlocks)
                if (i < b.Length) result.Add(b[i]);
        for (var i = 0; i < ecc; i++)
            foreach (var b in eccBlocks) result.Add(b[i]);
        return result.ToArray();
    }

    private static int Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    private static byte[] ComputeDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ComputeRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    // ───── Матрица ─────

    private static bool[,] BuildMatrix(byte[] codewords, int version)
    {
        var size = 17 + 4 * version;
        var modules = new bool[size, size]; // [y, x]
        var isFunction = new bool[size, size];

        void Set(int x, int y, bool dark)
        {
            modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        // Полосы синхронизации.
        for (var i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
        // Поисковые узоры с разделителями.
        void Finder(int cx, int cy)
        {
            for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int xx = cx + dx, yy = cy + dy;
                    if (xx >= 0 && xx < size && yy >= 0 && yy < size) Set(xx, yy, dist != 2 && dist != 4);
                }
        }
        Finder(3, 3); Finder(size - 4, 3); Finder(3, size - 4);
        // Выравнивающие узоры.
        var pos = AlignPositions[version];
        for (var i = 0; i < pos.Length; i++)
            for (var j = 0; j < pos.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == pos.Length - 1) || (i == pos.Length - 1 && j == 0)) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        Set(pos[i] + dx, pos[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        // Зарезервировать место под формат и версию (содержимое впишем после выбора маски).
        DrawFormat(modules, isFunction, size, 0);
        if (version >= 7) DrawVersion(modules, isFunction, size, version);

        // Данные змейкой снизу вверх по парам столбцов.
        var bitIndex = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vert : vert;
                    if (!isFunction[y, x] && bitIndex < codewords.Length * 8)
                    {
                        modules[y, x] = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                        bitIndex++;
                    }
                }
        }

        // Выбор маски по штрафам.
        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, isFunction, size, mask);
            DrawFormat(modules, isFunction, size, mask);
            var penalty = Penalty(modules, size);
            if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
            ApplyMask(modules, isFunction, size, mask); // маска обратима: снимаем
        }
        ApplyMask(modules, isFunction, size, bestMask);
        DrawFormat(modules, isFunction, size, bestMask);
        return modules;
    }

    private static void DrawFormat(bool[,] m, bool[,] f, int size, int mask)
    {
        // Уровень M имеет код 0 в двух битах формата.
        var data = (0 << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = ((data << 10) | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        void Set(int x, int y, bool dark) { m[y, x] = dark; f[y, x] = true; }

        for (var i = 0; i <= 5; i++) Set(8, i, Bit(i));
        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
        for (var i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(i));
        Set(8, size - 8, true); // всегда тёмный модуль
    }

    private static void DrawVersion(bool[,] m, bool[,] f, int size, int version)
    {
        var rem = version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = (version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var dark = ((bits >> i) & 1) != 0;
            int a = size - 11 + i % 3, b = i / 3;
            m[b, a] = dark; f[b, a] = true;
            m[a, b] = dark; f[a, b] = true;
        }
    }

    private static void ApplyMask(bool[,] m, bool[,] f, int size, int mask)
    {
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if (f[y, x]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0
                };
                if (invert) m[y, x] = !m[y, x];
            }
    }

    // Штрафы по четырём правилам спецификации (длинные ряды, блоки 2×2, узоры, как у поискового, баланс тёмных и светлых).
    private static int Penalty(bool[,] m, int size)
    {
        var total = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < size; a++)
            {
                var run = 1;
                for (var b = 1; b < size; b++)
                {
                    bool prev = pass == 0 ? m[a, b - 1] : m[b - 1, a], cur = pass == 0 ? m[a, b] : m[b, a];
                    if (cur == prev) { run++; continue; }
                    if (run >= 5) total += 3 + run - 5;
                    run = 1;
                }
                if (run >= 5) total += 3 + run - 5;

                // 1:1:3:1:1 с четырьмя светлыми модулями с любой стороны.
                for (var b = 0; b + 11 <= size; b++)
                {
                    bool Get(int k) => pass == 0 ? m[a, b + k] : m[b + k, a];
                    if (Get(0) && !Get(1) && Get(2) && Get(3) && Get(4) && !Get(5) && Get(6) && !Get(7) && !Get(8) && !Get(9) && !Get(10)) total += 40;
                    if (!Get(0) && !Get(1) && !Get(2) && !Get(3) && Get(4) && !Get(5) && Get(6) && Get(7) && Get(8) && !Get(9) && Get(10)) total += 40;
                }
            }
        for (var y = 0; y + 1 < size; y++)
            for (var x = 0; x + 1 < size; x++)
                if (m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) total += 3;
        var dark = 0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (m[y, x]) dark++;
        var percent = dark * 100 / (size * size);
        total += Math.Abs(percent - 50) / 5 * 10;
        return total;
    }
}
