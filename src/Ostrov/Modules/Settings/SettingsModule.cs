using System.Windows;

namespace Ostrov.Modules.Settings;

/// <summary>Вкладка «Настройки»: шестерёнка одна в правом углу полосы вкладок (остальные вкладки по центру). Разделы «Общие» и «ИИ и поиск».</summary>
public sealed class SettingsModule : IIslandModule
{
    public string Id => "settings";
    public string Title => "Настройки";
    public string Icon => ""; // Segoe Fluent Icons: Settings
    public bool IsSecondary => true; // единственная вкладка в правом углу, остальные по центру
    public double ExpandedHeight => 290;
    public System.Windows.Media.Color[]? BackdropPalette => Ostrov.Backdrop.TabPalettes.Settings;

    public FrameworkElement CreateView() => new SettingsView();
}
