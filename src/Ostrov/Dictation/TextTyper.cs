using System.Runtime.InteropServices;

namespace Ostrov.Dictation;

/// <summary>
/// Печатает текст в окно, где сейчас фокус, так, будто он набран на клавиатуре (SendInput, режим Unicode: каждая буква — отдельное событие).
/// Буфер обмена и его история не задействуются. Любые языки и символы, раскладка не важна. Перед печатью остров «съедает» зажатый правый Alt
/// (см. <see cref="PushToTalkHook"/>), поэтому программа не принимает буквы за сочетания клавиш.
/// Окна, запущенные от имени администратора, Windows для обычных программ закрывает: туда печатать нельзя (<see cref="Type"/> вернёт false).
/// </summary>
internal static class TextTyper
{
    /// <summary>Напечатать текст. Переводы строк заменяются пробелами (диктовка не должна отправлять сообщения нажатием Enter).</summary>
    public static bool Type(string text) => Type(text, out _);

    /// <summary>Печать с подсчётом: <paramref name="typed"/> — сколько знаков (с начала текста с учётом замены переводов строк) уже напечатано, если ввод оборвался на середине.</summary>
    public static bool Type(string text, out int typed)
    {
        typed = 0;
        if (string.IsNullOrEmpty(text)) return true;
        text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

        const int PerBatch = 200; // длинный кусок отправляем порциями, чтобы программа успевала разбирать события
        for (var start = 0; start < text.Length; start += PerBatch)
        {
            var part = text.AsSpan(start, Math.Min(PerBatch, text.Length - start));
            var inputs = new Input[part.Length * 2];
            for (var i = 0; i < part.Length; i++)
            {
                inputs[i * 2] = Key(part[i], up: false);
                inputs[i * 2 + 1] = Key(part[i], up: true);
            }
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent != inputs.Length) { typed = start + (int)(sent / 2); return false; } // окно администратора или ввод заблокирован
            typed = start + part.Length;
        }
        return true;
    }

    private static Input Key(char unit, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KeybdInput { wVk = 0, wScan = unit, dwFlags = 0x0004u /* UNICODE */ | (up ? 0x0002u /* KEYUP */ : 0u) } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeybdInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
