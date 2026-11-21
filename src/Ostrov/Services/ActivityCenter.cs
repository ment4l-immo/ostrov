using System.Windows;

namespace Ostrov.Services;

/// <summary>
/// Одна пассивная активность в свёрнутом острове (таймер, секундомер, рендер After Effects, музыка…).
/// Источник создаёт запись и заменяет её при каждом изменении (по <see cref="Id"/>).
/// </summary>
public sealed class ActivityEntry
{
    public required string Id { get; init; }

    /// <summary>Чем больше, тем важнее: самая важная становится плашкой, следующая (с кружком) — кружком рядом.</summary>
    public required int Priority { get; init; }

    /// <summary>Плашка: остров слегка меняется до этого размера. Содержимое обновляет сам источник.</summary>
    public required FrameworkElement Pill { get; init; }
    public required double PillWidth { get; init; }
    public required double PillHeight { get; init; }

    /// <summary>Содержимое кружка (<see cref="ActivityCenter.BubbleSize"/> × <see cref="ActivityCenter.BubbleSize"/>), если активность может быть второй. Null — не может.</summary>
    public FrameworkElement? Bubble { get; init; }

    /// <summary>Вкладка, которую открывает клик по плашке или кружку (например, "time"). Null — остров раскрывается на последней вкладке.</summary>
    public string? ModuleId { get; init; }

    /// <summary>Считается ли эта активность «идущей» для метки-точки под значком вкладки. У постоянной плашки пустого острова (время и погода) выключено: иначе точка у «Времени» горела бы всегда.</summary>
    public bool TabMark { get; init; } = true;

    /// <summary>Плашка висит до клика (клик закрывает её, не раскрывая остров; источник узнаёт через <see cref="IslandBus.ActivityDismissed"/>).</summary>
    public bool DismissOnClick { get; init; }

    /// <summary>
    /// Содержимое «нотча» (<see cref="ActivityCenter.CompactWidth"/> × <see cref="ActivityCenter.CompactHeight"/>) — сильно вжатого острова с полоской,
    /// который показывается в полноэкранном видео. Null — в видео эта активность не показывается (например, музыка).
    /// </summary>
    public FrameworkElement? Compact { get; init; }

    /// <summary>Размер нотча этой активности: обычно <see cref="ActivityCenter.CompactWidth"/> × <see cref="ActivityCenter.CompactHeight"/>, но знак вроде «Время вышло» крупнее.</summary>
    public double CompactWidth { get; init; } = ActivityCenter.CompactWidth;
    public double CompactHeight { get; init; } = ActivityCenter.CompactHeight;

    /// <summary>Вызывается, когда пользователь кликом закрыл эту активность (<see cref="DismissOnClick"/>).</summary>
    public Action? OnDismissed { get; init; }

    /// <summary>Мышь вошла на свёрнутый остров (true) или ушла (false), пока эта активность главная: например, чтобы показать стрелку.</summary>
    public Action<bool>? OnHover { get; init; }

    /// <summary>
    /// Клик по плашке (точка в координатах плашки): true — активность клик обработала (например, нажали на стрелку),
    /// остров при этом не раскрывается.
    /// </summary>
    public Func<System.Windows.Point, bool>? OnPillClick { get; init; }

    /// <summary>
    /// Программы (имена процессов без .exe), в которых эта активность не показывается, пока они в фокусе (М-014): Claude прячется в VS Code,
    /// рендер в After Effects. Запись при этом не теряется: когда пользователь выйдет из программы, плашка («готово» тоже) появится.
    /// </summary>
    public IReadOnlyList<string>? HideWhenForeground { get; init; }

    /// <summary>
    /// Расширенный вид («то же, но полнее»): остров растёт по правому клику на плашке, пока курсор над ним (М-017).
    /// Показывает те же данные, что плашка, без новой информации, но без обрезки и покрупнее. Null — расширять нечем.
    /// </summary>
    public FrameworkElement? Wide { get; init; }
    public double WideWidth { get; init; }
    public double WideHeight { get; init; }
}

/// <summary>
/// Список активностей свёрнутого острова. Показывает как у Apple: самая важная — плашкой, две следующие по важности
/// (если у них есть кружок) — маленькими кружками по бокам: вторая справа, третья слева. Остальные ждут. Работает на потоке интерфейса.
/// Приоритеты по договорённости: Claude (110) → рендер After Effects (100) → таймер (50) → секундомер (40) → музыка (10).
/// Клик по кружку меняет его местами с плашкой (<see cref="Promote"/>). Такой выбор держится, пока не изменится состав активностей
/// (появилась новая или одна закончилась) и пока у какой-нибудь не случится событие («готово», «ошибка»): тогда снова по приоритету.
/// </summary>
public static class ActivityCenter
{
    /// <summary>Диаметр кружка; центр кружка стоит на одной линии со значками плашки (1,5 px выше центра острова).</summary>
    public const double BubbleSize = 30;

    /// <summary>Размер «нотча» в полноэкранном видео: та же плашка у верхнего края, но сильно вжатая по высоте, внутри полоска.</summary>
    public const double CompactWidth = 150, CompactHeight = 14;

    /// <summary>Скругление нижних углов нотча (не больше <see cref="CompactHeight"/>; 7 было «острым», 14 — полная дуга).</summary>
    public const double CompactRadius = 12;

    private static readonly List<ActivityEntry> Entries = [];

    public static ActivityEntry? Main { get; private set; }

    /// <summary>Кружок справа от острова.</summary>
    public static ActivityEntry? Secondary { get; private set; }

    /// <summary>Кружок слева от острова.</summary>
    public static ActivityEntry? Tertiary { get; private set; }

    /// <summary>Главная плашка сейчас в расширенном виде (правый клик, М-017).</summary>
    public static bool Widened { get; private set; }

