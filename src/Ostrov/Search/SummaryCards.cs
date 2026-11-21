using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Search;

// Данные итогов дня. Любой раздел можно не заполнять (null или пусто): его карточка просто не показывается.
internal sealed record AppTime(string Name, TimeSpan Time);
internal sealed record ClaudeDay(TimeSpan Time, int Sessions, int Steps, IReadOnlyList<(string Project, TimeSpan Time)> Projects);
internal sealed record RenderRow(string Composition, TimeSpan Duration, bool Ok);
internal sealed record TrackPlay(string Title, string Artist, int Plays);
internal sealed record MusicDay(TimeSpan Time, IReadOnlyList<TrackPlay> Top);
internal sealed record PhoneDay(int Notifications, IReadOnlyList<(string App, int Count)> Apps, int? BatteryStart, int? BatteryEnd);
internal sealed record GitRow(string Project, int Commits, int Added, int Removed);

internal sealed record DaySummaryData
{
    public DateTime Date { get; init; } = DateTime.Today;
    public TimeSpan? ActiveTime { get; init; }
    public IReadOnlyList<AppTime>? Apps { get; init; }
    public ClaudeDay? Claude { get; init; }
    public IReadOnlyList<RenderRow>? Renders { get; init; }
    public PhoneDay? Phone { get; init; }
    public MusicDay? Music { get; init; }
    public IReadOnlyList<GitRow>? Git { get; init; }

    /// <summary>Заголовок и подпись карточки-обзора (по умолчанию «Итоги дня» и дата).</summary>
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
}

