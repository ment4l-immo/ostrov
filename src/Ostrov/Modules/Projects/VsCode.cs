using System.Diagnostics;
using System.IO;

namespace Ostrov.Modules.Projects;

/// <summary>
/// Открытие папки в VS Code. Находим Code.exe рядом с <c>code.cmd</c> из PATH (или в стандартных местах установки)
/// и запускаем его напрямую с папкой — так же, как пункт «Открыть с помощью Code» в Проводнике.
/// Если папка уже открыта в каком-то окне, VS Code просто покажет это окно.
/// </summary>
internal static class VsCode
{
    private static string? _exe;

    public static bool TryOpen(string folder, out string? error)
    {
        error = null;
        var exe = _exe ??= FindExe();
        if (exe == null)
        {
            error = "VS Code не найден";
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { folder } });
            return true;
        }
        catch (Exception e)
        {
            error = "Не удалось открыть: " + e.Message;
            return false;
        }
    }

    private static string? FindExe()
    {
        // 1) Через PATH: ...\Microsoft VS Code\bin\code.cmd → ...\Microsoft VS Code\Code.exe
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var dir = raw.Trim().Trim('"');
                if (!File.Exists(Path.Combine(dir, "code.cmd"))) continue;
                var root = Directory.GetParent(dir)?.FullName;
                if (root != null && File.Exists(Path.Combine(root, "Code.exe"))) return Path.Combine(root, "Code.exe");
            }
            catch { /* некорректный путь в PATH — пропускаем */ }
        }

        // 2) Стандартные места установки.
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
