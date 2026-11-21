using Ostrov.Services;

namespace Ostrov.Modules.Phone;

/// <summary>Уведомление телефона. Текст сообщения не читается и не хранится: только приложение, отправитель и время.</summary>
internal sealed record PhoneNotice(uint Id, string App, string Sender, DateTime Time, string? Code = null);

/// <summary>
/// Код подтверждения в тексте уведомления («Ваш код 482915»). Текст уведомления остров не хранит: он читается один раз при опросе, из него
/// берутся только цифры кода (в памяти, пока уведомление лежит в центре уведомлений Windows). Нужны слово про код и 4–8 цифр подряд (можно «123 456» или «123-456»).
/// </summary>
internal static class CodeFinder
{
    private static readonly System.Text.RegularExpressions.Regex Hint = new(
        @"код|code|парол|password|otp|\bpin\b|подтвержд|verif|confirm|одноразов|смс|sms",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex Digits = new(
        @"(?<![\d])(\d{3}[ \-]\d{3}|\d{4,8})(?![\d])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 300 || !Hint.IsMatch(text)) return null;
        var m = Digits.Match(text);
        if (!m.Success) return null;
        var code = new string(m.Value.Where(char.IsDigit).ToArray());
        return code.Length is >= 4 and <= 8 ? code : null;
    }
}

/// <summary>
/// Для каких приложений телефона остров показывает «Новое сообщение в X» (решение пользователя 2026-11-15: только выбранные;
/// по умолчанию мессенджеры, их он на телефоне читает). Выбор хранится в <see cref="AppSettings.PhoneApps"/>.
/// </summary>
internal static class PhoneApps
{
    private static readonly string[] Messengers =
        ["telegram", "whatsapp", "viber", "signal", "messenger", "vk", "вконтакте", "discord", "messages", "сообщения", "skype", "wechat"];

    /// <summary>Известные мессенджеры: всегда есть в списке приложений, даже если уведомление от них ещё не приходило.</summary>
    public static readonly string[] KnownMessengers = ["Telegram", "WhatsApp", "Viber", "Signal", "Messenger", "ВКонтакте", "Discord"];

    // Источники уведомлений «Связи с телефоном» в реестре: по одному на приложение телефона (ключ — идентификатор приложения на iPhone).
    private static readonly (string Id, string Name)[] KnownSources =
    [
        ("ph.telegra.Telegraph", "Telegram"), ("ru.keepcoder.Telegram", "Telegram"), ("net.whatsapp.WhatsApp", "WhatsApp"),
        ("com.vk.vkclient", "ВКонтакте"), ("com.hammerandchisel.discord", "Discord"), ("com.viber", "Viber"),
        ("org.whispersystems.signal", "Signal"), ("com.facebook.Messenger", "Messenger")
    ];

    /// <summary>
    /// Мессенджеры, у которых уведомления выключены в самой Windows (Параметры → Система → Уведомления → Связь с телефоном): тогда Windows
    /// их не показывает вовсе, «Связь с телефоном» держит их только у себя, и остров их не видит. Только чтение реестра.
    /// </summary>
    public static IReadOnlyList<string> DisabledInWindows()
    {
        var list = new List<string>();
        try
        {
            foreach (var (id, name) in KnownSources)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Microsoft.YourPhone_8wekyb3d8bbwe!YourPhoneNotifications_" + id);
                if (key?.GetValue("Enabled") is int enabled && enabled == 0 && !list.Contains(name)) list.Add(name);
            }
        }
        catch
        {
            // Реестр недоступен: подсказки не будет, остальное работает.
        }
        return list;
    }

    public static bool IsMessenger(string app) =>
        Messengers.Any(m => app.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsEnabled(string app)
    {
        var rule = AppSettings.Load().PhoneApps?.FirstOrDefault(r => string.Equals(r.Name, app, StringComparison.OrdinalIgnoreCase));
        return rule?.Enabled ?? IsMessenger(app);
    }

    public static void SetEnabled(string app, bool on)
    {
        var settings = AppSettings.Load();
        settings.PhoneApps ??= [];
        var rule = settings.PhoneApps.FirstOrDefault(r => string.Equals(r.Name, app, StringComparison.OrdinalIgnoreCase));
        if (rule == null) settings.PhoneApps.Add(new PhoneAppRule { Name = app, Enabled = on });
        else rule.Enabled = on;
        settings.Save();
    }
}
