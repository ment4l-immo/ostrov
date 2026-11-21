using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Ostrov.Services;

namespace Ostrov.Modules.Projects;

/// <summary>
/// Содержимое вкладки «Проекты». Сверху переключатель «Избранные | Недавние», поле поиска и (для «Избранных») кнопка «+ Добавить».
/// «Избранные» — список, который ведёт пользователь (закреплённые сверху); «Недавние» — папки, которые открывали в VS Code,
/// по свежести работы над ними (без тех, что уже в «Избранных»).
/// Строка: слева в отдельном поле точка «открыт сейчас в VS Code», название папки и под ним серым «родительская папка · когда»
/// (<see cref="ProjectCaption"/>). Клик открывает папку в VS Code и сворачивает остров.
/// При наведении справа появляются кнопки: булавка (закрепить, только в «Избранных») и «×» (убрать из «Избранных») или «+» (добавить недавний в «Избранные»).
/// Поиск: поле всегда в шапке; печатать можно сразу, без клика. Стрелки двигают подсветку, Enter открывает подсвеченное, Esc очищает.
/// </summary>
internal sealed class ProjectsView : Grid
{
    private const int MaxRecents = 15;
    public const double RowHeight = 40;

    // Значки шрифта Segoe Fluent Icons.
    private const string GlyphClose = "", GlyphAdd = "", GlyphPin = "", GlyphPinned = "", GlyphSearch = "";

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static readonly Brush Selected = Frozen(Color.FromRgb(0x40, 0x40, 0x40));
    private static readonly Brush FieldBackground = Frozen(Hues.GlassSurfaceColor);

    /// <summary>Строка списка и то, что нужно, чтобы её перекрасить (подсветка, кнопки справа).</summary>
    private sealed class RowItem(Border row, Action open, Button action, Button? pin, bool pinned)
    {
        public Border Row { get; } = row;
        public Action Open { get; } = open;
        public HoverGlow Glow { get; } = new();

        public void Paint(bool hot)
        {
            Row.Background = hot ? Glow.Brush : Brushes.Transparent;
            action.Opacity = hot ? 1 : 0;
            if (pin != null) pin.Opacity = hot ? 1 : pinned ? 0.5 : 0; // закреплённый помечен приглушённой булавкой и без наведения
        }
    }

    private readonly ProjectsStore _store;
    private readonly Func<string, (bool Ok, string? Error)> _open;
    private readonly Func<string?> _pickFolder;
    private readonly Func<IReadOnlyList<RecentProject>> _recents;
    private readonly Func<IReadOnlySet<string>> _openNow;

