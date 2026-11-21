using Windows.Media.Control;

namespace Ostrov.Modules.Player;

/// <summary>Управление музыкой без открытой вкладки «Плеер» (команды из строки Alt+Q): пауза, воспроизведение, следующий и предыдущий трек, громкость.</summary>
internal static class MediaControl
{
    public enum Action { Pause, Play, Next, Previous }

    /// <summary>Выполнить действие; вернуть текст ошибки или null. Берётся сеанс Apple Music, а если его нет, текущий сеанс Windows.</summary>
    public static async Task<string?> RunAsync(Action action)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetSessions()
                              .FirstOrDefault(s => s.SourceAppUserModelId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase))
                          ?? manager.GetCurrentSession();
            if (session == null) return "Музыка не запущена";
            var ok = action switch
            {
                Action.Pause => await session.TryPauseAsync(),
                Action.Play => await session.TryPlayAsync(),
                Action.Next => await session.TrySkipNextAsync(),
                _ => await session.TrySkipPreviousAsync()
            };
            return ok ? null : "Плеер не принял команду";
        }
        catch
        {
            return "Не удалось связаться с плеером";
        }
    }

    /// <summary>Громкость Apple Music (0–100 %); общую громкость Windows не трогает.</summary>
    public static string? SetVolume(int percent) =>
        AppleMusicVolume.TrySetLevel(Math.Clamp(percent, 0, 100) / 100.0) ? null : "Apple Music не запущена";
}
