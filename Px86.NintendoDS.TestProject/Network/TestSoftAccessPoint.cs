using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Px86.NintendoDS.EmulatorCore.Hardware.Wifi;
using Px86.NintendoDS.EmulatorCore.Network;

namespace Px86.NintendoDS.TestProject.Network
{
    /// <summary>
    /// The access point and router, driven with hand-built frames the way a
    /// DS's Wifi library sends them: association, DHCP, ARP, DNS, and TCP/UDP
    /// through the NAT to servers on this machine (no external network).
    /// </summary>
    [TestClass]
    public class TestSoftAccessPoint
    {
        private static readonly byte[] ConsoleMac = { 0x00, 0x09, 0xBF, 0x12, 0x34, 0x56 };
        private static readonly byte[] Broadcast = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        private static readonly byte[] ConsoleIp = NatRouter.ClientAddress.GetAddressBytes();
        private static readonly byte[] GatewayIp = NatRouter.Gateway.GetAddressBytes();

        private static byte[] Management(int subtype, byte[] a1, byte[] a3, params byte[] body)
        {
            var f = new byte[24 + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(f, (ushort)(subtype << 4));
            a1.CopyTo(f, 4); ConsoleMac.CopyTo(f, 10); a3.CopyTo(f, 16);
            body.CopyTo(f, 24);
            return f;
        }

        /// <summary>An Ethernet payload sent to the AP as a ToDS data frame with an LLC/SNAP header.</summary>
        private static WifiPacket Data(byte[] destinationMac, ushort etherType, byte[] payload)
        {
            var f = new byte[24 + 8 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(f, 0x0108);
            SoftAccessPoint.Bssid.CopyTo(f, 4); ConsoleMac.CopyTo(f, 10); destinationMac.CopyTo(f, 16);
            new byte[] { 0xAA, 0xAA, 0x03, 0, 0, 0 }.CopyTo(f, 24);
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(30), etherType);
            payload.CopyTo(f, 32);
            return new WifiPacket { Frame = f, Sender = 1 };
        }

        private static WifiPacket Ip(byte[] destinationIp, byte protocol, byte[] transport) =>
            Data(SoftAccessPoint.Bssid, Packets.EtherTypeIPv4, Packets.IPv4(ConsoleIp, destinationIp, protocol, transport, 1));

        /// <summary>Wait for a frame from the AP that <paramref name="match"/> accepts (beacons are skipped).</summary>
        private static byte[] Expect(SoftAccessPoint ap, Func<byte[], bool> match, string what, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                while (ap.TryTake(out var p))
                    if (match(p.Frame))
                        return p.Frame;
                Thread.Sleep(2);
            }
            Assert.Fail($"no {what} from the access point");
            return null!;
        }

        private static ushort Fc(byte[] f) => BinaryPrimitives.ReadUInt16LittleEndian(f);

        /// <summary>The IPv4 packet inside a FromDS data frame (null for other frames).</summary>
        private static byte[]? IpOf(byte[] f, byte protocol)
        {
            if (Fc(f) != 0x0208 || f.Length < 32 + 20 || BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(30)) != Packets.EtherTypeIPv4)
                return null;
            var ip = f.AsSpan(32).ToArray();
            return ip[9] == protocol ? ip : null;
        }

        [TestMethod]
        public void ProbeAuthenticateAssociate()
        {
            using var ap = new SoftAccessPoint();
            ap.Receive(new WifiPacket { Frame = Management(4, Broadcast, Broadcast, 0, 0), Sender = 1 }); // probe, any SSID
            var probe = Expect(ap, f => Fc(f) == 0x0050, "probe response");
            Assert.IsTrue(Encoding.ASCII.GetString(probe).Contains(SoftAccessPoint.Ssid), "SSID in the probe response");

            ap.Receive(new WifiPacket { Frame = Management(11, SoftAccessPoint.Bssid, SoftAccessPoint.Bssid, 0, 0, 1, 0, 0, 0), Sender = 1 });
            var auth = Expect(ap, f => Fc(f) == 0x00B0, "authentication");
            Assert.AreEqual(2, BinaryPrimitives.ReadUInt16LittleEndian(auth.AsSpan(26)), "auth sequence 2");
            Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(auth.AsSpan(28)), "auth status success");
            CollectionAssert.AreEqual(ConsoleMac, auth[4..10]);

