using System.Windows.Threading;
using Ostrov.Services;
using Windows.Devices.Enumeration.Pnp;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Ostrov.Modules.Phone;

/// <summary>
/// Связь с iPhone через то, что уже даёт Windows (проверено на ПК пользователя 2026-11-15, права администратора не нужны):
/// заряд — свойство Bluetooth-устройства (<c>{104EA319-…} 2</c>), уведомления — центр уведомлений Windows, куда их кладёт «Связь с телефоном»
/// (читает официальный <see cref="UserNotificationListener"/>). Работает, только пока iPhone на связи по Bluetooth.
/// Опрос: заряд раз в 15 с, уведомления раз в 3 с. Вспышки на острове (<see cref="ActivityCenter.ShowFlash"/>): телефон подключился,
/// заряд упал до 20 % и до 10 %, новое сообщение (только для выбранных приложений). Текст сообщений не читается и нигде не хранится.
/// </summary>
internal sealed class PhoneLink : IDisposable
{
    public static PhoneLink Instance { get; } = new();

    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ConnectedKey = "{83DA6326-97A6-4088-9453-A1923F573B29} 15";
    private const string NameKey = "System.ItemNameDisplay";
    private static readonly string[] Props = [NameKey, BatteryKey, ConnectedKey];
    private static readonly TimeSpan FlashFor = TimeSpan.FromMilliseconds(3200);

    private readonly DispatcherTimer _batteryTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HashSet<(uint Id, long Stamp)> _seen = []; // Id и время создания: повторное уведомление из того же чата может прийти с тем же Id
    private PhoneFlash? _flash;
    private bool _batteryBusy, _noticeBusy, _batteryPolled, _noticesPolled;
    private int _warnTier;

    /// <summary>Название телефона («iPhone») или null, если ни одного не нашли.</summary>
    public string? Name { get; private set; }

    public bool Connected { get; private set; }

    /// <summary>Заряд, %; null — не знаем (телефон не на связи или Windows его не отдаёт).</summary>
    public int? Battery { get; private set; }

    /// <summary>Есть доступ к уведомлениям Windows.</summary>
    public bool NoticeAccess { get; private set; } = true;

    /// <summary>Уведомления телефона, новые сверху (не больше 30).</summary>
    public IReadOnlyList<PhoneNotice> Notices { get; private set; } = [];

    public event Action? Changed;

    /// <summary>Пришло новое уведомление (любого приложения; выбор приложений тут не учитывается). Вкладка «Телефон» по нему даёт вспышку цвета приложения.</summary>
    public event Action<PhoneNotice>? Arrived;

