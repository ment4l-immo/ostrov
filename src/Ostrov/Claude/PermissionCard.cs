using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Claude;

/// <summary>
/// Карточка запроса разрешения на острове: слева проект, инструмент и главное (команда или путь файла, до 3 строк),
/// справа кнопки «Разрешить» (коралловая — цвет запросов Claude) и «Запретить» (серая).
/// </summary>
internal sealed class PermissionCard : Grid, IPromptCard
{
    public const double CardWidth = 480, CardHeight = 112;

    /// <summary>Коралловый — четвёртый цвет острова, только для запросов Claude.</summary>
    public static readonly Color Coral = Color.FromRgb(0xD9, 0x77, 0x57);

    private readonly ClaudeRequest _request;
    private readonly TextBlock _header;

    public PermissionCard(ClaudeRequest request, Action onAllow, Action onDeny)
    {
        _request = request;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _header = new TextBlock
        {
            FontSize = 12,
            Foreground = Ui.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // Текст привязан к верху на уровне верхней кнопки (две кнопки по 30 с зазором 8 стоят по центру карточки),
        // чтобы заголовок был на одном месте у коротких и длинных запросов.
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, (CardHeight - (30 + 8 + 30)) / 2 - 2, 0, 0)
        };
        text.Children.Add(_header);
        if (request.Detail.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = LimitLines(request.Detail),
                FontSize = 13,
                Foreground = Hues.Ink,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                LineHeight = 17,
                MaxHeight = 51 // три строки
            });
        SetColumn(text, 0);
        Children.Add(text);

        var allow = Ui.Pill("Разрешить", accent: Coral);
        var deny = Ui.Pill("Запретить");
        foreach (var b in new[] { allow, deny })
        {
            b.Width = 112;
            b.Margin = new Thickness(0);
        }
        deny.Margin = new Thickness(0, 8, 0, 0);
        allow.Click += (_, _) => onAllow();
        deny.Click += (_, _) => onDeny();

        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);
        SetColumn(buttons, 1);
        Children.Add(buttons);

        SetQueued(0);
    }

    // Многострочная команда: показываем первые три непустые строки, остальное помечаем многоточием.
    private static string LimitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        return lines.Count > 3 ? string.Join("\n", lines.Take(3)) + " …" : string.Join("\n", lines);
    }

    /// <summary>Сколько ещё запросов ждёт своей очереди (показывается в строке над текстом).</summary>
    public void SetQueued(int count)
    {
        var title = $"{_request.Project} · {_request.ToolLabel}";
        _header.Text = count > 0 ? $"{title} · ещё {count}" : title;
    }
}