            ap.Receive(new WifiPacket { Frame = Management(0, SoftAccessPoint.Bssid, SoftAccessPoint.Bssid, 0x01, 0, 0x0A, 0), Sender = 1 });
            var assoc = Expect(ap, f => Fc(f) == 0x0010, "association response");
            Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(assoc.AsSpan(26)), "assoc status success");
            Assert.AreEqual(0xC001, BinaryPrimitives.ReadUInt16LittleEndian(assoc.AsSpan(28)), "AID 1");
            Assert.AreEqual(1, ap.AssociatedStations);
        }

        [TestMethod]
        public void DhcpGivesTheConsoleAnAddress()
        {
            using var ap = new SoftAccessPoint();
            var dhcp = new byte[300];
            dhcp[0] = 1; dhcp[1] = 1; dhcp[2] = 6;
            BinaryPrimitives.WriteUInt32BigEndian(dhcp.AsSpan(4), 0x12345678);
            ConsoleMac.CopyTo(dhcp, 28);
            BinaryPrimitives.WriteUInt32BigEndian(dhcp.AsSpan(236), 0x63825363);
            dhcp[240] = 53; dhcp[241] = 1; dhcp[242] = 1; dhcp[243] = 255; // DISCOVER
            byte[] any = { 0, 0, 0, 0 }, all = { 255, 255, 255, 255 };
            ap.Receive(Data(Broadcast, Packets.EtherTypeIPv4, Packets.IPv4(any, all, Packets.ProtocolUdp, Packets.Udp(any, all, 68, 67, dhcp), 1)));

            var offer = IpOf(Expect(ap, f => IpOf(f, Packets.ProtocolUdp) != null, "DHCP offer"), Packets.ProtocolUdp)!;
            var bootp = offer.AsSpan(28);
            Assert.AreEqual(2, bootp[0], "BOOTREPLY");
            Assert.AreEqual(0x12345678u, BinaryPrimitives.ReadUInt32BigEndian(bootp[4..]), "xid echoed");
            CollectionAssert.AreEqual(ConsoleIp, bootp.Slice(16, 4).ToArray(), "yiaddr 10.0.2.15");
            Assert.AreEqual(2, bootp[242], "message type OFFER");
            Assert.AreEqual(Packets.Checksum(offer.AsSpan(0, 20)), 0, "IPv4 header checksum valid");
        }

        [TestMethod]
        public void ArpForTheGatewayIsAnswered()
        {
            using var ap = new SoftAccessPoint();
            var arp = new byte[28];
            BinaryPrimitives.WriteUInt16BigEndian(arp, 1);
            BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(2), 0x0800);
            arp[4] = 6; arp[5] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(6), 1);
            ConsoleMac.CopyTo(arp, 8); ConsoleIp.CopyTo(arp, 14); GatewayIp.CopyTo(arp, 24);
            ap.Receive(Data(Broadcast, Packets.EtherTypeArp, arp));
            var reply = Expect(ap, f => Fc(f) == 0x0208 && BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(30)) == Packets.EtherTypeArp, "ARP reply");
            Assert.AreEqual(2, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(32 + 6)), "ARP reply opcode");
            CollectionAssert.AreEqual(SoftAccessPoint.Bssid, reply[(32 + 8)..(32 + 14)], "gateway MAC = AP");
        }

        [TestMethod]
        public void DnsUsesTheResolver()
        {
            using var ap = new SoftAccessPoint();
            ap.Router.Resolver = name => name == "nas.example.test" ? IPAddress.Parse("192.0.2.7") : null;
            var query = new List<byte> { 0xAB, 0xCD, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in "nas.example.test".Split('.'))
            {
                query.Add((byte)label.Length);
                query.AddRange(Encoding.ASCII.GetBytes(label));
            }
            query.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            ap.Receive(Ip(GatewayIp, Packets.ProtocolUdp, Packets.Udp(ConsoleIp, GatewayIp, 5353, 53, query.ToArray())));
            var ip = IpOf(Expect(ap, f => IpOf(f, Packets.ProtocolUdp) != null, "DNS answer"), Packets.ProtocolUdp)!;
            var dns = ip.AsSpan(28);
            Assert.AreEqual(0xABCD, BinaryPrimitives.ReadUInt16BigEndian(dns), "id");
            Assert.AreEqual(1, BinaryPrimitives.ReadUInt16BigEndian(dns[6..]), "one answer");
            CollectionAssert.AreEqual(new byte[] { 192, 0, 2, 7 }, dns[^4..].ToArray(), "A record");
        }

        [TestMethod]
        public void TcpThroughTheNatReachesAnEchoServer()
        {
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            int port = ((IPEndPoint)server.LocalEndpoint).Port;
            var echo = Task.Run(async () =>
            {
                using var client = await server.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[256];
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                    await stream.WriteAsync(buffer.AsMemory(0, n));
            });

            using var ap = new SoftAccessPoint();
            byte[] remote = IPAddress.Loopback.GetAddressBytes();
            ushort consolePort = 40000;
            uint seq = 1000;
            WifiPacket Segment(uint sequence, uint ack, ushort flags, byte[] data, ushort mss = 0) =>
                Ip(remote, Packets.ProtocolTcp, Packets.Tcp(ConsoleIp, remote, consolePort, (ushort)port, sequence, ack, flags, 8192, data, mss));
            byte[]? TcpOf(byte[] f) => IpOf(f, Packets.ProtocolTcp) is { } ip ? ip[20..] : null;

            ap.Receive(Segment(seq, 0, Packets.TcpSyn, Array.Empty<byte>(), mss: 1024));
            var synAck = TcpOf(Expect(ap, f => TcpOf(f) is { } t && (t[13] & 0x12) == 0x12, "SYN-ACK"))!;
            Assert.AreEqual(seq + 1, BinaryPrimitives.ReadUInt32BigEndian(synAck.AsSpan(8)), "SYN acknowledged");
            uint serverSeq = BinaryPrimitives.ReadUInt32BigEndian(synAck.AsSpan(4)) + 1;
            seq++;
            ap.Receive(Segment(seq, serverSeq, Packets.TcpAck, Array.Empty<byte>()));

            byte[] hello = Encoding.ASCII.GetBytes("hello from the DS");
            ap.Receive(Segment(seq, serverSeq, Packets.TcpAck | Packets.TcpPsh, hello));
            seq += (uint)hello.Length;
            var reply = TcpOf(Expect(ap, f => TcpOf(f) is { } t && t.Length > (t[12] >> 4) * 4, "echoed data"))!;
            var payload = reply[((reply[12] >> 4) * 4)..];
            Assert.AreEqual("hello from the DS", Encoding.ASCII.GetString(payload));
            Assert.AreEqual(seq, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8)), "our data acknowledged");
            ushort sum = Packets.Checksum(reply, Packets.PseudoHeaderSum(remote, ConsoleIp, Packets.ProtocolTcp, reply.Length));
            Assert.AreEqual(0, sum, "TCP checksum valid");
            Assert.AreEqual(1, ap.Router.OpenConnections);

            serverSeq += (uint)payload.Length;
            ap.Receive(Segment(seq, serverSeq, Packets.TcpAck | Packets.TcpFin, Array.Empty<byte>()));
            Expect(ap, f => TcpOf(f) is { } t && (t[13] & Packets.TcpFin) != 0, "FIN from the server side");
            server.Stop();
        }

        [TestMethod]
        public void UdpThroughTheNatReachesAnEchoServer()
        {
            using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
            _ = Task.Run(async () =>
            {
                var r = await server.ReceiveAsync();
                await server.SendAsync(r.Buffer, r.Buffer.Length, r.RemoteEndPoint);
            });
            using var ap = new SoftAccessPoint();
            byte[] remote = IPAddress.Loopback.GetAddressBytes();
            ap.Receive(Ip(remote, Packets.ProtocolUdp, Packets.Udp(ConsoleIp, remote, 41000, (ushort)port, Encoding.ASCII.GetBytes("ping"))));
            var ip = IpOf(Expect(ap, f => IpOf(f, Packets.ProtocolUdp) != null, "UDP reply"), Packets.ProtocolUdp)!;
            Assert.AreEqual(41000, BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(22)), "back to the console's port");
            Assert.AreEqual("ping", Encoding.ASCII.GetString(ip.AsSpan(28)));
        }
    }
}
