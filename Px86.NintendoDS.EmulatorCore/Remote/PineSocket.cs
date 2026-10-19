using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Px86.NintendoDS.Common.Disassembler;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Hardware;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.EmulatorCore.Remote
{
    /// <summary>
    /// PINE IPC server: lets debuggers, cheat tools and CodeDesigner read and
    /// write memory and control execution over TCP, using the same wire
    /// protocol as PCSX2's PINE and CodeDesigner's <c>InternalPineSocket</c>.
    ///
    /// Wire format (little-endian)
    ///   Request:  [total_size : u32][opcode : u8][args…] ([opcode][args…] may repeat)
    ///   Reply:    [total_size : u32][status : u8][data…]
    ///   status = 00h (IPC_OK) or FFh (IPC_FAIL). Commands packed into one
    ///   request run in order, atomically (the emulator doesn't move in
    ///   between), and their reply data is concatenated. If any of them fails
    ///   the reply is just [5][FFh].
    ///
    /// Target CPU
    ///   Memory, register, PC, step and breakpoint opcodes act on the
    ///   connection's selected CPU: the ARM9 until <see cref="PineOpcodes.SelectCpu"/>
    ///   picks the ARM7. Opcodes that carry their own CPU byte (watchpoints,
    ///   disassembly, GetRegister) use 0 = ARM9, 1 = ARM7.
    ///
    /// The socket listens on 127.0.0.1 only. Opcode layouts: docs/pine-protocol.md.
    /// Reference: https://github.com/GovanifY/pine
    /// </summary>
    public sealed class PineSocket : IDisposable
    {
        public const int DefaultPort = 28013;
        public const string VersionString = "Px86.NintendoDS PINE 1.0";

        /// <summary>Largest request accepted; bigger ones close the connection.</summary>
        public const int MaxRequestBytes = 16 * 1024 * 1024;

        /// <summary>Largest ReadBuffer reply.</summary>
        public const int MaxReadBufferBytes = 16 * 1024 * 1024;

        private readonly EmulatorHost _host;
        private readonly List<TcpClient> _clients = new();
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        public PineSocket(EmulatorHost host, int port = DefaultPort)
        {
            _host = host;
            Port = port;
        }

        /// <summary>The TCP port (after <see cref="Start"/>, the actual one if 0 was asked for).</summary>
        public int Port { get; private set; }

        /// <summary>Answers GSStreamGetPort/GetStatus. Null means the GS stream is off.</summary>
        public GsStreamSocket? GsStream { get; set; }

        /// <summary>Where SaveScreenshot writes its PNGs.</summary>
        public string ScreenshotDirectory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Px86.NintendoDS");

        /// <summary>Builds the options for the LoadRom opcode (BIOS paths and so on). Default: just the ROM.</summary>
        public Func<string, GameLoadOptions> LoadOptions { get; set; } = path => new GameLoadOptions { RomPath = path };

        /// <summary>
        /// Answers GetSettings with the frontend's settings as JSON. The core has no
        /// settings of its own, so null (no frontend) makes the opcode fail.
        /// </summary>
        public Func<string>? GetSettings { get; set; }

        /// <summary>
        /// Applies a partial settings JSON for SetSettings and returns the JSON
        /// report of what changed. Null makes the opcode fail.
        /// </summary>
        public Func<string, string>? SetSettings { get; set; }

        /// <summary>A client sent the Shutdown opcode.</summary>
        public event Action? ShutdownRequested;

        /// <summary>Connection and error messages.</summary>
        public event Action<string>? Log;

        public int ClientCount
        {
            get { lock (_clients) return _clients.Count; }
        }

        // ── Lifetime ──────────────────────────────────────────────────────────

        public void Start()
        {
            if (_listener != null)
                throw new InvalidOperationException("The PINE socket is already started.");
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { Name = "Px86 PINE accept", IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch (SocketException) { }
            _listener = null;
            lock (_clients)
            {
                foreach (var c in _clients)
                    c.Close();
                _clients.Clear();
            }
        }

        public void Dispose() => Stop();

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                client.NoDelay = true;
                lock (_clients)
                    _clients.Add(client);
                Log?.Invoke($"[PINE] client connected ({ClientCount} total)");
                new Thread(() => ServeClient(client)) { Name = "Px86 PINE client", IsBackground = true }.Start();
            }
        }

        private void ServeClient(TcpClient client)
        {
            var connection = new Connection();
            try
            {
                using var stream = client.GetStream();
                var sizeBuffer = new byte[4];
                while (_running)
                {
                    if (!ReadExact(stream, sizeBuffer, 4))
                        break;
                    int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(sizeBuffer);
                    if (size < 5 || size > MaxRequestBytes)
                        break;
                    var packet = new byte[size];
                    sizeBuffer.CopyTo(packet, 0);
                    if (!ReadExact(stream, packet.AsSpan(4)))
                        break;

                    byte[] reply = Process(packet, connection);
                    stream.Write(reply);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
            }
            finally
            {
                lock (_clients)
                    _clients.Remove(client);
                client.Close();
                Log?.Invoke($"[PINE] client disconnected ({ClientCount} left)");
            }
        }

        private static bool ReadExact(Stream stream, Span<byte> buffer, int count = -1)
        {
            if (count >= 0)
                buffer = buffer[..count];
            while (buffer.Length > 0)
            {
                int n = stream.Read(buffer);
                if (n == 0)
                    return false;
                buffer = buffer[n..];
            }
            return true;
        }

        // ── Dispatch ──────────────────────────────────────────────────────────

        /// <summary>Per-connection state.</summary>
        public sealed class Connection
        {
            public DebugCpu Cpu { get; set; } = DebugCpu.Arm9;
        }

        /// <summary>Process one request packet (size header included) and build the reply. Public for tests and in-process use.</summary>
        public byte[] Process(byte[] packet, Connection connection)
        {
            var reply = new Reply();
            bool ok = _host.Invoke(() =>
            {
                var request = new Request(packet, 4);
                while (!request.AtEnd)
                {
                    byte opcode = request.U8();
                    try
                    {
                        if (!Execute(opcode, ref request, reply, connection))
                            return false;
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException
                                                   or IOException or UnauthorizedAccessException or IndexOutOfRangeException)
                    {
                        Log?.Invoke($"[PINE] opcode {opcode:X2} failed: {ex.Message}");
                        return false;
                    }
                }
                return true;
            });
            return ok ? reply.ToArray(PineOpcodes.IpcOk) : new Reply().ToArray(PineOpcodes.IpcFail);
        }

        private bool Execute(byte opcode, ref Request rq, Reply rp, Connection conn)
        {
            var nds = _host.Emulator;
            var cpu = conn.Cpu;
            switch (opcode)
            {
                // ── Memory ────────────────────────────────────────────────────
                case PineOpcodes.Read8:
                {
                    uint a = rq.U32();
                    if (nds == null) return false;
                    rp.U8(DebugMemory.Read8(nds, cpu, a));
                    return true;
                }
                case PineOpcodes.Read16:
                {
                    uint a = rq.U32();
                    if (nds == null) return false;
                    rp.U16(DebugMemory.Read16(nds, cpu, a));
                    return true;
                }
                case PineOpcodes.Read32:
                {
                    uint a = rq.U32();
                    if (nds == null) return false;
                    rp.U32(DebugMemory.Read32(nds, cpu, a));
                    return true;
                }
                case PineOpcodes.Read64:
                {
                    uint a = rq.U32();
                    if (nds == null) return false;
                    rp.U32(DebugMemory.Read32(nds, cpu, a));
                    rp.U32(DebugMemory.Read32(nds, cpu, a + 4));
                    return true;
                }
                case PineOpcodes.Write8:
                {
                    uint a = rq.U32();
                    byte v = rq.U8();
                    if (nds == null) return false;
                    DebugMemory.Write8(nds, cpu, a, v);
                    return true;
                }
                case PineOpcodes.Write16:
                {
                    uint a = rq.U32();
                    ushort v = rq.U16();
                    if (nds == null) return false;
                    DebugMemory.Write16(nds, cpu, a, v);
                    return true;
                }
                case PineOpcodes.Write32:
                {
                    uint a = rq.U32();
                    uint v = rq.U32();
                    if (nds == null) return false;
                    DebugMemory.Write32(nds, cpu, a, v);
                    return true;
                }
                case PineOpcodes.Write64:
                {
                    uint a = rq.U32();
                    uint lo = rq.U32();
                    uint hi = rq.U32();
                    if (nds == null) return false;
                    DebugMemory.Write32(nds, cpu, a, lo);
                    DebugMemory.Write32(nds, cpu, a + 4, hi);
                    return true;
                }
                case PineOpcodes.ReadBuffer:
                {
                    uint a = rq.U32();
                    uint length = rq.U32();
                    if (nds == null || length > MaxReadBufferBytes) return false;
                    DebugMemory.ReadBlock(nds, cpu, a, rp.Reserve((int)length));
                    return true;
                }
                case PineOpcodes.WriteBuffer:
                {
                    uint a = rq.U32();
                    var data = rq.Bytes((int)rq.U32());
                    if (nds == null) return false;
                    DebugMemory.WriteBlock(nds, cpu, a, data);
                    return true;
                }

                // ── Game info ─────────────────────────────────────────────────
                case PineOpcodes.Version:
                    rp.String(VersionString);
                    return true;
                case PineOpcodes.Title:
                    rp.String(nds?.Rom?.Header.Title ?? "");
                    return true;
                case PineOpcodes.Id:
                    rp.String(nds?.Rom?.Header.GameCode ?? "");
                    return true;
                case PineOpcodes.Uuid:
                    rp.String(nds?.Rom is { } rom ? $"{rom.Header.GameCode}-{rom.Header.HeaderCrc:X4}-{rom.Header.SecureAreaCrc:X4}" : "");
                    return true;
                case PineOpcodes.GameVersion:
                    rp.String(nds?.Rom is { } r ? $"{r.Header.RomVersion}" : "");
                    return true;
                case PineOpcodes.Status:
                    rp.U32((uint)_host.Status);
                    return true;

                // ── Save states ───────────────────────────────────────────────
                case PineOpcodes.SaveState:
                {
                    byte slot = rq.U8();
                    return slot < GameSession.StateSlots && _host.SaveState(slot);
                }
                case PineOpcodes.LoadState:
                {
                    byte slot = rq.U8();
                    return slot < GameSession.StateSlots && _host.LoadState(slot);
                }
                case PineOpcodes.SaveStateFile:
                    return _host.SaveStateFile(rq.Utf8(rq.U16()));
                case PineOpcodes.LoadStateFile:
                    return _host.LoadStateFile(rq.Utf8(rq.U16()));

                // ── Execution control ─────────────────────────────────────────
                case PineOpcodes.GetProgramCounter:
                    if (nds == null) return false;
                    rp.U32(nds.Cpu(cpu).Pc);
                    rp.U8(_host.IsPaused ? (byte)1 : (byte)0);
                    return true;
                case PineOpcodes.Pause:
                    _host.Pause();
                    return true;
                case PineOpcodes.Resume:
                    _host.Resume();
                    return true;
                case PineOpcodes.Step:
                {
                    uint? pc = _host.Step(cpu);
                    if (pc == null) return false;
                    rp.U32(pc.Value);
                    return true;
                }
                case PineOpcodes.StepInto:
                    return _host.Step(cpu) != null;
                case PineOpcodes.StepOver:
                    return _host.StepOver(cpu);
                case PineOpcodes.StepOut:
                    return _host.StepOut(cpu);
                case PineOpcodes.Reset:
                    return _host.Reset();
                case PineOpcodes.FrameAdvance:
                {
                    byte frames = rq.U8();
                    if (frames == 0 || nds == null) return false;
                    _host.FrameAdvance(frames);
                    return true;
                }
                case PineOpcodes.GetFps:
                    rp.F32((float)_host.Fps);
                    return true;
                case PineOpcodes.SetLimiterMode:
                {
                    byte mode = rq.U8();
                    if (mode > 3) return false;
                    _host.Limiter = (LimiterMode)mode;
                    return true;
                }

                // ── Breakpoints ───────────────────────────────────────────────
                case PineOpcodes.SetBreakpoint:
                    _host.Debugger.AddBreakpoint(cpu, rq.U32());
                    return true;
                case PineOpcodes.ClearBreakpoint:
                    return _host.Debugger.RemoveBreakpoint(cpu, rq.U32());
                case PineOpcodes.ClearAllBreakpoints:
                    _host.Debugger.ClearBreakpoints(DebugCpu.Arm9);
                    _host.Debugger.ClearBreakpoints(DebugCpu.Arm7);
                    return true;
                case PineOpcodes.ListBreakpoints:
                {
                    byte filter = rq.U8();
                    var list = _host.Debugger.Breakpoints.Where(b => filter == 0xFF || (byte)b.Cpu == filter).ToList();
                    rp.U32((uint)list.Count);
                    foreach (var (bpCpu, address) in list)
                    {
                        rp.U32(address);
                        rp.U8(1);
                        rp.U8((byte)bpCpu);
                    }
                    return true;
                }
                case PineOpcodes.GetBreakInfo:
                {
                    var b = _host.LastBreak;
                    rp.U8(b == null ? (byte)0 : (byte)1);
                    rp.U8((byte)(b?.Cpu ?? 0));
                    rp.U8((byte)(b?.Reason ?? 0));
                    rp.U32(b?.Pc ?? 0);
                    rp.U32(b?.Address ?? 0);
                    rp.U8(b?.IsWrite == true ? (byte)1 : (byte)0);
                    return true;
                }

                // ── Watchpoints ───────────────────────────────────────────────
                case PineOpcodes.AddWatch:
                {
                    var watchCpu = ParseCpu(rq.U8());
                    uint start = rq.U32();
                    uint end = rq.U32();
                    byte cond = rq.U8();
                    if (watchCpu == null || end <= start || (cond & 3) == 0) return false;
                    _host.Debugger.AddWatch(new Watchpoint(watchCpu.Value, start, end, (WatchCondition)(cond & 3)));
                    return true;
                }
                case PineOpcodes.RemoveWatch:
                {
                    var watchCpu = ParseCpu(rq.U8());
                    uint start = rq.U32();
                    uint end = rq.U32();
                    return watchCpu != null && _host.Debugger.RemoveWatch(watchCpu.Value, start, end);
                }
                case PineOpcodes.ListWatches:
                {
                    byte filter = rq.U8();
                    var list = _host.Debugger.Watches.Where(w => filter == 0xFF || (byte)w.Cpu == filter).ToList();
                    rp.U32((uint)list.Count);
                    foreach (var w in list)
                    {
                        rp.U32(w.Start);
                        rp.U32(w.End);
                        rp.U8((byte)w.Condition);
                        rp.U8(1);                                   // action: break
                        rp.U8(w.Cpu == DebugCpu.Arm9 ? (byte)1 : (byte)2); // PCSX2's raw CPU numbering
                    }
                    return true;
                }
                case PineOpcodes.ClearAllWatches:
                    _host.Debugger.ClearWatches();
                    return true;

                // ── Registers ─────────────────────────────────────────────────
                case PineOpcodes.GetRegisters:
                {
                    if (nds == null) return false;
                    var core = nds.Cpu(cpu);
                    for (int i = 0; i < 15; i++)
                        rp.U32(core.R[i]);
                    rp.U32(core.Pc);
                    rp.U32(core.Cpsr);
                    rp.U32(core.Spsr);
                    return true;
                }
                case PineOpcodes.GetRegister:
                {
                    var regCpu = ParseCpu(rq.U8());
                    byte category = rq.U8();
                    byte number = rq.U8();
                    if (nds == null || regCpu == null) return false;
                    uint? value = PineRegisters.Read(nds, regCpu.Value, category, number);
                    if (value == null) return false;
                    rp.U32(value.Value);
                    rp.U32(0);
                    rp.U32(0);
                    rp.U32(0);
                    return true;
                }
                case PineOpcodes.SetRegister:
                {
                    var regCpu = ParseCpu(rq.U8());
                    byte category = rq.U8();
                    byte number = rq.U8();
                    uint value = rq.U32();
                    rq.Bytes(12);
                    if (nds == null || regCpu == null) return false;
                    return PineRegisters.Write(nds, regCpu.Value, category, number, value);
                }
                case PineOpcodes.SelectCpu:
                {
                    var selected = ParseCpu(rq.U8());
                    if (selected == null) return false;
                    conn.Cpu = selected.Value;
                    return true;
                }

                // ── Disassembly ───────────────────────────────────────────────
                case PineOpcodes.Disassemble:
                {
                    byte selector = rq.U8();
                    uint address = rq.U32();
                    ushort count = rq.U16();
                    var disCpu = ParseCpu((byte)(selector & 0x0F));
                    if (nds == null || disCpu == null || count > 4096) return false;
                    bool thumb = (selector & 0x20) != 0 || ((selector & 0x10) == 0 && nds.Cpu(disCpu.Value).IsThumb);
                    rp.U16(count);
                    var bus = DebugMemory.Bus(nds, disCpu.Value);
                    for (int i = 0; i < count; i++)
                    {
                        string text;
                        if (thumb)
                        {
                            address &= ~1u;
                            text = ThumbDisassembler.Disassemble(bus.Fetch16(address), address, bus.Fetch16(address + 2));
                        }
                        else
                        {
                            address &= ~3u;
                            text = ArmDisassembler.Disassemble(bus.Fetch32(address), address);
                        }
                        var bytes = Encoding.UTF8.GetBytes(text);
                        if (bytes.Length > 255) bytes = bytes[..255];
                        rp.U32(address);
                        rp.U8((byte)bytes.Length);
                        rp.Raw(bytes);
                        address += thumb ? 2u : 4u;
                    }
                    return true;
                }

                // ── Screenshots / video ───────────────────────────────────────
                case PineOpcodes.SaveScreenshot:
                {
                    if (nds == null) return false;
                    Directory.CreateDirectory(ScreenshotDirectory);
                    string name = $"{nds.Rom?.Header.GameCode ?? "NDS"}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
                    ScreenCapture.SavePng(nds, Path.Combine(ScreenshotDirectory, name));
                    return true;
                }
                case PineOpcodes.GsStreamGetPort:
                    rp.U16((ushort)(GsStream?.IsListening == true ? GsStream.Port : 0));
                    return true;
                case PineOpcodes.GsStreamGetStatus:
                {
                    var gs = GsStream;
                    rp.U8(gs?.IsListening == true ? (byte)1 : (byte)0);
                    rp.U16((ushort)(gs?.IsListening == true ? gs.Port : 0));
                    rp.U32((uint)(gs?.SubscriberCount ?? 0));
                    rp.U32((uint)(gs?.FramesDelivered ?? 0));
                    rp.U32((uint)(gs?.FramesDropped ?? 0));
                    return true;
                }
                case PineOpcodes.GsStreamPing:
                    return true;

                // ── Input ─────────────────────────────────────────────────────
                case PineOpcodes.PadGetState:
                {
                    byte pad = rq.U8();
                    if (pad != 0) return false;
                    var input = _host.RemoteInput;
                    rp.U32((uint)input.Buttons);
                    rp.U8(0x80); rp.U8(0x80); rp.U8(0x80); rp.U8(0x80);
                    for (int i = 0; i < 16; i++)
                        rp.U8(((int)input.Buttons & (1 << i)) != 0 ? (byte)0xFF : (byte)0);
                    return true;
                }
                case PineOpcodes.PadSetButton:
                {
                    byte pad = rq.U8();
                    byte index = rq.U8();
                    float value = rq.F32();
                    if (pad != 0 || index > 12) return false;
                    var input = _host.RemoteInput;
                    var bit = (NdsButton)(1 << index);
                    _host.RemoteInput = input with { Buttons = value >= 0.5f ? input.Buttons | bit : input.Buttons & ~bit };
                    return true;
                }
                case PineOpcodes.PadSetAnalog:
                {
                    byte pad = rq.U8();
                    rq.Bytes(4); // the DS has no sticks
                    return pad == 0;
                }
                case PineOpcodes.PadSetState:
                {
                    byte pad = rq.U8();
                    uint buttons = rq.U32();
                    rq.Bytes(4 + 16);
                    if (pad != 0) return false;
                    _host.RemoteInput = _host.RemoteInput with { Buttons = (NdsButton)(buttons & 0x1FFF) };
                    return true;
                }
                case PineOpcodes.PadGetType:
                    rp.U8(rq.U8() == 0 ? (byte)1 : (byte)0);
                    return true;
                case PineOpcodes.SetTouch:
                {
                    bool down = rq.U8() != 0;
                    ushort x = rq.U16();
                    ushort y = rq.U16();
                    if (down && (x > 255 || y > 191)) return false;
                    _host.RemoteInput = _host.RemoteInput with { Touch = down ? (x, y) : null };
                    return true;
                }

                // ── DS host control ───────────────────────────────────────────
                case PineOpcodes.GetFrameCount:
                    rp.U64((ulong)(nds?.FrameCount ?? 0));
                    return true;
                case PineOpcodes.LoadRom:
                {
                    string path = rq.Utf8(rq.U16());
                    _host.LoadGame(LoadOptions(path));
                    return true;
                }
                // ── Cheats ────────────────────────────────────────────────────
                case PineOpcodes.CheatList:
                {
                    var cheats = _host.Cheats.Cheats;
                    rp.U32((uint)cheats.Count);
                    foreach (var c in cheats)
                    {
                        var name = Encoding.UTF8.GetBytes(c.Name);
                        if (name.Length > 255) name = name[..255];
                        rp.U8(c.Enabled ? (byte)1 : (byte)0);
                        rp.U8((byte)c.Type);
                        rp.U8((byte)name.Length);
                        rp.Raw(name);
                    }
                    return true;
                }
                case PineOpcodes.CheatAdd:
                {
                    byte type = rq.U8();
                    bool enabled = rq.U8() != 0;
                    string name = rq.Utf8(rq.U16());
                    string code = rq.Utf8(rq.U16());
                    if (type > 1) return false;
                    var cheat = new EmulatorCore.Cheats.Cheat { Name = name, Code = code, Type = (EmulatorCore.Cheats.CheatType)type, Enabled = enabled };
                    if (cheat.Validate() is { } error)
                    {
                        Log?.Invoke($"[PINE] cheat '{name}' rejected: {error}");
                        return false;
                    }
                    int index = -1;
                    if (!_host.EditCheats(list => { list.Cheats.Add(cheat); index = list.Cheats.Count - 1; }))
                        return false;
                    rp.U32((uint)index);
                    return true;
                }
                case PineOpcodes.CheatEnable:
                {
                    uint index = rq.U32();
                    bool enabled = rq.U8() != 0;
                    if (index >= _host.Cheats.Cheats.Count) return false;
                    return _host.EditCheats(list => list.Cheats[(int)index].Enabled = enabled);
                }
                case PineOpcodes.CheatRemove:
                {
                    uint index = rq.U32();
                    if (index >= _host.Cheats.Cheats.Count) return false;
                    return _host.EditCheats(list => list.Cheats.RemoveAt((int)index));
                }
                case PineOpcodes.CheatClear:
                    return _host.EditCheats(list => list.Cheats.Clear());

                case PineOpcodes.CaptureGx:
                {
                    string path = rq.Utf8(rq.U16());
                    return _host.CaptureGx(path, (file, error) =>
                        Log?.Invoke(error == null ? $"[PINE] GX capture written to {file}" : $"[PINE] GX capture failed: {error.Message}"));
                }
                // ── Code analysis (ExecutionTracker, StringScanner) ──
                case PineOpcodes.TrackerControl:
                {
                    byte action = rq.U8();
                    if (nds == null || action > 3) return false;
                    if (action == 2) nds.Tracker.Clear();
                    else if (action != 3) nds.Tracker.Enabled = action == 1;
                    rp.U8(nds.Tracker.Enabled ? (byte)1 : (byte)0);
                    return true;
                }
                case PineOpcodes.GetCoverage:
                {
                    uint start = rq.U32(), length = rq.U32();
                    if (nds == null || length > 0x400000) return false;
                    var bits = nds.Tracker.Coverage(cpu, start, (int)length);
                    rp.U32((uint)bits.Length);
                    rp.Raw(bits);
                    return true;
                }
                case PineOpcodes.GetCallStack:
                {
                    if (nds == null) return false;
                    var frames = nds.Tracker.CallStack(cpu);
                    rp.U32((uint)frames.Count);
                    foreach (var f in frames)
                    {
                        rp.U32(f.CallSite);
                        rp.U32(f.Target);
                        rp.U32(f.ReturnAddress);
                        rp.U32(f.StackPointer);
                        rp.U8((byte)((f.Thumb ? 1 : 0) | (f.Interrupt ? 2 : 0)));
                    }
                    return true;
                }
                case PineOpcodes.GetCallCounts:
                {
                    int max = rq.U16();
                    if (nds == null) return false;
                    var counts = nds.Tracker.CallCounts(cpu, max == 0 ? 100 : max);
                    rp.U32((uint)counts.Count);
                    foreach (var (target, calls) in counts)
                    {
                        rp.U32(target);
                        rp.U64((ulong)calls);
                    }
                    return true;
                }
                case PineOpcodes.ScanStrings:
                {
                    uint start = rq.U32(), length = rq.U32();
                    int min = rq.U8();
                    if (nds == null || length is 0 or > 0x1000000) return false;
                    var data = new byte[length];
                    if (start == 0x02000000 && length <= NdsEmulator.MainRamSize)
                        nds.MainRam.AsSpan(0, (int)length).CopyTo(data);
                    else
                        DebugMemory.ReadBlock(nds, cpu, start, data);
                    var found = StringScanner.Scan(data, start, Math.Max(min, 2));
                    int count = Math.Min(found.Count, 20000);
                    rp.U32((uint)count);
                    foreach (var s in found.Take(count))
                    {
                        var text = System.Text.Encoding.UTF8.GetBytes(s.Text.Length > 255 ? s.Text[..255] : s.Text);
                        rp.U32(s.Address);
                        rp.U16((ushort)s.Length);
                        rp.U8((byte)text.Length);
                        rp.Raw(text);
                    }
                    return true;
                }
                case PineOpcodes.HookList:
                {
                    if (nds == null) return false;
                    var hooks = nds.Hooks.List();
                    rp.U32((uint)hooks.Count);
                    foreach (var hook in hooks)
                    {
                        rp.U8((byte)hook.Cpu);
                        rp.U32(hook.Address);
                        rp.U8((byte)hook.Kind);
                        rp.U8(hook.Automatic ? (byte)1 : (byte)0);
                    }
                    return true;
                }
                case PineOpcodes.HookAdd:
                {
                    byte hookCpu = rq.U8();
                    uint address = rq.U32();
                    byte kind = rq.U8();
                    if (nds == null || hookCpu > 1 || !Enum.IsDefined((EmulatorCore.Debugging.HookKind)kind)) return false;
                    nds.Hooks.Add((DebugCpu)hookCpu, address, (EmulatorCore.Debugging.HookKind)kind);
                    return true;
                }
                case PineOpcodes.HookRemove:
                {
                    byte hookCpu = rq.U8();
                    uint address = rq.U32();
                    return nds != null && hookCpu <= 1 && nds.Hooks.Remove((DebugCpu)hookCpu, address);
                }
                case PineOpcodes.Shutdown:
                    ShutdownRequested?.Invoke();
                    return true;
                case PineOpcodes.GetSettings:
                    if (GetSettings == null)
                        return false;
                    rp.String(GetSettings());
                    return true;
                case PineOpcodes.SetSettings:
                {
                    string json = rq.Utf8(rq.U16());
                    if (SetSettings == null)
                        return false;
                    rp.String(SetSettings(json));
                    return true;
                }

                default:
                    // Threads, modules, symbols, locals, network: nothing to report on a DS.
                    return false;
            }
        }

        private static DebugCpu? ParseCpu(byte value) => value switch
        {
            0 => DebugCpu.Arm9,
            1 => DebugCpu.Arm7,
            _ => null,
        };

        // ── Request / reply buffers ───────────────────────────────────────────

        private ref struct Request
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _pos;

            public Request(byte[] data, int start)
            {
                _data = data;
                _pos = start;
            }

            public bool AtEnd => _pos >= _data.Length;

            private ReadOnlySpan<byte> Take(int count)
            {
                if (count < 0 || _pos + count > _data.Length)
                    throw new InvalidDataException("PINE request is truncated.");
                var span = _data.Slice(_pos, count);
                _pos += count;
                return span;
            }

            public byte U8() => Take(1)[0];
            public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
            public ReadOnlySpan<byte> Bytes(int count) => Take(count);
            public string Utf8(int count) => Encoding.UTF8.GetString(Take(count));
        }

        private sealed class Reply
        {
            private byte[] _buffer = new byte[256];
            private int _length = 5;

            private Span<byte> Grow(int count)
            {
                if (_length + count > _buffer.Length)
                    Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
                var span = _buffer.AsSpan(_length, count);
                _length += count;
                return span;
            }

            public Span<byte> Reserve(int count) => Grow(count);
            public void U8(byte v) => Grow(1)[0] = v;
            public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), v);
            public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), v);
            public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), v);
            public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Grow(4), v);
            public void Raw(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Grow(bytes.Length));

            /// <summary>PINE string: u32 length including the NUL terminator, the UTF-8 bytes, then NUL.</summary>
            public void String(string s)
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                U32((uint)bytes.Length + 1);
                Raw(bytes);
                U8(0);
            }

            public byte[] ToArray(byte status)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_buffer, (uint)_length);
                _buffer[4] = status;
                return _buffer[.._length];
            }
        }
    }
}
