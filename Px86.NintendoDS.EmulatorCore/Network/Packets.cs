using System.Buffers.Binary;
using System.Net;

namespace Px86.NintendoDS.EmulatorCore.Network
{
    /// <summary>
    /// Helpers for the small network stack behind the software access point:
    /// Ethernet II, ARP, IPv4, UDP and TCP headers, written from the RFCs
    /// (RFC 791 IPv4, RFC 768 UDP, RFC 793 TCP, RFC 826 ARP, RFC 1071 checksum).
    /// All multi-byte header fields are big-endian.
    /// </summary>
    public static class Packets
    {
        public const ushort EtherTypeIPv4 = 0x0800;
        public const ushort EtherTypeArp = 0x0806;
        public const byte ProtocolIcmp = 1, ProtocolTcp = 6, ProtocolUdp = 17;

        public const ushort TcpFin = 0x01, TcpSyn = 0x02, TcpRst = 0x04, TcpPsh = 0x08, TcpAck = 0x10;

        /// <summary>RFC 1071 one's-complement sum, starting from <paramref name="initial"/>.</summary>
        public static ushort Checksum(ReadOnlySpan<byte> data, uint initial = 0)
        {
            uint sum = initial;
            int i = 0;
            for (; i + 1 < data.Length; i += 2)
                sum += (uint)(data[i] << 8 | data[i + 1]);
            if (i < data.Length)
                sum += (uint)(data[i] << 8);
            while (sum >> 16 != 0)
                sum = (sum & 0xFFFF) + (sum >> 16);
            return (ushort)~sum;
        }

        /// <summary>Sum of the TCP/UDP pseudo-header (source, destination, protocol, length).</summary>
        public static uint PseudoHeaderSum(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, byte protocol, int length)
        {
            uint sum = 0;
            for (int i = 0; i < 4; i += 2)
            {
                sum += (uint)(source[i] << 8 | source[i + 1]);
                sum += (uint)(destination[i] << 8 | destination[i + 1]);
            }
            return sum + protocol + (uint)length;
        }

        public static byte[] Ethernet(ReadOnlySpan<byte> destination, ReadOnlySpan<byte> source, ushort type, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[14 + payload.Length];
            destination[..6].CopyTo(frame);
            source[..6].CopyTo(frame.AsSpan(6));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), type);
            payload.CopyTo(frame.AsSpan(14));
            return frame;
        }

        /// <summary>An IPv4 packet (no options, don't fragment) around <paramref name="payload"/>.</summary>
        public static byte[] IPv4(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, byte protocol, ReadOnlySpan<byte> payload, ushort id)
        {
            var packet = new byte[20 + payload.Length];
            packet[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), id);
            packet[6] = 0x40; // DF
            packet[8] = 64;   // TTL
            packet[9] = protocol;
            source[..4].CopyTo(packet.AsSpan(12));
            destination[..4].CopyTo(packet.AsSpan(16));
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20)));
            payload.CopyTo(packet.AsSpan(20));
            return packet;
        }

        public static byte[] Udp(ReadOnlySpan<byte> sourceIp, ReadOnlySpan<byte> destinationIp, ushort sourcePort, ushort destinationPort, ReadOnlySpan<byte> data)
        {
            var segment = new byte[8 + data.Length];
            BinaryPrimitives.WriteUInt16BigEndian(segment, sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), destinationPort);
            BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(4), (ushort)segment.Length);
            data.CopyTo(segment.AsSpan(8));
            ushort sum = Checksum(segment, PseudoHeaderSum(sourceIp, destinationIp, ProtocolUdp, segment.Length));
            BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(6), sum == 0 ? (ushort)0xFFFF : sum);
            return segment;
        }

        public static byte[] Tcp(ReadOnlySpan<byte> sourceIp, ReadOnlySpan<byte> destinationIp, ushort sourcePort, ushort destinationPort,
                                 uint sequence, uint acknowledgment, ushort flags, ushort window, ReadOnlySpan<byte> data, ushort mss = 0)
        {
            int headerLength = mss != 0 ? 24 : 20;
            var segment = new byte[headerLength + data.Length];
            BinaryPrimitives.WriteUInt16BigEndian(segment, sourcePort);
            BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), destinationPort);
            BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(4), sequence);
            BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(8), acknowledgment);
            segment[12] = (byte)(headerLength / 4 << 4);
            segment[13] = (byte)flags;
            BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(14), window);
            if (mss != 0)
            {
                segment[20] = 2;
                segment[21] = 4;
                BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(22), mss);
            }
            data.CopyTo(segment.AsSpan(headerLength));
            BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(16), Checksum(segment, PseudoHeaderSum(sourceIp, destinationIp, ProtocolTcp, segment.Length)));
            return segment;
        }

        public static IPAddress Address(ReadOnlySpan<byte> bytes) => new(bytes[..4]);

        public static uint ToUInt32(ReadOnlySpan<byte> ip) => BinaryPrimitives.ReadUInt32BigEndian(ip);

        public static byte[] Bytes(IPAddress address) => address.GetAddressBytes();
    }
}
