using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Phone.Bridge;

/// <summary>Квадрат с QR-кодом: белая подложка со скруглением, чёрные модули (тихая зона 3 модуля). Размер модуля целый, чтобы края были чёткими.</summary>
internal sealed class QrView : FrameworkElement
{
    private readonly bool[,] _matrix;
    private readonly double _size;

    public QrView(bool[,] matrix, double size)
    {
        _matrix = matrix;
        _size = size;
        Width = Height = size;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Hues.Ink, null, new Rect(0, 0, _size, _size), 14, 14);
        var n = _matrix.GetLength(0);
        var cell = Math.Max(2, Math.Floor(_size / (n + 6)));
        var offset = Math.Floor((_size - cell * n) / 2);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    if (!_matrix[y, x]) continue;
                    double px = offset + x * cell, py = offset + y * cell;
                    ctx.BeginFigure(new Point(px, py), true, true);
                    ctx.LineTo(new Point(px + cell, py), false, false);
                    ctx.LineTo(new Point(px + cell, py + cell), false, false);
                    ctx.LineTo(new Point(px, py + cell), false, false);
                }
        }
        geometry.Freeze();
        dc.DrawGeometry(Brushes.Black, null, geometry);
    }
}

/// <summary>
/// Окно моста внутри вкладки «Телефон»: слева QR-код (и адрес текстом на случай, если камера не берёт), справа состояние, зона «брось файлы,
/// чтобы забрать на телефоне» (перетаскивание и «Выбрать файлы…») и лента: что ушло на телефон и что пришло с него. Принятые файлы
/// лежат в <c>Загрузки\Остров</c> и сразу попадают на полку; принятый текст — в историю буфера.
/// </summary>
internal sealed class BridgePanel : Grid
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Soft = Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static readonly Brush RowBack = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ZoneIdle = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ZoneHot = Frozen(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));

    private sealed record Entry(bool Incoming, string Path, string Name, long Size);

    private readonly BridgeServer _server;
    private readonly TextBlock _status = new() { FontSize = 12, Foreground = Soft, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _qrHost = new() { Width = 156, Height = 156 };
    private readonly TextBlock _url = new() { FontSize = 10.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
    private readonly Rectangle _zone = new() { RadiusX = 12, RadiusY = 12, StrokeThickness = 1.3, StrokeDashArray = [4, 3], Stroke = ZoneIdle };
    private readonly StackPanel _log = new();
    private readonly TextBlock _empty = new() { FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Text = "Пока пусто" };
    private readonly List<Entry> _entries = [];
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _shownUrl;

    /// <summary>Нажали «назад»: закрыть мост.</summary>
    public event Action? CloseRequested;

    public BridgePanel(BridgeServer server)
    {
        _server = server;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Шапка: назад, название, состояние.
        var back = Ui.IconButton("", 14, 28);
        back.ToolTip = "Закрыть мост";
        back.Click += (_, _) => CloseRequested?.Invoke();
        var title = new TextBlock { Text = "Мост с телефоном", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 1) };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumn(title, 1);
        SetColumn(_status, 2);
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(back);
        header.Children.Add(title);
        header.Children.Add(_status);
        Children.Add(header);

        // Тело: слева QR, справа зона и лента.
        var body = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetRow(body, 1);
        Children.Add(body);

        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(_qrHost);
        left.Children.Add(_url);
        body.Children.Add(left);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetColumn(right, 1);

        var zoneGrid = new Grid { Height = 66, Margin = new Thickness(0, 0, 0, 8) };
        zoneGrid.Children.Add(_zone);
        var zoneText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        zoneText.Children.Add(new TextBlock { Text = "Брось сюда файлы, чтобы забрать на телефоне", FontSize = 12, Foreground = Soft, HorizontalAlignment = HorizontalAlignment.Center });
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
        var pick = Ui.Pill("Выбрать файлы…", padding: 12);
        pick.Height = 24; pick.Margin = new Thickness(0, 0, 6, 0);
        pick.Click += (_, _) => PickFiles();
        var folder = Ui.Pill("Папка", padding: 12);
        folder.Height = 24; folder.Margin = new Thickness(0);
        folder.ToolTip = "Открыть Загрузки\\Остров";
        folder.Click += (_, _) => OpenFolder();
        folder.Margin = new Thickness(0, 0, 6, 0);
        var clip = Ui.Pill("Буфер: выкл", padding: 12);
        clip.Height = 24; clip.Margin = new Thickness(0);
        clip.ToolTip = "Показывать телефону последние тексты из буфера обмена (там бывают пароли и коды). Выключается, когда закрывается мост.";
        clip.Click += (_, _) =>
        {
            _server.ShareClipboard = !_server.ShareClipboard;
            clip.Content = _server.ShareClipboard ? "Буфер: вкл" : "Буфер: выкл";
        };
        pickRow.Children.Add(pick);
        pickRow.Children.Add(folder);
        pickRow.Children.Add(clip);
        zoneText.Children.Add(pickRow);
        zoneGrid.Children.Add(zoneText);
        right.Children.Add(zoneGrid);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _log };
        SetRow(scroll, 1);
        right.Children.Add(scroll);
        body.Children.Add(right);

        _tick.Tick += (_, _) => UpdateStatus();
        Loaded += (_, _) => { _tick.Start(); Refresh(); };
        Action<bool> onDrag = on => _zone.Stroke = on ? ZoneHot : ZoneIdle;
        Unloaded += (_, _) => { _tick.Stop(); IslandBus.FileDragChanged -= onDrag; };
        IslandBus.FileDragChanged += onDrag;
        Rebuild();
    }

    /// <summary>Подхватить адрес сервера (нарисовать QR) и обновить состояние.</summary>
    public void Refresh()
    {
        if (_server.Url != _shownUrl)
        {
            _shownUrl = _server.Url;
            _qrHost.Child = null;
            if (_shownUrl != null)
            {
                try { _qrHost.Child = new QrView(QrCode.Encode(_shownUrl), 156); }
                catch { /* адрес не влез в QR: остаётся текстом */ }
                _url.Text = _shownUrl.Replace("http://", "");
            }
            else _url.Text = _server.Error ?? "";
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_server.Running) { _status.Text = _server.Error ?? "Мост выключен"; return; }
        var online = (DateTime.UtcNow - _server.LastSeen).TotalSeconds < 12;
        if (!online && _server.Warning != null)
        {
            _status.Text = "Домашняя сеть идёт через VPN: телефон не достучится";
            var host = _server.Url is { } u ? new Uri(u).Host : "";
            var subnet = string.Join('.', host.Split('.').Take(3)) + ".0/24";
            _status.ToolTip = _server.Warning + ". Отключи VPN на время передачи (при включённом VPN домашняя сеть недоступна). Либо добавь сеть " + subnet + " в исключения раздельного туннелирования Amnezia.";
            return;
        }
        if (online) { _status.Text = "Телефон подключён"; _status.ToolTip = null; return; }
        // Телефон ещё ни разу не достучался: подсказываем, где искать причину (до ПК ничего не дошло — это сеть или телефон, не Windows).
        var waited = (DateTime.UtcNow - _server.StartedAt).TotalSeconds;
        if (_server.Connections == 0 && waited > 25)
        {
            _status.Text = "До ПК ничего не дошло. VPN на iPhone выключен?";
            _status.ToolTip = "Телефон должен быть в той же сети Wi-Fi, что и ПК (не на мобильном интернете), а VPN на iPhone выключен: через VPN адрес домашней сети недоступен.";
        }
        else if (_server.Connections > 0 && _server.LastSeen == DateTime.MinValue)
        {
            _status.Text = "Кто-то стучится, но ссылка неверная";
            _status.ToolTip = null;
        }
        else
        {
            _status.Text = "Наведи камеру iPhone на QR-код";
            _status.ToolTip = null;
        }
    }

    // ───── Лента ─────

    /// <summary>Файлы для телефона (из перетаскивания или выбора). Возвращает, сколько новых добавлено.</summary>
    public int AddOutgoing(IEnumerable<string> paths)
    {
        var list = paths.Where(File.Exists).ToList();
        var added = _server.AddToOutbox(list);
        foreach (var p in list)
            if (!_entries.Any(e => !e.Incoming && string.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase)))
            {
                long size = 0;
                try { size = new FileInfo(p).Length; } catch { }
                _entries.Insert(0, new Entry(false, p, System.IO.Path.GetFileName(p), size));
            }
        Rebuild();
        return added;
    }

    /// <summary>С телефона пришёл файл.</summary>
    public void AddIncoming(string path, long size)
    {
        _entries.Insert(0, new Entry(true, path, System.IO.Path.GetFileName(path), size));
        Rebuild();
    }

    /// <summary>С телефона пришёл текст.</summary>
    public void AddIncomingText(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        _entries.Insert(0, new Entry(true, "", line.Length > 60 ? line[..60] + "…" : line, text.Length));
        Rebuild();
    }

    private void Rebuild()
    {
        _log.Children.Clear();
        if (_entries.Count == 0) { _log.Children.Add(_empty); return; }
        foreach (var e in _entries.Take(30)) _log.Children.Add(BuildRow(e));
    }

    private UIElement BuildRow(Entry e)
    {
        var glyph = new TextBlock
        {
            Text = e.Incoming ? "" : "", FontFamily = Ui.IconFont, FontSize = 12, Foreground = Soft,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = e.Incoming ? "С телефона" : "На телефон"
        };
        var name = new TextBlock { Text = e.Name, FontSize = 12.5, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(name, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(name);

        FrameworkElement tail;
        if (e.Incoming)
            tail = new TextBlock { Text = e.Path.Length == 0 ? "текст" : Size(e.Size), FontSize = 11.5, Foreground = Soft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        else
        {
            var remove = Ui.IconButton("", 10, 22);
            remove.ToolTip = "Убрать с телефона";
            remove.Click += (_, _) =>
            {
                _server.RemoveFromOutbox(e.Path);
                _entries.Remove(e);
                Rebuild();
            };
            tail = remove;
        }
        SetColumn(tail, 2);
        grid.Children.Add(tail);

        var row = new Border { Background = RowBack, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 6, 0), Height = 28, Margin = new Thickness(0, 0, 0, 4), Child = grid };
        if (e.Incoming && e.Path.Length > 0)
        {
            row.Cursor = Cursors.Hand;
            row.ToolTip = "Показать в папке";
            row.MouseLeftButtonUp += (_, _) => RevealFile(e.Path);
        }
        return row;
    }

    private static string Size(long n) =>
        n < 1024 ? n + " Б" : n < 1_048_576 ? (n / 1024) + " КБ" : n < 1_073_741_824 ? (n / 1_048_576.0).ToString("0.#") + " МБ" : (n / 1_073_741_824.0).ToString("0.##") + " ГБ";

    private static void RevealFile(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch { /* проводник не открылся: не критично */ }
    }

    private static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(BridgeServer.ReceiveDir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + BridgeServer.ReceiveDir + "\"") { UseShellExecute = true });
        }
        catch { /* не открылась */ }
    }

    private void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Какие файлы отправить на телефон?", Multiselect = true, CheckFileExists = true };
        IslandBus.Hold(true); // пока открыто системное окно, остров не сворачивается
        try
        {
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddOutgoing(dialog.FileNames);
        }
        finally { IslandBus.Hold(true); } // мост открыт: остров по-прежнему держится
    }
}

/// <summary>Маленький значок QR-кода для кнопки в шапке вкладки: три «поисковых» квадрата и несколько точек (16×16).</summary>
internal sealed class QrIcon : FrameworkElement
{
    public QrIcon()
    {
        Width = Height = 16;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(Hues.Ink, 1.3);
        foreach (var (x, y) in new[] { (1.0, 1.0), (10.0, 1.0), (1.0, 10.0) })
        {
            dc.DrawRoundedRectangle(null, pen, new Rect(x + 0.65, y + 0.65, 3.7, 3.7), 0.8, 0.8);
            dc.DrawRectangle(Hues.Ink, null, new Rect(x + 1.9, y + 1.9, 1.2, 1.2));
        }
        foreach (var (x, y) in new[] { (10.0, 10.0), (13.0, 10.0), (10.0, 13.0), (13.0, 13.0), (11.5, 11.5) })
            dc.DrawRectangle(Hues.Ink, null, new Rect(x, y, 1.6, 1.6));
    }
}
