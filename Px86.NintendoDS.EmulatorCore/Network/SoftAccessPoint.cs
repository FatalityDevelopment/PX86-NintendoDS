using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Px86.NintendoDS.EmulatorCore.Hardware.Wifi;

namespace Px86.NintendoDS.EmulatorCore.Network
{
    /// <summary>
    /// A software 802.11b access point for the emulated console: open
    /// network "Px86-AP", answering on every channel.
    ///
    ///   Beacons        every 100 ms (wall clock), and probe responses to probe requests
    ///   Auth / assoc   open-system authentication (sequence 1 → 2), association (AID 1)
    ///   Data           ToDS frames with an LLC/SNAP header become Ethernet II frames for the
    ///                  <see cref="NatRouter"/>; its replies go back as FromDS data frames
    ///   Deauth/disassoc forget the station
    ///
    /// Frames are 802.11 header + body without FCS, as <see cref="WifiPacket"/> carries them.
    ///
    /// Reference: IEEE 802.11-1999 (management frames §7.2.3, data frames §7.2.2), RFC 1042 (LLC/SNAP);
    ///            GBATEK "DS Wifi IEEE802.11 Managment Frames (Type=0)".
    /// </summary>
    public sealed class SoftAccessPoint : IDisposable
    {
        public const string Ssid = Hardware.Spi.FirmwareFlash.AccessPointSsid;
        public const byte Channel = 6;

        /// <summary>The access point's MAC / BSSID (locally administered "PX86").</summary>
        public static readonly byte[] Bssid = { 0x02, 0x50, 0x58, 0x38, 0x36, 0x01 };

        private static readonly byte[] LlcSnap = { 0xAA, 0xAA, 0x03, 0x00, 0x00, 0x00 };
        private static readonly byte[] SupportedRates = { 0x82, 0x84, 0x8B, 0x96 }; // 1, 2 (basic), 5.5, 11 Mbit/s

        private readonly ConcurrentQueue<WifiPacket> _outgoing = new();
        private readonly ConcurrentDictionary<long, byte> _stations = new(); // MAC (as long) → associated
        private readonly Timer _beaconTimer;
        private readonly DateTime _started = DateTime.UtcNow;
        private int _sequence;

        public SoftAccessPoint(NatRouter? router = null)
        {
            Router = router ?? new NatRouter(Bssid, _ => { });
            Router.Output = SendToStation;
            _beaconTimer = new Timer(_ => SendBeacon(), null, 0, 100);
        }

        public NatRouter Router { get; }

        /// <summary>Id the AP uses as a <see cref="WifiPacket.Sender"/>.</summary>
        public long Id { get; } = Random.Shared.NextInt64(1, long.MaxValue);

        public int AssociatedStations => _stations.Count(s => s.Value == 2);

        public event Action<string>? Log;

        /// <summary>The next frame for the console, if any.</summary>
        public bool TryTake(out WifiPacket packet) => _outgoing.TryDequeue(out packet!);

