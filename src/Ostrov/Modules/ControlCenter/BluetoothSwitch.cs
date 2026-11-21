using System.Windows;
using Windows.Devices.Radios;

namespace Ostrov.Modules.ControlCenter;

/// <summary>
/// Переключатель Bluetooth через штатный интерфейс Windows (WinRT «Radios»): без сторонних библиотек и без прав администратора.
/// Проверено на этом компьютере: доступ разрешается, радио одно.
/// </summary>
internal sealed class BluetoothSwitch
{
    private Radio? _radio;
    private Task? _init;

    /// <summary>Инициализация закончилась (успешно или нет): до этого не стоит писать «нет адаптера».</summary>
    public bool Ready { get; private set; }

    public bool Available => _radio != null;
    public bool IsOn => _radio?.State == RadioState.On;

    /// <summary>Состояние изменилось (в том числе из «Параметров» Windows). Приходит в потоке интерфейса.</summary>
    public event Action? Changed;

    public Task EnsureAsync() => _init ??= InitAsync();

    private async Task InitAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (_radio != null)
                    _radio.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
            }
        }
        catch
        {
            // Нет доступа к радио — плитка покажет «Нет адаптера».
        }

        Ready = true;
        if (_radio == null) _init = null; // адаптера нет сейчас — при следующем открытии вкладки спросим снова
        Changed?.Invoke();
    }

    /// <returns>true, если Windows приняла команду.</returns>
    public async Task<bool> SetAsync(bool on)
    {
        if (_radio == null) return false;
        try
        {
            return await _radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch
        {
            return false;
        }
    }
}
