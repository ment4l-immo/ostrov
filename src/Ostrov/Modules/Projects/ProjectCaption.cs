using System.IO;

namespace Ostrov.Modules.Projects;

/// <summary>Подпись под названием проекта: «родительская папка · когда». Чистая логика, без интерфейса (проверяется отдельно).</summary>
internal static class ProjectCaption
{
    /// <summary>Имя родительской папки (для корня диска — сам диск, например «D:\»); у корня диска родителя нет — пусто.</summary>
    public static string Parent(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('\\', '/');
            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent)) return "";
            var name = Path.GetFileName(parent);
            return string.IsNullOrEmpty(name) ? parent : name; // родитель — корень диска: «C:\»
        }
        catch { return ""; }
    }

    /// <summary>«сегодня», «вчера», «3 дня назад»; дальше месяцами, потом «больше года назад». Считаем по календарным дням, не по часам.</summary>
    public static string Ago(DateTime when, DateTime now)
    {
        var days = (now.Date - when.Date).Days;
        if (days <= 0) return "сегодня";
        if (days == 1) return "вчера";
        if (days <= 30) return $"{days} {Plural(days, "день", "дня", "дней")} назад";
        if (days < 365) return $"{Math.Min(11, days / 30)} мес. назад";
        return "больше года назад";
    }

    /// <summary>Подпись для «Избранных»: когда открывали через остров.</summary>
    public static string Favorite(string path, DateTime? lastOpened, DateTime now) =>
        Join(Parent(path), lastOpened is { } o ? Ago(o, now) : "ещё не открывали");

    /// <summary>Подпись для «Недавних»: когда в папке последний раз менялись файлы (не то же самое, что «открывали»).</summary>
    public static string Recent(string path, DateTime lastActive, DateTime now)
    {
        var ago = Ago(lastActive, now);
        return Join(Parent(path), "правки " + ago);
    }

    /// <summary>
    /// Подходит ли проект под фильтр. Фильтр режется по пробелам, каждое слово должно встретиться в названии папки
    /// или в имени родительской папки (регистр не важен). Пустой фильтр подходит всем.
    /// </summary>
    public static bool Matches(string path, string filter)
    {
        var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Length == 0) name = path; // корень диска
        var haystack = name + " " + Parent(path);
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static string Join(string parent, string when) => parent.Length == 0 ? when : parent + " · " + when;

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