    private readonly Button _tabMine = Ui.Pill("Избранные");
    private readonly Button _tabRecent = Ui.Pill("Недавние");
    private readonly Button _add = Ui.Pill("+  Добавить");
    private readonly StackPanel _list = new();
    private readonly TextBlock _empty = new() { Foreground = Ui.Gray, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Foreground = Ui.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private readonly TextBox _filter = Ui.BareTextBox();
    private readonly TextBlock _filterHint = new() { Text = "Поиск", Foreground = Dim, FontSize = 12.5, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
    private readonly Button _filterClear = Ui.IconButton(GlyphClose, 9, 20);
    private readonly Border _filterBox = new() { Width = 154, Height = 28, CornerRadius = new CornerRadius(14), Background = FieldBackground };

    private readonly Grid _middle; // средняя ячейка шапки: поле поиска (или короткое сообщение на его месте)
    private readonly List<RowItem> _rows = [];
    private int _hot = -1; // подсвеченная строка (под мышью или выбранная стрелками); Enter открывает её
    private bool _showRecent;
    private int _buildVersion; // каждая перестройка списка отменяет ещё не завершённую загрузку недавних
    private IReadOnlyList<RecentProject>? _recentsCache;
    private DateTime _recentsLoadedAt;
    private IReadOnlySet<string> _openNames = new HashSet<string>();
    private bool _autoCollapseSuspended;
    private bool _hooked;

    private string Filter => _filter.Text.Trim();

    /// <param name="open">Как открыть папку (по умолчанию — в VS Code). Подменяется для проверки.</param>
    /// <param name="pickFolder">Как выбрать папку (по умолчанию — системное окно выбора). Подменяется для проверки.</param>
    /// <param name="recents">Откуда брать недавние (по умолчанию — хранилище VS Code). Подменяется для проверки.</param>
    /// <param name="openNow">Какие папки открыты сейчас в окнах VS Code (по умолчанию — по заголовкам окон). Подменяется для проверки.</param>
    public ProjectsView(
        ProjectsStore store,
        Func<string, (bool Ok, string? Error)>? open = null,
        Func<string?>? pickFolder = null,
        Func<IReadOnlyList<RecentProject>>? recents = null,
        Func<IReadOnlySet<string>>? openNow = null)
    {
        _store = store;
        _open = open ?? (folder => (VsCode.TryOpen(folder, out var error), error));
        _pickFolder = pickFolder ?? PickFolderDialog;
        _recents = recents ?? (() => VsCodeRecents.Load());
        _openNow = openNow ?? VsCodeWindows.OpenFolderNames;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        foreach (var b in new[] { _tabMine, _tabRecent })
        {
            b.Margin = new Thickness(0);
            b.Height = 26;
        }
        _tabMine.Click += (_, _) => SwitchTo(recent: false);
        _tabRecent.Click += (_, _) => SwitchTo(recent: true);
        var switcher = new Border
        {
            CornerRadius = new CornerRadius(15), Background = FieldBackground, Padding = new Thickness(2),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _tabMine, _tabRecent } }
        };
        PillSlider.Attach(switcher);

        _add.Height = 28;
        _add.Margin = new Thickness(0);
        _add.HorizontalAlignment = HorizontalAlignment.Right;
        _add.Click += (_, _) => AddProject();

        // Поле поиска: лупа, текст с подсказкой «Поиск», крестик очистки. Текст в строке состояния (ошибки) временно занимает его место.
        _filter.FontSize = 12.5;
        _filter.VerticalAlignment = VerticalAlignment.Center;
        _filterClear.Visibility = Visibility.Collapsed;
        _filterClear.Margin = new Thickness(0, 0, 4, 0);
        _filterClear.Foreground = Ui.Gray;
        _filterClear.Click += (_, _) => { _filter.Text = ""; _filter.Focus(); };
        var glyph = new TextBlock { Text = GlyphSearch, FontFamily = Ui.IconFont, FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 7, 0) };
        var textArea = new Grid { Children = { _filterHint, _filter } };
        var fieldGrid = new Grid();
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(glyph, 0);
        SetColumn(textArea, 1);
        SetColumn(_filterClear, 2);
        fieldGrid.Children.Add(glyph);
        fieldGrid.Children.Add(textArea);
        fieldGrid.Children.Add(_filterClear);
        _filterBox.Child = fieldGrid;
        _filterBox.Cursor = Cursors.IBeam;
        _filterBox.MouseLeftButtonDown += (_, _) => _filter.Focus();
        _filter.TextChanged += (_, _) => OnFilterChanged();
        _filter.PreviewKeyDown += OnFilterKeyDown;

