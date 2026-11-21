using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Flash;

/// <summary>
/// Короткие подсказки на острове (М-020): язык ввода, Caps Lock и Num Lock, громкость Windows, микрофон. Каждые 150 мс сверяются с прошлым
/// состоянием и, если что-то изменилось, показывают вспышку 1,6 с (<see cref="ActivityCenter.ShowFlash"/>). Права администратора не нужны,
/// никаких глобальных перехватчиков клавиш: только опрос состояния.
/// Язык: смотрим раскладку окна, которое в фокусе, и мигаем, только если она сменилась в том же окне (при переходе между программами,
/// у которых своя раскладка, подсказки нет). Громкость и микрофон: устройство по умолчанию; переключение устройства вспышкой не считается.
/// Показывает вспышку или нет (игра, видео, нотч, раскрытый остров), решает окно острова.
/// </summary>
internal sealed class SystemHints : IDisposable
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan Reopen = TimeSpan.FromSeconds(3);

    private System.Threading.Timer? _timer;
    private readonly object _gate = new();
    private int _busy;
    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    private readonly FlashPill _pill = new();

    private IntPtr _window;
    private int _lang = -1;
    private bool? _caps, _num;

    private EndpointAudio? _output, _input;
    private float _volume = -1;
    private bool _outputMuted, _inputMuted;
    private bool _outputKnown, _inputKnown;
    private DateTime _reopenAt;

    // Опрос идёт в фоновом потоке (на потоке интерфейса он отнимал ≈ 3,5 мс каждые 150 мс и давал мелкие подтормаживания);
    // на поток интерфейса уходит только показ вспышки.
    public void Start()
    {
        _timer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return;
            try { lock (_gate) Tick(); }
            finally { Volatile.Write(ref _busy, 0); }
        }, null, 150, 150);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            _output?.Dispose();
            _input?.Dispose();
        }
    }

    private void Tick()
    {
        try
        {
            CheckLanguage();
            CheckLocks();
            CheckAudio();
        }
        catch
        {
            // Подсказки не должны ронять остров из-за неожиданного ответа системы.
        }
    }

    // ───── Язык ввода ─────

    private void CheckLanguage()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        var thread = GetWindowThreadProcessId(window, out _);
        var lang = (int)(GetKeyboardLayout(thread).ToInt64() & 0xFFFF);
        if (window == _window && _lang != -1 && lang != _lang) Flash(p => p.ShowLanguage(LanguageCode(lang)));
        _window = window;
        _lang = lang;
    }

    private static string LanguageCode(int langId)
    {
        try { return CultureInfo.GetCultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch { return "?"; }
    }

    // ───── Caps Lock и Num Lock ─────

    private void CheckLocks()
    {
        var caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        var num = (GetKeyState(VK_NUMLOCK) & 1) != 0;
        if (_caps is { } oldCaps && oldCaps != caps) Flash(p => p.ShowLock("CAPS", caps));
        else if (_num is { } oldNum && oldNum != num) Flash(p => p.ShowLock("NUM", num));
        _caps = caps;
        _num = num;
    }

    // ───── Громкость и микрофон ─────

    private void CheckAudio()
    {
        var now = DateTime.UtcNow;
        if (now >= _reopenAt)
        {
            _reopenAt = now + Reopen;
            Reacquire(capture: false, ref _output, ref _outputKnown);
            Reacquire(capture: true, ref _input, ref _inputKnown);
        }

        if (_output != null && _output.TryRead(out var volume, out var muted))
        {
            // Меньше половины процента (шум округления) не считаем: вспышка только на настоящее движение ползунка или выключение звука.
            if (_outputKnown && (Math.Abs(volume - _volume) >= 0.005f || muted != _outputMuted)) Flash(p => p.ShowVolume(volume, muted));
            _volume = volume;
            _outputMuted = muted;
            _outputKnown = true;
        }

        if (_input != null && _input.TryRead(out _, out var micMuted))
        {
            if (_inputKnown && micMuted != _inputMuted) Flash(p => p.ShowMicrophone(micMuted));
            _inputMuted = micMuted;
            _inputKnown = true;
        }
    }

    // Устройство по умолчанию могли сменить (наушники): тогда начинаем следить за новым и вспышку на скачок громкости не даём.
    private static void Reacquire(bool capture, ref EndpointAudio? current, ref bool known)
    {
        var next = EndpointAudio.OpenDefault(capture);
        if (next == null)
        {
            current?.Dispose();
            current = null;
            known = false;
            return;
        }
        if (current != null && current.Id == next.Id)
        {
            next.Dispose();
            return;
        }
        current?.Dispose();
        current = next;
        known = false;
    }

    private void Flash(Action<FlashPill> show)
    {
        _ui.BeginInvoke(() =>
        {
            show(_pill);
            ActivityCenter.ShowFlash(_pill, FlashPill.ViewWidth, FlashPill.ViewHeight, ShowFor);
        });
    }

    private const int VK_CAPITAL = 0x14, VK_NUMLOCK = 0x90;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
}
