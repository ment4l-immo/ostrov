using System.Windows;

namespace Ostrov.Services;

/// <summary>
/// Связь функций с окном острова без прямой зависимости друг от друга.
/// Функция просит «привлечь внимание» (раскрыть остров на своей вкладке) или закрыть остров.
/// </summary>
public static class IslandBus
{
    /// <summary>Раскрыть остров на вкладке с указанным Id и держать раскрытым до клика.</summary>
    public static event Action<string>? AttentionRequested;

    /// <summary>Пользователь «принял» уведомление: остров сворачивается, функции сбрасывают своё состояние уведомления.</summary>
    public static event Action? Dismissed;

    /// <summary>Функции понадобилась другая высота острова (например, раскрылись подробности). Высоту читают из <c>IIslandModule.ExpandedHeight</c>.</summary>
    public static event Action<string>? HeightChanged;

    public static void RequestHeight(string moduleId) => HeightChanged?.Invoke(moduleId);

    /// <summary>Не сворачивать остров, пока открыто системное окно (например, выбор папки): true — держать, false — отпустить.</summary>
    public static event Action<bool>? HoldChanged;

    public static void Hold(bool on) => HoldChanged?.Invoke(on);

    /// <summary>Открыт мост с телефона: остров держится раскрытым (передача не должна обрываться от клика в другое окно), карточки Alt+Q и Claude его не закрывают.</summary>
    public static bool BridgeOpen { get; set; }

    /// <summary>Просто свернуть остров (например, после открытия проекта), без «принятия» уведомлений.</summary>
    public static event Action? CollapseRequested;

    public static void RequestCollapse() => CollapseRequested?.Invoke();

    /// <summary>Скрыть (false) или вернуть (true) полосу вкладок внизу: вкладка занимает всю высоту (режим текста песни в плеере).</summary>
    public static event Action<bool>? TabsVisibleChanged;

    public static void SetTabsVisible(bool visible) => TabsVisibleChanged?.Invoke(visible);

    /// <summary>Не сворачивать остров по таймеру «5 секунд без мыши» (пока читают текст песни); клик мимо по-прежнему сворачивает.</summary>
    public static event Action<bool>? AutoCollapseSuspendedChanged;

    public static void SuspendAutoCollapse(bool on) => AutoCollapseSuspendedChanged?.Invoke(on);

    /// <summary>Остров начинает раскрываться (до анимации): вкладки могут вернуть обычный вид без видимого скачка.</summary>
    public static event Action? Expanding;

    public static void RaiseExpanding() => Expanding?.Invoke();

    public static void RequestAttention(string moduleId) => AttentionRequested?.Invoke(moduleId);

    /// <summary>Открыть остров на нужной вкладке (команда «открой погоду» из Alt+Q): обычное раскрытие, не «закреплённое».</summary>
    public static event Action<string>? TabRequested;
    public static void RequestTab(string moduleId) => TabRequested?.Invoke(moduleId);
    public static void Dismiss() => Dismissed?.Invoke();

    /// <summary>
    /// Показать поверх вкладок свою карточку (например, запрос разрешения Claude): остров раскроется до заданного размера
    /// и закрепится (не сворачивается сам и от клика мимо), пока карточку не снимут. Фокус у других окон не отбирается.
    /// </summary>
    public static event Action<FrameworkElement, double, double>? OverlayShown;

    /// <summary>Убрать карточку: остров свернётся, а вкладки вернутся как были.</summary>
    public static event Action? OverlayCleared;

    /// <summary>Сейчас поверх вкладок показана карточка (запрос Claude или поиск). Ставит окно острова.</summary>
    public static bool OverlayActive { get; set; }

    public static void ShowOverlay(FrameworkElement view, double width, double height) => OverlayShown?.Invoke(view, width, height);
    public static void ClearOverlay() => OverlayCleared?.Invoke();

    /// <summary>Программа в фокусе (имя процесса без .exe); окно самого острова не в счёт. Ставит окно острова.</summary>
    public static string? ForegroundApp { get; private set; }

