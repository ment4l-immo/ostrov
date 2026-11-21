using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ostrov.Services;

namespace Ostrov.Modules.Buffer;

/// <summary>
/// Содержимое вкладки «Буфер». Сверху переключатель «История | Полка» и кнопка «Очистить» (для открытого раздела).
/// История: строки с текстом или картинкой, клик кладёт запись в буфер обмена и сворачивает остров.
/// Полка: файлы, перетащенные на остров; строку можно перетащить из острова наружу (тогда она уходит с полки).
/// Пока файлы тянут над островом, показывается подсказка «Отпусти…».
/// </summary>
internal sealed class BufferView : Grid
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static readonly Brush Selected = Frozen(Color.FromRgb(0x40, 0x40, 0x40));
    private static readonly Brush HintBg = Frozen(Color.FromArgb(0xF8, 0x00, 0x00, 0x00));
    private static readonly Brush HintLine = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));

    private readonly ClipboardHistory _history;
    private readonly ShelfStore _shelf;
    private readonly Func<ClipEntry, bool> _restore;

    private readonly Button _tabHistory = Ui.Pill("История");
    private readonly Button _tabShelf = Ui.Pill("Полка");
    private readonly Button _clear = Ui.Pill("Очистить");
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty = new() { Foreground = Ui.Gray, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Foreground = Ui.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 1) };
    private readonly TextBlock _statusIcon = new() { Text = "", FontFamily = Ui.IconFont, FontSize = 13, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 1, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly Border _dropHint;
    private readonly Dictionary<string, BitmapSource> _thumbs = [];
    private bool _showShelf;
    private bool _hooked;

    public BufferView(ClipboardHistory history, ShelfStore shelf, Func<ClipEntry, bool> restore)
    {
        _history = history;
        _shelf = shelf;
        _restore = restore;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        foreach (var b in new[] { _tabHistory, _tabShelf })
        {
            b.Margin = new Thickness(0);
            b.Height = 26;
        }
        _tabHistory.Click += (_, _) => SwitchTo(shelf: false);
        _tabShelf.Click += (_, _) => SwitchTo(shelf: true);
        var switcher = new Border
        {
            CornerRadius = new CornerRadius(15), Background = Frozen(Hues.GlassSurfaceColor), Padding = new Thickness(2),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _tabHistory, _tabShelf } }
        };
        PillSlider.Attach(switcher);

        _clear.Height = 28;
        _clear.Margin = new Thickness(0);
        _clear.HorizontalAlignment = HorizontalAlignment.Right;
        _clear.Click += (_, _) =>
        {
            if (_showShelf) _shelf.Clear();
            else _history.Clear();
        };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { switcher, _statusIcon, _status } });
        header.Children.Add(_clear);
        SetRow(header, 0);
        Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list, Margin = new Thickness(0, 0, 0, 6) };
        Ui.SoftBottom(scroll);
        SetRow(scroll, 1);
        SetRow(_empty, 1);
        Children.Add(scroll);
        Children.Add(_empty);

        _dropHint = new Border
        {
            Background = HintBg, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 6),
            BorderBrush = HintLine, BorderThickness = new Thickness(1.5), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Отпусти, чтобы положить на полку", Foreground = Hues.Ink, FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        SetRow(_dropHint, 1);
        Children.Add(_dropHint);

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); ClearStatus(); };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            ClearStatus();
            IslandBus.RequestCollapse();
        };

        _history.Changed += () => { if (IsLoaded && !_showShelf) Rebuild(); };
        _shelf.Changed += () => { if (IsLoaded && _showShelf) Rebuild(); };
        IslandBus.FileDragChanged += OnFileDrag;
        IslandBus.FilesDropped += OnFilesDropped;

        Loaded += (_, _) =>
        {
            Rebuild();
            // Остров раскрыт — окно активируется: на полке перечитываем, не пропали ли файлы.
            if (!_hooked && Window.GetWindow(this) is { } window)
            {
                _hooked = true;
                // Перестраиваем только если что-то изменилось: иначе первый клик по острову (он и активирует окно)
                // пересоздал бы строки прямо под курсором, и нажатие, с которого начинается перетаскивание, потерялось бы.
                window.Activated += (_, _) => { if (_showShelf && ShelfSignature() != _shelfSignature) Rebuild(); };
            }
        };
    }

    private void OnFileDrag(bool on)
    {
        if (on && !_showShelf) SwitchTo(shelf: true);
        _dropHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFilesDropped(string[] paths)
    {
        _dropHint.Visibility = Visibility.Collapsed;
        if (!_showShelf) SwitchTo(shelf: true);
    }

    private void SwitchTo(bool shelf)
    {
        _showShelf = shelf;
        Rebuild();
        Ui.FadeIn(_list);
    }

    private void Rebuild()
    {
        Ui.MarkSelected(_tabHistory, !_showShelf);
        Ui.MarkSelected(_tabShelf, _showShelf);
        _list.Children.Clear();
        if (_showShelf) BuildShelf();
        else BuildHistory();
        _clear.IsEnabled = _list.Children.Count > 0;
    }

    // ───── История ─────

    private void BuildHistory()
    {
        var items = _history.Entries;
        _empty.Text = "Здесь появится всё, что копируешь\n(текст и картинки)";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in items.ToList())
            _list.Children.Add(BuildHistoryRow(entry));
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private UIElement BuildHistoryRow(ClipEntry entry)
    {
        UIElement leading;
        string title;
        string tooltip;
        if (entry.Kind == ClipKind.Image)
        {
            title = $"Изображение  {entry.Width}×{entry.Height}";
            tooltip = "Открыть (значок слева — скопировать)";
            // Миниатюра слева: при наведении на ней появляется значок копирования, клик по ней копирует картинку.
            var picture = new Border
            {
                CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Frozen(Hues.SurfaceColor),
                Child = Thumbnail(entry) is { } thumb ? new Image { Source = thumb, Stretch = Stretch.UniformToFill } : null
            };
            var shade = new Border
            {
                CornerRadius = new CornerRadius(4), Background = Frozen(Color.FromArgb(0xB3, 0, 0, 0)), Opacity = 0,
                Child = new TextBlock
                {
                    Text = "", FontFamily = Ui.IconFont, FontSize = 13, Foreground = Hues.Ink,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            var host = new Grid
            {
                Width = IconWidth, Height = 22, Margin = new Thickness(0, 0, 10, 0), Background = Brushes.Transparent,
                ToolTip = "Скопировать в буфер обмена", Children = { picture, shade },
                // Скругление обрезает и картинку, и затемнение одинаково (у Border рамка скругляется, а содержимое остаётся прямоугольным:
                // без этого по углам из-под затемнения торчали бы острые края миниатюры).
                Clip = new RectangleGeometry(new Rect(0, 0, IconWidth, 22), 4, 4)
            };
            host.MouseEnter += (_, _) => shade.Opacity = 1;
            host.MouseLeave += (_, _) => shade.Opacity = 0;
            leading = host;
        }
        else
        {
            leading = Glyph("", Ui.Gray);
            var text = entry.Text ?? "";
            title = Spaces.Replace(text.Trim(), " ");
            if (title.Length > 200) title = title[..200];
            tooltip = text.Length > 600 ? text[..600] + "…" : text;
        }

        var time = entry.Time.Date == DateTime.Today ? entry.Time.ToString("HH:mm") : entry.Time.ToString("dd.MM");
        void Copy()
        {
            if (_restore(entry)) ConfirmCopied();
            else ShowStatus("Буфер занят — попробуй ещё раз");
        }
        var isImage = entry.Kind == ClipKind.Image;
        // Текст: клик по строке копирует (открывать его негде). Картинка: клик по строке открывает, значок слева копирует.
        return BuildRow(leading, title, Hues.Ink, time, tooltip, "Убрать из истории", () => _history.Remove(entry),
            click: isImage ? () => OpenPath(_history.ImagePath(entry)) : Copy, drag: null,
            key: entry.Id, leadingClick: isImage ? Copy : null);
    }

    /// <summary>Ширина значка слева у всех строк: названия в истории и на полке начинаются на одной линии.</summary>
    private const double IconWidth = 36;

    private static TextBlock Glyph(string glyph, Brush brush) => new()
    {
        Text = glyph, FontFamily = Ui.IconFont, FontSize = 15, Width = IconWidth, TextAlignment = TextAlignment.Center,
        Foreground = brush, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
    };

    private BitmapSource? Thumbnail(ClipEntry entry)
    {
        if (_thumbs.TryGetValue(entry.Id, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelHeight = 44;
            image.UriSource = new Uri(_history.ImagePath(entry));
            image.EndInit();
            image.Freeze();
            _thumbs[entry.Id] = image;
            return image;
        }
        catch { return null; }
    }

    // ───── Полка ─────

    private void BuildShelf()
    {
        var items = _shelf.GetAll();
        _shelfSignature = ShelfSignature();
        _empty.Text = "Перетащи файлы на остров —\nони полежат здесь, пока не понадобятся";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var path in items)
            _list.Children.Add(BuildShelfRow(path));
    }

    private string _shelfSignature = "";
    private string? _pressedKey; // запись, на которой нажали левую кнопку мыши

    /// <summary>Что сейчас на полке и существует ли это: по изменению понимаем, что список надо перестроить.</summary>
    private string ShelfSignature() =>
        string.Join("|", _shelf.GetAll().Select(p => (File.Exists(p) || Directory.Exists(p) ? "+" : "-") + p));

    /// <summary>Открыть файл, картинку или папку программой по умолчанию (как двойной щелчок в проводнике) и свернуть остров.</summary>
    private void OpenPath(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            IslandBus.RequestCollapse();
        }
        catch { ShowStatus("Не удалось открыть"); }
    }

    private UIElement BuildShelfRow(string path)
    {
        var isDir = Directory.Exists(path);
        var exists = isDir || File.Exists(path);
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = path;

        var icon = Glyph(isDir ? "" : "", exists ? Ui.Gray : Dim);
        var where = exists ? (Path.GetDirectoryName(path) ?? "") : "файл не найден";
        return BuildRow(icon, name, exists ? Hues.Ink : Dim, where, path, "Убрать с полки", () => _shelf.Remove(path),
            click: exists ? () => OpenPath(path) : null, drag: exists ? path : null, key: path);
    }

    /// <summary>Общая строка списка: [значок] название — серая подпись справа — «×» при наведении.</summary>
    private UIElement BuildRow(UIElement leading, string title, Brush titleBrush, string right, string tooltip,
        string actionTip, Action remove, Action? click, string? drag, string key, Action? leadingClick = null)
    {
        var titleText = new TextBlock
        {
            Text = title, FontSize = 13, Foreground = titleBrush, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip
        };
        var rightText = new TextBlock
        {
            Text = right, FontSize = 11.5, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 6, 0)
        };
        var button = Ui.IconButton("", 10, 22);
        button.ToolTip = actionTip;
        button.Opacity = 0;
        button.Click += (_, _) => remove();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(leading, 0);
        grid.Children.Add(leading);
        SetColumn(titleText, 1);
        SetColumn(rightText, 2);
        SetColumn(button, 3);
        grid.Children.Add(titleText);
        grid.Children.Add(rightText);
        grid.Children.Add(button);

        var row = new Border
        {
            Height = 32, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 4, 0),
            Background = Brushes.Transparent, Cursor = click != null ? Cursors.Hand : Cursors.Arrow, Child = grid
        };
        var glow = new HoverGlow();
        row.MouseEnter += (_, a) => { glow.Follow(row, a); row.Background = glow.Brush; button.Opacity = 1; };
        row.MouseMove += (_, a) => glow.Follow(row, a);
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; button.Opacity = 0; };

        // Нажатие и отпускание на одной и той же записи — клик (запись узнаём по ключу, а не по самой строке:
        // если список перестроился между нажатием и отпусканием, клик не теряется);
        // сдвиг мыши с зажатой кнопкой дальше порога — перетаскивание наружу.
        Point? start = null;
        row.PreviewMouseLeftButtonDown += (_, e) => { _pressedKey = key; start = e.GetPosition(row); };
        row.MouseLeave += (_, _) => { if (Mouse.LeftButton != MouseButtonState.Pressed) start = null; };
        row.MouseLeftButtonUp += (_, _) =>
        {
            start = null;
            if (_pressedKey != key) return;
            _pressedKey = null;
            click?.Invoke();
        };
        if (leadingClick != null && leading is FrameworkElement lead)
        {
            lead.Cursor = Cursors.Hand;
            lead.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true; // клик по значку не должен ещё и открывать строку
                start = null;
                if (_pressedKey != key) return;
                _pressedKey = null;
                leadingClick();
            };
        }
        if (drag != null)
        {
            row.MouseMove += (_, e) =>
            {
                if (start is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
                var d = e.GetPosition(row) - from;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                start = null;
                _pressedKey = null;
                DragOut(row, drag);
            };
        }
        return row;
    }

    /// <summary>
    /// Вытащить файл с полки наружу. Файл только копируется (разрешены «копировать» и «ссылка», «перемещение» нет),
    /// исходный остаётся на месте. Если бросили куда-то принимающее — запись уходит с полки.
    /// </summary>
    private void DragOut(UIElement source, string path)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path });
        data.SetData("Ostrov.Shelf", true); // метка: свой же остров эту перетяжку не принимает
        DragDropEffects effect;
        IslandBus.Hold(true);
        try { effect = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Link); }
        finally { IslandBus.Hold(false); }
        if (effect != DragDropEffects.None) _shelf.Remove(path);
    }

    private void ShowStatus(string text, bool success = false)
    {
        _status.Text = text;
        _status.Foreground = success ? Hues.Ink : Ui.Gray;
        _status.Margin = new Thickness(success ? 6 : 12, 0, 0, 1);
        _statusIcon.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void ClearStatus()
    {
        _status.Text = "";
        _statusIcon.Visibility = Visibility.Collapsed;
    }

    /// <summary>Запись положена в буфер: показываем «Скопировано» и сворачиваем остров, когда надпись успели прочитать.</summary>
    private void ConfirmCopied()
    {
        ShowStatus("Скопировано", success: true);
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }
}
