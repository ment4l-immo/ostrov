using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Dictation;

/// <summary>
/// Голосовая диктовка: зажал правый Alt — говоришь, отпустил — закончил. Остров слушает микрофон, пока клавиша зажата, звук по кусочкам уходит на
/// распознавание (<see cref="RealtimeTranscriber"/>), а готовый текст тут же печатается в окно, где стоит курсор (<see cref="TextTyper"/>).
/// На свёрнутом острове плашка с волной голоса; в полноэкранной игре остров скрыт, плашки нет, но диктовка работает.
/// Служба живёт на потоке интерфейса, клавиатурный перехватчик, микрофон и сеть шлют ей события со своих потоков.
/// </summary>
internal sealed class DictationService : IDisposable
{
    private const string EntryId = "dictation";
    private const int Priority = 130; // выше Claude: пока зажата клавиша, ничто не должно перекрывать знак записи
    private static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(40);    // совсем короткое касание записью не считается; задержка мала, чтобы не терять начало слова
    private static readonly TimeSpan MinSpeech = TimeSpan.FromMilliseconds(400);  // короче — случайное нажатие, ничего не отправляем
    private static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(2);      // дольше запись сама заканчивается и допечатывается

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly MicCapture _mic = new();
    private readonly DispatcherTimer _arm = new();
    private readonly DispatcherTimer _hideTimer = new();
    private readonly DispatcherTimer _maxTimer = new();
    private PushToTalkHook? _hook;
    private int _hookKey;
    private bool _paused; // пока в настройках ждут новую клавишу, перехватчик снят
    private DictationPill? _pill;
    private RealtimeTranscriber? _transcriber;
    private Action<byte[]>? _feed;
    private int _session;           // номер записи: опоздавшие события прежней записи игнорируются
    private bool _recording;
    private DateTime _startedAt;
    private bool _typingFailedShown;
    // Пока клавиша диктовки зажата, система считает зажатым Alt (или другую клавишу): пробел превратился бы в Alt+Пробел (меню окна) и пропал.
    // Поэтому распознанное копится и печатается сразу после отпускания.
    private readonly object _typeLock = new();
    private readonly System.Text.StringBuilder _heldText = new();
    private readonly System.Text.StringBuilder _unprinted = new(); // то, что не удалось напечатать в эту запись
    private bool _keyDown;
    // Сведения для журнала диагностики (без текста речи).
    private long _bytes;
    private float _peak;
    private int _typedChars, _typeCalls, _typeFails;
    private string _typedInto = "";
    private string _startApp = "";
    private System.IO.MemoryStream? _wav;

    /// <summary>Работающая служба (для настроек: на время выбора клавиши перехватчик снимается).</summary>
    public static DictationService? Current { get; private set; }

