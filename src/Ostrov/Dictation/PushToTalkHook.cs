using System.Runtime.InteropServices;

namespace Ostrov.Dictation;

/// <summary>
/// Логика «держи клавишу — говори» без обращения к Windows (чтобы её можно было проверить сценариями). Следит за одной клавишей (по умолчанию правый Alt):
/// нажатие и удержание — <see cref="Pressed"/>, отпускание — <see cref="Released"/>. Саму клавишу программы не видят (её «съедает» перехватчик):
/// иначе отпускание одиночного Alt подсвечивало бы меню в окне, куда печатается текст, а при зажатом Alt текст воспринимался бы как сочетания.
/// Если вместе с ней нажата любая другая клавиша — это обычное сочетание (например, правый Alt+Q): запись отменяется, а Alt и эта клавиша
/// честно «проигрываются» системе заново, чтобы сочетание сработало как обычно.
/// </summary>
internal sealed class PushToTalkLogic
{
    public const int VkRMenu = 0xA5;

    public enum Decision { Pass, Swallow, ReplayHeldThenKey }
    private enum Phase { Idle, Held, Combo }

    private readonly int _key;
    private Phase _phase;

    /// <summary>Клавиша, за которой следит логика.</summary>
    public int Key => _key;

    public PushToTalkLogic(int key = VkRMenu) => _key = key;

    public event Action? Pressed, Released, Cancelled;

    /// <summary>Клавиша зажата сейчас (после <see cref="Pressed"/> и до отпускания или отмены).</summary>

    /// <param name="injected">Событие создала программа (в том числе проигрывание сочетания): его не трогаем.</param>
    public Decision OnKey(int vk, bool down, bool injected)
    {
        if (injected) return Decision.Pass;

        if (vk == _key)
        {
            if (down)
            {
                switch (_phase)
                {
                    case Phase.Idle: _phase = Phase.Held; Pressed?.Invoke(); return Decision.Swallow;
                    case Phase.Held: return Decision.Swallow; // автоповтор зажатой клавиши
                    default: return Decision.Pass; // сочетание уже проиграно системе: дальше как обычно
                }
            }
            switch (_phase)
            {
                case Phase.Held: _phase = Phase.Idle; Released?.Invoke(); return Decision.Swallow;
                case Phase.Combo: _phase = Phase.Idle; return Decision.Pass;
                default: return Decision.Pass;
            }
        }

        if (down && _phase == Phase.Held)
        {
            _phase = Phase.Combo;
            Cancelled?.Invoke();
            return Decision.ReplayHeldThenKey;
        }
        return Decision.Pass;
    }
}

/// <summary>
/// Низкоуровневый перехват клавиатуры (WH_KEYBOARD_LL) на отдельном потоке: права администратора не нужны, текст нажатых клавиш не читается и не хранится —
/// смотрится только, нажата ли «клавиша диктовки» и нажато ли что-нибудь ещё вместе с ней. События приходят на потоке перехватчика.
/// </summary>
internal sealed class PushToTalkHook : IDisposable
{
    public PushToTalkLogic Logic { get; }

    private Thread? _thread;
    private uint _threadId;
    private LowLevelProc? _proc;
    private IntPtr _hook;

    public PushToTalkHook(PushToTalkLogic logic) => Logic = logic;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Ostrov push-to-talk" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(2000);
    }

    public void Dispose()
    {
        var t = _thread;
        _thread = null;
        if (t == null) return;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        t.Join(1000);
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _proc = Callback;
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(null), 0);
        ready.Set();
        if (_hook == IntPtr.Zero) return;
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var msg = (int)wParam;
                var down = msg is 0x0100 or 0x0104;
                var up = msg is 0x0101 or 0x0105;
                if (down || up)
                {
                    var data = Marshal.PtrToStructure<KbdHookStruct>(lParam);
                    var injected = (data.flags & 0x10) != 0;
                    // Правый Alt на этой системе приходит вместе с «лишним» левым Ctrl (код сканирования 0x21D, как у AltGr). Alt мы съедаем,
                    // а этот Ctrl оставался бы зажатым на всё время диктовки: буквы превращались бы в Ctrl+буква (голосовой ввод и панель
                    // команд в VS Code), пробел пропадал. Поэтому служебный Ctrl съедаем тоже, и нажатие, и отпускание.
                    if (Logic.Key == PushToTalkLogic.VkRMenu && data.vkCode == 0xA2 && data.scanCode == 0x21D) return (IntPtr)1;
                    switch (Logic.OnKey((int)data.vkCode, down, injected))
                    {
                        case PushToTalkLogic.Decision.Swallow:
                            return (IntPtr)1;
                        case PushToTalkLogic.Decision.ReplayHeldThenKey:
                            Replay(data);
                            return (IntPtr)1;
                    }
                }
            }
            catch
            {
                // Перехватчик не должен ронять ввод: при любой ошибке клавиша идёт дальше как обычно.
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // Клавиша диктовки (нажатие) и сразу за ней нажатая другая: система видит их в том же порядке, что нажал пользователь.
    private void Replay(KbdHookStruct other)
    {
        var inputs = new Input[2];
        var held = Logic.Key;
        inputs[0] = Key((ushort)held, (ushort)MapVirtualKey((uint)held, 0), extended: DictationKeys.IsExtended(held));
        inputs[1] = Key((ushort)other.vkCode, (ushort)other.scanCode, (other.flags & 0x01) != 0);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(ushort vk, ushort scan, bool extended) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KeybdInput { wVk = vk, wScan = scan, dwFlags = extended ? 0x0001u : 0u } }
    };

    // ---------- описания системных вызовов ----------

    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdHookStruct { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
