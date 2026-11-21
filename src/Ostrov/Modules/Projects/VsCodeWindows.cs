using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Ostrov.Modules.Projects;

/// <summary>
/// Какие папки сейчас открыты в окнах VS Code. Читаются только заголовки видимых окон процесса Code (локально, ничего
/// не пишется и не уходит в сеть). Заголовок по умолчанию: «файл - папка - Visual Studio Code», без открытого файла:
/// «папка - Visual Studio Code». Если пользователь поменял <c>window.title</c> в настройках VS Code, точка просто не появится.
/// Две папки с одним названием подсветятся обе: по заголовку их не различить.
/// </summary>
internal static partial class VsCodeWindows
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const string AppName = "Visual Studio Code";

    /// <summary>Названия папок, открытых сейчас в окнах VS Code (без учёта регистра).</summary>
    public static IReadOnlySet<string> OpenFolderNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isCodeByPid = new Dictionary<uint, bool>();
        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                var length = GetWindowTextLength(hWnd);
                if (length <= 0 || length > 1000) return true;
                var text = new StringBuilder(length + 1);
                GetWindowText(hWnd, text, text.Capacity);
                var title = text.ToString();
                if (!title.Contains(AppName, StringComparison.Ordinal)) return true;

                GetWindowThreadProcessId(hWnd, out var pid);
                if (!isCodeByPid.TryGetValue(pid, out var isCode)) isCodeByPid[pid] = isCode = IsCodeProcess(pid);
                if (isCode) foreach (var name in RootCandidates(title)) names.Add(name);
            }
            catch { /* окно закрылось по ходу перебора или процесс недоступен — пропускаем */ }
            return true;
        }, IntPtr.Zero);
        return names;
    }

    private static bool IsCodeProcess(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.StartsWith("Code", StringComparison.OrdinalIgnoreCase); // «Code», «Code - Insiders»
        }
        catch { return false; }
    }

    /// <summary>
    /// Из заголовка окна достаёт возможные названия папки: всё, что стоит перед «Visual Studio Code», кроме первого
    /// сегмента, если их несколько (это имя открытого файла). Профиль, если он назван, тоже попадает в кандидаты — это безвредно.
    /// </summary>
    public static IReadOnlyList<string> RootCandidates(string title)
    {
        var segments = title.Split(" - ").Select(s => s.Trim()).ToList();
        var app = segments.FindIndex(s => s.StartsWith(AppName, StringComparison.Ordinal));
        if (app < 0) return [];
        var before = segments.Take(app).ToList();
        if (before.Count >= 2) before.RemoveAt(0);
        return before.Select(Clean).Where(s => s.Length > 0).ToList();
    }

    // Убираем пометки вроде «[Extension Development Host]», «[WSL: Ubuntu]» и « (Workspace)».
    private static string Clean(string segment)
    {
        var s = LeadingTags().Replace(segment, "");
        s = TrailingTags().Replace(s, "");
        s = WorkspaceSuffix().Replace(s, "");
        return s.Trim();
    }

    [GeneratedRegex(@"^\s*(\[[^\]]*\]\s*)+")] private static partial Regex LeadingTags();
    [GeneratedRegex(@"(\s*\[[^\]]*\])+\s*$")] private static partial Regex TrailingTags();
    [GeneratedRegex(@"\s*\((Workspace|Рабочая область)\)\s*$")] private static partial Regex WorkspaceSuffix();
}
