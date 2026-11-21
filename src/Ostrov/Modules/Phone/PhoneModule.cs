using System.Windows;

namespace Ostrov.Modules.Phone;

/// <summary>Вкладка «Телефон»: заряд iPhone и его уведомления (приложение и отправитель). Заняла место скрытых «Новостей».</summary>
public sealed class PhoneModule : IIslandModule
{
    public string Id => "phone";
    public string Title => "Телефон";
    public string Icon => ""; // Segoe Fluent Icons: телефон
    public System.Windows.Media.Color[]? BackdropPalette => Ostrov.Backdrop.TabPalettes.Phone;

    // Пока открыто окно моста (QR), остров выше: под QR помещается адрес текстом.
    public double ExpandedHeight => PhoneView.BridgeActive ? 340 : 300;

    public FrameworkElement CreateView() => new PhoneView(this);
}
