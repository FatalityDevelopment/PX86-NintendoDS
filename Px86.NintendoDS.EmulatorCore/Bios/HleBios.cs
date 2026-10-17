using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.Common.Interfaces;
using Px86.NintendoDS.EmulatorCore.Hardware;
using Px86.NintendoDS.FileFormats.Checksums;

namespace Px86.NintendoDS.EmulatorCore.Bios
{
    /// <summary>
    /// High-level emulation of the DS BIOS SWI functions, used when no BIOS
    /// dumps are provided. Installed as <see cref="ArmCpu.SwiHandler"/>.
    ///
    ///   SWI  ARM9 ARM7  Function
    ///   00h   ✓    ✓    SoftReset (unsupported, logged)
    ///   03h   ✓    ✓    WaitByLoop
    ///   04h   ✓    ✓    IntrWait (flags at [DTCM+3FF8h] / [0380FFF8h])
    ///   05h   ✓    ✓    VBlankIntrWait
    ///   06h   ✓    ✓    Halt
    ///   07h        ✓    Sleep
    ///   08h        ✓    SoundBias
    ///   09h   ✓    ✓    Div            0Bh ✓ ✓ CpuSet      0Ch ✓ ✓ CpuFastSet
    ///   0Dh   ✓    ✓    Sqrt           0Eh ✓ ✓ GetCRC16    0Fh ✓ ✓ IsDebugger
    ///   10h   ✓    ✓    BitUnPack      11h ✓ ✓ LZ77 (8-bit writes)
    ///   12h   ✓    ✓    LZ77 by callback (16-bit writes)
    ///   13h   ✓    ✓    Huffman by callback
    ///   14h   ✓    ✓    RL (8-bit writes)   15h ✓ ✓ RL by callback (16-bit writes)
    ///   16h   ✓         Diff8bitUnFilter    18h ✓   Diff16bitUnFilter
    ///   1Ah/1Bh/1Ch ✓   GetSineTable / GetPitchTable / GetVolumeTable
    ///   1Dh        ✓    GetBootProcs (returns 0)
    ///   1Fh   ✓         CustomPost
    ///
    /// Waiting functions halt the core and rewind PC onto the SWI, so it runs
    /// again (with "discard" cleared) after the IRQ handler returns — the same
    /// observable behaviour as the BIOS loop.
    ///
    /// Callback-based decompression calls back into guest code through
    /// <see cref="CallGuest"/>, which runs the callback on the CPU until it
    /// returns to the trap loop in <see cref="BiosStub"/>.
    ///
    /// Reference: GBATEK "BIOS Functions".
    /// </summary>
    public sealed class HleBios
    {
        private readonly NdsEmulator _nds;
        private readonly bool _isArm9;

        // Set while IntrWait is halted waiting; the re-executed SWI must not discard again.
        private bool _retryingWait;

        public HleBios(NdsEmulator nds, bool isArm9)
        {
            _nds = nds;
            _isArm9 = isArm9;
        }

        public event Action<ArmCpu, uint>? UnsupportedSwi;

        private ArmCpu Cpu => _isArm9 ? _nds.Arm9 : _nds.Arm7;
        private IArmMemoryBus Bus => Cpu.Bus;
        private InterruptController Irq => _isArm9 ? _nds.Irq9 : _nds.Irq7;

        private uint TrapAddress => (_isArm9 ? 0xFFFF0000u : 0x00000000u) + BiosStub.GuestReturnOffset;

        /// <summary>Address of the BIOS IRQ check bits the game's handler ORs into.</summary>
        private uint IrqFlagsAddress => _isArm9 ? _nds.Cp15.DtcmBase + 0x3FF8 : 0x0380FFF8u;

