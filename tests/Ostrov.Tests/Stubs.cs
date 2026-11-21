// Минимальные заглушки только для того, чтобы CommandParser.cs собрался без WinRT и всего окна.
// Реальные MediaControl/IslandBus не включаются в проект тестов.

namespace Ostrov.Modules.Player
{
    internal static class MediaControl
    {
        public enum Action { Pause, Play, Next, Previous }

        public static Action? LastAction;
        public static int? LastVolume;

        public static Task<string?> RunAsync(Action action)
        {
            LastAction = action;
            return Task.FromResult<string?>(null);
        }

        public static string? SetVolume(int percent)
        {
            LastVolume = percent;
            return null;
        }
    }
}

namespace Ostrov.Services
{
    internal static class IslandBus
    {
        public static string? LastTab;

        public static void RequestTab(string id) => LastTab = id;
    }
}

namespace Ostrov.Modules.News
{
    // Упоминается в XML-комментарии NewsProfile.Suggestion.
    internal static class NewsReview { }
}
