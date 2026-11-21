using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.AfterEffects;
using Ostrov.Modules;
using Ostrov.Reminders;
using Ostrov.Services;

namespace Ostrov.Search;

/// <summary>
/// Действия, которые ИИ делает по просьбе из строки Alt+Q (решения пользователя 2026-11-15: «не как команда, а прогон текста через ИИ»).
/// Модель получает описание функций, сама решает, просьба ли это, и передаёт нам готовые параметры; остров выполняет действие и показывает
/// карточку в ответе. Функции: напоминание, «после рендера усыпить/выключить», пересчёт валют. Результат модели не возвращается (ответ на этом
/// заканчивается), поэтому всё, что нужно показать, показывает сама карточка.
/// </summary>
internal static class AiActions
{
    internal static readonly CultureInfo Ru = new("ru-RU");

    /// <summary>Описание функций для OpenAI (Responses API, тип function).</summary>
    internal static IReadOnlyList<object> Tools() =>
    [
        new Dictionary<string, object>
        {
            ["type"] = "function", ["name"] = "set_reminder",
            ["description"] = "Поставить напоминание на указанное время. Вызывай, когда пользователь просит напомнить о чём-то («напомни через 20 минут проверить рендер», «напомни завтра в 10 позвонить»).",
            ["parameters"] = Schema(
                ("text", "string", "О чём напомнить, коротко, от второго лица или безлично (например «Проверить рендер»)."),
                ("at", "string", "Когда напомнить: локальное время компьютера в формате ГГГГ-ММ-ДДTЧЧ:ММ:СС без часового пояса. Считай от текущего времени, указанного в указаниях.")),
            ["strict"] = false
        },
        new Dictionary<string, object>
        {
            ["type"] = "function", ["name"] = "after_render_action",
            ["description"] = "Что сделать с компьютером после того, как закончится рендер в After Effects: усыпить, выключить или отменить ранее поставленное. Вызывай, когда пользователь просит «после рендера усыпи/выключи компьютер» или отменить это.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "sleep", "shutdown", "cancel" }, ["description"] = "sleep — сон, shutdown — выключение, cancel — отменить." }
                },
                ["required"] = new[] { "action" }
            },
            ["strict"] = false
        },
        new Dictionary<string, object>
        {
            ["type"] = "function", ["name"] = "convert_currency",
            ["description"] = "Пересчитать сумму из одной валюты в другую по свежему курсу. Вызывай всегда, когда просят перевести или пересчитать валюту либо узнать курс валюты, вместо поиска в интернете.",
            ["parameters"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["amount"] = new Dictionary<string, object> { ["type"] = "number", ["description"] = "Сумма; если просят просто курс валюты, то 1." },
                    ["from"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Код исходной валюты ISO 4217, например USD, EUR, RUB, KZT." },
                    ["to"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Код валюты, в которую считать; если не названа, RUB." }
                },
                ["required"] = new[] { "amount", "from", "to" }
            },
            ["strict"] = false
        }
    ];

    private static Dictionary<string, object> Schema(params (string Name, string Type, string Description)[] props) => new()
    {
        ["type"] = "object",
        ["properties"] = props.ToDictionary(p => p.Name, p => (object)new Dictionary<string, object> { ["type"] = p.Type, ["description"] = p.Description }),
        ["required"] = props.Select(p => p.Name).ToArray()
    };

    /// <summary>Добавка к указаниям модели: текущее время (нужно, чтобы считать «через 20 минут», «завтра в 10») и правила действий.</summary>
    internal static string InstructionsNote() =>
        " Сейчас у пользователя " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (" + Ru.DateTimeFormat.GetDayName(DateTime.Now.DayOfWeek) + "), часовой пояс его компьютера. " +
        "У тебя есть функции острова: set_reminder (напоминания), after_render_action (что сделать после рендера в After Effects), convert_currency (пересчёт валют). " +
        "Если пользователь просит одно из этого, вызови функцию с точными параметрами (время считай от текущего), без выдуманных значений; сам курс не называй, его покажет карточка. " +
        "После вызова функции текст не нужен, максимум одна короткая фраза. Для обычных вопросов функции не вызывай.";

    /// <summary>Выполнить вызов функции. Возвращает карточку для ответа и короткую запись для истории диалога; null — функция неизвестна.</summary>
    internal static (FrameworkElement Card, string Note)? Run(string name, string? argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var args = doc.RootElement;
            return name switch
            {
                "set_reminder" => SetReminder(args),
                "after_render_action" => AfterRender(args),
                "convert_currency" => Convert(args),
                _ => null
            };
        }
        catch (Exception ex)
        {
            return (new ActionCard(ActionCard.Warning(), "Не получилось", ex.Message, error: true), "[действие не выполнено]");
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name, double fallback)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return fallback;
    }

    private static (FrameworkElement, string) SetReminder(JsonElement a)
    {
        var text = Str(a, "text");
        if (!DateTime.TryParse(Str(a, "at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
            return (new ActionCard(ActionCard.Warning(), "Не понял время", "Скажи, когда напомнить: например «через 20 минут» или «завтра в 10».", error: true), "[напоминание не поставлено]");
        if (ReminderService.Current is not { } service)
            return (new ActionCard(ActionCard.Warning(), "Напоминания недоступны", "Служба не запущена.", error: true), "[напоминание не поставлено]");
        if (due < DateTime.Now.AddSeconds(2) && due > DateTime.Now.AddMinutes(-1)) due = DateTime.Now.AddSeconds(2);
        var reminder = service.Add(due, text);
        if (reminder == null)
            return (new ActionCard(ActionCard.Warning(), "Это время уже прошло", "Назови время в будущем.", error: true), "[напоминание не поставлено]");
        var detail = When(reminder.Due) + " · " + reminder.Text;
        return (new ActionCard(ActionCard.Bell(), "Напомню", detail), "[поставлено напоминание: " + detail + "]");
    }

    private static (FrameworkElement, string) AfterRender(JsonElement a)
    {
        switch (Str(a, "action").ToLowerInvariant())
        {
            case "sleep":
                RenderAfterAction.Arm(AfterRenderAction.Sleep);
                return (new ActionCard(ActionCard.Moon(), "После рендера усну", "Когда After Effects закончит рендер, на острове пойдёт отсчёт 60 с; клик по плашке отменит."), "[после рендера: сон]");
            case "shutdown":
                RenderAfterAction.Arm(AfterRenderAction.Shutdown);
                return (new ActionCard(ActionCard.Power(), "После рендера выключу", "Когда After Effects закончит рендер, на острове пойдёт отсчёт 60 с; клик по плашке отменит."), "[после рендера: выключение]");
            default:
                RenderAfterAction.Cancel();
                return (new ActionCard(ActionCard.Check(), "Отменено", "После рендера ничего делать не буду."), "[после рендера: отменено]");
        }
    }

    private static (FrameworkElement, string) Convert(JsonElement a)
    {
        var amount = Num(a, "amount", 1);
        var from = Str(a, "from").Trim().ToUpperInvariant();
        var to = Str(a, "to").Trim().ToUpperInvariant();
        if (to.Length == 0) to = "RUB";
        if (from.Length != 3 || to.Length != 3)
            return (new ActionCard(ActionCard.Warning(), "Не понял валюту", "Назови валюты, например «100 долларов в рубли».", error: true), "[пересчёт не выполнен]");
        return (new CurrencyCard(amount, from, to), $"[показан пересчёт: {amount.ToString(CultureInfo.InvariantCulture)} {from} в {to}]");
    }

    internal static string When(DateTime due)
    {
        var now = DateTime.Now;
        var time = due.ToString("HH:mm", CultureInfo.InvariantCulture);
        var minutes = (due - now).TotalMinutes;
        if (due.Date == now.Date) return minutes < 60 ? $"через {Math.Max(1, (int)Math.Round(minutes))} мин, в {time}" : $"сегодня в {time}";
        if (due.Date == now.Date.AddDays(1)) return $"завтра в {time}";
        return due.ToString("d MMMM", Ru) + " в " + time;
    }
}

/// <summary>Карточка результата действия ИИ в ответе: значок в круге, заголовок белым, пояснение серым. Только белое и серое.</summary>
internal sealed class ActionCard : Border
{
    public ActionCard(FrameworkElement icon, string title, string detail, bool error = false)
    {
        Background = new SolidColorBrush(Hues.SurfaceColor);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(12, 10, 14, 10);
        HorizontalAlignment = HorizontalAlignment.Left;
        MaxWidth = 460;
        Margin = new Thickness(0, 2, 0, 4);

        var badge = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Hues.SurfaceHighColor),
            VerticalAlignment = VerticalAlignment.Center, Child = icon
        };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = error ? Ui.Gray : Hues.Ink });
        texts.Children.Add(new TextBlock { Text = detail, FontSize = 12.5, Foreground = Ui.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(texts);
        Child = row;
    }

    public static FrameworkElement Bell() => NoticeIcons.Fill(NoticeIcons.Bell, 18);
    public static FrameworkElement Moon() => NoticeIcons.Fill(NoticeIcons.Moon, 18);
    public static FrameworkElement Power() => NoticeIcons.Stroke(NoticeIcons.Power, 18);
    public static FrameworkElement Warning() => new TextBlock { Text = "!", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Hues.Ink, Margin = new Thickness(0, -1, 0, 0) };
    public static FrameworkElement Check() => new TextBlock { Text = "✓", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Hues.Ink };
}
