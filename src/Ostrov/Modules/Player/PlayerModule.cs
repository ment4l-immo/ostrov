using System.Windows;

namespace Ostrov.Modules.Player;

/// <summary>Состояние плеера, которое нужно модулю (высота зависит от режима текста). Ставит <see cref="PlayerView"/>.</summary>
internal static class PlayerState
{
    public static bool LyricsOpen { get; set; }
}

/// <summary>Плеер: показывает текущий трек из системных медиа-функций Windows (Apple Music) и управляет им.</summary>
public sealed class PlayerModule : IIslandModule
{
    public string Id => "player";
    public string Title => "Плеер";
    public string Icon => "";

    private readonly MusicActivity _activity = new(); // мини-остров музыки (обложка и волна) в свёрнутом острове

    // Обычная высота 220; в режиме текста песни остров вытягивается вниз до 450 (решение пользователя).
    public double ExpandedHeight => PlayerState.LyricsOpen ? 450 : 214;

    public FrameworkElement CreateView() => new PlayerView();
}
