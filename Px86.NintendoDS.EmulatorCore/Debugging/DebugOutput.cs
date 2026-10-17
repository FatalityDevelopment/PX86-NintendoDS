using System.Text;
using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.Common.Cpu;

namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>One line of game debug output.</summary>
    public readonly record struct DebugLine(long Index, long Frame, DebugCpu Cpu, string Text);

    /// <summary>
    /// Debug text that programs print through the emulator debug channels
    /// (homebrew, development builds, test programs). Both CPUs have them.
    ///
    ///   04FFFA00h–A0Fh  R  Emulator ID, 16 bytes ("no$gba Px86" padded with spaces; libraries check the "no$gba" prefix)
    ///   04FFFA10h       W  String out: the 32-bit value points to a zero-terminated string
    ///   04FFFA14h       W  String out with %param% expansion
    ///   04FFFA18h       W  String out with %param% expansion, then a line feed
    ///   04FFFA1Ch       W  Char out (low byte, any access width)
    ///   04FFFA20h–A27h  R  Clock cycles of this CPU (64-bit)
    ///   04FFF000h       W  Ensata char out (ARM9, 8-bit). Ensata detection is not emulated: games
    ///                      that detect it stop talking to the ARM7.
    ///
    /// In-code messages: an executed "mov r12,r12" (ARM E1A0C00Ch, Thumb 46E4h)
    /// followed by a branch over "dcw 6464h, dcw flags, ascii text" prints the
    /// text, which ends at a zero byte or at the branch target.
    ///
    /// %param%: r0–r15, sp, lr, pc (8-digit hex), scanline, frame, totalclks,
    /// lastclks (cycles since the previous lastclks/zeroclks), zeroclks.
    ///
    /// Text is collected per CPU until a line feed, then becomes a <see cref="DebugLine"/>.
    /// Reading the strings has no side effects (<see cref="DebugMemory"/>), and
    /// nothing here changes emulation.
    ///
    /// Reference: GBATEK "DS Debug Registers (Emulator/Devkits)", "Debug Messages".
    /// </summary>
    public sealed class DebugOutput
    {
        public const uint PortBase = 0xFFFA00;   // offsets relative to 04000000h
        public const uint PortEnd = 0xFFFA28;
        public const uint REG_STRING = 0xFFFA10, REG_STRING_PARAMS = 0xFFFA14, REG_STRING_LINE = 0xFFFA18;
        public const uint REG_CHAR = 0xFFFA1C, REG_CLOCKS = 0xFFFA20, REG_ENSATA_CHAR = 0xFFF000;

        public const int MaxLines = 5000;
        private const int MaxString = 256;
        private const string Id = "no$gba Px86     ";

        private readonly NdsEmulator _nds;
        private readonly object _lock = new();
        private readonly List<DebugLine> _lines = new();
        private readonly StringBuilder[] _pending = { new(), new() };
        private readonly long[] _lastClocks = new long[2];
        private long _nextIndex;

        public DebugOutput(NdsEmulator nds) => _nds = nds;

        /// <summary>A complete line was printed (emulation thread).</summary>
        public event Action<DebugLine>? MessageLogged;

        /// <summary>Lines printed since start (or the last <see cref="Clear"/>), including ones dropped from the window.</summary>
        public long TotalLines { get { lock (_lock) return _nextIndex; } }

        /// <summary>Kept lines with <see cref="DebugLine.Index"/> ≥ <paramref name="fromIndex"/> (any thread).</summary>
        public DebugLine[] Snapshot(long fromIndex = 0)
        {
            lock (_lock)
            {
                int start = _lines.FindIndex(l => l.Index >= fromIndex);
                return start < 0 ? Array.Empty<DebugLine>() : _lines.GetRange(start, _lines.Count - start).ToArray();
            }
        }

        /// <summary>Text printed so far that hasn't ended with a line feed yet.</summary>
        public string PendingText(DebugCpu cpu)
        {
            lock (_lock) return _pending[(int)cpu].ToString();
        }

        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
                _nextIndex = 0;
            }
        }

        /// <summary>Power-on: forget half-written lines and the lastclks marks (the log itself stays).</summary>
        public void Reset()
        {
            lock (_lock)
            {
                foreach (var b in _pending) b.Clear();
                Array.Clear(_lastClocks);
            }
        }

        // ── Ports ─────────────────────────────────────────────────────────────

        /// <summary>True for offsets (from 04000000h) that belong to the debug ports.</summary>
        public static bool IsPort(uint offset) => (offset >= PortBase && offset < PortEnd) || offset == REG_ENSATA_CHAR;

        public uint Read(DebugCpu cpu, uint offset, int bytes)
        {
            uint value = 0;
            for (int i = 0; i < bytes; i++)
                value |= (uint)ReadByte(cpu, offset + (uint)i) << (8 * i);
            return value;
        }

        private byte ReadByte(DebugCpu cpu, uint offset)
        {
            if (offset >= PortBase && offset < PortBase + 16)
                return (byte)Id[(int)(offset - PortBase)];
            if (offset >= REG_CLOCKS && offset < REG_CLOCKS + 8)
                return (byte)(Cpu(cpu).TotalCycles >> (int)(8 * (offset - REG_CLOCKS)));
            return 0;
        }

        /// <summary>A write of <paramref name="bytes"/> bytes to a debug port.</summary>
        public void Write(DebugCpu cpu, uint offset, uint value, int bytes)
        {
            switch (offset)
            {
                case REG_STRING when bytes == 4:
                    Append(cpu, ReadString(cpu, value, uint.MaxValue));
                    break;
                case REG_STRING_PARAMS when bytes == 4:
                    Append(cpu, Expand(cpu, ReadString(cpu, value, uint.MaxValue)));
                    break;
                case REG_STRING_LINE when bytes == 4:
                    Append(cpu, Expand(cpu, ReadString(cpu, value, uint.MaxValue)) + "\n");
                    break;
                case REG_CHAR:
                case REG_ENSATA_CHAR when cpu == DebugCpu.Arm9:
                    Append(cpu, ((char)(value & 0xFF)).ToString());
                    break;
            }
        }

        /// <summary>The CPU executed "mov r12,r12" at its PC: print the message that follows, if there is one.</summary>
        public void OnNoCashMessage(ArmCpu core)
        {
            var cpu = core == _nds.Arm9 ? DebugCpu.Arm9 : DebugCpu.Arm7;
            uint pc = core.Pc;
            uint data, end;
            if (core.IsThumb)
            {
                ushort branch = DebugMemory.Read16(_nds, cpu, pc + 2);
                if ((branch & 0xF800) != 0xE000)
                    return;
                end = (uint)(pc + 2 + 4 + (((int)branch << 21) >> 20));
                data = pc + 4;
            }
            else
            {
                uint branch = DebugMemory.Read32(_nds, cpu, pc + 4);
                if ((branch & 0x0F000000) != 0x0A000000)
                    return;
                end = (uint)(pc + 4 + 8 + (((int)branch << 8) >> 6));
                data = pc + 8;
            }
            if (DebugMemory.Read16(_nds, cpu, data) != 0x6464)
                return;
            string text = ReadString(cpu, data + 4, end);
            lock (_lock)
            {
                if (_pending[(int)cpu].Length > 0)
                    Flush(cpu);
            }
            Append(cpu, Expand(cpu, text) + "\n");
        }

        // ── Text ──────────────────────────────────────────────────────────────

        private ArmCpu Cpu(DebugCpu cpu) => cpu == DebugCpu.Arm9 ? _nds.Arm9 : _nds.Arm7;

        private string ReadString(DebugCpu cpu, uint address, uint end)
        {
            var text = new StringBuilder();
            for (int i = 0; i < MaxString && address + (uint)i < end; i++)
            {
                byte b = DebugMemory.Read8(_nds, cpu, address + (uint)i);
                if (b == 0)
                    break;
                text.Append(b is >= 0x20 and < 0x7F or (byte)'\n' or (byte)'\t' ? (char)b : '?');
            }
            return text.ToString();
        }

        /// <summary>Replace the %param% fields (unknown names stay as they are).</summary>
        public string Expand(DebugCpu cpu, string text)
        {
            if (!text.Contains('%'))
                return text;
            var core = Cpu(cpu);
            var result = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf('%', i);
                int close = open < 0 ? -1 : text.IndexOf('%', open + 1);
                if (close < 0)
                {
                    result.Append(text, i, text.Length - i);
                    break;
                }
                result.Append(text, i, open - i);
                string name = text.Substring(open + 1, close - open - 1);
                string? value = Parameter(cpu, core, name.ToLowerInvariant());
                if (value == null)
                {
                    result.Append('%').Append(name);
                    i = close; // the closing % may open the next field
                    continue;
                }
                result.Append(value);
                i = close + 1;
            }
            return result.ToString();
        }

        private string? Parameter(DebugCpu cpu, ArmCpu core, string name)
        {
            switch (name)
            {
                case "sp": return $"{core.R[13]:X8}";
                case "lr": return $"{core.R[14]:X8}";
                case "pc": case "r15": return $"{core.Pc:X8}";
                case "scanline": return _nds.Lcd.VCount.ToString();
                case "frame": return _nds.FrameCount.ToString();
                case "totalclks": return core.TotalCycles.ToString();
                case "lastclks":
                {
                    long elapsed = core.TotalCycles - _lastClocks[(int)cpu];
                    _lastClocks[(int)cpu] = core.TotalCycles;
                    return elapsed.ToString();
                }
                case "zeroclks":
                    _lastClocks[(int)cpu] = core.TotalCycles;
                    return "";
            }
            if (name.Length is 2 or 3 && name[0] == 'r' && int.TryParse(name.AsSpan(1), out int r) && r is >= 0 and < 15)
                return $"{core.R[r]:X8}";
            return null;
        }

        private void Append(DebugCpu cpu, string text)
        {
            var finished = new List<DebugLine>();
            lock (_lock)
            {
                var pending = _pending[(int)cpu];
                foreach (char c in text)
                {
                    if (c == '\n')
                        finished.Add(Flush(cpu));
                    else if (c != '\r')
                    {
                        pending.Append(c);
                        if (pending.Length >= 1024)
                            finished.Add(Flush(cpu)); // runaway output without line feeds
                    }
                }
            }
            foreach (var line in finished)
                MessageLogged?.Invoke(line);
        }

        /// <summary>Close the pending line (caller holds the lock).</summary>
        private DebugLine Flush(DebugCpu cpu)
        {
            var line = new DebugLine(_nextIndex++, _nds.FrameCount, cpu, _pending[(int)cpu].ToString());
            _pending[(int)cpu].Clear();
            _lines.Add(line);
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);
            return line;
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("DebugOutput");
            lock (_lock)
            {
                for (int i = 0; i < 2; i++)
                {
                    string text = _pending[i].ToString();
                    s.Sync(ref text);
                    _pending[i].Clear().Append(text);
                }
                s.Sync(_lastClocks);
            }
        }
    }
}
