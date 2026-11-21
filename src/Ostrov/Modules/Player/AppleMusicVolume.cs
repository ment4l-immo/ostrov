using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ostrov.Modules.Player;

/// <summary>
/// Громкость Apple Music через микшер Windows: меняется громкость аудиосеансов именно этой программы (как ползунок Apple Music
/// в «Микшере громкости»), общая громкость Windows не трогается. Звук Apple Music идёт из процессов AMPLibraryAgent.exe
/// и AppleMusic.exe (проверено пробой 2026-11-12): трогаем сеансы обоих. Права администратора не нужны.
/// </summary>
internal static class AppleMusicVolume
{
    private static readonly string[] ProcessNames = ["AMPLibraryAgent", "AppleMusic"];

    /// <summary>Текущая громкость 0..1 и «звук выключен»; false, если у Apple Music сейчас нет аудиосеансов (не запущена).</summary>
    public static bool TryGet(out double level, out bool muted)
    {
        double max = 0;
        var anyMuted = false;
        var found = false;
        Visit(volume =>
        {
            if (volume.GetMasterVolume(out var l) == 0) max = Math.Max(max, l);
            if (volume.GetMute(out var m) == 0) anyMuted |= m;
            found = true;
        });
        level = max;
        muted = anyMuted;
        return found;
    }

    public static bool TrySetLevel(double level)
    {
        var ok = false;
        var value = (float)Math.Clamp(level, 0, 1);
        Visit(volume =>
        {
            var context = Guid.Empty;
            ok |= volume.SetMasterVolume(value, ref context) == 0;
        });
        return ok;
    }

    public static bool TrySetMuted(bool muted)
    {
        var ok = false;
        Visit(volume =>
        {
            var context = Guid.Empty;
            ok |= volume.SetMute(muted, ref context) == 0;
        });
        return ok;
    }

    private static HashSet<uint> AppleProcessIds()
    {
        var ids = new HashSet<uint>();
        foreach (var name in ProcessNames)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name)) ids.Add((uint)p.Id);
            }
            catch { /* процесс исчез */ }
        }
        return ids;
    }

    // Обходит сеансы устройства вывода по умолчанию, принадлежащие процессам Apple Music.
    private static void Visit(Action<Native.ISimpleAudioVolume> action)
    {
        var pids = AppleProcessIds();
        if (pids.Count == 0) return;
        try
        {
            var enumerator = (Native.IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (enumerator.GetDefaultAudioEndpoint(0 /* вывод */, 1 /* мультимедиа */, out var device) != 0) return;
            var iid = typeof(Native.IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var managerObject) != 0) return;
            var manager = (Native.IAudioSessionManager2)managerObject;
            if (manager.GetSessionEnumerator(out var sessions) != 0) return;
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0) continue;
                try
                {
                    control.GetProcessId(out var pid);
                    if (pids.Contains(pid) && control is Native.ISimpleAudioVolume volume) action(volume);
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
        }
        catch
        {
            // Звуковая служба недоступна: громкости нет.
        }
    }

    private static class Native
    {
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
