using System.Windows;

namespace Ostrov.Modules.Buffer;

/// <summary>Вкладка «Буфер»: история буфера обмена и полка для файлов.</summary>
public sealed class BufferModule : IIslandModule
{
    /// <summary>Файлы, перетащенные на остров, открывают именно эту вкладку (см. MainWindow).</summary>
    public const string ModuleId = "buffer";

    public string Id => ModuleId;
    public string Title => "Буфер";
    public string Icon => "";
    public double ExpandedHeight => 260;
    public System.Windows.Media.Color[]? BackdropPalette => Ostrov.Backdrop.TabPalettes.Buffer;

    public FrameworkElement CreateView()
    {
        var service = ClipboardService.Instance;
        return new BufferView(service.History, service.Shelf, service.Restore);
    }
}