        public bool Handle(ArmCpu cpu, uint number)
        {
            uint[] r = cpu.R;
            switch (number)
            {
                case 0x03: // WaitByLoop
                    return true;

                case 0x04:
                    IntrWait(cpu, r[0] != 0, r[1]);
                    return true;

                case 0x05:
                    IntrWait(cpu, discard: true, mask: 1);
                    return true;

                case 0x06:
                    cpu.Halted = true;
                    return true;

                case 0x07 when !_isArm9:
                    cpu.Halted = true;
                    return true;

                case 0x08 when !_isArm9:
                {
                    uint bias = Bus.Read32(0x04000504);
                    Bus.Write32(0x04000504, (bias & ~0x3FFu) | (r[0] != 0 ? 0x200u : 0u));
                    return true;
                }

                case 0x09:
                    Divide(r);
                    return true;

                case 0x0B:
                    CpuSet(r[0], r[1], r[2]);
                    return true;

                case 0x0C:
                    CpuFastSet(r[0], r[1], r[2]);
                    return true;

                case 0x0D:
                    r[0] = MathUnit.IntegerSquareRoot(r[0]);
                    return true;

                case 0x0E:
                    r[0] = GetCrc16((ushort)r[0], r[1], r[2]);
                    return true;

                case 0x0F:
                    r[0] = 0;
                    return true;

                case 0x10:
                    BitUnPack(r[0], r[1], r[2]);
                    return true;

                case 0x11:
                    Lz77(r[0], r[1], wide: false, callbacks: 0, param: 0);
                    return true;

                case 0x12:
                    Lz77(r[0], r[1], wide: true, callbacks: r[3], param: r[2]);
                    return true;

                case 0x13:
                    Huffman(r[0], r[1], r[3]);
                    return true;

                case 0x14:
                    RunLength(r[0], r[1], wide: false, callbacks: 0, param: 0);
                    return true;

                case 0x15:
                    RunLength(r[0], r[1], wide: true, callbacks: r[3], param: r[2]);
                    return true;

                case 0x16 when _isArm9:
                    Diff8(r[0], r[1]);
                    return true;

                case 0x18 when _isArm9:
                    Diff16(r[0], r[1]);
                    return true;

                case 0x1A when !_isArm9:
                    r[0] = SoundTables.Sine[r[0] & 0x3F];
                    return true;

                case 0x1B when !_isArm9:
                    r[0] = SoundTables.Pitch[Math.Min(r[0], 0x2FFu)];
                    return true;

                case 0x1C when !_isArm9:
                    r[0] = SoundTables.Volume[Math.Min(r[0], 0x2D3u)];
                    return true;

                case 0x1D when !_isArm9:
                    r[0] = r[1] = r[3] = 0;
                    return true;

                case 0x1F when _isArm9:
                    Bus.Write8(0x04000300, (byte)r[0]);
                    return true;

                default:
                    UnsupportedSwi?.Invoke(cpu, number);
                    return true;
            }
        }

        // ── Waiting ───────────────────────────────────────────────────────────

        private void IntrWait(ArmCpu cpu, bool discard, uint mask)
        {
            Irq.WriteIme(1);
            uint flagsAddress = IrqFlagsAddress;
            uint flags = Bus.Read32(flagsAddress);

            if (discard && !_retryingWait)
            {
                Bus.Write32(flagsAddress, flags & ~mask);
            }
            else if ((flags & mask) != 0)
            {
                Bus.Write32(flagsAddress, flags & ~mask);
                _retryingWait = false;
                return;
            }

            // Not satisfied yet: halt, and run this SWI again once an IRQ has been serviced.
            _retryingWait = true;
            cpu.Halted = true;
            cpu.Branch(cpu.Pc);
        }

        // ── Arithmetic ────────────────────────────────────────────────────────

