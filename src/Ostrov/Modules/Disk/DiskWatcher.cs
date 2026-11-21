using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ostrov.Modules.ControlCenter;
using Ostrov.Reminders;
using Ostrov.Services;

namespace Ostrov.Modules.Disk;

/// <summary>Состояние одного локального диска.</summary>
internal sealed record DiskInfo(string Name, long Free, long Total)
{
    public double UsedFraction => Total <= 0 ? 0 : 1.0 - (double)Free / Total;
}

/// <summary>
/// Предупреждение о нехватке места (решения пользователя 2026-11-15): когда на любом локальном диске свободно меньше 10 ГБ
/// (<see cref="AppSettings.DiskWarnGb"/>), на острове плашка до клика. Показывается на старте, если уже мало, и один раз, когда место
/// упало ниже порога во время работы; повторно по тому же диску не беспокоит, пока место не вернётся выше порога (с запасом 1 ГБ).
/// Проверка раз в 5 минут и через 8 секунд после запуска; читаются только размеры дисков.
/// </summary>
internal sealed class DiskWatcher
{
    private const string EntryId = "disk";
    private const int Priority = 95;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<DiskInfo>> _read;
    private bool _registered;
    private DiskPill? _pill;

    public DiskWatcher(Func<IReadOnlyList<DiskInfo>>? read = null) => _read = read ?? ReadDrives;

    public void Start()
    {
        _timer.Tick += (_, _) => Check();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        first.Tick += (_, _) => { first.Stop(); Check(); _timer.Start(); };
        first.Start();
    }

    internal static IReadOnlyList<DiskInfo> ReadDrives()
    {
        var list = new List<DiskInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DiskInfo(d.Name.TrimEnd('\\'), d.AvailableFreeSpace, d.TotalSize));
            }
        }
        catch
        {
            // Диск отвалился посреди чтения: в этот раз пропускаем.
        }
        return list;
    }

    /// <summary>Одна проверка (вызывается таймером; открыт для проверок).</summary>
    internal void Check()
    {
        var limit = Math.Max(1, AppSettings.Load().DiskWarnGb) * 1024L * 1024 * 1024;
        var disks = _read();
        var low = disks.Where(d => d.Free < limit).ToList();

        // Диск, где места снова хватает с запасом, снова может предупредить.
        foreach (var d in disks)
            if (d.Free > limit + 1024L * 1024 * 1024) _warned.Remove(d.Name);

        var fresh = low.Where(d => _warned.Add(d.Name)).ToList();
        if (low.Count == 0) { Unregister(); return; }
        if (fresh.Count == 0 && _registered) { _pill?.Set(low); return; } // плашка уже висит: только обновляем цифры
        if (fresh.Count == 0) return; // уже предупреждали и клик закрыл плашку: не повторяем
        Show(low);
    }

    private void Show(IReadOnlyList<DiskInfo> low)
    {
        _pill ??= new DiskPill();
        _pill.Set(low);
        if (_registered) return;
        _registered = true;
        ActivityCenter.Set(new ActivityEntry
        {
            Id = EntryId, Priority = Priority,
            Pill = _pill, PillWidth = DiskPill.ViewWidth, PillHeight = DiskPill.ViewHeight,
            Compact = new NoticeAlert("Мало места на диске"), CompactWidth = NoticeAlert.ViewWidth, CompactHeight = NoticeAlert.ViewHeight,
            DismissOnClick = true,
            OnDismissed = () => { _registered = false; ActivityCenter.Remove(EntryId); }
        });
    }

    private void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        ActivityCenter.Remove(EntryId);
    }
}

/// <summary>
/// Плашка «Мало места» 288×56: слева кольцо с долей занятого места самого заполненного диска, справа название и сколько осталось.
/// Только белое и серое.
/// </summary>
internal sealed class DiskPill : Grid
{
    public const double ViewWidth = 288, ViewHeight = 56;
    private static readonly CultureInfo Ru = new("ru-RU");
    private readonly LoadRing _ring = new() { ArcColor = Hues.AlertColor, Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 3) };
    private readonly TextBlock _title = new() { Text = "Мало места на диске", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink };
    private readonly TextBlock _detail = new() { FontSize = 12.5, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };

    public DiskPill()
    {
        Width = ViewWidth;
        Height = ViewHeight;
        Children.Add(_ring);
        Children.Add(new TextBlock
        {
            Text = "!", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Hues.Alert,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18 + 12, 0, 0, 4)
        });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(60, 0, 16, 3) };
        texts.Children.Add(_title);
        texts.Children.Add(_detail);
        Children.Add(texts);
    }

    public void Set(IReadOnlyList<DiskInfo> low)
    {
        var worst = low.OrderBy(d => d.Free).First();
        _ring.Fraction = worst.UsedFraction;
        var parts = low.OrderBy(d => d.Free).Take(2).Select(d => $"{d.Name} · {FormatGb(d.Free)} свободно");
        _detail.Text = string.Join("   ", parts) + (low.Count > 2 ? $"   ещё {low.Count - 2}" : "");
    }

    internal static string FormatGb(long bytes) => (bytes / 1073741824.0).ToString(bytes < 10L * 1073741824 ? "0.0" : "0", Ru) + " ГБ";
}
