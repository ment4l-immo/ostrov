using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Ostrov.Dictation;

/// <summary>Микрофон: идентификатор устройства Windows и название для показа.</summary>
internal sealed record MicDevice(string Id, string Name);

/// <summary>Список микрофонов компьютера для настроек диктовки.</summary>
internal static class MicDevices
{
    /// <summary>Идентификатор микрофона по умолчанию в Windows или null, если микрофонов нет.</summary>
    public static string? DefaultId()
    {
        try
        {
            var id = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch { return null; }
    }

    /// <summary>Включённые микрофоны по именам. Пустой список — микрофонов нет или Windows не ответила.</summary>
    public static async Task<List<MicDevice>> ListAsync()
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture).AsTask();
            return found.Where(d => d.IsEnabled && !string.IsNullOrWhiteSpace(d.Name))
                .Select(d => new MicDevice(d.Id, d.Name.Trim()))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }
}