    /// <summary>Короткая вспышка поверх главной плашки (М-020: язык ввода, Caps/Num Lock, громкость, микрофон): вид и размер.</summary>
    public sealed record FlashState(FrameworkElement View, double Width, double Height);

    /// <summary>Вспышка, которая идёт сейчас, или null. Показывает её окно острова (только свёрнутым, не нотчем), сама уходит через заданное время.</summary>
    public static FlashState? Flash { get; private set; }

    private static System.Windows.Threading.DispatcherTimer? _flashTimer;

    /// <summary>Показать вспышку на <paramref name="duration"/>; новая вспышка (или та же, обновлённая) продлевает показ. Вызывать на потоке интерфейса.</summary>
    public static void ShowFlash(FrameworkElement view, double width, double height, TimeSpan duration)
    {
        Flash = new FlashState(view, width, height);
        if (_flashTimer == null)
        {
            _flashTimer = new System.Windows.Threading.DispatcherTimer();
            _flashTimer.Tick += (_, _) =>
            {
                _flashTimer!.Stop();
                Flash = null;
                Changed?.Invoke();
            };
        }
        _flashTimer.Stop();
        _flashTimer.Interval = duration;
        _flashTimer.Start();
        Changed?.Invoke();
    }

    /// <summary>Программа в фокусе (имя процесса без .exe) или null.</summary>
    public static string? Foreground { get; private set; }

    /// <summary>Сменилась программа в фокусе: записи, которые в ней прячутся (<see cref="ActivityEntry.HideWhenForeground"/>), исчезают или возвращаются.</summary>
    public static void SetForeground(string? processName)
    {
        if (string.Equals(Foreground, processName, StringComparison.OrdinalIgnoreCase)) return;
        Foreground = processName;
        Recompute(reset: false);
    }

    private static bool IsHidden(ActivityEntry e) =>
        Foreground is { Length: > 0 } app && e.HideWhenForeground is { } hide && hide.Any(h => string.Equals(h, app, StringComparison.OrdinalIgnoreCase));

    // Ручной порядок после клика по кружку: идентификаторы по местам (плашка, кружок справа, кружок слева). null — по приоритету.
    private static List<string>? _manual;

    /// <summary>Самая важная активность, у которой есть нотч для полноэкранного видео (<see cref="ActivityEntry.Compact"/>).</summary>
    public static ActivityEntry? MainCompact { get; private set; }

    /// <summary>Изменился состав активностей (сменилась плашка или кружок, либо источник заменил запись).</summary>
    public static event Action? Changed;

    public static void Set(ActivityEntry entry)
    {
        var previous = Entries.FirstOrDefault(e => e.Id == entry.Id);
        Entries.RemoveAll(e => e.Id == entry.Id);
        Entries.Add(entry);
        // Новая активность или событие у прежней (появился знак «готово»/«ошибка», он висит до клика): ручной выбор сбрасывается.
        Recompute(reset: previous is null || (entry.DismissOnClick && !previous.DismissOnClick));
    }

    /// <summary>Есть ли сейчас активность, относящаяся к вкладке (для метки-точки под значком вкладки).</summary>
    public static bool HasForModule(string moduleId) => Entries.Any(e => e.ModuleId == moduleId && e.TabMark);

    public static void Remove(string id)
    {
        // Выбор пользователя сбрасывается, только если ушла активность, видимая на экране или участвовавшая в выборе:
        // невидимая четвёртая (например, музыка на паузе) его не трогает.
        var shown = Main?.Id == id || Secondary?.Id == id || Tertiary?.Id == id || (_manual?.Contains(id) ?? false);
        if (Entries.RemoveAll(e => e.Id == id) > 0) Recompute(reset: shown);
    }

    /// <summary>Клик по кружку: активность становится главной, прежняя главная встаёт на её место.</summary>
    public static void Promote(string id)
    {
        var visible = new[] { Main, Secondary, Tertiary }.Where(e => e != null).Select(e => e!.Id).ToList();
        var i = visible.IndexOf(id);
        if (i <= 0) return;
        (visible[0], visible[i]) = (visible[i], visible[0]);
        _manual = visible;
        Widened = false;
        Recompute(reset: false);
    }

    /// <summary>Расширить главную плашку (если у неё есть расширенный вид) или вернуть обычную.</summary>
    public static void SetWidened(bool on)
    {
        on = on && Main?.Wide != null;
        if (Widened == on) return;
        Widened = on;
        Changed?.Invoke();
    }

    private static void Recompute(bool reset)
    {
        if (reset) _manual = null;
        var byPriority = Entries.Where(e => !IsHidden(e)).OrderByDescending(e => e.Priority).ToList();
        var ordered = byPriority;
        if (_manual != null)
        {
            // Выбор пользователя: его места первыми, остальные (пришедшие позже или не выбранные) идут за ними по приоритету.
            var chosen = _manual.Select(id => byPriority.FirstOrDefault(e => e.Id == id)).OfType<ActivityEntry>().ToList();
            ordered = chosen.Concat(byPriority.Where(e => chosen.All(c => c.Id != e.Id))).ToList();
        }

        var previousMain = Main?.Id;
        Main = ordered.FirstOrDefault();
        var bubbles = ordered.Skip(1).Where(e => e.Bubble != null).Take(2).ToList(); // третьей и дальше не видно
        Secondary = bubbles.ElementAtOrDefault(0);
        Tertiary = bubbles.ElementAtOrDefault(1);
        MainCompact = byPriority.FirstOrDefault(e => e.Compact != null); // нотч выбирается только по приоритету
        if (reset || Main?.Id != previousMain || Main?.Wide == null) Widened = false;
        Changed?.Invoke();
    }
}