/// <summary>
/// Карточки «итогов дня» для ответа в Alt+Q (макет по просьбе пользователя 2026-11-15: карточки появляются только по его просьбе, оформление готово
/// заранее, внутрь просто вписываются данные). Разделы: overview, apps, claude, renders, phone, music, git. Только белое и серое; коралловый у Claude.
/// Источники данных ещё не подключены: сейчас карточки собираются из <see cref="Sample"/>.
/// </summary>
internal static class SummaryCards
{
    private static readonly CultureInfo Ru = new("ru-RU");
    private static readonly Brush Gray = Ui.Gray;
    private static readonly Brush Coral = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));
    private static readonly Brush CardBg = Frozen(Hues.GlassSurfaceColor); // как у карточек «Главной»
    private static readonly Brush TileBg = Frozen(Hues.SurfaceHighColor); // тот же оттенок, что у карточки, чуть светлее (раньше нейтральный #252525 выбивался)
    private static readonly Brush TrackBg = Frozen(Hues.SurfaceHighColor);
    private static readonly Brush BadgeBg = Frozen(Hues.SurfaceHighColor);
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Идентификаторы разделов в порядке показа.</summary>

    public static IReadOnlyList<FrameworkElement> Build(DaySummaryData d, IReadOnlyCollection<string>? sections = null)
    {
        bool Want(string id) => sections == null || sections.Contains(id, StringComparer.OrdinalIgnoreCase);
        var list = new List<FrameworkElement>();
        if (Want("overview")) list.Add(Overview(d));
        if (Want("apps") && d.Apps is { Count: > 0 }) list.Add(Apps(d.Apps));
        if (Want("claude") && d.Claude is { } c) list.Add(Claude(c));
        if (Want("renders") && d.Renders is { Count: > 0 }) list.Add(Renders(d.Renders));
        if (Want("phone") && d.Phone is { } p) list.Add(Phone(p));
        if (Want("music") && d.Music is { } m) list.Add(Music(m));
        if (Want("git") && d.Git is { Count: > 0 }) list.Add(Git(d.Git));
        return list;
    }

    // ---------- разделы ----------

    private static FrameworkElement Overview(DaySummaryData d)
    {
        var tiles = new List<(string, string)>();
        if (d.ActiveTime is { } active) tiles.Add((Dur(active), "за ПК"));
        if (d.Git is { Count: > 0 }) tiles.Add((d.Git.Sum(g => g.Commits).ToString(Ru), Plural(d.Git.Sum(g => g.Commits), "коммит", "коммита", "коммитов")));
        if (d.Renders is { Count: > 0 }) tiles.Add((d.Renders.Count.ToString(Ru), Plural(d.Renders.Count, "рендер", "рендера", "рендеров")));
        if (d.Phone is { } phone) tiles.Add((phone.Notifications.ToString(Ru), Plural(phone.Notifications, "уведомление", "уведомления", "уведомлений")));
        var date = d.Date.ToString("dddd, d MMMM", Ru);
        return Shell("", d.Title ?? "Итоги дня", d.Subtitle ?? date, Gray, tiles.Count > 0 ? Tiles(tiles.Take(4).ToArray()) : new TextBlock { Text = "Пока нечего показать", FontSize = 12.5, Foreground = Gray });
    }

    private static FrameworkElement Apps(IReadOnlyList<AppTime> apps)
    {
        var top = apps.OrderByDescending(a => a.Time).Take(5).ToList();
        var max = top[0].Time.TotalSeconds;
        var rows = top.Select(a => (a.Name, a.Time.TotalSeconds / Math.Max(1, max), Dur(a.Time))).ToList();
        var total = TimeSpan.FromSeconds(apps.Sum(a => a.Time.TotalSeconds));
        return Shell("", "Программы", Dur(total), Gray, Bars(rows, Hues.Ink));
    }

    private static FrameworkElement Claude(ClaudeDay c)
    {
        var body = new StackPanel();
        body.Children.Add(Tiles((c.Sessions.ToString(Ru), Plural(c.Sessions, "сессия", "сессии", "сессий")),
                                (c.Projects.Count.ToString(Ru), Plural(c.Projects.Count, "проект", "проекта", "проектов")),
                                (c.Steps.ToString("#,0", Ru), Plural(c.Steps, "шаг", "шага", "шагов"))));
        if (c.Projects.Count > 0)
        {
            var top = c.Projects.OrderByDescending(p => p.Time).Take(4).ToList();
            var max = top[0].Time.TotalSeconds;
            var bars = Bars(top.Select(p => (p.Project, p.Time.TotalSeconds / Math.Max(1, max), Dur(p.Time))).ToList(), Coral);
            bars.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 10, 0, 0));
            body.Children.Add(bars);
        }
        return Shell(ClaudeGlyph, "Claude", Dur(c.Time), Coral, body);
    }

    private static FrameworkElement Renders(IReadOnlyList<RenderRow> renders)
    {
        var body = new StackPanel();
        foreach (var r in renders.Take(5))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var mark = new TextBlock { Text = r.Ok ? "✓" : "!", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = r.Ok ? Hues.Ink : Gray, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = r.Composition, FontSize = 12.5, Foreground = r.Ok ? Hues.Ink : Gray, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var time = new TextBlock { Text = Clock(r.Duration), FontSize = 12, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); Grid.SetColumn(time, 2);
            row.Children.Add(mark); row.Children.Add(name); row.Children.Add(time);
            body.Children.Add(row);
        }
        var failed = renders.Count(r => !r.Ok);
        var total = TimeSpan.FromSeconds(renders.Sum(r => r.Duration.TotalSeconds));
        body.Children.Add(new TextBlock
        {
            Text = "Всего " + Dur(total) + (failed > 0 ? ", с ошибкой: " + failed : ""), FontSize = 11.5, Foreground = Gray, Margin = new Thickness(0, 2, 0, 0)
        });
        return Shell("", "After Effects", renders.Count + " " + Plural(renders.Count, "рендер", "рендера", "рендеров"), Hues.Ink, body);
    }

    private static FrameworkElement Phone(PhoneDay p)
    {
        var body = new StackPanel();
        if (p.Apps.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var (app, count) in p.Apps.OrderByDescending(a => a.Count).Take(6)) chips.Children.Add(Chip(app, count.ToString(Ru)));
            body.Children.Add(chips);
        }
        if (p.BatteryStart is { } a && p.BatteryEnd is { } b)
            body.Children.Add(new TextBlock { Text = $"Заряд: {a} % → {b} %", FontSize = 12, Foreground = Gray, Margin = new Thickness(0, 6, 0, 0) });
        return Shell("", "Телефон", p.Notifications + " " + Plural(p.Notifications, "уведомление", "уведомления", "уведомлений"), Hues.Ink, body);
    }

    private static FrameworkElement Music(MusicDay m)
    {
        var body = new StackPanel();
        var n = 1;
        foreach (var t in m.Top.Take(4))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var num = new TextBlock { Text = (n++).ToString(), FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { FontSize = 12.5, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new System.Windows.Documents.Run(t.Title));
            title.Inlines.Add(new System.Windows.Documents.Run("  " + t.Artist) { Foreground = Gray });
            var plays = new TextBlock { Text = "×" + t.Plays, FontSize = 12, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); Grid.SetColumn(plays, 2);
            row.Children.Add(num); row.Children.Add(title); row.Children.Add(plays);
            body.Children.Add(row);
        }
        return Shell("", "Музыка", Dur(m.Time), Hues.Ink, body);
    }

    private static FrameworkElement Git(IReadOnlyList<GitRow> git)
    {
        var body = new StackPanel();
        foreach (var g in git.OrderByDescending(g => g.Commits).Take(4))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 74 });
            var name = new TextBlock { Text = g.Project, FontSize = 12.5, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var delta = new TextBlock { FontSize = 12, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            delta.Inlines.Add(new System.Windows.Documents.Run("+" + g.Added.ToString("#,0", Ru)) { Foreground = Hues.Ink });
            delta.Inlines.Add(new System.Windows.Documents.Run(" / −" + g.Removed.ToString("#,0", Ru)));
            var commits = new TextBlock
            {
                Text = g.Commits + " " + Plural(g.Commits, "коммит", "коммита", "коммитов"), FontSize = 12, Foreground = Gray,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(delta, 1); Grid.SetColumn(commits, 2);
            row.Children.Add(name); row.Children.Add(delta); row.Children.Add(commits);
            body.Children.Add(row);
        }
        var total = git.Sum(g => g.Commits);
        return Shell("", "Код", total + " " + Plural(total, "коммит", "коммита", "коммитов"), Hues.Ink, body);
    }

    // ---------- детали оформления ----------

    // Значок ассистента в карточке итогов: нейтральная четырёхлучевая звезда (чужие логотипы в проект не входят).
    private const string ClaudeGlyph = "@assistant";

    /// <summary>
    /// Значок из шрифта иконок, поставленный по настоящим границам рисунка, а не по рамке строки текста: у шрифтовых значков метрики строки
    /// смещены, и в круге они стояли неровно. Контур буквы строится из шрифта, его центр совмещается с центром квадрата <paramref name="box"/>.
    /// </summary>
    private static FrameworkElement CenteredGlyph(string glyph, double size, Brush fill, double box)
    {
        var text = new FormattedText(glyph, Ru, FlowDirection.LeftToRight, new Typeface(Ui.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Hues.Ink, 1.0);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var b = geometry.Bounds;
        if (b.IsEmpty) return new Border();
        var moved = geometry.Clone();
        moved.Transform = new TranslateTransform(box / 2 - (b.X + b.Width / 2), box / 2 - (b.Y + b.Height / 2));
        moved.Freeze();
        return new System.Windows.Shapes.Path { Data = moved, Fill = fill, Width = box, Height = box, IsHitTestVisible = false };
    }

    /// <summary>Нейтральная четырёхлучевая звезда по центру квадрата.</summary>
    private static FrameworkElement ClaudeSpark(double size, Brush brush)
    {
        var c = size / 2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c, 0), true, true);
            ctx.QuadraticBezierTo(new Point(c, c), new Point(size, c), true, true);
            ctx.QuadraticBezierTo(new Point(c, c), new Point(c, size), true, true);
            ctx.QuadraticBezierTo(new Point(c, c), new Point(0, c), true, true);
            ctx.QuadraticBezierTo(new Point(c, c), new Point(c, 0), true, true);
        }
        g.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = g, Fill = brush, Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
    }

    // Оболочка карточки: значок в круге, заголовок, справа главное значение (акцентным цветом, если он задан), ниже содержимое.
    private static Border Shell(string glyph, string title, string? value, Brush accent, UIElement body)
    {
        var badge = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = BadgeBg, VerticalAlignment = VerticalAlignment.Center,
            Child = glyph == ClaudeGlyph
                ? ClaudeSpark(16, Coral)
                : CenteredGlyph(glyph, 13, ReferenceEquals(accent, Gray) ? Hues.Ink : accent, 28)
        };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        Grid.SetColumn(titleText, 1);
        head.Children.Add(badge);
        head.Children.Add(titleText);
        if (value != null)
        {
            var valueText = new TextBlock
            {
                Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ReferenceEquals(accent, Coral) ? Coral : ReferenceEquals(accent, Gray) ? Gray : Hues.Ink
            };
            Grid.SetColumn(valueText, 2);
            head.Children.Add(valueText);
        }
        if (body is FrameworkElement fe) fe.Margin = new Thickness(0, 11, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        return new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 13),
            HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 460, Margin = new Thickness(0, 2, 0, 4), Child = stack
        };
    }

    // Строки «подпись · полоска · значение»: полоска серая подложка и заливка нужного цвета.
    private static FrameworkElement Bars(IReadOnlyList<(string Label, double Fraction, string Value)> rows, Brush fill)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 64 });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bottom = i == rows.Count - 1 ? 0 : 8;
            var label = new TextBlock { Text = rows[i].Label, FontSize = 12.5, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, bottom) };
            var f = Math.Clamp(rows[i].Fraction, 0.03, 1);
            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f + 0.0001, GridUnitType.Star) });
            inner.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = fill, Opacity = i == 0 ? 1 : 0.7 });
            var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = TrackBg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, bottom), Child = inner };
            var value = new TextBlock { Text = rows[i].Value, FontSize = 12, Foreground = Gray, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, bottom) };
            Grid.SetRow(label, i); Grid.SetRow(bar, i); Grid.SetColumn(bar, 1); Grid.SetRow(value, i); Grid.SetColumn(value, 2);
            grid.Children.Add(label); grid.Children.Add(bar); grid.Children.Add(value);
        }
        return grid;
    }

    // Плитки с крупным числом и подписью, в один ряд поровну.
    private static FrameworkElement Tiles(params (string Value, string Label)[] items)
    {
        var grid = new UniformGrid { Columns = items.Length };
        for (var i = 0; i < items.Length; i++)
        {
            var tile = new StackPanel();
            tile.Children.Add(new TextBlock { Text = items[i].Value, FontSize = items.Length >= 4 ? 16 : 18, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink });
            tile.Children.Add(new TextBlock { Text = items[i].Label, FontSize = 11.5, Foreground = Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            grid.Children.Add(new Border
            {
                Background = TileBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 8, 11, 9),
                Margin = new Thickness(0, 0, i == items.Length - 1 ? 0 : 6, 0), Child = tile
            });
        }
        return grid;
    }

    private static FrameworkElement Chip(string text, string count)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Hues.Ink });
        content.Children.Add(new TextBlock { Text = count, FontSize = 12, Foreground = Gray, Margin = new Thickness(6, 0, 0, 0) });
        return new Border { Background = TileBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 4, 9, 5), Margin = new Thickness(0, 0, 6, 6), Child = content };
    }

    private static string Dur(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "меньше минуты";
        var h = (int)t.TotalHours;
        var m = t.Minutes;
        return h > 0 ? (m > 0 ? $"{h} ч {m} мин" : $"{h} ч") : $"{m} мин";
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    internal static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        return a is >= 11 and <= 14 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }

    /// <summary>Примерные данные для макета (как будут выглядеть карточки с настоящими числами).</summary>
    public static DaySummaryData Sample() => new()
    {
        Date = new DateTime(2026, 11, 15),
        ActiveTime = TimeSpan.FromMinutes(6 * 60 + 40),
        Apps =
        [
            new("Visual Studio Code", TimeSpan.FromMinutes(3 * 60 + 5)), new("Chrome", TimeSpan.FromMinutes(1 * 60 + 40)),
            new("After Effects", TimeSpan.FromMinutes(55)), new("Discord", TimeSpan.FromMinutes(32)), new("Apple Music", TimeSpan.FromMinutes(18))
        ],
        Claude = new ClaudeDay(TimeSpan.FromMinutes(3 * 60 + 20), 4, 212,
            [("ostrov", TimeSpan.FromMinutes(2 * 60 + 5)), ("shop-site", TimeSpan.FromMinutes(58)), ("bot", TimeSpan.FromMinutes(17))]),
        Renders = [new("Intro v3", TimeSpan.FromSeconds(78), true), new("Outro", TimeSpan.FromSeconds(214), true), new("Logo reveal", TimeSpan.FromSeconds(41), false)],
        Phone = new PhoneDay(43, [("Telegram", 28), ("Instagram", 9), ("Почта", 6)], 87, 58),
        Music = new MusicDay(TimeSpan.FromMinutes(2 * 60 + 10), [new("Ты", "Автостопом по фазе сна", 5), new("Пятница", "Кишлак", 3), new("Intro", "The xx", 2)]),
        Git = [new("ostrov", 7, 1240, 310), new("bot", 2, 96, 12)]
    };
}