    public static void SetForegroundApp(string? processName)
    {
        ForegroundApp = processName;
        ActivityCenter.SetForeground(processName);
    }

    /// <summary>Пользователь кликом закрыл индикатор с <c>dismissOnClick</c>.</summary>
    public static event Action? ActivityDismissed;

    /// <summary>
    /// Показать индикатор рендера After Effects в свёрнутом острове (остров слегка вырастает до заданного размера).
    /// Остальные активности (таймер, музыка…) заводятся напрямую через <see cref="ActivityCenter"/>.
    /// <paramref name="dismissOnClick"/> = true: индикатор висит до клика (клик закрывает его, не раскрывая остров,
    /// и срабатывает <see cref="ActivityDismissed"/>). Повторный вызов заменяет индикатор.
    /// </summary>
    public static void ShowActivity(FrameworkElement view, double width, double height, bool dismissOnClick, FrameworkElement? compact = null,
        FrameworkElement? bubble = null, FrameworkElement? wide = null, double wideWidth = 0, double wideHeight = 0) =>
        ActivityCenter.Set(new ActivityEntry
        {
            Id = "render", Priority = 100, Pill = view, PillWidth = width, PillHeight = height, DismissOnClick = dismissOnClick, Compact = compact,
            Bubble = bubble, Wide = wide, WideWidth = wideWidth, WideHeight = wideHeight,
            HideWhenForeground = ActivityApps.AfterEffects // рендер не показывается, пока в фокусе After Effects (М-014)
        });

    /// <summary>Убрать индикатор рендера: остров плавно возвращается к обычному размеру (или показывает другую активность).</summary>
    public static void ClearActivity() => ActivityCenter.Remove("render");

    public static void NotifyActivityDismissed() => ActivityDismissed?.Invoke();

    /// <summary>Файлы перетащили на остров (из проводника и т. п.). Принимает вкладка «Буфер» (полка).</summary>
    public static event Action<string[]>? FilesDropped;

    public static void DropFiles(string[] paths) => FilesDropped?.Invoke(paths);

    /// <summary>
    /// Окно, которому сейчас принадлежат бросаемые на остров файлы (мост с телефоном): если задано, остров не открывает «Буфер» и не кладёт
    /// файлы на полку, а отдаёт их этому обработчику (true — забрал).
    /// </summary>
    public static Func<string[], bool>? FileDropOverride { get; set; }

    /// <summary>Над островом начали (true) или закончили (false) тянуть файлы: вкладка показывает подсказку.</summary>
    public static event Action<bool>? FileDragChanged;

    public static void SetFileDrag(bool on) => FileDragChanged?.Invoke(on);

    /// <summary>Только что скопированный текст — секрет (например, ключ API): вкладка «Буфер» должна его забыть (убрать из истории и из буфера обмена).</summary>
    public static event Action<string>? SecretCopied;

    public static void ForgetClipboardText(string text) => SecretCopied?.Invoke(text);

    /// <summary>
    /// Остров сейчас в «сжатом режиме»: идёт полноэкранное видео, остров не раскрывается и показывает только нотч с полоской
    /// (таймер, секундомер, рендер AE). Источники сигналов в этом режиме не раскрывают остров и не играют мелодий. Ставит окно острова.
    /// </summary>
    public static bool CompactMode { get; private set; }

    public static event Action<bool>? CompactModeChanged;

    public static void SetCompact(bool compact)
    {
        if (CompactMode == compact) return;
        CompactMode = compact;
        CompactModeChanged?.Invoke(compact);
    }

    /// <summary>Пользователь изменил настройки в окне настроек: окно острова и службы перечитывают то, что им нужно.</summary>
    public static event Action? SettingsChanged;

    public static void NotifySettingsChanged() => SettingsChanged?.Invoke();

    /// <summary>Виден ли остров на экране (не скрыт через трей или полноэкранным приложением). Ставит окно острова.</summary>
    public static bool IsVisible { get; private set; }

    public static event Action<bool>? VisibilityChanged;

    public static void SetVisible(bool visible)
    {
        IsVisible = visible;
        VisibilityChanged?.Invoke(visible);
    }
}
