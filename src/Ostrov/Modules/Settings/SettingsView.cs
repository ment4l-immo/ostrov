using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ostrov.Search;
using Ostrov.Services;

namespace Ostrov.Modules.Settings;

/// <summary>
/// Содержимое вкладки «Настройки». Сверху переключатель «Общие | ИИ и поиск | Программы», под ним строки «название — элемент управления».
/// Изменения применяются сразу, кнопки «Сохранить» нет (настройки пишутся в <c>settings.json</c>, ключ OpenAI — отдельно, в зашифрованном виде).
/// Общие: автозапуск с Windows, через сколько секунд неподвижной мыши остров сворачивается сам, градусы или радианы в калькуляторе.
/// ИИ и поиск: ключ OpenAI (заменить, удалить), модель (список или своё имя), горячая клавиша быстрого вопроса.
/// Программы (М-006): список программ, над которыми остров сжимается в нотч, пока они в фокусе (по умолчанию браузеры); у каждой выключатель,
/// добавленные самим пользователем можно убрать, а новые добавить из списка открытых окон.
/// </summary>
internal sealed class SettingsView : Grid
{
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static readonly Brush Selected = Frozen(Color.FromRgb(0x40, 0x40, 0x40));
    private static readonly Brush FieldBackground = Frozen(Hues.GlassSurfaceColor);

    private const double RowHeight = 42;
    private static readonly int[] CollapseChoices = [3, 5, 10, 0];
    private static readonly (string Label, string Model)[] Models = [("5-mini", "gpt-5-mini"), ("5.4-mini", "gpt-5.4-mini"), ("6.1-terra", "gpt-6.1-terra")];

    private readonly Button _tabGeneral = Ui.Pill("Общие");
    private readonly Button _tabAi = Ui.Pill("ИИ и поиск");
    private readonly Button _tabApps = Ui.Pill("Программы");
    private readonly Button _tabServer = Ui.Pill("Сервер");
    private readonly Button _tabDictation = Ui.Pill("Диктовка");
    private readonly Grid _dict = new();
    private readonly StackPanel _micList = new();
    private readonly StackPanel _server = new();
    private readonly StackPanel _general = new();
    private readonly StackPanel _ai = new();
    private readonly ScrollViewer _aiHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    private readonly Grid _apps = new();
    private int _page;
    private readonly TextBlock _status = new() { Foreground = Hues.Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
    // Сообщение о результате («Ключ сохранён») всплывает плашкой в правом нижнем углу страницы: в строке с переключателем страниц для него нет места.
    private readonly Border _statusChip = new() { CornerRadius = new CornerRadius(13), Background = Frozen(Hues.SurfaceHighColor), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 0, 4, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // Общие
    private readonly ToggleSwitch _autostart = new();
    private readonly PicturePicker _night = new("Ночной режим", "с 22 до 7 приглушает фоны и белый", ["Выкл", "С 22 до 7"], SettingsPictures.Night);
    private readonly ToggleSwitch _journalOn = new();
    private readonly ScrollViewer _generalHost = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    private readonly ToggleSwitch _dictation = new();
    private readonly PicturePicker _collapse = new("Сворачивать остров сам", "когда мышь неподвижна", ["3 с", "5 с", "10 с", "Никогда"], SettingsPictures.Collapse, 56);
    private readonly SegmentedControl _trig = new("Градусы", "Радианы");
    private readonly PicturePicker _glass = new("Стекло острова", "насколько остров просвечивает", ["Слабо", "Средне", "Сильно"], SettingsPictures.Glass, 80);
    private readonly ToggleSwitch _blurOn = new();
    private readonly TextBox _dictWords = Ui.BareTextBox();

    // ИИ и поиск
    private readonly TextBlock _keyState = new() { FontSize = 12, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _keyReplace = Ui.Pill("Заменить", padding: 12);
    private readonly Button _keyDelete = Ui.Pill("Удалить", padding: 12);
    private readonly TextBox _keyBox = Ui.BareTextBox();
    private readonly Border _keyField = new() { Height = 28, CornerRadius = new CornerRadius(14), Background = FieldBackground, Padding = new Thickness(12, 0, 12, 1), Visibility = Visibility.Collapsed, Width = 200 };
    private readonly PicturePicker _model = new("Модель ответов", "для вопросов по Alt+Q", Models.Select(m => m.Label).ToArray(), i => SettingsPictures.Model(Models[i].Label), 74, 30);
    private readonly TextBox _customModel = Ui.BareTextBox();
    private readonly Button _hotkey = Ui.Pill("Alt+Q", padding: 14);
    private bool _capturing;
    private bool _capturingDict;
    private readonly Button _dictKey = Ui.Pill("Правый Alt", padding: 14);
    private bool _loading;

    // Сервер (тоннель до своего сервера для OpenAI без VPN)
    private readonly ToggleSwitch _tunnelOn = new();
    private readonly TextBox _tunnelHost = Ui.BareTextBox();
    private readonly TextBox _tunnelPort = Ui.BareTextBox();
    private readonly TextBox _tunnelUser = Ui.BareTextBox();
    private readonly TextBlock _tunnelState = new() { FontSize = 12, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _tunnelKey = Ui.Pill("Ключ", padding: 12);
    private readonly Button _tunnelTest = Ui.Pill("Проверить", padding: 12);

    public SettingsView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        foreach (var b in new[] { _tabGeneral, _tabAi, _tabDictation, _tabApps, _tabServer }) { b.Margin = new Thickness(0); b.Height = 26; }
        _tabGeneral.Click += (_, _) => ShowPage(0);
        _tabAi.Click += (_, _) => ShowPage(1);
        _tabApps.Click += (_, _) => ShowPage(2);
        _tabServer.Click += (_, _) => ShowPage(3);
        _tabDictation.Click += (_, _) => ShowPage(4);
        var switcher = new Border
        {
            CornerRadius = new CornerRadius(15), Background = FieldBackground, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _tabGeneral, _tabAi, _tabDictation, _tabApps, _tabServer } }
        };
        PillSlider.Attach(switcher);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(switcher);
        _statusChip.Child = _status;
        SetRow(header, 0);
        Children.Add(header);

        BuildGeneral();
        BuildAi();
        BuildApps();
        BuildServer();
        BuildDictation();
        SetRow(_dict, 1);
        SetRow(_server, 1);
        _generalHost.Content = _general;
        SetRow(_generalHost, 1);
        _aiHost.Content = _ai;
        SetRow(_aiHost, 1);
        SetRow(_apps, 1);
        Children.Add(_generalHost);
        Children.Add(_aiHost);
        Children.Add(_apps);
        Children.Add(_server);
        Children.Add(_dict);
        SetRow(_statusChip, 1);
        Panel.SetZIndex(_statusChip, 10);
        Children.Add(_statusChip);

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _status.Text = ""; _statusChip.Visibility = Visibility.Collapsed; };
        Loaded += (_, _) => { Refresh(); ShowPage(_page); };
        // Остров раскрывается заново: настройки могли поменяться снаружи (автозапуск в меню трея, ключ в карточке Alt+Q).
        IslandBus.Expanding += () => { if (IsLoaded) { Refresh(); Dispatcher.BeginInvoke(CascadeCurrentPage); } };
    }

