using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ostrov.Modules.ControlCenter;

/// <summary>Сон и выключение компьютера. Подтверждение (два нажатия для выключения) делает вкладка.</summary>
internal static class PowerActions
{
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>Сон (не гибернация). Вызов возвращается только после пробуждения, поэтому идёт в фоне.</summary>
    public static void Sleep() => Task.Run(() => SetSuspendState(false, false, false));

    /// <summary>
    /// Обычное выключение без принуждения: если в программе есть несохранённая работа, Windows покажет окно «программа мешает выключению»,
    /// а не закроет её молча.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch
        {
            // Не получилось запустить shutdown.exe — ничего не делаем.
        }
    }
}
