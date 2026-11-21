using System.Windows;
using Ostrov.Modules.ControlCenter;

namespace Ostrov.Modules.Hub;

/// <summary>
/// Вкладка «Главная» (хаб): всё главное одним взглядом. Открывается при каждом раскрытии острова (решение пользователя 2026-11-18).
/// Плитки телефона, нагрузки, плеера и погоды, ряд круглых кнопок (Bluetooth, VPN, таймер 25 мин, сон, выключение).
/// Клик по кнопке действует сразу, клик по плитке открывает её полную вкладку.
/// </summary>
public sealed class HubModule : IIslandModule
{
    // Модуль создаётся при запуске острова: заранее «будим» драйверный счётчик видеокарты (см. Nvml), иначе первые секунды кольцо читает 0.
    public HubModule() => Nvml.Warmup();

    public string Id => "hub";
    public string Title => "Главная";
    public string Icon => ""; // Segoe Fluent Icons: дом

    // Три ряда: плитки 84 + кнопки 78 + плитки 84 с зазорами по 10 = 266 px под содержимое (шапка и низ острова считаются отдельно).
    public double ExpandedHeight => 332;
    public double ExpandedWidth => 560;
    public System.Windows.Media.Color[]? BackdropPalette => Ostrov.Backdrop.TabPalettes.Hub;

    public FrameworkElement CreateView() =>
        new HubView(new BluetoothSwitch(), ModuleCatalog.All.OfType<Time.TimeModule>().First().Timer);
}
