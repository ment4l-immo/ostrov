using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Ostrov.Modules.Projects;
using Ostrov.Services;

namespace Ostrov.Search;

internal enum SummaryPeriod { Today, Yesterday, Week }

/// <summary>
/// «Итоги дня» в Alt+Q (решения пользователя 2026-11-16): фраза вроде «итоги дня» или «что я сегодня сделал» распознаётся сама, без ИИ, и в ответе
/// появляются карточки из <see cref="SummaryCards"/>. Данные: журнал дня (<see cref="DayJournal"/>: программы, музыка, Claude) и git по проектам из «Избранных».
/// Всё считается на компьютере, в сеть ничего не уходит.
/// </summary>
internal static class DaySummaryService
{
    private static readonly CultureInfo Ru = new("ru-RU");

    /// <summary>Просит ли фраза итоги: «итоги дня», «итоги за неделю», «итоги вчера», «что я сегодня сделал»…</summary>
    public static SummaryPeriod? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 70) return null;
        var text = Regex.Replace(input.Trim().TrimEnd('.', '!', '?'), @"\s+", " ").ToLowerInvariant();
        const RegexOptions o = RegexOptions.CultureInvariant;
        if (Regex.IsMatch(text, @"^(?:покажи |дай |открой )?итоги?(?: (?:за |на )?(?:день|дня|сегодня|сегодняшнего дня))?$", o)
            || Regex.IsMatch(text, @"^что я (?:сегодня )?(?:сделал|делал)(?: сегодня)?$", o)
            || Regex.IsMatch(text, @"^(?:мои )?итоги дня$", o)) return SummaryPeriod.Today;
        if (Regex.IsMatch(text, @"^(?:покажи |дай )?итоги? (?:за )?вчера(?:шнего дня)?$|^вчерашние итоги$|^что я (?:сделал|делал) вчера$", o)) return SummaryPeriod.Yesterday;
        if (Regex.IsMatch(text, @"^(?:покажи |дай )?итоги? (?:за )?(?:эту )?(?:неделю|недели|7 дней)$|^итоги недели$|^что я (?:сделал|делал) (?:за )?(?:эту )?неделю$", o)) return SummaryPeriod.Week;

        // Свободные формулировки: «сколько сегодня провёл времени за компом», «чем я занимался вчера», «сколько я просидел за пк за неделю».
        var free = text.Replace('ё', 'е');
        var asksTime = Regex.IsMatch(free, @"^сколько .*(?:врем|провел|просидел|просидела|проработал|работал|сидел).* (?:за|у) (?:комп|пк\b|ноут)", o)
            || Regex.IsMatch(free, @"^(?:сколько )?врем\w* (?:я )?(?:сегодня |вчера )?(?:за|у) (?:комп|пк\b|ноут)", o)
            || Regex.IsMatch(free, @"^чем я (?:сегодня |вчера |за неделю )?занимал(?:ся|ась)(?: сегодня| вчера| за неделю| на этой неделе)?$", o)
            || Regex.IsMatch(free, @"^(?:покажи |дай )?статистику? (?:за )?(?:день|сегодня|вчера|неделю)$", o);
        if (asksTime)
        {
            if (free.Contains("вчера")) return SummaryPeriod.Yesterday;
            if (free.Contains("недел") || free.Contains("7 дней")) return SummaryPeriod.Week;
            return SummaryPeriod.Today;
        }
        return null;
    }

    /// <summary>Собрать данные и построить карточки (на потоке интерфейса; git считается в фоне).</summary>
    public static async Task<IReadOnlyList<FrameworkElement>> BuildAsync(SummaryPeriod period)
    {
        var today = DateTime.Today;
        var (from, to) = period switch
        {
            SummaryPeriod.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            SummaryPeriod.Week => (today.AddDays(-6), today),
            _ => (today, today)
        };

        var journal = DayJournal.Current;
        var enabled = AppSettings.Load().DayJournalEnabled;
        var day = journal != null ? journal.Range(from, to) : new DayData();

        var gitTask = Task.Run(() => CollectGit(from, to));
        var git = await gitTask;

        var apps = day.Apps.Where(a => a.Value >= 60).Select(a => new AppTime(a.Key, TimeSpan.FromSeconds(a.Value))).ToList();
        var c = day.Claude;
        ClaudeDay? claude = c.Seconds > 0 || c.Steps > 0
            ? new ClaudeDay(TimeSpan.FromSeconds(c.Seconds), c.Sessions.Count, c.Steps,
                c.Projects.Select(p => (p.Key, TimeSpan.FromSeconds(p.Value))).OrderByDescending(p => p.Item2).ToList())
            : null;
        var played = day.Music.Values.Where(m => m.Seconds >= 30).ToList();
        MusicDay? music = played.Count > 0
            ? new MusicDay(TimeSpan.FromSeconds(played.Sum(m => m.Seconds)),
                played.OrderByDescending(m => m.Plays).ThenByDescending(m => m.Seconds).Take(3).Select(m => new TrackPlay(m.Title, m.Artist, Math.Max(1, m.Plays))).ToList())
            : null;

        var (title, subtitle) = period switch
        {
            SummaryPeriod.Week => ("Итоги недели", $"{from.ToString("d MMM", Ru)} – {to.ToString("d MMM", Ru)}"),
            SummaryPeriod.Yesterday => ("Итоги дня", from.ToString("dddd, d MMMM", Ru)),
            _ => ("Итоги дня", from.ToString("dddd, d MMMM", Ru))
        };
        if (!enabled) subtitle = "журнал дня выключен в настройках";

        var data = new DaySummaryData
        {
            Date = from,
            Title = title,
            Subtitle = subtitle,
            ActiveTime = day.Active >= 60 ? TimeSpan.FromSeconds(day.Active) : null,
            Apps = apps.Count > 0 ? apps : null,
            Claude = claude,
            Music = music,
            Git = git.Count > 0 ? git : null
        };
        return SummaryCards.Build(data, ["overview", "apps", "claude", "music", "git"]);
    }

    // ───── git ─────

    private static List<GitRow> CollectGit(DateTime from, DateTime to)
    {
        var rows = new List<GitRow>();
        IReadOnlyList<ProjectEntry> projects;
        try { projects = new ProjectsStore().GetAll(); }
        catch { return rows; }
        foreach (var project in projects)
        {
            try
            {
                var path = project.Path;
                if (!Directory.Exists(path)) continue;
                var gitPath = Path.Combine(path, ".git");
                if (!Directory.Exists(gitPath) && !File.Exists(gitPath)) continue;
                if (GitFor(path, from, to) is { } row) rows.Add(row);
            }
            catch { /* проект пропускаем */ }
        }
        return rows.OrderByDescending(r => r.Commits).ToList();
    }

    private static GitRow? GitFor(string path, DateTime from, DateTime to)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in new[]
        {
            "-C", path, "-c", "core.quotepath=false", "log", "--branches", "--no-merges",
            "--since=" + from.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture),
            "--until=" + to.AddDays(1).ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture),
            "--pretty=tformat:%H", "--shortstat"
        }) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = Process.Start(psi);
        if (process == null) return null;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync(); // читаем и выбрасываем: иначе git при длинном stderr зависнет на полном канале
        if (!process.WaitForExit(8000)) { try { process.Kill(true); } catch { } return null; }
        if (process.ExitCode != 0) return null;

        var commits = 0; var added = 0; var removed = 0;
        foreach (var line in output.Result.Split('\n'))
        {
            var l = line.Trim();
            if (Regex.IsMatch(l, "^[0-9a-f]{40}$")) { commits++; continue; }
            if (Regex.Match(l, @"(\d+) insertion") is { Success: true } ins) added += int.Parse(ins.Groups[1].Value, CultureInfo.InvariantCulture);
            if (Regex.Match(l, @"(\d+) deletion") is { Success: true } del) removed += int.Parse(del.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        if (commits == 0) return null;
        return new GitRow(Path.GetFileName(path.TrimEnd('\\', '/')), commits, added, removed);
    }
}