    public void Start()
    {
        _batteryTimer.Tick += async (_, _) => await PollBatteryAsync();
        _noticeTimer.Tick += async (_, _) => await PollNoticesAsync();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await PollBatteryAsync();
            await PollNoticesAsync();
            _batteryTimer.Start();
            _noticeTimer.Start();
        };
        first.Start();
    }

    public void Dispose()
    {
        _batteryTimer.Stop();
        _noticeTimer.Stop();
    }

    // ───── Заряд ─────

    private async Task PollBatteryAsync()
    {
        if (_batteryBusy) return;
        _batteryBusy = true;
        try
        {
            var all = await PnpObject.FindAllAsync(PnpObjectType.Device, Props, "System.Devices.DeviceInstanceId:~~\"BTH\"").AsTask();
            string? name = null;
            int? battery = null;
            var connected = false;
            foreach (var d in all)
            {
                var label = d.Properties.TryGetValue(NameKey, out var n) ? n as string : null;
                if (label == null || !label.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) continue;
                var top = d.Id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)
                          || (d.Id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase));
                var isLe = d.Id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase);
                var on = d.Properties.TryGetValue(ConnectedKey, out var c) && c is true;
                var level = d.Properties.TryGetValue(BatteryKey, out var b) && b != null ? Convert.ToInt32(b) : (int?)null;

                if (top && on)
                {
                    connected = true;
                    if (isLe || name == null) name = label; // у BLE-узла настоящее имя «iPhone»
                }
                // Заряд лежит на узле BLE (точнее) или на служебном узле Hands-Free (шаг 10 %); BLE важнее.
                if (level is { } l && (isLe || battery == null)) battery = l;
            }

            var wasConnected = Connected;
            var first = !_batteryPolled;
            _batteryPolled = true;
            Name = connected ? name : Name; // имя помним и когда телефон отошёл
            Connected = connected;
            Battery = connected ? battery : null;

            if (connected)
            {
                if (!first && !wasConnected)
                {
                    _warnTier = Tier(Battery);
                    ShowFlash(f => f.ShowConnected(Name ?? "iPhone", Battery));
                }
                else CheckLow();
            }
            else _warnTier = 0;
            Changed?.Invoke();
        }
        catch
        {
            // Windows не отдала список устройств: в следующий раз.
        }
        finally { _batteryBusy = false; }
    }

    private static int Tier(int? battery) => battery is { } b ? (b <= 10 ? 10 : b <= 20 ? 20 : 0) : 0;

    // Предупреждаем один раз на 20 % и один раз на 10 %; после подъёма выше 25 % всё сбрасывается.
    private void CheckLow()
    {
        if (Battery is not { } b) return;
        if (b > 25) { _warnTier = 0; return; }
        var tier = Tier(b);
        if (tier == 0 || (_warnTier != 0 && tier >= _warnTier)) return;
        _warnTier = tier;
        ShowFlash(f => f.ShowLow(Name ?? "iPhone", b));
    }

    // ───── Уведомления ─────

    private async Task PollNoticesAsync()
    {
        if (_noticeBusy) return;
        _noticeBusy = true;
        try
        {
            var listener = UserNotificationListener.Current;
            var allowed = listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed;
            if (allowed != NoticeAccess) { NoticeAccess = allowed; Changed?.Invoke(); }
            if (!allowed) return;

            var list = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask();
            var current = new List<PhoneNotice>();
            foreach (var n in list)
            {
                var model = n.AppInfo?.AppUserModelId;
                if (model == null || !model.Contains("YourPhone", StringComparison.OrdinalIgnoreCase)) continue;
                var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) continue;
                var lines = binding.GetTextElements();
                // Три строки: приложение на телефоне, отправитель (или заголовок), текст. Двухстрочные — служебные подсказки «Связи с телефоном».
                if (lines.Count < 3) continue;
                var app = lines[0].Text?.Trim();
                if (string.IsNullOrEmpty(app)) continue;
                current.Add(new PhoneNotice(n.Id, app, lines[1].Text?.Trim() ?? "", n.CreationTime.LocalDateTime, CodeFinder.Find(lines[2].Text)));
            }
            current.Sort((a, b) => b.Time.CompareTo(a.Time));

            var fresh = _noticesPolled ? current.Where(c => !_seen.Contains((c.Id, c.Time.Ticks))).ToList() : [];
            _noticesPolled = true;
            foreach (var c in current) _seen.Add((c.Id, c.Time.Ticks));

            foreach (var app in current.Select(c => c.App).Distinct()) _ = PhoneAppIcons.GetAsync(app); // значки подгружаются заранее

            var changed = current.Count != Notices.Count || current.Zip(Notices).Any(p => p.First.Id != p.Second.Id || p.First.Time != p.Second.Time);
            Notices = current.Take(30).ToList();
            if (changed) Changed?.Invoke();
            foreach (var f in fresh.OrderBy(f => f.Time)) Arrived?.Invoke(f);

            // Говорим о самом свежем из тех, что пришли разом, и только если приложение выбрано.
            var shown = fresh.OrderByDescending(f => f.Time).FirstOrDefault(f => PhoneApps.IsEnabled(f.App));
            if (shown != null)
            {
                // Даём значку до 1,2 с на загрузку (в первый раз он идёт из сети), иначе вспышка покажет букву и сама подменит её позже.
                if (AppSettings.Load().PhoneFlashes) await Task.WhenAny(PhoneAppIcons.GetAsync(shown.App), Task.Delay(1200));
                ShowFlash(f => f.ShowMessage(shown.App));
            }
        }
        catch
        {
            // Центр уведомлений не ответил: в следующий раз.
        }
        finally { _noticeBusy = false; }
    }

    /// <summary>Мост принял файл или текст с телефона: короткая вспышка на свёрнутом острове (если вспышки не выключены).</summary>
    public void NotifyReceived(string name) => ShowFlash(f => f.ShowReceived(name));

    private void ShowFlash(Action<PhoneFlash> show)
    {
        if (!AppSettings.Load().PhoneFlashes) return; // выключатель в шапке вкладки «Телефон»
        _flash ??= new PhoneFlash();
        show(_flash);
        ActivityCenter.ShowFlash(_flash, PhoneFlash.ViewWidth, PhoneFlash.ViewHeight, FlashFor);
    }
}
