using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Px86.NintendoDS.EmulatorCore.Network
{
    /// <summary>
    /// A tiny router between the emulated console and the host's network, in
    /// the style of user-mode NAT: the console talks Ethernet/IPv4 to it, and
    /// it turns that into ordinary host sockets. Nothing is installed on the
    /// host, and no administrator rights are needed.
    ///
    ///   Network 10.0.2.0/24: gateway (and DNS) 10.0.2.1, the console gets 10.0.2.15 by DHCP.
    ///   ARP      answers for every address but the console's own (proxy ARP).
    ///   DHCP     DISCOVER → OFFER, REQUEST → ACK (RFC 2131).
    ///   DNS      queries to 10.0.2.1 are forwarded to <see cref="DnsServer"/> when set, otherwise answered
    ///            from the host's resolver (A records only).
    ///   UDP      one host socket per console source port.
    ///   TCP      one host connection per console connection; this side runs a minimal TCP endpoint
    ///            (handshake, in-order data, window, go-back-N retransmission, FIN/RST). TLS and HTTP pass
    ///            through untouched — the game does its own SSL.
    ///   ICMP     echo requests to the gateway are answered.
    ///
    /// Thread-safe: frames from the console arrive on the emulator thread, host
    /// data on thread-pool threads; replies go out through <see cref="Output"/>.
    ///
    /// Reference: RFC 791, 768, 793, 826, 1035, 2131.
    /// </summary>
    public sealed class NatRouter : IDisposable
    {
        public static readonly IPAddress Gateway = IPAddress.Parse("10.0.2.1");
        public static readonly IPAddress ClientAddress = IPAddress.Parse("10.0.2.15");
        public static readonly IPAddress Netmask = IPAddress.Parse("255.255.255.0");

        private static readonly byte[] GatewayIp = Gateway.GetAddressBytes();
        private static readonly byte[] ClientIp = ClientAddress.GetAddressBytes();
        private static readonly byte[] BroadcastMac = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        private readonly byte[] _gatewayMac;
        private readonly ConcurrentDictionary<ushort, UdpFlow> _udp = new();
        private readonly ConcurrentDictionary<(ushort, uint, ushort), TcpFlow> _tcp = new();
        private readonly Timer _timer;
        private byte[] _clientMac = BroadcastMac;
        private int _ipId;
        private bool _disposed;

        /// <param name="gatewayMac">The router's MAC (the access point's BSSID).</param>
        /// <param name="output">Receives Ethernet frames for the console.</param>
        public NatRouter(byte[] gatewayMac, Action<byte[]> output, IPAddress? dnsServer = null)
        {
            _gatewayMac = gatewayMac;
            Output = output;
            DnsServer = dnsServer;
            _timer = new Timer(_ => Tick(), null, 100, 100);
        }

        /// <summary>Receives Ethernet frames for the console.</summary>
        public Action<byte[]> Output { get; set; }

        /// <summary>Upstream DNS server (for replacement WFC servers); null = the host's own resolver.</summary>
        public IPAddress? DnsServer { get; set; }

        /// <summary>Overrides name resolution (tests, or fixed host entries). Return null to fall back.</summary>
        public Func<string, IPAddress?>? Resolver { get; set; }

        public int OpenConnections => _tcp.Count;

        public event Action<string>? Log;

        // ── Console → router ──────────────────────────────────────────────────

        public void HandleEthernet(byte[] frame)
        {
            if (_disposed || frame.Length < 14)
                return;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12));
            var payload = frame.AsSpan(14);
            if (type == Packets.EtherTypeArp)
                HandleArp(payload);
            else if (type == Packets.EtherTypeIPv4)
            {
                _clientMac = frame.AsSpan(6, 6).ToArray();
                HandleIPv4(payload);
            }
        }

        private void HandleArp(ReadOnlySpan<byte> arp)
        {
            if (arp.Length < 28 || BinaryPrimitives.ReadUInt16BigEndian(arp[6..]) != 1)
                return; // only requests
            var senderMac = arp.Slice(8, 6);
            var senderIp = arp.Slice(14, 4);
            var targetIp = arp.Slice(24, 4);
            if (targetIp.SequenceEqual(ClientIp) || targetIp.SequenceEqual(senderIp))
                return; // the console's own address (or a gratuitous ARP)
            _clientMac = senderMac.ToArray();
            var reply = new byte[28];
            arp[..6].CopyTo(reply);                          // hardware/protocol types and sizes
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), 2);
            _gatewayMac.CopyTo(reply, 8);
            targetIp.CopyTo(reply.AsSpan(14));
            senderMac.CopyTo(reply.AsSpan(18));
            senderIp.CopyTo(reply.AsSpan(24));
            Output(Packets.Ethernet(senderMac, _gatewayMac, Packets.EtherTypeArp, reply));
        }

        private void HandleIPv4(ReadOnlySpan<byte> ip)
        {
            if (ip.Length < 20 || ip[0] >> 4 != 4)
                return;
            int headerLength = (ip[0] & 0xF) * 4;
            int total = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(ip[2..]), ip.Length);
            if (total < headerLength || (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x3FFF) != 0)
                return; // fragments are not supported (the DS doesn't send them)
            byte protocol = ip[9];
            var source = ip.Slice(12, 4);
            var destination = ip.Slice(16, 4);
            var payload = ip[headerLength..total];
            switch (protocol)
            {
                case Packets.ProtocolUdp: HandleUdp(source, destination, payload); break;
                case Packets.ProtocolTcp: HandleTcp(destination, payload); break;
                case Packets.ProtocolIcmp: HandleIcmp(source, destination, payload); break;
            }
        }

        private void SendIp(ReadOnlySpan<byte> source, byte protocol, ReadOnlySpan<byte> payload)
        {
            ushort id = (ushort)Interlocked.Increment(ref _ipId);
            var packet = Packets.IPv4(source, ClientIp, protocol, payload, id);
            Output(Packets.Ethernet(_clientMac, _gatewayMac, Packets.EtherTypeIPv4, packet));
        }

        private void HandleIcmp(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, ReadOnlySpan<byte> icmp)
        {
            if (icmp.Length < 8 || icmp[0] != 8 || !destination.SequenceEqual(GatewayIp))
                return;
            var reply = icmp.ToArray();
            reply[0] = 0;
            reply[2] = reply[3] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), Packets.Checksum(reply));
            SendIp(GatewayIp, Packets.ProtocolIcmp, reply);
        }

        // ── UDP, DHCP, DNS ────────────────────────────────────────────────────

        private void HandleUdp(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, ReadOnlySpan<byte> udp)
        {
            if (udp.Length < 8)
                return;
            ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
            ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp[2..]);
            int length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(udp[4..]), udp.Length);
            var data = udp[8..length].ToArray();

            if (destinationPort == 67)
            {
                HandleDhcp(data);
                return;
            }
            if (destinationPort == 53 && destination.SequenceEqual(GatewayIp))
            {
                HandleDns(sourcePort, data);
                return;
            }
            SendUdpToHost(sourcePort, new IPEndPoint(Packets.Address(destination), destinationPort), data, replyFrom: null);
        }

        /// <summary>
        /// Send through the host socket for this console port. Replies come back
        /// as if from the remote end, or from <paramref name="replyFrom"/> (DNS forwarding).
        /// </summary>
        private void SendUdpToHost(ushort consolePort, IPEndPoint remote, byte[] data, IPEndPoint? replyFrom)
        {
            var flow = _udp.GetOrAdd(consolePort, port => new UdpFlow(this, port));
            flow.ReplyFrom = replyFrom;
            flow.LastUsed = DateTime.UtcNow;
            try
            {
                flow.Socket.Send(data, data.Length, remote);
            }
            catch (SocketException ex)
            {
                Log?.Invoke($"UDP to {remote}: {ex.Message}");
            }
        }

        private void DeliverUdp(IPEndPoint from, ushort consolePort, byte[] data)
        {
            var segment = Packets.Udp(from.Address.GetAddressBytes(), ClientIp, (ushort)from.Port, consolePort, data);
            SendIp(from.Address.GetAddressBytes(), Packets.ProtocolUdp, segment);
        }

        private sealed class UdpFlow : IDisposable
        {
            public readonly UdpClient Socket = new(new IPEndPoint(IPAddress.Any, 0));
            public IPEndPoint? ReplyFrom;
            public DateTime LastUsed = DateTime.UtcNow;
            private readonly NatRouter _router;
            private readonly ushort _port;

            public UdpFlow(NatRouter router, ushort port)
            {
                _router = router;
                _port = port;
                _ = ReceiveLoop();
            }

            private async Task ReceiveLoop()
            {
                while (true)
                {
                    UdpReceiveResult result;
                    try { result = await Socket.ReceiveAsync(); }
                    catch (ObjectDisposedException) { return; }
                    catch (SocketException) { continue; }
                    LastUsed = DateTime.UtcNow;
                    _router.DeliverUdp(ReplyFrom ?? result.RemoteEndPoint, _port, result.Buffer);
                }
            }

            public void Dispose() => Socket.Dispose();
        }

        private void HandleDhcp(byte[] request)
        {
            if (request.Length < 240 || request[0] != 1 || BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(236)) != 0x63825363)
                return;
            int messageType = 0;
            for (int i = 240; i < request.Length && request[i] != 255;)
            {
                byte option = request[i];
                if (option == 0) { i++; continue; }
                if (i + 1 >= request.Length) break;
                int length = request[i + 1];
                if (option == 53 && length >= 1 && i + 2 < request.Length)
                    messageType = request[i + 2];
                i += 2 + length;
            }
            int replyType = messageType switch { 1 => 2, 3 => 5, _ => 0 }; // DISCOVER → OFFER, REQUEST → ACK
            if (replyType == 0)
                return;

            var reply = new byte[300];
            reply[0] = 2;                                   // BOOTREPLY
            reply[1] = 1;
            reply[2] = 6;
            request.AsSpan(4, 4).CopyTo(reply.AsSpan(4));   // xid
            request.AsSpan(10, 2).CopyTo(reply.AsSpan(10)); // flags
            ClientIp.CopyTo(reply, 16);                     // yiaddr
            GatewayIp.CopyTo(reply, 20);                    // siaddr
            request.AsSpan(28, 16).CopyTo(reply.AsSpan(28)); // chaddr
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(236), 0x63825363);
            int o = 240;
            void Option(byte code, ReadOnlySpan<byte> value)
            {
                reply[o++] = code;
                reply[o++] = (byte)value.Length;
                value.CopyTo(reply.AsSpan(o));
                o += value.Length;
            }
            Option(53, new[] { (byte)replyType });
            Option(54, GatewayIp);
            Option(51, new byte[] { 0x00, 0x01, 0x51, 0x80 }); // 1 day
            Option(1, Netmask.GetAddressBytes());
            Option(3, GatewayIp);
            Option(6, GatewayIp);
            reply[o] = 255;

            _clientMac = request.AsSpan(28, 6).ToArray();
            byte[] broadcast = { 255, 255, 255, 255 };
            var segment = Packets.Udp(GatewayIp, broadcast, 67, 68, reply);
            ushort id = (ushort)Interlocked.Increment(ref _ipId);
            var packet = Packets.IPv4(GatewayIp, broadcast, Packets.ProtocolUdp, segment, id);
            Output(Packets.Ethernet(_clientMac, _gatewayMac, Packets.EtherTypeIPv4, packet));
            Log?.Invoke(replyType == 5 ? $"DHCP: console is {ClientAddress}." : "DHCP: offer sent.");
        }

        private void HandleDns(ushort consolePort, byte[] query)
        {
            string? name = DnsQuestionName(query, out ushort qtype);
            var overridden = name != null ? Resolver?.Invoke(name) : null;
            if (overridden == null && DnsServer != null)
            {
                SendUdpToHost(consolePort, new IPEndPoint(DnsServer, 53), query, replyFrom: new IPEndPoint(Gateway, 53));
                return;
            }
            if (name == null)
                return;
            _ = Task.Run(async () =>
            {
                IPAddress[] addresses = Array.Empty<IPAddress>();
                if (overridden != null)
                    addresses = new[] { overridden };
                else if (qtype == 1)
                {
                    try { addresses = await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork); }
                    catch (SocketException) { }
                }
                DeliverUdp(new IPEndPoint(Gateway, 53), consolePort, DnsAnswer(query, addresses));
            });
        }

        /// <summary>The first question's name ("a.b.c"), or null if the query is malformed.</summary>
        public static string? DnsQuestionName(ReadOnlySpan<byte> query, out ushort qtype)
        {
            qtype = 0;
            if (query.Length < 17 || BinaryPrimitives.ReadUInt16BigEndian(query[4..]) == 0)
                return null;
            var name = new StringBuilder();
            int i = 12;
            while (i < query.Length && query[i] != 0)
            {
                int length = query[i];
                if (length > 63 || i + 1 + length > query.Length)
                    return null;
                if (name.Length > 0) name.Append('.');
                name.Append(Encoding.ASCII.GetString(query.Slice(i + 1, length)));
                i += 1 + length;
            }
            if (i + 5 > query.Length)
                return null;
            qtype = BinaryPrimitives.ReadUInt16BigEndian(query[(i + 1)..]);
            return name.ToString();
        }

        /// <summary>A response with A records (or NXDOMAIN when there are none) for the query's question.</summary>
        public static byte[] DnsAnswer(ReadOnlySpan<byte> query, IReadOnlyList<IPAddress> addresses)
        {
            int questionEnd = 12;
            while (questionEnd < query.Length && query[questionEnd] != 0)
                questionEnd += 1 + query[questionEnd];
            questionEnd = Math.Min(questionEnd + 5, query.Length);
            var answer = new List<byte>(query[..questionEnd].ToArray());
            answer[2] = (byte)(0x81 | (query[2] & 0x01)); // response, recursion desired copied
            answer[3] = (byte)(addresses.Count > 0 ? 0x80 : 0x83); // recursion available; NXDOMAIN
            answer[4] = 0; answer[5] = 1;
            answer[6] = 0; answer[7] = (byte)addresses.Count;
            for (int i = 8; i < 12; i++) answer[i] = 0;
            foreach (var address in addresses)
            {
                answer.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4 });
                answer.AddRange(address.GetAddressBytes());
            }
            return answer.ToArray();
        }

        // ── TCP ───────────────────────────────────────────────────────────────

        private void HandleTcp(ReadOnlySpan<byte> destination, ReadOnlySpan<byte> tcp)
        {
            if (tcp.Length < 20)
                return;
            ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
            ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]);
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]);
            uint acknowledgment = BinaryPrimitives.ReadUInt32BigEndian(tcp[8..]);
            int headerLength = (tcp[12] >> 4) * 4;
            ushort flags = (ushort)(tcp[13] & 0x3F);
            ushort window = BinaryPrimitives.ReadUInt16BigEndian(tcp[14..]);
            if (headerLength < 20 || headerLength > tcp.Length)
                return;
            var data = tcp[headerLength..].ToArray();
            var key = (sourcePort, Packets.ToUInt32(destination), destinationPort);

            if (!_tcp.TryGetValue(key, out var flow))
            {
                if ((flags & Packets.TcpSyn) == 0 || (flags & Packets.TcpAck) != 0)
                {
                    if ((flags & Packets.TcpRst) == 0)
                        SendTcpRaw(destination.ToArray(), destinationPort, sourcePort, acknowledgment, sequence + (uint)data.Length + 1, Packets.TcpRst | Packets.TcpAck);
                    return;
                }
                ushort mss = 536;
                for (int i = 20; i + 3 < headerLength;)
                {
                    byte kind = tcp[i];
                    if (kind == 0) break;
                    if (kind == 1) { i++; continue; }
                    if (kind == 2 && tcp[i + 1] == 4) mss = BinaryPrimitives.ReadUInt16BigEndian(tcp[(i + 2)..]);
                    i += Math.Max(2, (int)tcp[i + 1]);
                }
                flow = new TcpFlow(this, key, destination.ToArray(), destinationPort, sourcePort, sequence + 1, window, mss);
                if (_tcp.TryAdd(key, flow))
                    flow.Connect();
                return;
            }
            flow.OnSegment(sequence, acknowledgment, flags, window, data);
        }

        private void SendTcpRaw(byte[] remoteIp, ushort remotePort, ushort consolePort, uint sequence, uint acknowledgment, ushort flags,
                                ReadOnlySpan<byte> data = default, ushort window = 0xFFFF, ushort mss = 0)
        {
            var segment = Packets.Tcp(remoteIp, ClientIp, remotePort, consolePort, sequence, acknowledgment, flags, window, data, mss);
            SendIp(remoteIp, Packets.ProtocolTcp, segment);
        }

        private void Tick()
        {
            if (_disposed)
                return;
            foreach (var flow in _tcp.Values)
                flow.OnTimer();
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            foreach (var (port, flow) in _udp)
                if (flow.LastUsed < cutoff && _udp.TryRemove(port, out _))
                    flow.Dispose();
        }

        /// <summary>One console TCP connection, terminated here and relayed to a host socket.</summary>
        private sealed class TcpFlow
        {
            private const int ReceiveWindow = 0x8000;
            private static readonly TimeSpan RetransmitAfter = TimeSpan.FromMilliseconds(400);

            private readonly NatRouter _router;
            private readonly (ushort, uint, ushort) _key;
            private readonly byte[] _remoteIp;
            private readonly ushort _remotePort, _consolePort;
            private readonly ushort _mss;
            private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            private readonly object _lock = new();
            private readonly List<byte> _outgoing = new(); // unacknowledged + unsent bytes, starting at _una
            private readonly SemaphoreSlim _writeOrder = new(1, 1);

            private uint _consoleNext;    // next sequence number expected from the console
            private uint _una;            // our oldest unacknowledged sequence number
            private int _inFlight;        // bytes of _outgoing already sent
            private ushort _consoleWindow;
            private DateTime _lastSend;
            private bool _synSent, _established, _hostClosed, _finSent, _finAcked, _consoleFin, _closed;

            public TcpFlow(NatRouter router, (ushort, uint, ushort) key, byte[] remoteIp, ushort remotePort, ushort consolePort,
                           uint consoleNext, ushort window, ushort mss)
            {
                _router = router;
                _key = key;
                _remoteIp = remoteIp;
                _remotePort = remotePort;
                _consolePort = consolePort;
                _consoleNext = consoleNext;
                _consoleWindow = window;
                _mss = (ushort)Math.Clamp((int)mss, 256, 1400);
                _una = (uint)Random.Shared.Next();
            }

            public void Connect()
            {
                var remote = new IPEndPoint(new IPAddress(_remoteIp), _remotePort);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await _socket.ConnectAsync(remote, timeout.Token);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        _router.Log?.Invoke($"TCP {remote}: {ex.Message}");
                        _router.SendTcpRaw(_remoteIp, _remotePort, _consolePort, 0, _consoleNext, Packets.TcpRst | Packets.TcpAck);
                        Close();
                        return;
                    }
                    lock (_lock)
                    {
                        // SYN-ACK: our SYN takes one sequence number.
                        Send(_una, Packets.TcpSyn | Packets.TcpAck, ReadOnlySpan<byte>.Empty, _mss);
                        _una++;
                        _synSent = true;
                    }
                    _ = ReadHost();
                });
            }

            private void Send(uint sequence, ushort flags, ReadOnlySpan<byte> data, ushort mss = 0)
            {
                _router.SendTcpRaw(_remoteIp, _remotePort, _consolePort, sequence, _consoleNext, flags, data, ReceiveWindow, mss);
                _lastSend = DateTime.UtcNow;
            }

            public void OnSegment(uint sequence, uint acknowledgment, ushort flags, ushort window, byte[] data)
            {
                lock (_lock)
                {
                    if (_closed)
                        return;
                    if ((flags & Packets.TcpRst) != 0)
                    {
                        Close();
                        return;
                    }
                    if ((flags & Packets.TcpSyn) != 0)
                    {
                        if (_synSent && !_established)
                            Send(_una - 1, Packets.TcpSyn | Packets.TcpAck, ReadOnlySpan<byte>.Empty, _mss); // our SYN-ACK was lost
                        return;
                    }
                    _consoleWindow = window;
                    if ((flags & Packets.TcpAck) != 0)
                    {
                        _established = true;
                        int acked = (int)(acknowledgment - _una);
                        int sentTotal = _inFlight + (_finSent ? 1 : 0);
                        if (acked > 0 && acked <= sentTotal)
                        {
                            int dataAcked = Math.Min(acked, _inFlight);
                            _outgoing.RemoveRange(0, dataAcked);
                            _inFlight -= dataAcked;
                            _una += (uint)dataAcked;
                            if (_finSent && acked > dataAcked)
                                _finAcked = true;
                        }
                    }

                    bool fin = (flags & Packets.TcpFin) != 0;
                    if (data.Length > 0 || fin)
                    {
                        if (sequence == _consoleNext)
                        {
                            _consoleNext += (uint)data.Length;
                            if (data.Length > 0)
                                WriteHost(data);
                            if (fin && !_consoleFin)
                            {
                                _consoleFin = true;
                                _consoleNext++;
                                try { _socket.Shutdown(SocketShutdown.Send); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                            }
                        }
                        Send(_una + (uint)_inFlight + (_finSent ? 1u : 0u), Packets.TcpAck, ReadOnlySpan<byte>.Empty); // (duplicate) ACK
                    }
                    Pump();
                    if (_consoleFin && _finAcked)
                        Close();
                }
            }

            private void WriteHost(byte[] data)
            {
                _ = Task.Run(async () =>
                {
                    await _writeOrder.WaitAsync();
                    try { await _socket.SendAsync(data, SocketFlags.None); }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                    finally { _writeOrder.Release(); }
                });
            }

            private async Task ReadHost()
            {
                var buffer = new byte[8192];
                while (true)
                {
                    int read;
                    try { read = await _socket.ReceiveAsync(buffer, SocketFlags.None); }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { read = 0; }
                    lock (_lock)
                    {
                        if (_closed)
                            return;
                        if (read == 0)
                        {
                            _hostClosed = true;
                            Pump();
                            return;
                        }
                        _outgoing.AddRange(buffer.AsSpan(0, read).ToArray());
                        Pump();
                    }
                    // Back-pressure: don't read far ahead of what the console has taken.
                    while (true)
                    {
                        lock (_lock)
                            if (_closed || _outgoing.Count < 256 * 1024) break;
                        await Task.Delay(10);
                    }
                }
            }

            /// <summary>Send what the console's window allows; FIN once the host side is done and everything is out.</summary>
            private void Pump()
            {
                if (!_synSent)
                    return;
                int window = Math.Max(_consoleWindow, (ushort)1);
                while (_inFlight < _outgoing.Count && _inFlight < window)
                {
                    int count = Math.Min(Math.Min(_mss, _outgoing.Count - _inFlight), window - _inFlight);
                    var chunk = _outgoing.GetRange(_inFlight, count).ToArray();
                    Send(_una + (uint)_inFlight, Packets.TcpAck | Packets.TcpPsh, chunk);
                    _inFlight += count;
                }
                if (_hostClosed && !_finSent && _inFlight == _outgoing.Count)
                {
                    Send(_una + (uint)_inFlight, Packets.TcpFin | Packets.TcpAck, ReadOnlySpan<byte>.Empty);
                    _finSent = true;
                }
            }

            public void OnTimer()
            {
                lock (_lock)
                {
                    if (_closed || DateTime.UtcNow - _lastSend < RetransmitAfter)
                        return;
                    if (_synSent && !_established)
                    {
                        Send(_una - 1, Packets.TcpSyn | Packets.TcpAck, ReadOnlySpan<byte>.Empty, _mss);
                    }
                    else if (_inFlight > 0 || (_finSent && !_finAcked))
                    {
                        // Go-back-N: resend everything unacknowledged (and the FIN after it).
                        _inFlight = 0;
                        _finSent = false;
                        Pump();
                    }
                }
            }

            private void Close()
            {
                lock (_lock)
                {
                    if (_closed)
                        return;
                    _closed = true;
                }
                _router._tcp.TryRemove(_key, out _);
                try { _socket.Dispose(); } catch (ObjectDisposedException) { }
            }

            public void Dispose() => Close();
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
            foreach (var flow in _udp.Values) flow.Dispose();
            foreach (var flow in _tcp.Values) flow.Dispose();
            _udp.Clear();
            _tcp.Clear();
        }
    }
}