        private static void Divide(uint[] r)
        {
            int numerator = (int)r[0];
            int denominator = (int)r[1];
            if (denominator == 0)
            {
                r[0] = numerator < 0 ? 1u : 0xFFFFFFFFu;
                r[1] = (uint)numerator;
                r[3] = 1;
                return;
            }
            if (numerator == int.MinValue && denominator == -1)
            {
                r[0] = 0x80000000u;
                r[1] = 0;
                r[3] = 0x80000000u;
                return;
            }
            int quotient = numerator / denominator;
            r[0] = (uint)quotient;
            r[1] = (uint)(numerator % denominator);
            r[3] = (uint)Math.Abs((long)quotient);
        }

        private uint GetCrc16(ushort initial, uint address, uint length)
        {
            var data = new byte[length & ~1u];
            for (int i = 0; i < data.Length; i++)
                data[i] = Bus.Read8(address + (uint)i);
            return Crc16.Compute(data, initial);
        }

        // ── Memory copy ───────────────────────────────────────────────────────

        private void CpuSet(uint source, uint dest, uint control)
        {
            uint count = control & 0x1FFFFF;
            bool fill = (control & (1u << 24)) != 0;
            bool wide = (control & (1u << 26)) != 0;

            if (wide)
            {
                source &= ~3u;
                dest &= ~3u;
                uint value = Bus.Read32(source);
                for (uint i = 0; i < count; i++)
                {
                    Bus.Write32(dest, fill ? value : Bus.Read32(source));
                    dest += 4;
                    if (!fill) source += 4;
                }
            }
            else
            {
                source &= ~1u;
                dest &= ~1u;
                ushort value = Bus.Read16(source);
                for (uint i = 0; i < count; i++)
                {
                    Bus.Write16(dest, fill ? value : Bus.Read16(source));
                    dest += 2;
                    if (!fill) source += 2;
                }
            }
        }

        private void CpuFastSet(uint source, uint dest, uint control)
        {
            uint count = ((control & 0x1FFFFF) + 7) & ~7u;
            bool fill = (control & (1u << 24)) != 0;
            source &= ~3u;
            dest &= ~3u;
            uint value = Bus.Read32(source);
            for (uint i = 0; i < count; i++)
            {
                Bus.Write32(dest, fill ? value : Bus.Read32(source));
                dest += 4;
                if (!fill) source += 4;
            }
        }

        // ── Guest callbacks ───────────────────────────────────────────────────

        /// <summary>
        /// Call a guest function (ARM or Thumb by bit 0) with up to three
        /// arguments and return its R0. The CPU state is restored afterwards.
        /// </summary>
        public uint CallGuest(uint function, uint arg0, uint arg1 = 0, uint arg2 = 0)
        {
            var cpu = Cpu;
            var saved = cpu.SaveState();
            uint trap = TrapAddress;

            cpu.R[0] = arg0;
            cpu.R[1] = arg1;
            cpu.R[2] = arg2;
            cpu.R[14] = trap;
            cpu.SetPc(function);

            for (int steps = 0; steps < 1_000_000 && cpu.Pc != trap; steps++)
                cpu.Step();

            uint result = cpu.R[0];
            cpu.RestoreState(saved, restoreCycles: false);
            return result;
        }

        /// <summary>Source reader that goes through callbacks (r3 structure) or straight to memory.</summary>
        private sealed class SourceReader
        {
            private readonly HleBios _bios;
            private readonly uint _callbacks;
            private uint _address;

            public SourceReader(HleBios bios, uint address, uint callbacks)
            {
                _bios = bios;
                _address = address;
                _callbacks = callbacks;
            }

            public bool UsesCallbacks => _callbacks != 0;

            public uint Open(uint dest, uint param)
            {
                if (_callbacks == 0)
                {
                    uint header = _bios.Bus.Read32(_address & ~3u);
                    _address += 4;
                    return header;
                }
                uint open = _bios.Bus.Read32(_callbacks);
                uint value = _bios.CallGuest(open, _address, dest, param);
                _address += 4;
                return value;
            }

            public byte Read8()
            {
                if (_callbacks == 0)
                    return _bios.Bus.Read8(_address++);
                uint get8 = _bios.Bus.Read32(_callbacks + 8);
                byte value = (byte)_bios.CallGuest(get8, _address);
                _address++;
                return value;
            }

