namespace Ostrov.AfterEffects;

/// <summary>Что показывать на пассивном индикаторе: идёт рендер, готово или ошибка.</summary>
internal enum RenderPhase { Rendering, Done, Error }

/// <summary>Один элемент очереди рендеринга AE (из файла состояния, который пишет скрипт <c>tools/ae</c>).</summary>
/// <param name="Status">QUEUED, RENDERING, DONE, ERR_STOPPED, USER_STOPPED и т. д. (имена статусов AE).</param>
internal sealed record RenderItem(string Status, double ElapsedSeconds, string Comp, string Output)
{
    public bool IsPending => Status is "QUEUED" or "RENDERING";
}

/// <param name="BeatUtc">Когда скрипт последний раз записал файл (его «пульс»): время изменения файла.</param>
internal sealed record RenderSnapshot(DateTime BeatUtc, IReadOnlyList<RenderItem> Items);

internal sealed record RenderView(RenderPhase Phase, TimeSpan Elapsed, string Title, string Detail);

/// <summary>
/// Определяет, идёт ли рендер в After Effects, по «пульсу» скрипта (проверено разведкой 2026-11-12, AE 26.0):
/// пока AE свободен, скрипт переписывает файл состояния раз в секунду; на время рендера AE занят, и скрипт полностью замолкает;
/// после рендера он оживает и видит элементы со статусом DONE / ERR_STOPPED. Скрипт ни разу не видит RENDERING напрямую.
/// Поэтому «рендер идёт» = пульс замолк при живом AE, а в последнем состоянии был элемент в очереди (QUEUED).
/// Класс чистый (без файлов, окон и процессов), чтобы его можно было проверить подставными событиями.
/// </summary>
internal sealed class RenderTracker
{
    /// <summary>Сколько тишины считаем «AE занят». Обычный пульс — 1 с, при открытом диалоге и т. п. AE тоже может ненадолго замолчать.</summary>
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(3.5);

    private RenderView? _sticky;           // «готово» / «ошибка»: висят до клика
    private bool _rendering;
    private DateTime _startedUtc;          // момент, с которого считаем время (последний пульс перед тишиной)
    private DateTime _aeStartUtc;          // процесс AE, при котором рендер начался
    private DateTime _resolvedBeatUtc = DateTime.MinValue; // по этому пульсу рендер уже разобран: заново его не начинаем
    private List<RenderItem> _watched = [];

    /// <summary>Клик по «готово»/«ошибка»: индикатор убирается.</summary>
    public void Dismiss() => _sticky = null;

    /// <param name="aeStartUtc">Когда запущен AfterFX.exe (null — AE не запущен).</param>
    /// <param name="snapshot">Последнее состояние из файла (null — файла нет или он не читается).</param>
    /// <param name="outputReady">
    /// Запасной сигнал конца: готовый выходной файл элемента уже лежит на месте, он новее начала рендера (второй аргумент), временного нет.
    /// </param>
    /// <returns>Что показывать сейчас (null — ничего).</returns>
    public RenderView? Update(DateTime nowUtc, DateTime? aeStartUtc, RenderSnapshot? snapshot, Func<RenderItem, DateTime, bool> outputReady)
    {
        // Скрипт работает именно в этом запуске AE, если пульс не старше запуска (иначе это файл от вчерашней сессии).
        var scriptAlive = aeStartUtc is { } start && snapshot is not null && snapshot.BeatUtc >= start.AddSeconds(-2);
        var silence = scriptAlive ? nowUtc - snapshot!.BeatUtc : TimeSpan.Zero;

        if (_rendering)
        {
            if (aeStartUtc is not { } now0 || Math.Abs((now0 - _aeStartUtc).TotalSeconds) > 5)
                return Settle(Failed("Рендер прерван", "After Effects закрылся"), snapshot?.BeatUtc);

            // Пульс вернулся после тишины: AE закончил, смотрим, чем кончилось.
            if (scriptAlive && snapshot!.BeatUtc > _startedUtc && silence <= SilenceLimit)
                return ResolveFromSnapshot(snapshot);

            // Пульса нет: запасной конец по готовым файлам (пульс может не вернуться, как было в одном из заходов разведки).
            if (_watched.Count > 0 && _watched.All(w => outputReady(w, _startedUtc)))
                return Settle(Finished(_watched, nowUtc - _startedUtc), snapshot?.BeatUtc);

            return RenderingView(nowUtc);
        }

        // Начало: пульс замолк, а в последнем состоянии был элемент в очереди.
        if (scriptAlive && silence > SilenceLimit && snapshot!.BeatUtc > _resolvedBeatUtc)
        {
            var pending = snapshot.Items.Where(i => i.IsPending).ToList();
            if (pending.Count > 0)
            {
                _rendering = true;
                _sticky = null;
                _watched = pending;
                _startedUtc = snapshot.BeatUtc;
                _aeStartUtc = aeStartUtc!.Value;
                return RenderingView(nowUtc);
            }
        }

        return _sticky;
    }

    // Подпись (название композиции или «N композиций») на обычной плашке не показывается, её читает расширенный вид (правый клик).
    private RenderView RenderingView(DateTime nowUtc) =>
        new(RenderPhase.Rendering, nowUtc - _startedUtc < TimeSpan.Zero ? TimeSpan.Zero : nowUtc - _startedUtc, "After Effects · рендер", Describe(_watched));

    private static string Describe(IReadOnlyList<RenderItem> items) =>
        items.Count == 0 ? "" : items.Count == 1 ? items[0].Comp : Plural(items.Count, "композиция", "композиции", "композиций");

    private RenderView? ResolveFromSnapshot(RenderSnapshot snapshot)
    {
        var results = _watched
            .Select(w => snapshot.Items.FirstOrDefault(i => SamePath(i.Output, w.Output) && i.Comp == w.Comp))
            .ToList();

        if (results.Any(r => r?.Status == "ERR_STOPPED"))
            return Settle(Failed("Ошибка рендера", "Проверь After Effects"), snapshot.BeatUtc);

        var done = results.Where(r => r?.Status == "DONE").Select(r => r!).ToList();
        if (done.Count > 0)
            return Settle(Finished(done, TimeSpan.FromSeconds(done.Sum(d => d.ElapsedSeconds))), snapshot.BeatUtc);

        // Остановили кнопкой Stop или элемент так и не начал рендериться (тишина была по другой причине): молчим.
        return Settle(null, snapshot.BeatUtc);
    }

    private RenderView? Settle(RenderView? result, DateTime? beatUtc)
    {
        _rendering = false;
        _watched = [];
        if (beatUtc is { } b && b > _resolvedBeatUtc) _resolvedBeatUtc = b;
        _sticky = result;
        return result;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static RenderView Failed(string title, string detail) => new(RenderPhase.Error, TimeSpan.Zero, title, detail);

    private static RenderView Finished(IReadOnlyList<RenderItem> items, TimeSpan elapsed)
    {
        return new RenderView(RenderPhase.Done, elapsed, "Рендер завершён", $"{Describe(items)} · {FormatElapsed(elapsed)}");
    }

    /// <summary>«0:42», «1:18», «1:02:03».</summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var total = (int)Math.Round(t.TotalSeconds);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var word = n % 100 is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
        return $"{n} {word}";
    }
}
