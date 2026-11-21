using System.Windows;

namespace Ostrov.Modules.News;

/// <summary>Вкладка «Новости»: заголовки из лент по играм и нейросетям, подбор по интересам и лайкам.</summary>
public sealed class NewsModule : IIslandModule
{
    public string Id => "news";
    public string Title => "Новости";
    public string Icon => ""; // Segoe Fluent Icons: карточка со строками
    public System.Windows.Media.Color[]? BackdropPalette => Ostrov.Backdrop.TabPalettes.News;
    private NewsView? _view;

    // Список 300; когда новость открыта для чтения, остров вытягивается до 410.
    public double ExpandedHeight => _view?.IsReading == true ? 410 : 300;

    public FrameworkElement CreateView() => _view = new NewsView();
}