            public uint Read32()
            {
                if (_callbacks == 0)
                {
                    uint word = _bios.Bus.Read32(_address & ~3u);
                    _address += 4;
                    return word;
                }
                uint get32 = _bios.Bus.Read32(_callbacks + 16);
                uint value = _bios.CallGuest(get32, _address);
                _address += 4;
                return value;
            }

            public void Close()
            {
                if (_callbacks == 0)
                    return;
                uint close = _bios.Bus.Read32(_callbacks + 4);
                if (close != 0)
                    _bios.CallGuest(close, _address);
            }
        }

        /// <summary>Destination writer: 8-bit units, or 16-bit units buffered in pairs (VRAM-safe).</summary>
        private sealed class DestWriter
        {
            private readonly IArmMemoryBus _bus;
            private readonly bool _wide;
            private uint _address;
            private int _pending = -1;

            public DestWriter(IArmMemoryBus bus, uint address, bool wide)
            {
                _bus = bus;
                _address = address;
                _wide = wide;
            }

            public uint Address => _address;

            public void Write(byte value)
            {
                if (!_wide)
                {
                    _bus.Write8(_address++, value);
                    return;
                }
                if ((_address & 1) == 0)
                {
                    _pending = value;
                }
                else
                {
                    _bus.Write16(_address - 1, (ushort)((_pending & 0xFF) | (value << 8)));
                    _pending = -1;
                }
                _address++;
            }

            /// <summary>Read back already-decompressed output (LZ77 back-references).</summary>
            public byte ReadBack(uint address)
            {
                if (_wide && _pending >= 0 && address == _address - 1)
                    return (byte)_pending;
                return _bus.Read8(address);
            }

            public void Flush()
            {
                if (_wide && _pending >= 0)
                    _bus.Write16(_address - 1, (ushort)(_pending & 0xFF));
                _pending = -1;
            }
        }

        // ── Decompression ─────────────────────────────────────────────────────

