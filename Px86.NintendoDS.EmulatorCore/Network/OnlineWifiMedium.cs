using System.Net;
using Px86.NintendoDS.EmulatorCore.Hardware.Wifi;

namespace Px86.NintendoDS.EmulatorCore.Network
{
    /// <summary>
    /// Local wireless plus the <see cref="SoftAccessPoint"/>: frames from the
    /// console go to the other consoles and to the access point, and the
    /// console receives from both. The access point bridges to the internet
    /// through its <see cref="NatRouter"/>.
    /// </summary>
    public sealed class OnlineWifiMedium : IWifiMedium, IDisposable
    {
        private readonly IWifiMedium _local;

        public OnlineWifiMedium(IWifiMedium local, IPAddress? dnsServer)
        {
            _local = local;
            AccessPoint = new SoftAccessPoint(new NatRouter(SoftAccessPoint.Bssid, _ => { }, dnsServer));
            AccessPoint.Log += m => Log?.Invoke(m);
            AccessPoint.Router.Log += m => Log?.Invoke(m);
        }

        public SoftAccessPoint AccessPoint { get; }
        public NatRouter Router => AccessPoint.Router;

        public event Action<string>? Log;

        /// <summary>"1.2.3.4" → that server; empty → the host's resolver.</summary>
        public static IPAddress? ParseDns(string? text) =>
            !string.IsNullOrWhiteSpace(text) && IPAddress.TryParse(text.Trim(), out var address) ? address : null;

        public void Send(WifiPacket packet)
        {
            _local.Send(packet);
            AccessPoint.Receive(packet);
        }

        public bool TryReceive(long receiver, TimeSpan timeout, out WifiPacket packet)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (AccessPoint.TryTake(out packet))
                    return true;
                var left = deadline - DateTime.UtcNow;
                var slice = left > TimeSpan.FromMilliseconds(2) ? TimeSpan.FromMilliseconds(2) : left;
                if (_local.TryReceive(receiver, slice, out packet))
                    return true;
                if (DateTime.UtcNow >= deadline)
                    return false;
            }
        }

        public int PeerCount(long self) => _local.PeerCount(self);

        public void Dispose() => AccessPoint.Dispose();
    }
}
