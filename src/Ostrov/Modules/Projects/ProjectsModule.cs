using System.Windows;

namespace Ostrov.Modules.Projects;

/// <summary>Вкладка «Проекты»: список папок для открытия в VS Code одним нажатием.</summary>
public sealed class ProjectsModule : IIslandModule
{
    public string Id => "projects";
    public string Title => "Проекты";
    // Строки двухстрочные (40 px): при высоте 220 на список оставалось ≈ 110 px, то есть меньше трёх строк.
    public double ExpandedHeight => 270;
    public System.Windows.Media.Color[]? BackdropPalette => Ostrov.Backdrop.TabPalettes.Projects;
    public string Icon => "";

    public ProjectsModule()
    {
        // Команда «открой ostrov» из строки Alt+Q: проект из «Избранных» по части названия папки.
        Ostrov.Services.IslandCommands.FindProject = query =>
        {
            var store = new ProjectsStore();
            var all = store.GetAll().Select(p => (Name: System.IO.Path.GetFileName(p.Path.TrimEnd((char)92, '/')), p.Path)).Where(p => p.Name.Length > 0).ToList();
            var match = all.FirstOrDefault(p => string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase));
            if (match.Name == null) match = all.FirstOrDefault(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            return match.Name == null ? null : match;
        };
        Ostrov.Services.IslandCommands.OpenProject = path =>
        {
            if (!VsCode.TryOpen(path, out var error)) return error ?? "Не удалось открыть проект";
            new ProjectsStore().Touch(path);
            return null;
        };
    }

    public FrameworkElement CreateView() => new ProjectsView(new ProjectsStore());
}