    public DictationService()
    {
        Current = this;
        _arm.Tick += (_, _) => { _arm.Stop(); BeginRecording(); };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); RemoveEntry(); };
        _maxTimer.Tick += (_, _) => { _maxTimer.Stop(); OnReleased(); };
        _mic.Level += level => { _pill?.SetLevel(level); if (level > _peak) _peak = level; };
    }

    public void Start()
    {
        IslandBus.SettingsChanged += Reload;
        Reload();
    }

    /// <summary>Снять перехватчик (ждём в настройках новую клавишу) и вернуть.</summary>
    public void Pause() { _paused = true; StopHook(); Abort(); }
    public void Resume() { _paused = false; Reload(); }

    public void Dispose()
    {
        if (Current == this) Current = null;
        IslandBus.SettingsChanged -= Reload;
        StopHook();
        Abort();
    }

    // Включена ли диктовка: ставим или снимаем перехватчик клавиатуры.
    private void Reload()
    {
        var settings = AppSettings.Load();
        var enabled = settings.DictationEnabled && !_paused;
        var key = DictationKeys.IsAllowed(settings.DictationKey) ? settings.DictationKey : DictationKeys.Default;
        if (enabled && _hook != null && _hookKey != key) { StopHook(); Abort(); } // клавишу сменили
        if (enabled && _hook == null)
        {
            _hookKey = key;
            var logic = new PushToTalkLogic(key);
            logic.Pressed += () => _ui.BeginInvoke(OnPressed);
            logic.Released += () => _ui.BeginInvoke(OnReleased);
            logic.Cancelled += () => _ui.BeginInvoke(Abort);
            _hook = new PushToTalkHook(logic);
            _hook.Start();
        }
        else if (!enabled)
        {
            StopHook();
            Abort();
        }
    }

    private void StopHook()
    {
        _hook?.Dispose();
        _hook = null;
    }

    private void OnPressed()
    {
        _hideTimer.Stop();
        if (_pill != null && !_recording) RemoveEntry(); // на экране ещё висела прошлая ошибка или «Печатаю»
        _arm.Interval = ArmDelay;
        _arm.Start();
    }

    private void OnReleased()
    {
        _maxTimer.Stop();
        if (_arm.IsEnabled) { _arm.Stop(); return; } // нажали и сразу отпустили: просто касание
        if (!_recording) return;
        _recording = false;
        FlushHeld(print: true);
        var session = _session;
        _mic.Stop();
        DetachMic();

        var transcriber = _transcriber;
        if (transcriber == null) { RemoveEntry(); return; }
        if (DateTime.UtcNow - _startedAt < MinSpeech)
        {
            transcriber.Abort(); // случайное нажатие: ничего не отправляем и не печатаем
            _transcriber = null;
            RemoveEntry();
            return;
        }

        _pill?.SetPhase(DictationPhase.Finishing);
        _ = FinishAsync(transcriber, session);
    }

    // Ждём, пока допечатается всё распознанное; потом плашка уходит (если за это время не случилась ошибка и не началась новая запись).
    private async Task FinishAsync(RealtimeTranscriber transcriber, int session)
    {
        try { await transcriber.FinishAsync(); }
        catch { /* сбой уже показан через Failed */ }
        LogSession(transcriber, "готово");
        _ = _ui.BeginInvoke(() =>
        {
            if (session != _session || _hideTimer.IsEnabled) return; // новая запись или ошибка уже на экране
            if (_transcriber == transcriber) _transcriber = null;
            RemoveEntry();
        });
    }

    // Отмена записи (нажали сочетание, диктовку выключили, остров закрывается).
    private void Abort()
    {
        _arm.Stop();
        _maxTimer.Stop();
        if (!_recording) return;
        _recording = false;
        FlushHeld(print: false);
        _session++;
        _mic.Stop();
        DetachMic();
        if (_transcriber != null) LogSession(_transcriber, "отменено");
        _transcriber?.Abort();
        _transcriber = null;
        RemoveEntry();
    }

    private void BeginRecording()
    {
        var session = ++_session;
        _typingFailedShown = false;
        lock (_typeLock) _unprinted.Clear();
        _bytes = 0; _peak = 0; _typedChars = 0; _typeCalls = 0; _typeFails = 0; _typedInto = "";
        _startApp = DictationLog.ForegroundApp();
        _wav = DictationLog.DumpAudio ? new System.IO.MemoryStream() : null;
        _pill = new DictationPill();
        var strip = new CompactStrip(Hues.Ink);
        strip.ShowDots(); // точки, а не полоска: чтобы диктовку не спутать с рендером и Claude
        ActivityCenter.Set(new ActivityEntry
        {
            Id = EntryId, Priority = Priority,
            Pill = _pill, PillWidth = DictationPill.ViewWidth, PillHeight = DictationPill.ViewHeight,
            Compact = strip, TabMark = false
        });

        // Без ключа OpenAI распознавать нечем: говорим сразу, а не после того, как человек договорит.
        if (!RealtimeTranscriber.HasKey())
        {
            Fail("Нужен ключ OpenAI: вставь его в настройках");
            return;
        }

        _recording = true;
        lock (_typeLock) { _keyDown = true; _heldText.Clear(); }
        _startedAt = DateTime.UtcNow;
        var transcriber = new RealtimeTranscriber(AppSettings.Load().DictationModel);
        _transcriber = transcriber;
        transcriber.Text += text =>
        {
            lock (_typeLock)
            {
                if (_keyDown) { _heldText.Append(text); return; } // клавиша ещё зажата: напечатаем после отпускания
            }
            TypeNow(text, session);
        };
        transcriber.Failed += message => _ui.BeginInvoke(() => { if (session == _session) Fail(message); });
        _feed = chunk => { Interlocked.Add(ref _bytes, chunk.Length); _wav?.Write(chunk); transcriber.Feed(chunk); };
        _mic.Chunk += _feed;
        transcriber.Start();

        void OnStarted(bool ok)
        {
            _mic.Started -= OnStarted;
            if (ok) return;
            _ui.BeginInvoke(() =>
            {
                if (session != _session) return;
                Fail(_mic.Error ?? "Микрофон недоступен");
            });
        }
        _mic.Started += OnStarted;
        _mic.Start();

        _maxTimer.Interval = MaxRecording;
        _maxTimer.Start();
    }

    // Клавиша отпущена (или запись прервана): печатаем накопленное или выбрасываем его.
    private void FlushHeld(bool print)
    {
        string text;
        lock (_typeLock)
        {
            _keyDown = false;
            text = _heldText.ToString();
            _heldText.Clear();
        }
        if (print && text.Length > 0) TypeNow(text, _session);
    }

    private void TypeNow(string text, int session)
    {
        var ok = TextTyper.Type(text, out var typed);
        _typeCalls++; _typedChars += text.Length;
        if (_typedInto.Length == 0) _typedInto = DictationLog.ForegroundApp();
        if (!ok)
        {
            _typeFails++;
            // Печать не вышла (окно от администратора и т. п.): продиктованное не пропадает, а ложится в буфер обмена.
            string all;
            // Уже напечатанное начало в буфер не кладём, иначе оно задвоится.
            var rest = typed > 0 && typed < text.Length ? text[typed..] : text;
            lock (_typeLock) { _unprinted.Append(rest); all = _unprinted.ToString(); }
            _ui.BeginInvoke(() =>
            {
                try { System.Windows.Clipboard.SetText(all); } catch { /* буфер занят: попробует следующая фраза */ }
                if (!_typingFailedShown)
                {
                    _typingFailedShown = true;
                    if (session == _session) Fail("Не напечаталось: текст в буфере, нажми Ctrl+V");
                }
            });
        }
    }

    private void DetachMic()
    {
        if (_feed != null) _mic.Chunk -= _feed;
        _feed = null;
    }

    // Ошибка: плашка на несколько секунд говорит причину и уходит; запись и распознавание останавливаются.
    private void Fail(string message)
    {
        _recording = false;
        FlushHeld(print: false);
        _maxTimer.Stop();
        _mic.Stop();
        DetachMic();
        if (_transcriber != null) LogSession(_transcriber, "ошибка: " + message);
        else DictationLog.Line($"ошибка: {message}");
        _transcriber?.Abort();
        _transcriber = null;
        _pill?.SetPhase(DictationPhase.Error, message);
        _hideTimer.Interval = TimeSpan.FromSeconds(3.5);
        _hideTimer.Start();
    }

    // Одна строка в журнал: чем закончилась запись и что успело произойти. Текст речи не пишется.
    private void LogSession(RealtimeTranscriber transcriber, string result)
    {
        var seconds = (DateTime.UtcNow - _startedAt).TotalSeconds;
        DictationLog.Line($"{result}; запись {seconds:F1} с, звука {_bytes / 48000.0:F1} с, пик {_peak:F2}; фраз вернулось {transcriber.PhrasesDone}; " +
                          $"напечатано {_typedChars} знаков за {_typeCalls} раз(а), сбоев печати {_typeFails}; фокус при начале: {_startApp}, при печати: {(_typedInto.Length > 0 ? _typedInto : "—")}");
        if (_wav is { Length: > 0 }) { DictationLog.SaveWav(_wav.ToArray()); _wav = null; }
    }

    private void RemoveEntry()
    {
        _pill = null;
        ActivityCenter.Remove(EntryId);
    }
}
