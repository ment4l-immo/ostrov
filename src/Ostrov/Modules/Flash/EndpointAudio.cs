using System.Runtime.InteropServices;

namespace Ostrov.Modules.Flash;

/// <summary>
/// Громкость и «выключен звук» у устройства звука по умолчанию (Core Audio, без сторонних пакетов): колонки или наушники (вывод) и микрофон (запись).
/// Только чтение; права администратора не нужны. Общую громкость Windows это показывает, а не громкость отдельной программы.
/// </summary>
internal sealed class EndpointAudio : IDisposable
{
    private IAudioEndpointVolume? _volume;

    /// <summary>Идентификатор устройства: сменился — значит, переключили устройство (громкость другого устройства вспышкой не считается).</summary>
    public string Id { get; }

    private EndpointAudio(IAudioEndpointVolume volume, string id)
    {
        _volume = volume;
        Id = id;
    }

    /// <param name="capture">false — вывод звука (колонки, наушники), true — запись (микрофон).</param>
    public static EndpointAudio? OpenDefault(bool capture)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (enumerator.GetDefaultAudioEndpoint(capture ? 1 : 0, 1 /* мультимедиа */, out device) != 0 || device == null) return null;
            device.GetId(out var id);
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var endpoint) != 0) return null;
            return new EndpointAudio((IAudioEndpointVolume)endpoint, id ?? "");
        }
        catch { return null; }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Текущая громкость (0…1) и выключен ли звук. false — устройство недоступно (отключили).</summary>
    public bool TryRead(out float volume, out bool muted)
    {
        volume = 0;
        muted = false;
        try { return _volume != null && _volume.GetMasterVolumeLevelScalar(out volume) == 0 && _volume.GetMute(out muted) == 0; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_volume == null) return;
        Marshal.ReleaseComObject(_volume);
        _volume = null;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    // Порядок методов важен (так устроена таблица методов COM): неиспользуемые стоят заглушками.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