        _middle = new Grid { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 10, 0), Children = { _filterBox, _status } };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(switcher, 0);
        SetColumn(_middle, 1);
        SetColumn(_add, 2);
        header.Children.Add(switcher);
        header.Children.Add(_middle);
        header.Children.Add(_add);
        SetRow(header, 0);
        Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _list, Margin = new Thickness(0, 0, 0, 6) };
        Ui.SoftBottom(scroll);
        SetRow(scroll, 1);
        SetRow(_empty, 1);
        Children.Add(scroll);
        Children.Add(_empty);

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _status.Text = ""; _filterBox.Opacity = 1; _filterBox.IsHitTestVisible = true; };
        // Остров раскрывается заново — поиск начинается с чистого листа (иначе вернулся бы старый фильтр).
        IslandBus.Expanding += () => { if (_filter.Text.Length > 0) _filter.Text = ""; };
        Loaded += (_, _) =>
        {
            Rebuild();
            FocusFilter();
            // Остров раскрыт — окно активируется: перечитываем списки, чтобы заметить переименованные, удалённые или новые папки.
            if (!_hooked && Window.GetWindow(this) is { } window)
            {
                _hooked = true;
                window.Activated += (_, _) => { Rebuild(); FocusFilter(); };
            }
        };
    }

    /// <summary>Фокус в поле поиска: так можно печатать сразу, а стрелки не переключают вкладки.</summary>
    private void FocusFilter() =>
        Dispatcher.BeginInvoke(() => { if (IsLoaded) Keyboard.Focus(_filter); }, DispatcherPriority.Input);

    private void SwitchTo(bool recent)
    {
        _showRecent = recent;
        Rebuild();
        Ui.FadeIn(_list);
    }

    private void OnFilterChanged()
    {
        var has = _filter.Text.Length > 0;
        _filterHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _filterClear.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        // Пока в поле есть текст, остров не сворачивается сам через 5 секунд (мышь может быть в стороне от острова, пока печатаешь).
        if (has != _autoCollapseSuspended)
        {
            _autoCollapseSuspended = has;
            IslandBus.SuspendAutoCollapse(has);
        }
        Rebuild(typing: true);
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (_rows.Count > 0) SetHot(Math.Min(_rows.Count - 1, _hot + 1), scroll: true);
                e.Handled = true;
                break;
            case Key.Up:
                if (_rows.Count > 0) SetHot(Math.Max(0, _hot - 1), scroll: true);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_hot >= 0 && _hot < _rows.Count) _rows[_hot].Open();
                e.Handled = true;
                break;
            case Key.Escape when _filter.Text.Length > 0:
                _filter.Text = "";
                e.Handled = true;
                break;
        }
    }

    /// <param name="typing">Перестройка из-за набора текста: недавние берём из памяти, открытые окна VS Code заново не опрашиваем.</param>
    private void Rebuild(bool typing = false)
    {
        Ui.MarkSelected(_tabMine, !_showRecent);
        Ui.MarkSelected(_tabRecent, _showRecent);
        _add.Visibility = _showRecent ? Visibility.Collapsed : Visibility.Visible;
        _middle.Margin = new Thickness(10, 0, _showRecent ? 0 : 10, 0); // без кнопки «Добавить» поле встаёт вровень с правым краем строк

        if (!typing)
        {
            try { _openNames = _openNow(); }
            catch { _openNames = new HashSet<string>(); }
        }

        _buildVersion++;
        _list.Children.Clear();
        _rows.Clear();
        _hot = -1;
        if (_showRecent) _ = BuildRecentAsync(useStale: typing);
        else BuildMine();
    }

    private void BuildMine()
    {
        var filter = Filter;
        var items = _store.GetAll().Where(p => ProjectCaption.Matches(p.Path, filter)).ToList();
        _empty.Text = filter.Length > 0 ? "Ничего не нашлось" : "Добавь папку проекта кнопкой «+ Добавить»\nили выбери из недавних";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var now = DateTime.Now;
        foreach (var project in items)
        {
            var path = project.Path;
            var pinned = project.Pinned;
            var caption = ProjectCaption.Favorite(path, project.LastOpened, now);
            AddRow(path, caption, GlyphClose, "Убрать из списка", () => { _store.Remove(path); Rebuild(typing: true); }, touch: true,
                pinned, () => { _store.SetPinned(path, !pinned); Rebuild(typing: true); });
        }
        FinishBuild();
    }

    /// <summary>Сканирование файлов проектов идёт в фоне; результат держим 30 секунд, чтобы не пересчитывать при каждом раскрытии.</summary>
    private async Task BuildRecentAsync(bool useStale)
    {
        var version = _buildVersion;
        if (_recentsCache == null || (!useStale && DateTime.Now - _recentsLoadedAt > TimeSpan.FromSeconds(30)))
        {
            _empty.Text = "Загрузка…";
            _empty.Visibility = Visibility.Visible;
            IReadOnlyList<RecentProject> loaded;
            try { loaded = await Task.Run(() => _recents()); }
            catch { loaded = []; }
            if (version != _buildVersion) return; // пока грузили, вкладку переключили или список перестроили
            _recentsCache = loaded;
            _recentsLoadedAt = DateTime.Now;
        }

        var filter = Filter;
        var mine = _store.GetAll().Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = _recentsCache.Where(r => !mine.Contains(r.Path.TrimEnd('\\', '/')))
            .Where(r => ProjectCaption.Matches(r.Path, filter)).Take(MaxRecents).ToList();
        _empty.Text = filter.Length > 0 ? "Ничего не нашлось" : "Недавних проектов VS Code не нашлось\n(или все они уже в избранных)";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Children.Clear();
        _rows.Clear();
        var now = DateTime.Now;
        foreach (var recent in items)
        {
            var path = recent.Path;
            var caption = ProjectCaption.Recent(path, recent.LastActive, now);
            AddRow(path, caption, GlyphAdd, "Добавить в избранные", () => { _store.Add(path); Rebuild(typing: true); }, touch: false, pinned: null, togglePin: null);
        }
        FinishBuild();
    }

    /// <summary>С текстом в поиске подсвечена первая найденная строка: Enter откроет её.</summary>
    private void FinishBuild() => SetHot(Filter.Length > 0 && _rows.Count > 0 ? 0 : -1);

    private void SetHot(int index, bool scroll = false)
    {
        _hot = index;
        for (var i = 0; i < _rows.Count; i++) _rows[i].Paint(i == _hot);
        if (scroll && index >= 0 && index < _rows.Count) _rows[index].Row.BringIntoView();
    }

    /// <summary>
    /// Две строки: название и под ним серая подпись («Desktop · вчера»); слева поле с точкой «открыт сейчас в VS Code».
    /// Полный путь — в подсказке на всей строке. <paramref name="pinned"/> = null — булавки нет (в «Недавних»).
    /// </summary>
    private void AddRow(string path, string caption, string actionGlyph, string actionTip, Action action, bool touch, bool? pinned, Action? togglePin)
    {
        var exists = Directory.Exists(path);
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = path; // корень диска
        var isOpen = exists && _openNames.Contains(name);

        var nameText = new TextBlock
        {
            Text = name, FontSize = 13, Foreground = exists ? Hues.Ink : Dim,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var captionText = new TextBlock
        {
            Text = exists ? caption : "папка не найдена", FontSize = 11.5, Foreground = exists ? Ui.Gray : Dim,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0)
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { nameText, captionText } };

        // Точка стоит против строки с названием (как «непрочитанное» в Почте); у остальных поле остаётся пустым, чтобы текст не «гулял».
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6, Fill = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 0, 0), IsHitTestVisible = false,
            Visibility = isOpen ? Visibility.Visible : Visibility.Hidden
        };

        var button = Ui.IconButton(actionGlyph, 10, 22);
        button.ToolTip = actionTip;
        button.Opacity = 0;
        button.Click += (_, _) => action();

        Button? pinButton = null;
        if (pinned is { } isPinned && togglePin != null)
        {
            pinButton = Ui.IconButton(isPinned ? GlyphPinned : GlyphPin, 11, 22);
            pinButton.ToolTip = isPinned ? "Открепить" : "Закрепить сверху";
            pinButton.Opacity = isPinned ? 0.5 : 0;
            pinButton.Click += (_, _) => togglePin();
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(dot, 0);
        SetColumn(texts, 1);
        SetColumn(button, 3);
        grid.Children.Add(dot);
        grid.Children.Add(texts);
        grid.Children.Add(button);
        if (pinButton != null)
        {
            SetColumn(pinButton, 2);
            grid.Children.Add(pinButton);
        }

        var row = new Border
        {
            Height = RowHeight, CornerRadius = new CornerRadius(10), Padding = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent, Cursor = exists ? Cursors.Hand : Cursors.Arrow, Child = grid, ToolTip = path
        };
        var item = new RowItem(row, () => Open(path, exists, touch), button, pinButton, pinned == true);
        _rows.Add(item);
        _list.Children.Add(row);

        row.MouseEnter += (_, a) => { item.Glow.Follow(row, a); SetHot(_rows.IndexOf(item)); };
        row.MouseMove += (_, a) => { item.Glow.Follow(row, a); if (_hot != _rows.IndexOf(item)) SetHot(_rows.IndexOf(item)); };
        row.MouseLeave += (_, _) => { if (_hot == _rows.IndexOf(item)) SetHot(-1); };
        row.MouseLeftButtonUp += (_, _) => item.Open();
    }

    private void Open(string path, bool exists, bool touch)
    {
        if (!exists) { ShowStatus("Папка не найдена"); return; }
        var (ok, error) = _open(path);
        if (!ok) { ShowStatus(error ?? "Не удалось открыть"); return; }
        if (touch)
        {
            _store.Touch(path);
            Rebuild(typing: true);
        }
        IslandBus.RequestCollapse();
    }

    private void AddProject()
    {
        var folder = _pickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;
        _store.Add(folder);
        Rebuild();
    }

    /// <summary>Системное окно выбора папки. Пока оно открыто, остров не сворачивается.</summary>
    private string? PickFolderDialog()
    {
        var dialog = new OpenFolderDialog { Title = "Выбери папку проекта", Multiselect = false };
        IslandBus.Hold(true);
        try { return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null; }
        finally { IslandBus.Hold(false); }
    }

    /// <summary>Короткое сообщение в шапке: на 4 секунды занимает место поля поиска.</summary>
    private void ShowStatus(string text)
    {
        _status.Text = text;
        _filterBox.Opacity = 0;
        _filterBox.IsHitTestVisible = false;
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