        /// <summary>A frame the console sent.</summary>
        public void Receive(WifiPacket packet)
        {
            var f = packet.Frame;
            if (f.Length < 24)
                return;
            ushort fc = packet.FrameControl;
            int type = (fc >> 2) & 3, subtype = (fc >> 4) & 0xF;
            var a1 = f.AsSpan(4, 6);
            var source = f.AsSpan(10, 6).ToArray();
            bool forUs = a1.SequenceEqual(Bssid) || (a1[0] & 1) != 0;
            if (!forUs)
                return;

            if (type == 0)
            {
                switch (subtype)
                {
                    case 4: // probe request: answer if it asks for us (or for any SSID)
                    {
                        string? ssid = FindSsid(f.AsSpan(24));
                        if (string.IsNullOrEmpty(ssid) || ssid == Ssid)
                            Enqueue(Management(5, source, BeaconBody()));
                        break;
                    }
                    case 11: // authentication
                    {
                        if (!a1.SequenceEqual(Bssid) || f.Length < 30) break;
                        ushort algorithm = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(24));
                        var body = new byte[6];
                        BinaryPrimitives.WriteUInt16LittleEndian(body, algorithm);
                        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), 2);
                        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), (ushort)(algorithm == 0 ? 0 : 13)); // 13 = unsupported algorithm
                        _stations[Key(source)] = 1;
                        Enqueue(Management(11, source, body));
                        break;
                    }
                    case 0: // association request
                    case 2: // reassociation request
                    {
                        if (!a1.SequenceEqual(Bssid)) break;
                        var body = new List<byte> { 0x01, 0x00, 0x00, 0x00, 0x01, 0xC0 }; // capability ESS, status 0, AID 1
                        body.Add(1); body.Add((byte)SupportedRates.Length); body.AddRange(SupportedRates);
                        _stations[Key(source)] = 2;
                        Enqueue(Management(subtype == 0 ? 1 : 3, source, body.ToArray()));
                        Log?.Invoke($"Access point: console {Mac(source)} connected.");
                        break;
                    }
                    case 10: // disassociation
                    case 12: // deauthentication
                        if (_stations.TryRemove(Key(source), out _))
                            Log?.Invoke($"Access point: console {Mac(source)} left.");
                        break;
                }
                return;
            }

            if (type == 2 && (fc & 0x0300) == 0x0100 && ((subtype & 4) == 0)) // data, ToDS, not a null frame
            {
                var body = f.AsSpan(24);
                if ((fc & 0x4000) != 0 || body.Length < 8 || !body[..6].SequenceEqual(LlcSnap))
                    return; // WEP-encrypted or not IP/ARP over SNAP
                var destination = f.AsSpan(16, 6);
                ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(body[6..]);
                Router.HandleEthernet(Packets.Ethernet(destination, source, etherType, body[8..]));
            }
        }

        /// <summary>An Ethernet frame from the router → a FromDS data frame for the station.</summary>
        private void SendToStation(byte[] ethernet)
        {
            var destination = ethernet.AsSpan(0, 6);
            var source = ethernet.AsSpan(6, 6);
            var frame = new byte[24 + 8 + ethernet.Length - 14];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x0208); // data, FromDS
            destination.CopyTo(frame.AsSpan(4));
            Bssid.CopyTo(frame, 10);
            source.CopyTo(frame.AsSpan(16));
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(22), NextSequence());
            LlcSnap.CopyTo(frame, 24);
            ethernet.AsSpan(12, 2).CopyTo(frame.AsSpan(30));
            ethernet.AsSpan(14).CopyTo(frame.AsSpan(32));
            Enqueue(frame);
        }

        private void SendBeacon() => Enqueue(Management(8, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, BeaconBody()));

        /// <summary>Timestamp, interval, capability, SSID, rates, DS parameter set (beacons and probe responses).</summary>
        private byte[] BeaconBody()
        {
            var body = new List<byte>();
            var timestamp = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(timestamp, (ulong)((DateTime.UtcNow - _started).Ticks / 10));
            body.AddRange(timestamp);
            body.AddRange(new byte[] { 100, 0 });   // beacon interval: 100 TU
            body.AddRange(new byte[] { 0x21, 0 });  // capability: ESS, short preamble
            byte[] ssid = Encoding.ASCII.GetBytes(Ssid);
            body.Add(0); body.Add((byte)ssid.Length); body.AddRange(ssid);
            body.Add(1); body.Add((byte)SupportedRates.Length); body.AddRange(SupportedRates);
            body.Add(3); body.Add(1); body.Add(Channel);
            return body.ToArray();
        }

        private byte[] Management(int subtype, ReadOnlySpan<byte> destination, ReadOnlySpan<byte> body)
        {
            var frame = new byte[24 + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)(subtype << 4));
            destination.CopyTo(frame.AsSpan(4));
            Bssid.CopyTo(frame, 10);
            Bssid.CopyTo(frame, 16);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(22), NextSequence());
            body.CopyTo(frame.AsSpan(24));
            return frame;
        }

        private ushort NextSequence() => (ushort)((Interlocked.Increment(ref _sequence) & 0xFFF) << 4);

        private void Enqueue(byte[] frame)
        {
            if (_outgoing.Count < 2048)
                _outgoing.Enqueue(new WifiPacket { Frame = frame, Rate = 0x14, Sender = Id });
        }

        private static string? FindSsid(ReadOnlySpan<byte> elements)
        {
            for (int i = 0; i + 1 < elements.Length;)
            {
                int id = elements[i], length = elements[i + 1];
                if (i + 2 + length > elements.Length) break;
                if (id == 0)
                    return Encoding.ASCII.GetString(elements.Slice(i + 2, length));
                i += 2 + length;
            }
            return null;
        }

        private static long Key(ReadOnlySpan<byte> mac)
        {
            long key = 0;
            foreach (byte b in mac[..6]) key = key << 8 | b;
            return key;
        }

        private static string Mac(ReadOnlySpan<byte> mac) => Convert.ToHexString(mac[..6]);

        public void Dispose()
        {
            _beaconTimer.Dispose();
            Router.Dispose();
        }
    }
}
