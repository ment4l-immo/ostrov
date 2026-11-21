using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Ostrov.Services;

namespace Ostrov.Search;

/// <summary>
/// Быстрый вопрос: по горячей клавише (Alt+Q) на острове появляется карточка со строкой ввода; Enter — ответ ИИ (OpenAI)
/// прямо на острове, Ctrl+Enter — поиск в Google в браузере по умолчанию. Клавиша занята только пока остров виден:
/// в полноэкранных играх и когда остров спрятан из трея она свободна для других программ.
/// </summary>
internal sealed class SearchService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 0x4F51;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Func<string, bool> _open;
    private HwndSource? _source;
    private SearchCard? _card;
    private SearchHotkey _hotkey = SearchHotkey.Default;

    /// <summary>Служба, запущенная в этом процессе (окно настроек меняет через неё сочетание клавиш).</summary>
    internal static SearchService? Current { get; private set; }

    /// <summary>Сочетание клавиш сейчас (из настроек; по умолчанию Alt+Q).</summary>
    internal SearchHotkey Hotkey => _hotkey;

    /// <summary>Горячая клавиша сейчас зарегистрирована (false — занята другой программой или остров скрыт).</summary>
    public bool HotkeyRegistered { get; private set; }

    /// <param name="open">Как открыть адрес (по умолчанию — браузер по умолчанию). Подменяется для проверки.</param>
    public SearchService(Func<string, bool>? open = null)
    {
        _open = open ?? OpenInBrowser;
        _hotkey = SearchHotkey.Parse(AppSettings.Load().SearchHotkey) ?? SearchHotkey.Default;
        Current = this;
    }

    /// <summary>Адрес поиска Google (для «искать в браузере»).</summary>
    public static string BuildUrl(string query) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString(query.Trim());

    private static bool OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    internal IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public void Start()
    {
        if (_source != null) return;
        // Окно только для сообщений: на экране его нет.
        _source = new HwndSource(0, 0, 0, 0, 0, "OstrovHotkey", HwndMessage);
        _source.AddHook(WndProc);
        IslandBus.VisibilityChanged += OnVisibilityChanged;
        if (IslandBus.IsVisible) Register();
    }

    private void OnVisibilityChanged(bool visible)
    {
        if (visible) Register();
        else Unregister();
    }

    private void Register()
    {
        if (HotkeyRegistered || _source == null) return;
        HotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, _hotkey.Modifiers | MOD_NOREPEAT, _hotkey.Vk);
    }

    /// <summary>Снять клавишу на время, пока окно настроек ждёт новое сочетание (иначе текущее сочетание открыло бы карточку).</summary>
    internal void PauseHotkey() => Unregister();

    /// <summary>Вернуть клавишу после <see cref="PauseHotkey"/> (если остров виден).</summary>
    internal void ResumeHotkey()
    {
        if (IslandBus.IsVisible) Register();
    }

    /// <summary>
    /// Сменить сочетание. Пробуем сразу занять новое: если его держит другая программа, остаётся прежнее и возвращается false.
    /// Остров должен быть виден (в окне настроек так и есть). Успех записывается в настройки.
    /// </summary>
    internal bool TrySetHotkey(SearchHotkey next)
    {
        if (_source == null) return false;
        if (next == _hotkey) return true;
        var wasRegistered = HotkeyRegistered;
        Unregister();
        var old = _hotkey;
        _hotkey = next;
        Register();
        if (!HotkeyRegistered)
        {
            _hotkey = old; // новое занято: возвращаем прежнее
            if (wasRegistered) Register();
            return false;
        }
        var settings = AppSettings.Load();
        settings.SearchHotkey = next.Format();
        settings.Save();
        return true;
    }

    private void Unregister()
    {
        if (!HotkeyRegistered || _source == null) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        HotkeyRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            OnHotkey();
        }
        return IntPtr.Zero;
    }

    private void OnHotkey()
    {
        if (!IslandBus.IsVisible) return;
        // Поиск уже открыт: та же клавиша закрывает его.
        if (_card is { IsClosed: false })
        {
            _card.Close();
            return;
        }
        // Сейчас на острове запрос Claude: его не перекрываем.
        if (IslandBus.OverlayActive) return;

        _card = new SearchCard(_open);
        IslandBus.ShowOverlay(_card, SearchCard.CardWidth, SearchCard.IdleHeight);
    }

    public void Dispose()
    {
        IslandBus.VisibilityChanged -= OnVisibilityChanged;
        Unregister();
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