    private void ShowPage(int page)
    {
        _page = page;
        _generalHost.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        _aiHost.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        _apps.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        _server.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        _dict.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
        Ui.MarkSelected(_tabDictation, page == 4);
        if (page == 4) RefreshMics();
        Ui.MarkSelected(_tabServer, page == 3);
        Ui.MarkSelected(_tabGeneral, page == 0);
        Ui.MarkSelected(_tabAi, page == 1);
        Ui.MarkSelected(_tabApps, page == 2);
        if (page != 2) ClosePicker();
        // Первый показ без анимации. Строки страниц «Общие», «ИИ и поиск», «Сервер» и «Диктовка» появляются каскадом снизу вверх (как на «Главной»,
        // но порядок обратный), «Программы» проявляются целиком.
        if (IsLoaded)
        {
            if (page == 2) Ui.FadeIn(_apps);
            else CascadeCurrentPage();
        }
    }

    private void CascadeCurrentPage()
    {
        if (_page == 2) return;
        var rows = (_page switch { 0 => _general.Children, 1 => _ai.Children, 3 => _server.Children, _ => _dict.Children }).OfType<FrameworkElement>();
        Ui.CascadeUp(rows.ToList());
    }

    /// <summary>Поле ввода с серой подсказкой, пока оно пустое.</summary>
    private static Grid WithPlaceholder(TextBox box, string text)
    {
        var hint = new TextBlock
        {
            Text = text, FontSize = 12.5, Foreground = Dim, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0)
        };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return new Grid { Children = { hint, box } };
    }

    private static Grid Row(string title, string? hint, FrameworkElement control)
    {
        var row = new Grid { Height = RowHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 12, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Hues.Ink });
        if (hint != null) texts.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Ui.Gray, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        SetColumn(texts, 0);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(0, 0, 4, 0);
        SetColumn(control, 1);
        row.Children.Add(texts);
        row.Children.Add(control);
        return row;
    }

    // ───── Общие ─────

    private void BuildGeneral()
    {
        _autostart.Toggled += on =>
        {
            if (_loading) return;
            try { AutostartService.SetEnabled(on); }
            catch { Say("Не удалось изменить автозапуск"); _autostart.Set(AutostartService.IsEnabled); }
        };
        _night.Changed += index =>
        {
            if (_loading) return;
            var on = index == 1;
            var settings = AppSettings.Load();
            settings.NightMode = on;
            settings.Save();
            IslandBus.NotifySettingsChanged();
        };
        _dictation.Toggled += on =>
        {
            if (_loading) return;
            var settings = AppSettings.Load();
            settings.DictationEnabled = on;
            settings.Save();
            IslandBus.NotifySettingsChanged();
        };
        _collapse.Changed += index =>
        {
            if (_loading) return;
            var settings = AppSettings.Load();
            settings.AutoCollapseSeconds = CollapseChoices[index];
            settings.Save();
            IslandBus.NotifySettingsChanged();
        };
        _trig.Changed += index =>
        {
            if (_loading) return;
            var settings = AppSettings.Load();
            settings.TrigInDegrees = index == 0;
            settings.Save();
            Calculator.Degrees = settings.TrigInDegrees;
        };
        _glass.Changed += index =>
        {
            if (_loading) return;
            var settings = AppSettings.Load();
            settings.GlassLevel = index;
            settings.Save();
            IslandBus.NotifySettingsChanged();
        };
        _blurOn.Toggled += on =>
        {
            if (_loading) return;
            var settings = AppSettings.Load();
            settings.BlurBackdrop = on;
            settings.Save();
            IslandBus.NotifySettingsChanged();
        };
        // Картинки вместо текстовых переключателей: стекло, автосворачивание, углы, ночной режим.
        _general.Children.Add(_glass);
        _general.Children.Add(_collapse);
        _general.Children.Add(_night);
        _general.Children.Add(Row("Углы в калькуляторе Alt+Q", "для sin, cos, tan", _trig));
        _general.Children.Add(Row("Запускать вместе с Windows", null, _autostart));
        _general.Children.Add(Row("Размытие фона", "за островом; если лагает, выключи", _blurOn));
        _journalOn.Toggled += on =>
        {
            if (_loading) return;
            var settings = AppSettings.Load();
            settings.DayJournalEnabled = on;
            settings.Save();
        };
        var clearJournal = Ui.Pill("Очистить", padding: 12);
        clearJournal.Margin = new Thickness(0, 0, 10, 0);
        clearJournal.Height = 26;
        clearJournal.Click += (_, _) => { Services.DayJournal.Current?.Clear(); Say("Журнал дня очищен"); };
        _journalOn.VerticalAlignment = VerticalAlignment.Center;
        var journalControls = new StackPanel { Orientation = Orientation.Horizontal, Children = { clearJournal, _journalOn } };
        _general.Children.Add(Row("Журнал дня", "для итогов дня: программы, музыка, Claude (на этом компьютере)", journalControls));
    }

    // ───── ИИ и поиск ─────

    private void BuildAi()
    {
        _keyReplace.Margin = new Thickness(0);
        _keyDelete.Margin = new Thickness(6, 0, 0, 0);
        _keyReplace.Height = _keyDelete.Height = 26;
        _keyReplace.Click += (_, _) => StartKeyEntry();
        _keyDelete.Click += (_, _) =>
        {
            OpenAiKeyStore.Clear();
            Say("Ключ удалён");
            RefreshKey();
        };
        _keyBox.FontSize = 12.5;
        _keyBox.VerticalAlignment = VerticalAlignment.Center;
        _keyField.Child = WithPlaceholder(_keyBox, "Вставь ключ sk-…");
        _keyBox.TextChanged += (_, _) =>
        {
            var text = _keyBox.Text.Trim();
            if (!OpenAiKeyStore.LooksLikeKey(text)) return; // ключ вставляют целиком: как только похоже на ключ, сохраняем
            if (!OpenAiKeyStore.Save(text)) { Say("Не удалось сохранить ключ"); return; }
            IslandBus.ForgetClipboardText(text); // ключ мог попасть в историю буфера обмена
            _keyBox.Text = "";
            StopKeyEntry();
            Say("Ключ сохранён");
            RefreshKey();
        };
        _keyBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; StopKeyEntry(); } };
        _keyBox.LostKeyboardFocus += (_, _) => { StopKeyEntry(); IslandBus.SuspendAutoCollapse(false); };
        _keyBox.GotKeyboardFocus += (_, _) => IslandBus.SuspendAutoCollapse(true);

        var keyControls = new StackPanel { Orientation = Orientation.Horizontal, Children = { _keyState, _keyField, _keyReplace, _keyDelete } };

        _model.Changed += index => SaveModel(Models[index].Model);
        _customModel.FontSize = 12.5;
        _customModel.VerticalAlignment = VerticalAlignment.Center;
        var customField = new Border { Height = 28, Width = 170, CornerRadius = new CornerRadius(14), Background = FieldBackground, Padding = new Thickness(12, 0, 12, 1), Child = WithPlaceholder(_customModel, "название модели") };
        _customModel.GotKeyboardFocus += (_, _) => IslandBus.SuspendAutoCollapse(true);
        _customModel.LostKeyboardFocus += (_, _) => { IslandBus.SuspendAutoCollapse(false); CommitCustomModel(); };
        _customModel.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitCustomModel(); Keyboard.ClearFocus(); } };
        customField.MouseLeftButtonDown += (_, _) => _customModel.Focus();

        _hotkey.Focusable = true;
        _hotkey.Margin = new Thickness(0);
        _hotkey.Height = 28;
        _hotkey.Click += (_, _) => StartCapture();
        _hotkey.PreviewKeyDown += OnCaptureKey;
        _hotkey.LostKeyboardFocus += (_, _) => StopCapture();

        _ai.Children.Add(Row("Ключ OpenAI", null, keyControls));
        _ai.Children.Add(_model);
        _ai.Children.Add(Row("Другая модель", "своё имя, Enter", customField));
        _ai.Children.Add(Row("Горячая клавиша вопроса", "нажми на сочетание и введи новое", _hotkey));
    }

    private void StartKeyEntry()
    {
        _keyState.Visibility = _keyReplace.Visibility = _keyDelete.Visibility = Visibility.Collapsed;
        _keyField.Visibility = Visibility.Visible;
        _keyBox.Text = "";
        _keyBox.Focus();
        Keyboard.Focus(_keyBox);
    }

    private void StopKeyEntry()
    {
        _keyField.Visibility = Visibility.Collapsed;
        _keyState.Visibility = _keyReplace.Visibility = Visibility.Visible;
        RefreshKey();
    }

    private void RefreshKey()
    {
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var key = OpenAiKeyStore.Load();
        if (!string.IsNullOrWhiteSpace(env))
        {
            _keyState.Text = "из переменной окружения";
            _keyDelete.Visibility = Visibility.Collapsed;
        }
        else if (key == null)
        {
            _keyState.Text = "Не задан";
            _keyDelete.Visibility = Visibility.Collapsed;
        }
        else
        {
            _keyState.Text = "sk-…" + key[^4..]; // только конец ключа
            _keyDelete.Visibility = Visibility.Visible;
        }
        _keyReplace.Content = key == null && string.IsNullOrWhiteSpace(env) ? "Вставить" : "Заменить";
    }

    private void SaveModel(string? model)
    {
        if (_loading) return;
        var settings = AppSettings.Load();
        settings.OpenAiModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        settings.Save();
    }

    private void CommitCustomModel()
    {
        if (_loading) return;
        var text = _customModel.Text.Trim();
        if (text.Length == 0)
        {
            // Пусто: возвращаемся к модели из списка (если выбрана своя, то к умолчанию).
            var current = AppSettings.Load().OpenAiModel;
            if (current != null && Models.All(m => m.Model != current)) SaveModel(null);
            RefreshModel();
            return;
        }
        SaveModel(text);
        RefreshModel();
    }

    private void RefreshModel()
    {
        var current = AppSettings.Load().OpenAiModel;
        var index = current == null ? 0 : Array.FindIndex(Models, m => m.Model == current);
        _model.Select(index);
        _customModel.Text = index < 0 ? current ?? "" : "";
    }


    // ───── Сервер (тоннель для OpenAI без VPN) ─────

    private Border ServerField(TextBox box, string placeholder, double width)
    {
        box.FontSize = 12.5;
        box.VerticalAlignment = VerticalAlignment.Center;
        var field = new Border { Height = 28, Width = width, CornerRadius = new CornerRadius(14), Background = FieldBackground, Padding = new Thickness(12, 0, 12, 1), Child = WithPlaceholder(box, placeholder) };
        box.GotKeyboardFocus += (_, _) => IslandBus.SuspendAutoCollapse(true);
        box.LostKeyboardFocus += (_, _) => { IslandBus.SuspendAutoCollapse(false); CommitServer(); };
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitServer(); Keyboard.ClearFocus(); } };
        field.MouseLeftButtonDown += (_, _) => box.Focus();
        return field;
    }

    private void BuildServer()
    {
        _tunnelOn.Toggled += on =>
        {
            if (_loading) return;
            var settings = AppSettings.Load();
            settings.TunnelEnabled = on;
            settings.Save();
            ServerTunnel.Reset();
            RefreshServerState();
        };
        var hostPort = new StackPanel { Orientation = Orientation.Horizontal };
        hostPort.Children.Add(ServerField(_tunnelHost, "адрес сервера", 150));
        var port = ServerField(_tunnelPort, "22", 56);
        port.Margin = new Thickness(6, 0, 0, 0);
        hostPort.Children.Add(port);

        _tunnelKey.Margin = new Thickness(0);
        _tunnelTest.Margin = new Thickness(6, 0, 0, 0);
        _tunnelKey.Height = _tunnelTest.Height = 26;
        _tunnelKey.Click += (_, _) => CopyTunnelKey();
        _tunnelTest.Click += async (_, _) => await TestTunnelAsync();

        var stateRow = new Grid { Height = RowHeight };
        stateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tunnelState.Margin = new Thickness(10, 0, 12, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Children = { _tunnelKey, _tunnelTest } };
        SetColumn(buttons, 1);
        stateRow.Children.Add(_tunnelState);
        stateRow.Children.Add(buttons);

        _server.Children.Add(Row("Тоннель к OpenAI", "через твой сервер, без VPN", _tunnelOn));
        _server.Children.Add(Row("Сервер", "адрес и порт SSH", hostPort));
        _server.Children.Add(Row("Пользователь", "отдельный, с запертым ключом", ServerField(_tunnelUser, "ostrov-tunnel", 150)));
        _server.Children.Add(stateRow);
    }

    private void RefreshServer()
    {
        var settings = AppSettings.Load();
        _tunnelOn.Set(settings.TunnelEnabled);
        _tunnelHost.Text = settings.TunnelHost ?? "";
        _tunnelPort.Text = settings.TunnelPort == 22 || settings.TunnelPort <= 0 ? "" : settings.TunnelPort.ToString();
        _tunnelUser.Text = settings.TunnelUser ?? "";
        RefreshServerState();
    }

    private void RefreshServerState()
    {
        var settings = AppSettings.Load();
        _tunnelState.Text =
            !System.IO.File.Exists(ServerTunnel.KeyPath) ? "Нет файла ключа tunnel_key"
            : !settings.TunnelEnabled ? "Выключен"
            : string.IsNullOrWhiteSpace(settings.TunnelHost) || string.IsNullOrWhiteSpace(settings.TunnelUser) ? "Заполни адрес и пользователя"
            : ServerTunnel.IsUp ? "Работает"
            : ServerTunnel.LastError.Length > 0 ? ServerTunnel.LastError
            : "Поднимется при первом запросе к OpenAI";
    }

    private void CommitServer()
    {
        if (_loading) return;
        var settings = AppSettings.Load();
        settings.TunnelHost = string.IsNullOrWhiteSpace(_tunnelHost.Text) ? null : _tunnelHost.Text.Trim();
        settings.TunnelUser = string.IsNullOrWhiteSpace(_tunnelUser.Text) ? null : _tunnelUser.Text.Trim();
        settings.TunnelPort = int.TryParse(_tunnelPort.Text.Trim(), out var p) && p is > 0 and < 65536 ? p : 22;
        settings.Save();
        ServerTunnel.Reset(); // новый адрес: старый тоннель закрыть, паузу после неудачи сбросить
        RefreshServerState();
    }

    private void CopyTunnelKey()
    {
        try
        {
            Clipboard.SetText(System.IO.File.ReadAllText(ServerTunnel.PublicKeyPath).Trim());
            Say("Публичный ключ скопирован");
        }
        catch { Say("Нет файла публичного ключа"); }
    }

    private async Task TestTunnelAsync()
    {
        CommitServer();
        _tunnelTest.IsEnabled = false;
        _tunnelState.Text = "Проверяю…";
        try
        {
            var (_, message) = await ServerTunnel.TestAsync();
            _tunnelState.Text = message;
        }
        finally { _tunnelTest.IsEnabled = true; }
    }

    // ───── Диктовка ─────

    private const double MicRowHeight = 32;

    private void BuildDictation()
    {
        _dict.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _dict.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _dict.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _dict.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
        _dict.RowDefinitions.Insert(2, new RowDefinition { Height = GridLength.Auto });
        var toggleRow = Row("Диктовка", "зажми клавишу и говори", _dictation);
        SetRow(toggleRow, 0);
        _dict.Children.Add(toggleRow);
        _dictKey.Focusable = true;
        _dictKey.Margin = new Thickness(0);
        _dictKey.Height = 28;
        _dictKey.Click += (_, _) => StartDictCapture();
        _dictKey.PreviewKeyDown += OnDictCaptureKey;
        _dictKey.LostKeyboardFocus += (_, _) => StopDictCapture();
        var keyRow = Row("Клавиша", "нажми на кнопку и на нужную клавишу", _dictKey);
        SetRow(keyRow, 1);
        _dict.Children.Add(keyRow);
        // Свои слова: жаргон и названия, которые модель записывает неправильно. Сохраняются, когда поле теряет фокус или по Enter.
        _dictWords.FontSize = 12.5;
        _dictWords.VerticalAlignment = VerticalAlignment.Center;
        var wordsField = new Border { Height = 28, Width = 190, CornerRadius = new CornerRadius(14), Background = FieldBackground, Padding = new Thickness(12, 0, 12, 1), Child = WithPlaceholder(_dictWords, "слова через запятую") };
        _dictWords.GotKeyboardFocus += (_, _) => IslandBus.SuspendAutoCollapse(true);
        _dictWords.LostKeyboardFocus += (_, _) => { IslandBus.SuspendAutoCollapse(false); CommitWords(); };
        _dictWords.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitWords(); Keyboard.ClearFocus(); } };
        wordsField.MouseLeftButtonDown += (_, _) => _dictWords.Focus();
        var wordsRow = Row("Свои слова", "жаргон и названия: так ИИ их узнаёт лучше", wordsField);
        SetRow(wordsRow, 2);
        _dict.Children.Add(wordsRow);
        var caption = new TextBlock { Text = "Микрофон", FontSize = 12, Foreground = Ui.Gray, Margin = new Thickness(10, 4, 0, 4) };
        SetRow(caption, 3);
        _dict.Children.Add(caption);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _micList };
        SetRow(scroll, 4);
        _dict.Children.Add(scroll);
    }

    private void CommitWords()
    {
        if (_loading) return;
        // Слова разделяются запятой, точкой с запятой или новой строкой; одинаковые убираются; всего не больше 500 знаков (подсказка модели короткая).
        var parts = _dictWords.Text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var joined = new System.Text.StringBuilder();
        foreach (var w in parts)
        {
            if (joined.Length + w.Length + 2 > 500) break;
            if (joined.Length > 0) joined.Append(", ");
            joined.Append(w);
        }
        var text = joined.ToString();
        var settings = AppSettings.Load();
        if ((settings.DictationWords ?? "") == text) return;
        settings.DictationWords = text.Length == 0 ? null : text;
        settings.Save();
        _loading = true; _dictWords.Text = text; _loading = false;
        Say(text.Length == 0 ? "Слова диктовки очищены" : $"Слов для диктовки: {parts.Count}");
    }

    // Выбор клавиши диктовки: пока ждём нажатие, перехватчик диктовки снят (иначе он «съел» бы правый Alt), остров не сворачивается.
    private void StartDictCapture()
    {
        if (_capturingDict) return;
        _capturingDict = true;
        Dictation.DictationService.Current?.Pause();
        _dictKey.Content = "Нажми клавишу…";
        IslandBus.SuspendAutoCollapse(true);
        _dictKey.Focus();
        Keyboard.Focus(_dictKey);
    }

    private void StopDictCapture()
    {
        if (!_capturingDict) return;
        _capturingDict = false;
        Dictation.DictationService.Current?.Resume();
        IslandBus.SuspendAutoCollapse(false);
        RefreshDictKey();
    }

    private void OnDictCaptureKey(object sender, KeyEventArgs e)
    {
        if (!_capturingDict) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.Escape) { StopDictCapture(); Keyboard.ClearFocus(); return; }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (!Dictation.DictationKeys.IsAllowed(vk))
        {
            Say("Подходят правые Ctrl, Alt, Shift, F1–F24, Pause, Scroll Lock, Insert, Menu");
            return;
        }
        var settings = AppSettings.Load();
        settings.DictationKey = vk;
        settings.Save();
        Say("Клавиша диктовки: " + Dictation.DictationKeys.Name(vk));
        StopDictCapture(); // возвращает перехватчик уже с новой клавишей
        IslandBus.NotifySettingsChanged();
        Keyboard.ClearFocus();
    }

    private void RefreshDictKey()
    {
        var vk = AppSettings.Load().DictationKey;
        _dictKey.Content = Dictation.DictationKeys.Name(Dictation.DictationKeys.IsAllowed(vk) ? vk : Dictation.DictationKeys.Default);
    }

    private int _micRefresh;

    // Список микрофонов читается заново при каждом показе страницы (микрофон могли воткнуть или вынуть).
    private async void RefreshMics()
    {
        var token = ++_micRefresh;
        var devices = await Dictation.MicDevices.ListAsync();
        if (token != _micRefresh) return;
        var chosen = AppSettings.Load().DictationMic;
        var defaultId = Dictation.MicDevices.DefaultId();
        var defaultName = devices.FirstOrDefault(d => d.Id == defaultId)?.Name;
        // Выбранный микрофон исчез (отключён): показываем выбор «Как в Windows», в настройках он не меняется.
        var chosenExists = !string.IsNullOrEmpty(chosen) && devices.Any(d => d.Id == chosen);

        _micList.Children.Clear();
        _micList.Children.Add(MicRow("Как в Windows", defaultName, null, !chosenExists));
        foreach (var d in devices) _micList.Children.Add(MicRow(d.Name, null, d.Id, chosenExists && d.Id == chosen));
        if (devices.Count == 0)
            _micList.Children.Add(new TextBlock { Text = "Микрофонов не нашлось", FontSize = 12, Foreground = Ui.Gray, Margin = new Thickness(10, 8, 0, 0) });
    }

    private Button MicRow(string title, string? hint, string? id, bool selected)
    {
        var row = Ui.ListRow(title, hint);
        row.Height = MicRowHeight;
        var check = new TextBlock
        {
            Text = selected ? "\uE73E" : "", FontFamily = Ui.IconFont, FontSize = 12, Foreground = Hues.Ink,
            Width = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1)
        };
        ((StackPanel)row.Content).Children.Insert(0, check);
        if (!selected) ((TextBlock)((StackPanel)row.Content).Children[1]).Foreground = Frozen(Color.FromRgb(0xCF, 0xCF, 0xCF));
        row.Click += (_, _) =>
        {
            var settings = AppSettings.Load();
            settings.DictationMic = id;
            settings.Save();
            IslandBus.NotifySettingsChanged();
            RefreshMics();
            Say(id == null ? "Микрофон: как в Windows" : "Микрофон: " + title);
        };
        return row;
    }

    // ───── Программы (М-006) ─────

    private const double AppRowHeight = 36;
    private readonly Grid _appsListPage = new();
    private readonly Grid _appsPickerPage = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _appList = new();
    private readonly StackPanel _pickerList = new();

    private void BuildApps()
    {
        // Страница 1: пояснение и «Добавить…», под ними список с выключателями.
        _appsListPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _appsListPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var add = Ui.Pill("Добавить…", padding: 12);
        add.Margin = new Thickness(0);
        add.Height = 26;
        add.Click += (_, _) => OpenPicker();
        _appsListPage.Children.Add(Header("Над ними остров сжимается в нотч, пока они в фокусе", add));
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _appList, Margin = new Thickness(0, 4, 0, 0) };
        SetRow(scroll, 1);
        _appsListPage.Children.Add(scroll);

        // Страница 2: открытые сейчас программы, нажатие добавляет.
        _appsPickerPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _appsPickerPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var cancel = Ui.Pill("Отмена", padding: 12);
        cancel.Margin = new Thickness(0);
        cancel.Height = 26;
        cancel.Click += (_, _) => ClosePicker();
        _appsPickerPage.Children.Add(Header("Выбери открытую программу", cancel));
        var pickScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _pickerList, Margin = new Thickness(0, 4, 0, 0) };
        SetRow(pickScroll, 1);
        _appsPickerPage.Children.Add(pickScroll);

        _apps.Children.Add(_appsListPage);
        _apps.Children.Add(_appsPickerPage);
    }

    // Строка над списком: серое пояснение слева, кнопка справа (шапка той же высоты, что и кнопка).
    private static Grid Header(string text, Button button)
    {
        var header = new Grid { Height = 30 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = new TextBlock
        {
            Text = text, FontSize = 12, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis
        };
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(0, 0, 4, 0);
        SetColumn(hint, 0);
        SetColumn(button, 1);
        header.Children.Add(hint);
        header.Children.Add(button);
        return header;
    }

    private void RefreshApps()
    {
        _appList.Children.Clear();
        foreach (var entry in ShrinkApps.Entries(AppSettings.Load())) _appList.Children.Add(AppRow(entry));
    }

    private Grid AppRow(AppListEntry entry)
    {
        var row = new Grid { Height = AppRowHeight };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = entry.Title.Length > 0 ? entry.Title : entry.Process;
        var icon = AppIcons.Create(entry.Process, entry.Path, title);
        icon.Margin = new Thickness(10, 0, 0, 0);
        SetColumn(icon, 0);
        row.Children.Add(icon);
        var name = new TextBlock
        {
            Text = title, FontSize = 13, Foreground = Hues.Ink, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetColumn(name, 1);
        row.Children.Add(name);

        // Свои программы можно убрать из списка совсем; стандартные (браузеры) только выключить.
        if (!ShrinkApps.IsDefault(entry.Process))
        {
            var remove = Ui.IconButton("\uE711", 11, 26);
            remove.ToolTip = "Убрать из списка";
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.Click += (_, _) => { ChangeApps(list => list.RemoveAll(e => e.Process == entry.Process)); RefreshApps(); };
            SetColumn(remove, 2);
            row.Children.Add(remove);
        }

        var toggle = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        toggle.Set(entry.Enabled);
        toggle.Toggled += on => ChangeApps(list => { if (list.FirstOrDefault(e => e.Process == entry.Process) is { } e) e.Enabled = on; });
        SetColumn(toggle, 3);
        row.Children.Add(toggle);
        return row;
    }

    // Изменить список в настройках; остров перечитает его сразу (нотч над текущей программой появится или уйдёт).
    private static void ChangeApps(Action<List<AppListEntry>> change)
    {
        var settings = AppSettings.Load();
        var list = ShrinkApps.Entries(settings);
        change(list);
        settings.ShrinkApps = list;
        settings.Save();
        IslandBus.NotifySettingsChanged();
    }

    private void OpenPicker()
    {
        _pickerList.Children.Clear();
        var listed = ShrinkApps.Entries(AppSettings.Load()).Select(e => e.Process).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var programs = RunningPrograms(listed);
        if (programs.Count == 0)
            _pickerList.Children.Add(new TextBlock { Text = "Других открытых программ с окном нет", FontSize = 12, Foreground = Ui.Gray, Margin = new Thickness(10, 10, 0, 0) });
        foreach (var (process, title, path) in programs)
        {
            var row = Ui.ListRow(title);
            row.Height = 32;
            var icon = AppIcons.Create(process, path, title);
            icon.Margin = new Thickness(0, 0, 10, 0);
            ((StackPanel)row.Content).Children.Insert(0, icon); // значок слева от названия
            row.Click += (_, _) =>
            {
                ChangeApps(list => list.Add(new AppListEntry { Process = process, Title = title, Enabled = true, Path = path.Length > 0 ? path : null }));
                ClosePicker();
                RefreshApps();
                Say("Добавлено: " + title);
            };
            _pickerList.Children.Add(row);
        }
        _appsListPage.Visibility = Visibility.Collapsed;
        _appsPickerPage.Visibility = Visibility.Visible;
    }

    private void ClosePicker()
    {
        _appsPickerPage.Visibility = Visibility.Collapsed;
        _appsListPage.Visibility = Visibility.Visible;
    }

    private static readonly HashSet<string> NotPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp"
    };

    /// <summary>Программы, у которых сейчас есть окно с заголовком (без самого острова и уже добавленных): имя процесса, название для показа и путь к exe (ради значка).</summary>
    private static List<(string Process, string Title, string Path)> RunningPrograms(HashSet<string> exclude)
    {
        var own = Process.GetCurrentProcess().ProcessName;
        var found = new Dictionary<string, (string Title, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || p.MainWindowTitle.Length == 0) continue;
                var name = p.ProcessName;
                if (name.Equals(own, StringComparison.OrdinalIgnoreCase) || NotPrograms.Contains(name) || exclude.Contains(name) || found.ContainsKey(name)) continue;
                string title = "", path = "";
                try
                {
                    var module = p.MainModule;
                    path = module?.FileName ?? "";
                    title = module?.FileVersionInfo.FileDescription ?? "";
                }
                catch { /* программа с повышенными правами: описание и путь недоступны, покажем имя процесса */ }
                found[name] = (string.IsNullOrWhiteSpace(title) ? name : title.Trim(), path);
            }
            catch { /* процесс закрылся, пока мы его разглядывали */ }
            finally { p.Dispose(); }
        }
        return found.Select(kv => (kv.Key, kv.Value.Title, kv.Value.Path)).OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ───── Горячая клавиша ─────

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        SearchService.Current?.PauseHotkey(); // пока ждём сочетание, текущее не должно срабатывать
        _hotkey.Content = "Нажми сочетание…";
        IslandBus.SuspendAutoCollapse(true);
        _hotkey.Focus();
        Keyboard.Focus(_hotkey);
    }

    private void StopCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        SearchService.Current?.ResumeHotkey();
        IslandBus.SuspendAutoCollapse(false);
        RefreshHotkey();
    }

    private void OnCaptureKey(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopCapture(); return; }
        var spec = SearchHotkey.FromKeyboard(key, Keyboard.Modifiers);
        if (spec is not { } next)
        {
            // Нажали только модификатор или клавишу без Alt/Ctrl/Win: ждём дальше молча. Обычная клавиша с модификатором, но отклонённая: объясняем.
            if (Keyboard.Modifiers != ModifierKeys.None && !SearchHotkey.IsAllowed(key, Keyboard.Modifiers))
                Say("Нужен Alt или Win (не Alt+F4, Alt+Tab, Alt+Esc)");
            return;
        }
        var service = SearchService.Current;
        if (service == null) { StopCapture(); return; }
        var ok = service.TrySetHotkey(next);
        _capturing = false;
        service.ResumeHotkey();
        IslandBus.SuspendAutoCollapse(false);
        if (!ok) Say("Это сочетание занято другой программой");
        RefreshHotkey();
        Keyboard.ClearFocus();
    }

    private void RefreshHotkey() =>
        _hotkey.Content = (SearchService.Current?.Hotkey ?? SearchHotkey.Default).Format();

    // ───── Общее ─────

    private void Say(string text)
    {
        _status.Text = text;
        _statusChip.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>Прочитать текущие значения (при показе вкладки и каждом раскрытии острова).</summary>
    private void Refresh()
    {
        _loading = true;
        try
        {
            var settings = AppSettings.Load();
            _autostart.Set(AutostartService.IsEnabled);
            _night.Select(settings.NightMode ? 1 : 0);
            _journalOn.Set(settings.DayJournalEnabled);
            _dictation.Set(settings.DictationEnabled);
            _collapse.Select(Array.IndexOf(CollapseChoices, settings.AutoCollapseSeconds));
            _trig.Select(settings.TrigInDegrees ? 0 : 1);
            _glass.Select(Math.Clamp(settings.GlassLevel, 0, 2));
            _blurOn.Set(settings.BlurBackdrop);
            _dictWords.Text = settings.DictationWords ?? "";
            RefreshKey();
            RefreshModel();
            RefreshHotkey();
            RefreshDictKey();
            RefreshApps();
            RefreshServer();
        }
        finally { _loading = false; }
    }
}
