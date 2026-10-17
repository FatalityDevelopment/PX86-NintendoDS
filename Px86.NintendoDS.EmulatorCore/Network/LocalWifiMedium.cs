using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Px86.NintendoDS.EmulatorCore.Hardware.Wifi;

namespace Px86.NintendoDS.EmulatorCore.Network
{
    /// <summary>
    /// Local wireless between emulator windows: every frame a console sends is
    /// multicast over UDP, and every member of the group receives it — on the
    /// same PC (loopback) or, optionally, across the LAN.
    ///
    /// Datagram layout (little-endian)
    ///   0  4  "PX86"
    ///   4  1  version (1)
    ///   5  1  rate (0Ah / 14h, as in the RX header)
    ///   6  1  multiplay REPLY slave number (0 if not a reply)
    ///   7  1  reserved
    ///   8  8  sender id (a random console id; own frames are dropped)
    ///   16 …  IEEE 802.11 frame without FCS
    ///
    /// A datagram with no frame is a presence announcement. Every member sends
    /// one when it joins and every second, and answers a newcomer's at once,
    /// so a multiplay master knows its slaves (and waits for their replies)
    /// before they have sent any frame.
    ///
    /// There is no clock sync between the consoles: frames are delivered as they
    /// arrive, and a multiplay master waits (in real time) for its slaves'
    /// replies. Games that are very strict about multiplay timing may drop the
    /// connection.
    /// </summary>
    public sealed class LocalWifiMedium : IWifiMedium, IDisposable
    {
        public const int DefaultPort = 7064;
        public static readonly IPAddress DefaultGroup = IPAddress.Parse("239.255.70.64");

        private const int HeaderSize = 16;
        private static readonly byte[] Magic = "PX86"u8.ToArray();

        private readonly UdpClient _socket;
        private readonly IPEndPoint _group;
        private readonly BlockingCollection<WifiPacket> _received = new(new ConcurrentQueue<WifiPacket>(), 4096);
        private readonly ConcurrentDictionary<long, DateTime> _peers = new();   // console ids, from frames
        private readonly ConcurrentDictionary<long, DateTime> _members = new(); // other media, from announcements
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _receiver;
        private readonly long _id = Random.Shared.NextInt64();
        private readonly Timer _presence;

        /// <param name="lan">Also reach consoles on other machines in the local network (multicast TTL 1).</param>
        public LocalWifiMedium(int port = DefaultPort, bool lan = false, IPAddress? group = null)
        {
            _group = new IPEndPoint(group ?? DefaultGroup, port);
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.ExclusiveAddressUse = false;
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            var iface = lan ? IPAddress.Any : IPAddress.Loopback;
            _socket.JoinMulticastGroup(_group.Address, iface);
            _socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, iface.GetAddressBytes());
            _socket.MulticastLoopback = true;
            _socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, lan ? 1 : 0);
            Port = port;
            _receiver = Task.Run(ReceiveLoop);
            _presence = new Timer(_ => Announce(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        public int Port { get; }

        public long DatagramsSent { get; private set; }
        public long DatagramsReceived { get; private set; }

        public void Send(WifiPacket packet)
        {
            var datagram = new byte[HeaderSize + packet.Frame.Length];
            Magic.CopyTo(datagram, 0);
            datagram[4] = 1;
            datagram[5] = (byte)packet.Rate;
            datagram[6] = (byte)packet.ReplyAid;
            BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(8), packet.Sender);
            packet.Frame.CopyTo(datagram, HeaderSize);
            SendDatagram(datagram);
            DatagramsSent++;
        }

        /// <summary>Other members of the group heard from in the last 5 s.</summary>
        public int KnownPeers => PeerCount(_id);

        private void Announce()
        {
            var datagram = new byte[HeaderSize];
            Magic.CopyTo(datagram, 0);
            datagram[4] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(8), _id);
            SendDatagram(datagram);
        }

        private void SendDatagram(byte[] datagram)
        {
            try
            {
                _socket.Send(datagram, datagram.Length, _group);
            }
            catch (SocketException)
            {
                // No route (e.g. no network at all): the frame is lost, as on a busy channel.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public bool TryReceive(long receiver, TimeSpan timeout, out WifiPacket packet)
        {
            while (timeout <= TimeSpan.Zero ? _received.TryTake(out packet!) : _received.TryTake(out packet!, timeout))
            {
                if (packet.Sender != receiver)
                    return true;
                timeout = TimeSpan.Zero; // our own datagram looped back: skip it
            }
            packet = null!;
            return false;
        }

        public int PeerCount(long self)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(5);
            return Math.Max(_peers.Count(p => p.Key != self && p.Value > cutoff), _members.Count(p => p.Value > cutoff));
        }

        private async Task ReceiveLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                var data = result.Buffer;
                if (data.Length < HeaderSize || !data.AsSpan(0, 4).SequenceEqual(Magic) || data[4] != 1)
                    continue;
                long sender = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
                if (data.Length == HeaderSize)
                {
                    if (sender == _id)
                        continue;
                    bool known = _members.TryGetValue(sender, out var seen) && seen > DateTime.UtcNow - TimeSpan.FromSeconds(5);
                    _members[sender] = DateTime.UtcNow;
                    if (!known)
                        Announce();
                    continue;
                }
                if (data.Length < HeaderSize + 10)
                    continue;
                _peers[sender] = DateTime.UtcNow;
                DatagramsReceived++;
                _received.TryAdd(new WifiPacket
                {
                    Frame = data.AsSpan(HeaderSize).ToArray(),
                    Rate = data[5],
                    ReplyAid = data[6],
                    Sender = sender,
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _presence.Dispose();
            _socket.Dispose();
            try { _receiver.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
            _stop.Dispose();
            _received.Dispose();
        }
    }
}
