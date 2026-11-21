using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace Ostrov.Modules.ControlCenter;

/// <summary>
/// Состояние VPN-клиентов (Amnezia и Happ) только на чтение и открытие их окна: включать и выключать их снаружи нельзя
/// (официального способа нет, решение пользователя 2026-11-12).
/// </summary>
internal static class VpnStatus
{
    public readonly record struct State(bool Amnezia, bool Happ)
    {
        public bool Any => Amnezia || Happ;

        public string Label => (Amnezia, Happ) switch
        {
            (true, true) => "Amnezia · Happ",
            (true, false) => "Amnezia",
            (false, true) => "Happ",
            _ => "Выкл"
        };
    }

    public static State Read()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();
            return new State(IsAmneziaUp(up), IsHappUp(up));
        }
        catch
        {
            return default;
        }
    }

    private static bool Has(NetworkInterface n, string text) =>
        n.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || n.Description.Contains(text, StringComparison.OrdinalIgnoreCase);

    // Amnezia с протоколом AmneziaWG поднимает адаптер «AmneziaVPN»; при выключении он исчезает.
    private static bool IsAmneziaUp(List<NetworkInterface> up) => up.Any(n => Has(n, "amnezia"));

    // Happ: определяем по своему адаптеру (режим TUN) или по системному прокси на этот же компьютер (режим «прокси»).
    private static bool IsHappUp(List<NetworkInterface> up) => up.Any(n => Has(n, "happ")) || SystemProxyIsLocal();

    private static bool SystemProxyIsLocal()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            return key?.GetValue("ProxyEnable") is int enabled && enabled == 1
                && key.GetValue("ProxyServer") is string server && server.Contains("127.0.0.1");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Открыть окно клиента: Amnezia, а если включён только Happ, то Happ.</summary>
    public static bool OpenClient(State state)
    {
        var amnezia = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AmneziaVPN", "AmneziaVPN.exe");
        var happ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlyFrogLLC", "Happ", "Happ.exe");
        var path = state is { Happ: true, Amnezia: false } && File.Exists(happ) ? happ
            : File.Exists(amnezia) ? amnezia
            : File.Exists(happ) ? happ
            : null;
        if (path == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
