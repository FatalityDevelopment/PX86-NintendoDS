using System.Net.Sockets;
using Px86.NintendoDS.EmulatorCore.Hardware.Wifi;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Network;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>What the emulated Wifi hardware is connected to.</summary>
    public enum WirelessMode
    {
        /// <summary>Nothing: games see no other consoles and no access points.</summary>
        Off,

        /// <summary>Other emulator windows on this PC (or the LAN): trades, battles, Download Play.</summary>
        Local,

        /// <summary>Local wireless plus a built-in access point that bridges to the internet (replacement WFC servers).</summary>
        Online,
    }

    /// <summary>
    /// Creates the Wifi medium for the chosen <see cref="WirelessMode"/> and
    /// connects every game the host starts to it.
    /// </summary>
    public sealed class WirelessServices : IDisposable
    {
        private readonly EmulatorHost _host;
        private LocalWifiMedium? _local;
        private OnlineWifiMedium? _online;

        public WirelessServices(EmulatorHost host)
        {
            _host = host;
            host.GameStarted += session => session.Emulator.Wifi.Medium = Medium;
        }

        public WirelessMode Mode { get; private set; }
        public IWifiMedium Medium { get; private set; } = NullWifiMedium.Instance;
        public LocalWifiMedium? Local => _local;
        public OnlineWifiMedium? Online => _online;

        public event Action<string>? Log;

        /// <summary>Switch modes (closing and reopening sockets as needed) and reconnect the running game.</summary>
        public bool Apply(WirelessMode mode, int port, bool lan, string? dnsServer)
        {
            Close();
            bool ok = true;
            try
            {
                if (mode != WirelessMode.Off)
                {
                    _local = new LocalWifiMedium(port, lan);
                    Log?.Invoke($"Local wireless on UDP port {port}{(lan ? " (LAN)" : " (this PC)")}.");
                }
                if (mode == WirelessMode.Online)
                {
                    _online = new OnlineWifiMedium(_local!, OnlineWifiMedium.ParseDns(dnsServer));
                    _online.Log += m => Log?.Invoke(m);
                    Log?.Invoke($"Access point \"{SoftAccessPoint.Ssid}\" online (DNS: {_online.Router.DnsServer?.ToString() ?? "this PC's resolver"}).");
                }
            }
            catch (SocketException ex)
            {
                Log?.Invoke($"Could not start wireless: {ex.Message}");
                Close();
                mode = WirelessMode.Off;
                ok = false;
            }
            Mode = mode;
            Medium = (IWifiMedium?)_online ?? (IWifiMedium?)_local ?? NullWifiMedium.Instance;
            _host.Invoke(() =>
            {
                if (_host.Emulator is { } nds)
                    nds.Wifi.Medium = Medium;
            });
            return ok;
        }

        /// <summary>Consoles heard from in the last few seconds.</summary>
        public int PeerCount() => _host.Emulator is { } nds ? Medium.PeerCount(nds.Wifi.Id) : 0;

        private void Close()
        {
            _online?.Dispose();
            _online = null;
            _local?.Dispose();
            _local = null;
            Medium = NullWifiMedium.Instance;
        }

        public void Dispose() => Close();

        // ── Instance number ───────────────────────────────────────────────────

        private static Mutex? _instanceMutex;

        /// <summary>
        /// 0 for the first emulator window on this PC, 1 for the second… Windows
        /// after the first get a different MAC address, so two copies started
        /// from the same firmware can see each other in local wireless play.
        /// </summary>
        public static int InstanceNumber { get; } = ClaimInstance();

        private static int ClaimInstance()
        {
            for (int n = 0; n < 16; n++)
            {
                try
                {
                    var mutex = new Mutex(true, $"Px86.NintendoDS.Instance{n}", out bool created);
                    if (created)
                    {
                        _instanceMutex = mutex;
                        return n;
                    }
                    mutex.Dispose();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                {
                    return 0;
                }
            }
            return 0;
        }
    }
}