        private void Lz77(uint source, uint dest, bool wide, uint callbacks, uint param)
        {
            var reader = new SourceReader(this, source, callbacks);
            uint header = reader.Open(dest, param);
            if ((int)header < 0)
                return;
            int size = (int)(header >> 8);
            var writer = new DestWriter(Bus, dest, wide);

            int written = 0;
            while (written < size)
            {
                byte flags = reader.Read8();
                for (int bit = 7; bit >= 0 && written < size; bit--)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        writer.Write(reader.Read8());
                        written++;
                    }
                    else
                    {
                        int b1 = reader.Read8();
                        int b2 = reader.Read8();
                        int length = (b1 >> 4) + 3;
                        int displacement = ((b1 & 0xF) << 8 | b2) + 1;
                        for (int i = 0; i < length && written < size; i++)
                        {
                            writer.Write(writer.ReadBack((uint)(writer.Address - displacement)));
                            written++;
                        }
                    }
                }
            }
            writer.Flush();
            reader.Close();
            Cpu.R[0] = (uint)size;
        }

        private void RunLength(uint source, uint dest, bool wide, uint callbacks, uint param)
        {
            var reader = new SourceReader(this, source, callbacks);
            uint header = reader.Open(dest, param);
            if ((int)header < 0)
                return;
            int size = (int)(header >> 8);
            var writer = new DestWriter(Bus, dest, wide);

            int written = 0;
            while (written < size)
            {
                byte flag = reader.Read8();
                if ((flag & 0x80) != 0)
                {
                    int length = (flag & 0x7F) + 3;
                    byte value = reader.Read8();
                    for (int i = 0; i < length && written < size; i++, written++)
                        writer.Write(value);
                }
                else
                {
                    int length = (flag & 0x7F) + 1;
                    for (int i = 0; i < length && written < size; i++, written++)
                        writer.Write(reader.Read8());
                }
            }
            writer.Flush();
            reader.Close();
            Cpu.R[0] = (uint)size;
        }

        private void Huffman(uint source, uint dest, uint callbacks)
        {
            var reader = new SourceReader(this, source, callbacks);
            uint header = reader.Open(dest, 0);
            if ((int)header < 0)
                return;
            int bits = (int)(header & 0xF);
            int size = (int)(header >> 8);

            int treeSize = (reader.Read8() + 1) * 2;
            var tree = new byte[treeSize];
            tree[0] = (byte)(treeSize / 2 - 1);
            for (int i = 1; i < treeSize; i++)
                tree[i] = reader.Read8();

            uint output = 0;
            int outputBits = 0;
            int written = 0;
            int node = 1; // root node (index 0 is the size byte)
            uint destAddress = dest & ~3u;

            while (written < size)
            {
                uint word = reader.Read32();
                for (int bit = 31; bit >= 0 && written < size; bit--)
                {
                    bool right = ((word >> bit) & 1) != 0;
                    byte current = tree[node];
                    int next = (node & ~1) + (current & 0x3F) * 2 + 2 + (right ? 1 : 0);
                    bool isData = right ? (current & 0x40) != 0 : (current & 0x80) != 0;

                    if (!isData)
                    {
                        node = next;
                        continue;
                    }

                    output |= (uint)(tree[next] & ((1 << bits) - 1)) << outputBits;
                    outputBits += bits;
                    node = 1;
                    if (outputBits == 32)
                    {
                        Bus.Write32(destAddress, output);
                        destAddress += 4;
                        written += 4;
                        output = 0;
                        outputBits = 0;
                    }
                }
            }
            reader.Close();
            Cpu.R[0] = (uint)size;
        }

        private void BitUnPack(uint source, uint dest, uint info)
        {
            int length = Bus.Read16(info);
            int sourceWidth = Bus.Read8(info + 2);
            int destWidth = Bus.Read8(info + 3);
            uint offsetWord = Bus.Read32(info + 4);
            uint offset = offsetWord & 0x7FFFFFFFu;
            bool zeroFlag = (offsetWord & 0x80000000u) != 0;

            if (sourceWidth is not (1 or 2 or 4 or 8) || destWidth is not (1 or 2 or 4 or 8 or 16 or 32))
                return;

            uint output = 0;
            int outputBits = 0;
            dest &= ~3u;
            int sourceMask = (1 << sourceWidth) - 1;

            for (int i = 0; i < length; i++)
            {
                byte b = Bus.Read8(source + (uint)i);
                for (int bit = 0; bit < 8; bit += sourceWidth)
                {
                    uint unit = (uint)((b >> bit) & sourceMask);
                    if (unit != 0 || zeroFlag)
                        unit += offset;
                    output |= (destWidth == 32 ? unit : unit & ((1u << destWidth) - 1)) << outputBits;
                    outputBits += destWidth;
                    if (outputBits >= 32)
                    {
                        Bus.Write32(dest, output);
                        dest += 4;
                        output = 0;
                        outputBits = 0;
                    }
                }
            }
        }

        private void Diff8(uint source, uint dest)
        {
            uint header = Bus.Read32(source & ~3u);
            int size = (int)(header >> 8);
            uint s = (source & ~3u) + 4;
            byte value = 0;
            for (int i = 0; i < size; i++)
            {
                value += Bus.Read8(s + (uint)i);
                Bus.Write8(dest + (uint)i, value);
            }
        }

        private void Diff16(uint source, uint dest)
        {
            uint header = Bus.Read32(source & ~3u);
            int size = (int)(header >> 8);
            uint s = (source & ~3u) + 4;
            ushort value = 0;
            for (int i = 0; i < size; i += 2)
            {
                value += Bus.Read16(s + (uint)i);
                Bus.Write16(dest + (uint)i, value);
            }
        }
    }
}
