using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Search;

/// <summary>
/// Небольшой разбор Markdown в элементы текста WPF (FlowDocument) для ответов ИИ: абзацы, заголовки, списки, цитаты, блоки кода,
/// таблицы (простым моноширинным текстом), **жирный**, *курсив*, `код`, ссылки. Белое и серое на чёрном, без новых цветов.
/// Годится для частично пришедшего текста: незакрытый блок кода просто считается кодом до конца.
/// </summary>
internal static class MiniMarkdown
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Brush CodeBackground = Frozen(Color.FromRgb(0x26, 0x26, 0x26));
    private static readonly Brush Gray = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly Brush CodeText = Frozen(Color.FromRgb(0xE6, 0xE6, 0xE6));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static readonly Regex InlinePattern = new(
        @"\*\*(?<b>.+?)\*\*" +
        @"|`(?<c>[^`]+)`" +
        @"|\[(?<lt>[^\]]+)\]\((?<lu>https?://[^)\s]+)\)" +
        @"|(?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*])" +
        @"|(?<![\w_])_(?<u>[^_\s][^_]*?)_(?![\w_])" +
        @"|(?<url>https?://[^\s)\]]+)",
        RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(\s*)[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^(\s*)(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableRow = new(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^[\s|:\-]+$", RegexOptions.Compiled);

    public static List<Block> Parse(string text, Action<string> openLink)
    {
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Блок кода ```…```
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                i++; // закрывающая строка (если дошло)
                blocks.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // Таблица: подряд идущие строки «| a | b |» — простым моноширинным текстом, без линий-разделителей
            if (TableRow.IsMatch(line))
            {
                var rows = new List<string[]>();
                while (i < lines.Length && TableRow.IsMatch(lines[i]))
                {
                    if (!TableSeparator.IsMatch(lines[i]))
                        rows.Add(lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim().Replace("**", "")).ToArray());
                    i++;
                }
                blocks.Add(CodeBlock(FormatTable(rows)));
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var p = new Paragraph { Margin = new Thickness(0, 6, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = heading.Groups[1].Length == 1 ? 16 : 15 };
                AddInlines(p.Inlines, heading.Groups[2].Value, openLink);
                blocks.Add(p);
                i++;
                continue;
            }

            // Списки: подряд идущие пункты одного вида
            var bullet = Bullet.Match(line);
            var numbered = Numbered.Match(line);
            if (bullet.Success || numbered.Success)
            {
                var ordered = numbered.Success && !bullet.Success;
                var list = new List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    MarkerOffset = 6, Padding = new Thickness(18, 0, 0, 0), Margin = new Thickness(0, 0, 0, 6)
                };
                if (ordered) list.StartIndex = int.Parse(numbered.Groups[2].Value);
                while (i < lines.Length)
                {
                    var b = Bullet.Match(lines[i]);
                    var n = Numbered.Match(lines[i]);
                    if (ordered ? !n.Success : !b.Success) break;
                    var para = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                    AddInlines(para.Inlines, ordered ? n.Groups[3].Value : b.Groups[2].Value, openLink);
                    list.ListItems.Add(new ListItem(para));
                    i++;
                }
                blocks.Add(list);
                continue;
            }

            // Цитата
            if (line.TrimStart().StartsWith('>'))
            {
                var p = new Paragraph { Margin = new Thickness(10, 0, 0, 6), Foreground = Gray };
                AddInlines(p.Inlines, line.TrimStart().TrimStart('>').Trim(), openLink);
                blocks.Add(p);
                i++;
                continue;
            }

            // Горизонтальная линия — пропускаем
            if (Regex.IsMatch(line, @"^\s*([-*_])\1{2,}\s*$")) { i++; continue; }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            AddInlines(paragraph.Inlines, line.Trim(), openLink);
            blocks.Add(paragraph);
            i++;
        }
        return blocks;
    }

    private static Block CodeBlock(string code) => new Paragraph(new Run(code))
    {
        FontFamily = Mono, FontSize = 12.5, Foreground = CodeText, Background = CodeBackground,
        Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 2, 0, 8), LineHeight = 18
    };

    private static string FormatTable(List<string[]> rows)
    {
        if (rows.Count == 0) return "";
        var cols = rows.Max(r => r.Length);
        var widths = new int[cols];
        foreach (var r in rows) for (var c = 0; c < r.Length; c++) widths[c] = Math.Max(widths[c], r[c].Length);
        return string.Join("\n", rows.Select(r => string.Join("  │  ", Enumerable.Range(0, cols).Select(c => (c < r.Length ? r[c] : "").PadRight(widths[c]))).TrimEnd()));
    }

    /// <summary>Добавить текст с разметкой (**жирный**, *курсив*, `код`, ссылки) в строку абзаца.</summary>
    internal static void AddInlines(InlineCollection inlines, string text, Action<string> openLink)
    {
        var pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            pos = m.Index + m.Length;

            if (m.Groups["b"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups["b"].Value, openLink);
                inlines.Add(bold);
            }
            else if (m.Groups["c"].Success)
            {
                inlines.Add(new Run(m.Groups["c"].Value) { FontFamily = Mono, FontSize = 12.5, Foreground = CodeText, Background = CodeBackground });
            }
            else if (m.Groups["lt"].Success)
            {
                inlines.Add(Link(m.Groups["lt"].Value, m.Groups["lu"].Value, openLink));
            }
            else if (m.Groups["i"].Success || m.Groups["u"].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["u"].Value, openLink);
                inlines.Add(italic);
            }
            else
            {
                var url = m.Groups["url"].Value.TrimEnd('.', ',', ';', ':', '!', '?');
                var rest = m.Groups["url"].Value[url.Length..];
                inlines.Add(Link(url, url, openLink));
                if (rest.Length > 0) inlines.Add(new Run(rest));
            }
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }

    /// <summary>Ссылка: белая с подчёркиванием, по клику уходит в браузер (через <paramref name="openLink"/>).</summary>
    internal static Hyperlink Link(string label, string url, Action<string> openLink)
    {
        var link = new Hyperlink(new Run(label)) { Foreground = Hues.Ink, Cursor = Cursors.Hand, ToolTip = url, Focusable = false };
        link.Click += (_, _) => openLink(url);
        return link;
    }
}
