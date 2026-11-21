using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Ostrov.Services;

namespace Ostrov.Modules.Settings;

/// <summary>
/// Значки программ для списка «Программы» в настройках: берутся из самого exe-файла (как в проводнике). Путь известен у добавленных программ
/// (он сохраняется) и у запущенных; у стандартных браузеров его ищем в реестре («App Paths») или у работающего процесса.
/// Не нашли значок: серая плитка с первой буквой названия. Найденное помнится до закрытия острова.
/// </summary>
internal static class AppIcons
{
    public const double Size = 20;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Значок программы или null, если его не нашли. <paramref name="path"/> — путь к exe, если он известен.</summary>
    public static ImageSource? Get(string process, string? path)
    {
        if (Cache.TryGetValue(process, out var cached) && (cached != null || string.IsNullOrEmpty(path))) return cached;
        var icon = FromFile(path) ?? FromFile(FindExe(process));
        Cache[process] = icon;
        return icon;
    }

    /// <summary>Элемент для строки: значок 20 px или плитка с буквой.</summary>
    public static FrameworkElement Create(string process, string? path, string title)
    {
        if (Get(process, path) is { } image)
        {
            var picture = new Image { Source = image, Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return picture;
        }
        var letter = (title.Length > 0 ? title : process).Substring(0, 1).ToUpperInvariant();
        return new Border
        {
            Width = Size, Height = Size, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Hues.SurfaceHighColor),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter, FontSize = 11, Foreground = Ui.Gray, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static ImageSource? FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    // Где лежит exe программы с таким именем процесса: сначала реестр установленных программ, потом уже запущенный процесс.
    private static string? FindExe(string process)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" })
            {
                try
                {
                    using var sub = root.OpenSubKey(key + process + ".exe");
                    if (sub?.GetValue(null) is string value && value.Trim('"') is { Length: > 0 } path && File.Exists(path)) return path;
                }
                catch { /* ветка реестра недоступна */ }
            }

        try
        {
            foreach (var p in Process.GetProcessesByName(process))
            {
                try
                {
                    if (p.MainModule?.FileName is { Length: > 0 } running) return running;
                }
                catch { /* программа с повышенными правами: путь недоступен */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* список процессов недоступен */ }
        return null;
    }
}
