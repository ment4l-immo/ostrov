using System.Windows;

namespace Ostrov.Modules;

/// <summary>
/// Одна функция острова (плеер, буфер, погода и т.д.).
/// Каждая функция живёт в своей папке и подключается через <see cref="ModuleCatalog"/>.
/// </summary>
public interface IIslandModule
{
    string Id { get; }

    /// <summary>Название — показывается во всплывающей подсказке вкладки.</summary>
    string Title { get; }

    /// <summary>Символ иконки из шрифта Segoe Fluent Icons.</summary>
    string Icon { get; }

    /// <summary>Вкладка «второго плана» (нужна редко): её значок стоит у правого края полосы вкладок и тусклее остальных.</summary>
    bool IsSecondary => false;

    /// <summary>Высота раскрытого острова на этой вкладке (в пределах разумного, не больше ~340).</summary>
    double ExpandedHeight => 220;

    /// <summary>Ширина раскрытого острова на этой вкладке (по умолчанию 520; окно острова 600 px, больше ~560 не делать).</summary>
    double ExpandedWidth => 520;

    /// <summary>
    /// Четыре цвета мягкого фона за вкладкой (общий фон острова <c>CoverGlow</c>); null — вкладка сама управляет фоном (плеер, погода)
    /// или фона нет. Окно острова берёт фон себе при открытии вкладки (М-019).
    /// </summary>
    System.Windows.Media.Color[]? BackdropPalette => null;

    /// <summary>Содержимое вкладки. Вызывается один раз, при первом открытии вкладки.</summary>
    FrameworkElement CreateView();
}
