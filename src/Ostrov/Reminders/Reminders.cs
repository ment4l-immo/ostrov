using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shapes = System.Windows.Shapes;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Reminders;

/// <summary>Одно напоминание: когда и о чём.</summary>
internal sealed record Reminder(Guid Id, DateTime Due, string Text);

/// <summary>
/// Напоминания (решения пользователя 2026-11-15): их ставит ИИ из строки Alt+Q («напомни через 20 минут проверить рендер»),
/// сработавшее показывается плашкой на острове до клика, без звука. Список лежит в <c>%AppData%\Ostrov\reminders.json</c>, поэтому
/// переживает перезапуск; если остров был закрыт в нужное время, напоминание покажется при следующем запуске. Работает на потоке интерфейса.
/// </summary>
internal sealed class ReminderService
{
    public static ReminderService? Current { get; private set; }

    private const int Priority = 95; // выше таймера и музыки, ниже рендера AE и Claude
    private readonly List<Reminder> _pending = [];
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _file;

    public ReminderService(string? file = null)
    {
        _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "reminders.json");
        Current = this;
        Load();
        _tick.Tick += (_, _) => Check();
        _tick.Start();
        Check();
    }


    /// <summary>Поставить напоминание. Время в прошлом (больше минуты назад) не принимается.</summary>
    public Reminder? Add(DateTime due, string text)
    {
        text = text.Trim();
        if (text.Length == 0) text = "Напоминание";
        if (text.Length > 200) text = text[..200];
        if (due < DateTime.Now.AddMinutes(-1) || due > DateTime.Now.AddYears(1)) return null;
        var reminder = new Reminder(Guid.NewGuid(), due, text);
        _pending.Add(reminder);
        Save();
        return reminder;
    }

    private void Check()
    {
        var now = DateTime.Now;
        var due = _pending.Where(r => r.Due <= now).ToList();
        if (due.Count == 0) return;
        foreach (var r in due) _pending.Remove(r);
        Save();
        foreach (var r in due) Fire(r);
    }

    private static void Fire(Reminder r)
    {
        var id = "reminder-" + r.Id.ToString("N");
        var pill = new ReminderPill(r.Text, r.Due);
        ActivityCenter.Set(new ActivityEntry
        {
            Id = id, Priority = Priority,
            Pill = pill, PillWidth = ReminderPill.ViewWidth, PillHeight = ReminderPill.ViewHeight,
            Compact = new NoticeAlert("Напоминание"), CompactWidth = NoticeAlert.ViewWidth, CompactHeight = NoticeAlert.ViewHeight,
            DismissOnClick = true,
            OnDismissed = () => ActivityCenter.Remove(id)
        });
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var items = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_file));
            if (items == null) return;
            foreach (var s in items)
                if (s.Due != default && !string.IsNullOrWhiteSpace(s.Text)) _pending.Add(new Reminder(s.Id == Guid.Empty ? Guid.NewGuid() : s.Id, s.Due, s.Text));
        }
        catch
        {
            // Файл повреждён: начинаем с пустого списка.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_pending.Select(r => new Stored { Id = r.Id, Due = r.Due, Text = r.Text }).ToList()));
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            // Не удалось сохранить: напоминание сработает до перезапуска.
        }
    }

    private sealed class Stored
    {
        public Guid Id { get; set; }
        public DateTime Due { get; set; }
        public string Text { get; set; } = "";
    }
}

/// <summary>Векторные значки для знаков острова (не зависят от шрифта значков).</summary>
internal static class NoticeIcons
{
    public static readonly Geometry Bell = Frozen("M12,3 C9,3 7,5.2 7,8 L7,12 L5,16 L19,16 L17,12 L17,8 C17,5.2 15,3 12,3 Z M10,18 C10,19.1 10.9,20 12,20 C13.1,20 14,19.1 14,18 Z");
    public static readonly Geometry Moon = Frozen("M20,13.5 A8,8 0 1 1 10.5,4 A6.5,6.5 0 0 0 20,13.5 Z");
    public static readonly Geometry Power = Frozen("M12,3 L12,12 M17.5,6.5 A8,8 0 1 1 6.5,6.5");

    private static Geometry Frozen(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }

    /// <summary>Значок 24×24 заданного размера.</summary>
    public static FrameworkElement Fill(Geometry g, double size, Brush? brush = null) => new Shapes.Path
    {
        Data = g, Fill = brush ?? Hues.Ink, Stretch = Stretch.Uniform, Width = size, Height = size
    };

    public static FrameworkElement Stroke(Geometry g, double size, double thickness = 2) => new Shapes.Path
    {
        Data = g, Stroke = Hues.Ink, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Fill = Brushes.Transparent, Stretch = Stretch.Uniform, Width = size, Height = size
    };
}

/// <summary>Плашка сработавшего напоминания 288×56: значок, «Напоминание» и текст, справа время.</summary>
internal sealed class ReminderPill : Grid
{
    public const double ViewWidth = 288, ViewHeight = 56;

    public ReminderPill(string text, DateTime due)
    {
        Width = ViewWidth;
        Height = ViewHeight;
        var icon = NoticeIcons.Fill(NoticeIcons.Bell, 22);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(18, 0, 0, 3);
        Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(52, 0, 64, 3) };
        texts.Children.Add(new TextBlock { Text = "Напоминание", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink });
        texts.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Services_Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        Children.Add(texts);

        Children.Add(new TextBlock
        {
            Text = due.ToString("HH:mm"), FontSize = 12.5, Foreground = Services_Gray, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 3)
        });
    }

    private static readonly Brush Services_Gray = Ostrov.Modules.Ui.Gray;
}

/// <summary>
/// Короткий знак для нотча в полноэкранном видео (200×36, как «Время вышло»): значок и надпись, плавно мигает, пока не кликнут. Белый.
/// Текст можно менять (обратный отсчёт после рендера).
/// </summary>
internal sealed class NoticeAlert : Grid
{
    public const double ViewWidth = 200, ViewHeight = 36;
    private readonly TextBlock _text = new()
    {
        FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 4.8)
    };

    public NoticeAlert(string text)
    {
        Width = ViewWidth;
        Height = ViewHeight;
        _text.Text = text;
        var icon = NoticeIcons.Fill(NoticeIcons.Bell, 18);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(16, 0, 0, 3);
        Children.Add(icon);
        Children.Add(_text);
        IsVisibleChanged += (_, _) => ApplyBlink();
    }

    public void SetText(string text) => _text.Text = text;

    private void ApplyBlink()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) return;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromSeconds(0.6)))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }
}
