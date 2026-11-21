using Microsoft.Win32;

namespace Ostrov.Services;

/// <summary>
/// Автозапуск через раздел автозагрузки текущего пользователя (HKCU\...\Run).
/// Права администратора не нужны, другие пользователи компьютера не затрагиваются.
/// </summary>
public static class AutostartService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ostrov";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (exe is not null) key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Если автозапуск включён, но запись указывает на другой exe (например, на самую первую сборку, после того как
    /// папка сборки поменялась), перенацеливает её на текущий. Выключенный автозапуск не трогает.
    /// </summary>
    public static void RefreshPath()
    {
        string? registered;
        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
            registered = key?.GetValue(ValueName) as string;

        var exe = Environment.ProcessPath;
        if (registered is null || exe is null) return;
        if (!string.Equals(registered.Trim('"'), exe, StringComparison.OrdinalIgnoreCase))
            SetEnabled(true);
    }

    /// <summary>При самом первом запуске включает автозапуск (так решил пользователь). Потом выбор пользователя не трогаем.</summary>
    public static void EnsureDefaultOn(AppSettings settings)
    {
        if (settings.AutostartInitialized) return;
        SetEnabled(true);
        settings.AutostartInitialized = true;
        settings.Save();
    }
}
