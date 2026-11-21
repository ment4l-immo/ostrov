using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ostrov.Modules;
using Ostrov.Modules.ControlCenter;
using Ostrov.Reminders;
using Ostrov.Services;

namespace Ostrov.AfterEffects;

internal enum AfterRenderAction { None, Sleep, Shutdown }

/// <summary>
/// «После рендера усыпи/выключи компьютер» (решения пользователя 2026-11-15: ставит ИИ из строки Alt+Q; защита: обратный отсчёт 60 секунд
/// и отмена кликом по плашке). Действие ставится заранее и ждёт ближайшего успешного конца рендера After Effects; ошибка рендера его не
/// запускает. Не хранится на диске (после перезапуска острова действия нет) и само «протухает» через 2 часа.
/// </summary>
internal static class RenderAfterAction
{
    /// <summary>Длина обратного отсчёта, секунд (меняется только в проверках).</summary>
    public static int CountdownSeconds { get; internal set; } = 60;
    private const string EntryId = "after-render";
    private static readonly TimeSpan Expires = TimeSpan.FromHours(2);

    private static AfterRenderAction _armed;
    private static DateTime _armedAt;
    private static DispatcherTimer? _timer;
    private static int _left;
    private static CountdownPill? _pill;
    private static NoticeAlert? _alert;

    /// <summary>Что сделает остров после ближайшего рендера (None — ничего).</summary>
    public static AfterRenderAction Armed => _armed != AfterRenderAction.None && DateTime.UtcNow - _armedAt < Expires ? _armed : AfterRenderAction.None;

    /// <summary>Выполняется ли сейчас обратный отсчёт.</summary>
    public static bool Counting => _timer is { IsEnabled: true };

    /// <summary>Заменяет выполнение действия (только проверки): без этого тест усыпил бы компьютер.</summary>
    internal static Action<AfterRenderAction> Perform { get; set; } = action =>
    {
        if (action == AfterRenderAction.Sleep) PowerActions.Sleep();
        else if (action == AfterRenderAction.Shutdown) PowerActions.Shutdown();
    };

    public static void Arm(AfterRenderAction action)
    {
        _armed = action;
        _armedAt = DateTime.UtcNow;
        if (action == AfterRenderAction.None) CancelCountdown();
    }

    /// <summary>Отменить и ожидание, и уже идущий отсчёт.</summary>
    public static void Cancel()
    {
        _armed = AfterRenderAction.None;
        CancelCountdown();
    }

    /// <summary>Рендер AE закончился успешно: если действие поставлено, начинается отсчёт.</summary>
    public static void OnRenderFinished()
    {
        var action = Armed;
        if (action == AfterRenderAction.None || Counting) return;
        StartCountdown(action);
    }

    private static void StartCountdown(AfterRenderAction action)
    {
        _left = CountdownSeconds;
        _pill = new CountdownPill(action);
        _alert = new NoticeAlert(Label(action, _left));
        _pill.Set(_left);
        ActivityCenter.Set(new ActivityEntry
        {
            Id = EntryId, Priority = 120, // важнее всего: время на отмену ограничено
            Pill = _pill, PillWidth = CountdownPill.ViewWidth, PillHeight = CountdownPill.ViewHeight,
            Compact = _alert, CompactWidth = NoticeAlert.ViewWidth, CompactHeight = NoticeAlert.ViewHeight,
            DismissOnClick = true,
            OnDismissed = Cancel // клик по плашке отменяет
        });
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick(action);
        _timer.Start();
    }

    private static void Tick(AfterRenderAction action)
    {
        // Остров не виден (игра, скрыт из трея; в видео есть нотч, его можно кликнуть): отменить отсчёт нечем, поэтому он стоит и начинается заново, когда остров снова на виду.
        if (!IslandBus.IsVisible && !IslandBus.CompactMode)
        {
            _left = CountdownSeconds;
            _pill?.Set(_left);
            _alert?.SetText(Label(action, _left));
            return;
        }
        _left--;
        if (_left > 0)
        {
            _pill?.Set(_left);
            _alert?.SetText(Label(action, _left));
            return;
        }
        CancelCountdown();
        _armed = AfterRenderAction.None;
        Perform(action);
    }

    private static void CancelCountdown()
    {
        _timer?.Stop();
        _timer = null;
        _pill = null;
        _alert = null;
        ActivityCenter.Remove(EntryId);
    }

    private static string Label(AfterRenderAction action, int left) =>
        (action == AfterRenderAction.Sleep ? "Сон через " : "Выключение через ") + left + " с";
}

/// <summary>Плашка отсчёта 288×56: значок, «Сон через 47 с» и подсказка отменить кликом; справа убывающая полоска.</summary>
internal sealed class CountdownPill : Grid
{
    public const double ViewWidth = 288, ViewHeight = 56;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink };
    private readonly AfterRenderAction _action;
    private readonly Border _bar = new() { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Left };
    private const double BarWidth = 252;

    public CountdownPill(AfterRenderAction action)
    {
        _action = action;
        Width = ViewWidth;
        Height = ViewHeight;
        var icon = NoticeIcons.Fill(action == AfterRenderAction.Sleep ? NoticeIcons.Moon : NoticeIcons.Power, 22);
        if (action == AfterRenderAction.Shutdown) icon = NoticeIcons.Stroke(NoticeIcons.Power, 22);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(18, 0, 0, 3);
        Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(52, 0, 18, 8) };
        texts.Children.Add(_title);
        texts.Children.Add(new TextBlock { Text = "Рендер завершён · нажми, чтобы отменить", FontSize = 12, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        Children.Add(texts);

        _bar.VerticalAlignment = VerticalAlignment.Bottom;
        _bar.Margin = new Thickness(18, 0, 0, 7);
        Children.Add(_bar);
    }

    public void Set(int left)
    {
        _title.Text = (_action == AfterRenderAction.Sleep ? "Сон через " : "Выключение через ") + left + " с";
        _bar.Width = Math.Max(0, BarWidth * left / RenderAfterAction.CountdownSeconds);
    }
}
