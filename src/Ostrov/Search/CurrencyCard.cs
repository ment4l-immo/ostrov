using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.Search;

/// <summary>
/// Карточка пересчёта валют в ответе ИИ: слева сумма (её можно править, результат пересчитывается сразу), справа результат,
/// у обеих сторон значок валюты и название; кнопка посередине меняет валюты местами. Внизу курс и дата. Только белое и серое.
/// Курсы грузятся один раз на карточку, дальше всё считается на месте.
/// </summary>
internal sealed class CurrencyCard : Border
{
    private static readonly CultureInfo Ru = new("ru-RU");
    private static readonly Brush Gray = Ui.Gray;
    private static readonly Brush Dim = Frozen(Color.FromRgb(0x8A, 0x91, 0x99));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["USD"] = "$", ["EUR"] = "€", ["RUB"] = "₽", ["GBP"] = "£", ["JPY"] = "¥", ["CNY"] = "¥", ["UAH"] = "₴", ["KZT"] = "₸", ["TRY"] = "₺",
        ["INR"] = "₹", ["PLN"] = "zł", ["CHF"] = "Fr", ["CZK"] = "Kč", ["KRW"] = "₩", ["BRL"] = "R$", ["ILS"] = "₪", ["THB"] = "฿", ["VND"] = "₫",
        ["PHP"] = "₱", ["NGN"] = "₦", ["GEL"] = "₾", ["AMD"] = "֏", ["AZN"] = "₼", ["BYN"] = "Br", ["CAD"] = "$", ["AUD"] = "$", ["NZD"] = "$",
        ["HKD"] = "$", ["SGD"] = "$", ["MXN"] = "$", ["SEK"] = "kr", ["NOK"] = "kr", ["DKK"] = "kr", ["HUF"] = "Ft", ["RON"] = "lei", ["BGN"] = "лв",
        ["AED"] = "د.إ", ["SAR"] = "﷼", ["EGP"] = "E£", ["ZAR"] = "R", ["IDR"] = "Rp", ["MYR"] = "RM", ["KGS"] = "с", ["MDL"] = "L", ["RSD"] = "дин"
    };

    private static readonly Dictionary<string, string> Names = new()
    {
        ["USD"] = "Доллар США", ["EUR"] = "Евро", ["RUB"] = "Российский рубль", ["GBP"] = "Фунт стерлингов", ["JPY"] = "Японская иена",
        ["CNY"] = "Китайский юань", ["UAH"] = "Украинская гривна", ["KZT"] = "Казахстанский тенге", ["TRY"] = "Турецкая лира", ["INR"] = "Индийская рупия",
        ["PLN"] = "Польский злотый", ["CHF"] = "Швейцарский франк", ["CZK"] = "Чешская крона", ["KRW"] = "Южнокорейская вона", ["BRL"] = "Бразильский реал",
        ["ILS"] = "Израильский шекель", ["THB"] = "Тайский бат", ["VND"] = "Вьетнамский донг", ["GEL"] = "Грузинский лари", ["AMD"] = "Армянский драм",
        ["AZN"] = "Азербайджанский манат", ["BYN"] = "Белорусский рубль", ["CAD"] = "Канадский доллар", ["AUD"] = "Австралийский доллар",
        ["NZD"] = "Новозеландский доллар", ["HKD"] = "Гонконгский доллар", ["SGD"] = "Сингапурский доллар", ["MXN"] = "Мексиканское песо",
        ["SEK"] = "Шведская крона", ["NOK"] = "Норвежская крона", ["DKK"] = "Датская крона", ["HUF"] = "Венгерский форинт", ["RON"] = "Румынский лей",
        ["BGN"] = "Болгарский лев", ["AED"] = "Дирхам ОАЭ", ["SAR"] = "Саудовский риял", ["EGP"] = "Египетский фунт", ["ZAR"] = "Южноафриканский рэнд",
        ["IDR"] = "Индонезийская рупия", ["MYR"] = "Малайзийский ринггит", ["KGS"] = "Киргизский сом", ["MDL"] = "Молдавский лей", ["RSD"] = "Сербский динар"
    };

    private readonly Func<string, CancellationToken, Task<RateTable>> _load;
    private bool _canRetry;
    private string _from, _to;
    private double _amount;
    private RateTable? _table;
    private bool _editing;

    private readonly TextBox _amountBox = Ui.BareTextBox();
    private readonly TextBlock _fromName = Small(), _toName = Small(), _result = new()
    {
        FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly TextBlock _footer = new() { FontSize = 11.5, Foreground = Gray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _fromBadgeHost = new(), _toBadgeHost = new();

    public CurrencyCard(double amount, string from, string to, Func<string, CancellationToken, Task<RateTable>>? load = null)
    {
        _amount = amount;
        _from = from.Trim().ToUpperInvariant();
        _to = to.Trim().ToUpperInvariant();
        _load = load ?? CurrencyService.GetAsync;

        Background = Frozen(Hues.SurfaceColor);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(14, 12, 14, 10);
        HorizontalAlignment = HorizontalAlignment.Left;
        MaxWidth = 460;
        Margin = new Thickness(0, 2, 0, 4);

        _amountBox.FontSize = 20;
        _amountBox.FontWeight = FontWeights.SemiBold;
        _amountBox.MinWidth = 70;
        _amountBox.MaxLength = 18;
        _amountBox.Text = FormatAmount(amount);
        _amountBox.TextChanged += (_, _) => { if (!_editing) OnAmountEdited(); };
        _amountBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(c => char.IsDigit(c) || c is ',' or '.' or ' ' or ' ');
        _amountBox.GotKeyboardFocus += (_, _) => _amountBox.SelectAll();

        var swap = Ui.IconButton("", 13, 28); // E8AB «Переключить»
        swap.ToolTip = "Поменять валюты местами";
        swap.Click += (_, _) => Swap();

        var left = Side(_fromBadgeHost, _amountBox, _fromName);
        var right = Side(_toBadgeHost, _result, _toName);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        swap.Margin = new Thickness(8, 0, 8, 0);
        swap.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(left, 0); SetColumn(swap, 1); SetColumn(right, 2);
        row.Children.Add(left); row.Children.Add(swap); row.Children.Add(right);

        var stack = new StackPanel();
        stack.Children.Add(row);
        _footer.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_footer);
        Child = stack;

        Redraw();
        _footer.Text = "Загружаю курс…";
        _footer.MouseLeftButtonUp += (_, _) => Retry();
        // Грузим сразу при создании, не дожидаясь показа. Пока ответ достраивается, документ перерисовывается, и карточка не раз выходит
        // из дерева и возвращается; загрузка от этого не зависит (раньше отмена при выгрузке навсегда оставляла «Загружаю курс…»).
        // Старт через диспетчер: продолжение после await обязано вернуться на поток интерфейса, откуда бы карточку ни создали.
        Dispatcher.BeginInvoke(() => _ = LoadAsync());
    }

    private void Retry()
    {
        if (!_canRetry) return;
        _canRetry = false;
        _footer.Cursor = Cursors.Arrow;
        _footer.Text = "Загружаю курс…";
        _ = LoadAsync();
    }

    private void Fail(string text, bool retry)
    {
        _footer.Text = text;
        _result.Text = "—";
        _canRetry = retry;
        _footer.Cursor = retry ? Cursors.Hand : Cursors.Arrow;
    }

    private static TextBlock Small() => new() { FontSize = 11.5, Foreground = Gray, TextTrimming = TextTrimming.CharacterEllipsis };

    private static void SetColumn(UIElement e, int c) => Grid.SetColumn(e, c);

    private static FrameworkElement Side(Border badgeHost, UIElement main, UIElement name)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        texts.Children.Add(main);
        texts.Children.Add(name);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(badgeHost, Dock.Left);
        row.Children.Add(badgeHost);
        row.Children.Add(texts);
        return row;
    }

    private async Task LoadAsync()
    {
        if (_table != null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var table = await _load(_from, cts.Token);
            if (!table.TryCross(_from, _to, out _)) throw new InvalidOperationException("Нет курса для " + _to);
            _table = table;
            Redraw();
        }
        catch (OperationCanceledException) { Fail("Курс не загрузился. Нажми здесь, чтобы повторить.", retry: true); }
        catch (InvalidOperationException ex) { Fail(ex.Message, retry: false); }
        catch (Exception) { Fail("Не удалось получить курс. Проверь интернет и нажми здесь, чтобы повторить.", retry: true); }
    }

    private void OnAmountEdited()
    {
        var text = _amountBox.Text.Replace(" ", "").Replace(" ", "").Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 1e15) _amount = v;
        else if (text.Length == 0) _amount = 0;
        else return;
        Redraw(keepAmountText: true);
    }

    private void Swap()
    {
        (_from, _to) = (_to, _from);
        // Результат становится новой суммой: пересчёт «туда и обратно» остаётся согласованным.
        if (_table != null && _table.TryCross(_to, _from, out var rate)) _amount = Math.Round(_amount * rate, 2);
        Redraw();
    }

    // Показать всё по текущему состоянию. Своё поле суммы не переписываем, пока в нём печатают.
    private void Redraw(bool keepAmountText = false)
    {
        _fromBadgeHost.Child = Badge(_from);
        _toBadgeHost.Child = Badge(_to);
        _fromName.Text = CurrencyName(_from);
        _toName.Text = CurrencyName(_to);
        if (!keepAmountText)
        {
            _editing = true;
            _amountBox.Text = FormatAmount(_amount);
            _editing = false;
        }
        if (_table != null && _table.TryCross(_from, _to, out var rate))
        {
            _result.Text = FormatResult(_amount * rate);
            var local = _table.UpdatedUtc.ToLocalTime();
            _footer.Text = $"1 {_from} = {FormatRate(rate)} {_to}  ·  курс на {local.ToString("d MMM, HH:mm", Ru)}  ·  exchangerate-api.com";
        }
        else if (_table != null) _result.Text = "—";
        else _result.Text = "…";
    }

    private static string CurrencyName(string code) => Names.TryGetValue(code, out var n) ? n : code;

    private static string FormatAmount(double v) => v.ToString(v % 1 == 0 ? "#,0" : "#,0.##", Ru);

    internal static string FormatResult(double v) =>
        v >= 100 ? v.ToString("#,0.00", Ru)
        : v >= 1 ? v.ToString("0.00##", Ru)
        : v >= 0.01 ? v.ToString("0.0000", Ru)
        : v.ToString("0.000000", Ru);

    internal static string FormatRate(double v) =>
        v >= 100 ? v.ToString("#,0.00", Ru) : v >= 1 ? v.ToString("0.00##", Ru) : v.ToString("0.0000##", Ru);

    // Значок валюты: круг, символ крупно и код мелко под ним.
    private static FrameworkElement Badge(string code)
    {
        var symbol = Symbols.TryGetValue(code, out var s) ? s : null;
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (symbol != null)
        {
            content.Children.Add(new TextBlock { Text = symbol, FontSize = symbol.Length > 2 ? 11 : 15, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Center, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });
            content.Children.Add(new TextBlock { Text = code, FontSize = 8, Foreground = Gray, HorizontalAlignment = HorizontalAlignment.Center, LineHeight = 9, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });
        }
        else
        {
            content.Children.Add(new TextBlock { Text = code, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Center });
        }
        return new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Frozen(Hues.SurfaceHighColor),
            BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Child = content, VerticalAlignment = VerticalAlignment.Center
        };
    }
}
