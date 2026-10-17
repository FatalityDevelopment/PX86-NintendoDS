using System.Runtime.CompilerServices;
using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.Common.Interfaces;
using Px86.NintendoDS.EmulatorCore.Cartridge;
using Px86.NintendoDS.EmulatorCore.Hardware;
using Px86.NintendoDS.EmulatorCore.Hardware.Spi;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.EmulatorCore.Video.Gpu3D;

namespace Px86.NintendoDS.EmulatorCore
{
    /// <summary>
    /// ARM946E-S memory bus.
    ///
    ///   00000000h  ITCM 32 KB (mirrored up to the CP15 ITCM size; fetches + data)
    ///   (movable)  DTCM 16 KB (CP15 C9,C1,0; data only)
    ///   02000000h  Main RAM 4 MB (mirrored to 02FFFFFFh)
    ///   03000000h  Shared WRAM (WRAMCNT: 32K / 2nd 16K / 1st 16K / none)
    ///   04000000h  I/O ports
    ///   05000000h  Palettes 2 KB (A BG, A OBJ, B BG, B OBJ)
    ///   06000000h  VRAM (BG-A, BG-B, OBJ-A, OBJ-B, LCDC)
    ///   07000000h  OAM 2 KB (A, B)
    ///   08000000h  GBA slot (open bus: no GBA cartridge)
    ///   FFFF0000h  ARM9 BIOS (4 KB, mirrored over 32 KB)
    ///
    /// TCM sits in front of the bus. DMA bypasses it (<see cref="DmaController.Active"/>).
    /// 8-bit writes to palette, VRAM and OAM are ignored, as on hardware.
    ///
    /// Reference: GBATEK "DS Memory Maps", "DS I/O Maps".
    /// </summary>
    public sealed class Arm9MemoryBus : IArmMemoryBus
    {
        public const int ItcmSize = 32 * 1024;
        public const int DtcmSize = 16 * 1024;

        private readonly NdsEmulator _nds;
        public readonly byte[] Itcm = new byte[ItcmSize];
        public readonly byte[] Dtcm = new byte[DtcmSize];

        // Cached TCM windows (rebuilt on CP15 changes).
        private uint _itcmReadLimit;
        private uint _itcmWriteLimit;
        private uint _dtcmBase;
        private uint _dtcmReadSize;
        private uint _dtcmWriteSize;

        public Arm9MemoryBus(NdsEmulator nds)
        {
            _nds = nds;
        }

        /// <summary>Unhandled I/O reads/writes (address, width, isWrite). For debugging and the trace log.</summary>
        public event Action<uint, int, bool>? UnhandledIo;

        public void Reset()
        {
            Array.Clear(Itcm);
            Array.Clear(Dtcm);
            UpdateTcm();
        }

        public void UpdateTcm()
        {
            var cp15 = _nds.Cp15;
            _itcmWriteLimit = cp15.ItcmEnabled ? cp15.ItcmSize : 0;
            _itcmReadLimit = cp15.ItcmEnabled && !cp15.ItcmLoadMode ? cp15.ItcmSize : 0;
            _dtcmBase = cp15.DtcmBase;
            _dtcmWriteSize = cp15.DtcmEnabled ? cp15.DtcmSize : 0;
            _dtcmReadSize = cp15.DtcmEnabled && !cp15.DtcmLoadMode ? cp15.DtcmSize : 0;
        }

        private bool Dma => _nds.Dma9.Active;

        public void Serialize(StateSerializer s)
        {
            s.Section("Arm9Bus");
            s.Sync(Itcm);
            s.Sync(Dtcm);
            if (s.IsReading)
                UpdateTcm();
        }

        // ── CPU interface ─────────────────────────────────────────────────────

        public uint Fetch32(uint address)
        {
            if (address < _itcmReadLimit)
                return MemoryAccess.Read32(Itcm, (int)(address & (ItcmSize - 4)));
            return BusRead32(address);
        }

        public ushort Fetch16(uint address)
        {
            if (address < _itcmReadLimit)
                return MemoryAccess.Read16(Itcm, (int)(address & (ItcmSize - 2)));
            return BusRead16(address);
        }

        public uint Read32(uint address)
        {
            if (!Dma)
            {
                if (address < _itcmReadLimit)
                    return MemoryAccess.Read32(Itcm, (int)(address & (ItcmSize - 4)));
                if (address - _dtcmBase < _dtcmReadSize)
                    return MemoryAccess.Read32(Dtcm, (int)((address - _dtcmBase) & (DtcmSize - 4)));
            }
            return BusRead32(address);
        }

        public ushort Read16(uint address)
        {
            if (!Dma)
            {
                if (address < _itcmReadLimit)
                    return MemoryAccess.Read16(Itcm, (int)(address & (ItcmSize - 2)));
                if (address - _dtcmBase < _dtcmReadSize)
                    return MemoryAccess.Read16(Dtcm, (int)((address - _dtcmBase) & (DtcmSize - 2)));
            }
            return BusRead16(address);
        }

        public byte Read8(uint address)
        {
            if (!Dma)
            {
                if (address < _itcmReadLimit)
                    return Itcm[address & (ItcmSize - 1)];
                if (address - _dtcmBase < _dtcmReadSize)
                    return Dtcm[(address - _dtcmBase) & (DtcmSize - 1)];
            }
            return BusRead8(address);
        }

        public void Write32(uint address, uint value)
        {
            if (!Dma)
            {
                if (address < _itcmWriteLimit)
                {
                    MemoryAccess.Write32(Itcm, (int)(address & (ItcmSize - 4)), value);
                    return;
                }
                if (address - _dtcmBase < _dtcmWriteSize)
                {
                    MemoryAccess.Write32(Dtcm, (int)((address - _dtcmBase) & (DtcmSize - 4)), value);
                    return;
                }
            }
            BusWrite32(address, value);
        }

        public void Write16(uint address, ushort value)
        {
            if (!Dma)
            {
                if (address < _itcmWriteLimit)
                {
                    MemoryAccess.Write16(Itcm, (int)(address & (ItcmSize - 2)), value);
                    return;
                }
                if (address - _dtcmBase < _dtcmWriteSize)
                {
                    MemoryAccess.Write16(Dtcm, (int)((address - _dtcmBase) & (DtcmSize - 2)), value);
                    return;
                }
            }
            BusWrite16(address, value);
        }

        public void Write8(uint address, byte value)
        {
            if (!Dma)
            {
                if (address < _itcmWriteLimit)
                {
                    Itcm[address & (ItcmSize - 1)] = value;
                    return;
                }
                if (address - _dtcmBase < _dtcmWriteSize)
                {
                    Dtcm[(address - _dtcmBase) & (DtcmSize - 1)] = value;
                    return;
                }
            }
            BusWrite8(address, value);
        }

        // ── System bus ────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SharedWramIndex(uint address, out bool mapped)
        {
            mapped = true;
            switch (_nds.Control.WramMode)
            {
                case 0: return (int)(address & 0x7FFF);
                case 1: return 0x4000 + (int)(address & 0x3FFF);
                case 2: return (int)(address & 0x3FFF);
                default: mapped = false; return 0;
            }
        }

        public uint BusRead32(uint address)
        {
            switch (address >> 24)
            {
                case 0x02: return MemoryAccess.Read32(_nds.MainRam, (int)(address & NdsEmulator.MainRamMask & ~3u));
                case 0x03:
                {
                    int i = SharedWramIndex(address & ~3u, out bool mapped);
                    return mapped ? MemoryAccess.Read32(_nds.SharedWram, i) : 0;
                }
                case 0x04: return ReadIo32(address & ~3u);
                case 0x05: return MemoryAccess.Read32(_nds.Palette, (int)(address & 0x7FC));
                case 0x06: return _nds.Vram.Read32(address);
                case 0x07: return MemoryAccess.Read32(_nds.Oam, (int)(address & 0x7FC));
                case 0x08:
                case 0x09: return GbaSlotOpenBus(address) | (GbaSlotOpenBus(address + 2) << 16);
                case 0x0A: return 0xFFFFFFFFu;
                case 0xFF:
                    if (address >= 0xFFFF0000u)
                        return MemoryAccess.Read32(_nds.Bios9, (int)(address & 0xFFC));
                    return 0;
                default: return 0;
            }
        }

        public ushort BusRead16(uint address)
        {
            switch (address >> 24)
            {
                case 0x02: return MemoryAccess.Read16(_nds.MainRam, (int)(address & NdsEmulator.MainRamMask & ~1u));
                case 0x03:
                {
                    int i = SharedWramIndex(address & ~1u, out bool mapped);
                    return mapped ? MemoryAccess.Read16(_nds.SharedWram, i) : (ushort)0;
                }
                case 0x04: return ReadIo16(address & ~1u);
                case 0x05: return MemoryAccess.Read16(_nds.Palette, (int)(address & 0x7FE));
                case 0x06: return _nds.Vram.Read16(address);
                case 0x07: return MemoryAccess.Read16(_nds.Oam, (int)(address & 0x7FE));
                case 0x08:
                case 0x09: return (ushort)GbaSlotOpenBus(address);
                case 0x0A: return 0xFFFF;
                case 0xFF:
                    if (address >= 0xFFFF0000u)
                        return MemoryAccess.Read16(_nds.Bios9, (int)(address & 0xFFE));
                    return 0;
                default: return 0;
            }
        }

        public byte BusRead8(uint address)
        {
            switch (address >> 24)
            {
                case 0x02: return _nds.MainRam[address & NdsEmulator.MainRamMask];
                case 0x03:
                {
                    int i = SharedWramIndex(address, out bool mapped);
                    return mapped ? _nds.SharedWram[i] : (byte)0;
                }
                case 0x04: return ReadIo8(address);
                case 0x05: return _nds.Palette[address & 0x7FF];
                case 0x06: return _nds.Vram.Read8(address);
                case 0x07: return _nds.Oam[address & 0x7FF];
                case 0x08:
                case 0x09: return (byte)(GbaSlotOpenBus(address & ~1u) >> (int)((address & 1) * 8));
                case 0x0A: return 0xFF;
                case 0xFF:
                    if (address >= 0xFFFF0000u)
                        return _nds.Bios9[address & 0xFFF];
                    return 0;
                default: return 0;
            }
        }

        /// <summary>No GBA cartridge: the bus floats to (address / 2) &amp; 0xFFFF.</summary>
        private static uint GbaSlotOpenBus(uint address) => (address >> 1) & 0xFFFF;

        public void BusWrite32(uint address, uint value)
        {
            switch (address >> 24)
            {
                case 0x02: MemoryAccess.Write32(_nds.MainRam, (int)(address & NdsEmulator.MainRamMask & ~3u), value); break;
                case 0x03:
                {
                    int i = SharedWramIndex(address & ~3u, out bool mapped);
                    if (mapped) MemoryAccess.Write32(_nds.SharedWram, i, value);
                    break;
                }
                case 0x04: WriteIo32(address & ~3u, value); break;
                case 0x05: MemoryAccess.Write32(_nds.Palette, (int)(address & 0x7FC), value); break;
                case 0x06: _nds.Vram.Write32(address, value); break;
                case 0x07: MemoryAccess.Write32(_nds.Oam, (int)(address & 0x7FC), value); break;
            }
        }

        public void BusWrite16(uint address, ushort value)
        {
            switch (address >> 24)
            {
                case 0x02: MemoryAccess.Write16(_nds.MainRam, (int)(address & NdsEmulator.MainRamMask & ~1u), value); break;
                case 0x03:
                {
                    int i = SharedWramIndex(address & ~1u, out bool mapped);
                    if (mapped) MemoryAccess.Write16(_nds.SharedWram, i, value);
                    break;
                }
                case 0x04: WriteIo16(address & ~1u, value, 0xFFFF); break;
                case 0x05: MemoryAccess.Write16(_nds.Palette, (int)(address & 0x7FE), value); break;
                case 0x06: _nds.Vram.Write16(address, value); break;
                case 0x07: MemoryAccess.Write16(_nds.Oam, (int)(address & 0x7FE), value); break;
            }
        }

        public void BusWrite8(uint address, byte value)
        {
            switch (address >> 24)
            {
                case 0x02: _nds.MainRam[address & NdsEmulator.MainRamMask] = value; break;
                case 0x03:
                {
                    int i = SharedWramIndex(address, out bool mapped);
                    if (mapped) _nds.SharedWram[i] = value;
                    break;
                }
                case 0x04: WriteIo8(address, value); break;
                // Palette, VRAM and OAM ignore 8-bit writes on the ARM9.
            }
        }

        // ── I/O ───────────────────────────────────────────────────────────────

        private static uint Offset(uint address) => address & 0x00FFFFFFu;

        /// <summary>32-bit registers whose access must not be split into halves.</summary>
        private bool TryReadIo32(uint offset, out uint value)
        {
            switch (offset)
            {
                case 0x0B0: case 0x0BC: case 0x0C8: case 0x0D4:
                    value = _nds.Dma9.ReadSource((int)(offset - 0x0B0) / 12); return true;
                case 0x0B4: case 0x0C0: case 0x0CC: case 0x0D8:
                    value = _nds.Dma9.ReadDestination((int)(offset - 0x0B4) / 12); return true;
                case 0x0B8: case 0x0C4: case 0x0D0: case 0x0DC:
                    value = _nds.Dma9.ReadControl((int)(offset - 0x0B8) / 12); return true;
                case 0x0E0: case 0x0E4: case 0x0E8: case 0x0EC:
                    value = _nds.Dma9.ReadFill((int)(offset - 0x0E0) / 4); return true;
                case 0x1A4: value = _nds.Cartridge.ReadRomControl(); return true;
                case 0x208: value = _nds.Irq9.Ime; return true;
                case 0x210: value = _nds.Irq9.Ie; return true;
                case 0x214: value = _nds.Irq9.If; return true;
                case 0x290: value = (uint)_nds.Math.DivNumerator; return true;
                case 0x294: value = (uint)(_nds.Math.DivNumerator >> 32); return true;
                case 0x298: value = (uint)_nds.Math.DivDenominator; return true;
                case 0x29C: value = (uint)(_nds.Math.DivDenominator >> 32); return true;
                case 0x2A0: value = (uint)_nds.Math.DivResult; return true;
                case 0x2A4: value = (uint)(_nds.Math.DivResult >> 32); return true;
                case 0x2A8: value = (uint)_nds.Math.DivRemainder; return true;
                case 0x2AC: value = (uint)(_nds.Math.DivRemainder >> 32); return true;
                case 0x2B4: value = _nds.Math.SqrtResult; return true;
                case 0x2B8: value = (uint)_nds.Math.SqrtParameter; return true;
                case 0x2BC: value = (uint)(_nds.Math.SqrtParameter >> 32); return true;
                case 0x100000: value = _nds.Ipc.Receive(0); return true;
                case 0x100010: value = _nds.Cartridge.ReadData(); return true;
            }
            if (Gpu3DEngine.Contains(offset))
            {
                value = _nds.Gpu3D.Read32(offset);
                return true;
            }
            value = 0;
            return false;
        }

        public uint ReadIo32(uint address)
        {
            uint offset = Offset(address);
            if (TryReadIo32(offset, out uint value))
                return value;
            return ReadIo16Core(offset) | ((uint)ReadIo16Core(offset + 2) << 16);
        }

        public ushort ReadIo16(uint address)
        {
            uint offset = Offset(address);
            if (TryReadIo32(offset & ~3u, out uint value))
                return (ushort)(value >> (int)((offset & 2) * 8));
            return ReadIo16Core(offset);
        }

        public byte ReadIo8(uint address)
        {
            uint offset = Offset(address);
            if (offset >= 0x240 && offset <= 0x249)
                return offset == 0x247 ? _nds.Control.WramMode : _nds.Vram.ReadControl(VramBankForOffset(offset));
            return (byte)(ReadIo16(address & ~1u) >> (int)((address & 1) * 8));
        }

        private ushort ReadIo16Core(uint offset)
        {
            if (offset < 0x70)
            {
                switch (offset)
                {
                    case LcdController.REG_DISPSTAT: return _nds.Lcd.ReadDispstat(0);
                    case LcdController.REG_VCOUNT: return (ushort)_nds.Lcd.VCount;
                    case Gpu3DEngine.REG_DISP3DCNT: return _nds.Gpu3D.ReadDisp3DControl();
                    default: return _nds.EngineA.Read16(offset);
                }
            }
            if (offset >= 0x1000 && offset < 0x1070)
                return _nds.EngineB.Read16(offset - 0x1000);

            switch (offset)
            {
                case 0x100: case 0x104: case 0x108: case 0x10C:
                    return _nds.Timers9.ReadCounter((int)(offset - 0x100) / 4);
                case 0x102: case 0x106: case 0x10A: case 0x10E:
                    return _nds.Timers9.ReadControl((int)(offset - 0x102) / 4);
                case Keypad.REG_KEYINPUT: return _nds.Keypad.KeyInput;
                case Keypad.REG_KEYCNT: return _nds.Keypad.ReadKeyControl(0);
                case IpcController.REG_IPCSYNC: return _nds.Ipc.ReadSync(0);
                case IpcController.REG_IPCFIFOCNT: return _nds.Ipc.ReadFifoControl(0);
                case CartridgeSlot.REG_AUXSPICNT: return _nds.Cartridge.ReadSpiControl();
                case CartridgeSlot.REG_AUXSPIDATA: return _nds.Cartridge.ReadSpiData();
                case 0x1A8: case 0x1AA: case 0x1AC: case 0x1AE:
                {
                    int i = (int)(offset - 0x1A8);
                    return (ushort)(_nds.Cartridge.ReadCommand(i) | (_nds.Cartridge.ReadCommand(i + 1) << 8));
                }
                case SystemControl.REG_EXMEMCNT: return _nds.Control.ExMemControl;
                case 0x240: case 0x242: case 0x244: case 0x246: case 0x248:
                    return (ushort)(ReadIo8(offset) | (ReadIo8(offset + 1) << 8));
                case MathUnit.REG_DIVCNT: return _nds.Math.DivControl;
                case MathUnit.REG_SQRTCNT: return _nds.Math.SqrtControl;
                case SystemControl.REG_POSTFLG: return _nds.Control.PostFlag9;
                case SystemControl.REG_POWCNT: return _nds.Control.PowerControl9;
                case 0x4004: return 0; // DSi SCFG: reads as NDS
            }

            UnhandledIo?.Invoke(0x04000000u | offset, 16, false);
            return 0;
        }

        private bool TryWriteIo32(uint offset, uint value, uint mask)
        {
            switch (offset)
            {
                case 0x0B0: case 0x0BC: case 0x0C8: case 0x0D4:
                    _nds.Dma9.WriteSource((int)(offset - 0x0B0) / 12, value, mask); return true;
                case 0x0B4: case 0x0C0: case 0x0CC: case 0x0D8:
                    _nds.Dma9.WriteDestination((int)(offset - 0x0B4) / 12, value, mask); return true;
                case 0x0B8: case 0x0C4: case 0x0D0: case 0x0DC:
                    _nds.Dma9.WriteControl((int)(offset - 0x0B8) / 12, value, mask); return true;
                case 0x0E0: case 0x0E4: case 0x0E8: case 0x0EC:
                    _nds.Dma9.WriteFill((int)(offset - 0x0E0) / 4, value, mask); return true;
                case IpcController.REG_IPCFIFOSEND:
                    _nds.Ipc.Send(0, value); return true;
                case CartridgeSlot.REG_ROMCTRL:
                    _nds.Cartridge.WriteRomControl(value, mask); return true;
                case 0x208: _nds.Irq9.WriteIme(value); return true;
                case 0x210: _nds.Irq9.WriteIe(value, mask); return true;
                case 0x214: _nds.Irq9.AcknowledgeIf(value & mask); return true;
                case 0x290: case 0x294:
                {
                    int word = (int)(offset - 0x290) / 4;
                    uint current = (uint)(_nds.Math.DivNumerator >> (word * 32));
                    _nds.Math.WriteNumerator(word, (current & ~mask) | (value & mask));
                    return true;
                }
                case 0x298: case 0x29C:
                {
                    int word = (int)(offset - 0x298) / 4;
                    uint current = (uint)(_nds.Math.DivDenominator >> (word * 32));
                    _nds.Math.WriteDenominator(word, (current & ~mask) | (value & mask));
                    return true;
                }
                case 0x2B8: case 0x2BC:
                {
                    int word = (int)(offset - 0x2B8) / 4;
                    uint current = (uint)(_nds.Math.SqrtParameter >> (word * 32));
                    _nds.Math.WriteSqrtParameter(word, (current & ~mask) | (value & mask));
                    return true;
                }
                case 0x100010: return true; // cart data out (writable carts only)
            }
            if (Gpu3DEngine.Contains(offset))
            {
                _nds.Gpu3D.Write32(offset, value, mask);
                return true;
            }
            return false;
        }

        public void WriteIo32(uint address, uint value)
        {
            uint offset = Offset(address);
            if (TryWriteIo32(offset, value, 0xFFFFFFFFu))
                return;
            WriteIo16Core(offset, (ushort)value, 0xFFFF);
            WriteIo16Core(offset + 2, (ushort)(value >> 16), 0xFFFF);
        }

        public void WriteIo16(uint address, ushort value, ushort mask)
        {
            uint offset = Offset(address);
            int shift = (int)(offset & 2) * 8;
            if (TryWriteIo32(offset & ~3u, (uint)value << shift, (uint)mask << shift))
                return;
            WriteIo16Core(offset, value, mask);
        }

        public void WriteIo8(uint address, byte value)
        {
            uint offset = Offset(address);
            if (offset >= 0x240 && offset <= 0x249)
            {
                if (offset == 0x247)
                    _nds.Control.WriteWramMode(value);
                else
                    _nds.Vram.WriteControl(VramBankForOffset(offset), value);
                return;
            }
            if (offset == CartridgeSlot.REG_COMMAND || (offset > 0x1A8 && offset < 0x1B0))
            {
                _nds.Cartridge.WriteCommand((int)(offset - 0x1A8), value);
                return;
            }
            int shift = (int)(offset & 1) * 8;
            WriteIo16(address & ~1u, (ushort)(value << shift), (ushort)(0xFF << shift));
        }

        private static int VramBankForOffset(uint offset) => offset switch
        {
            0x248 => VramController.BANK_H,
            0x249 => VramController.BANK_I,
            _ => (int)(offset - 0x240),
        };

        private void WriteIo16Core(uint offset, ushort value, ushort mask)
        {
            if (offset < 0x70)
            {
                switch (offset)
                {
                    case LcdController.REG_DISPSTAT: _nds.Lcd.WriteDispstat(0, value, mask); return;
                    case LcdController.REG_VCOUNT: _nds.Lcd.WriteVCount(value); return;
                    case Gpu3DEngine.REG_DISP3DCNT: _nds.Gpu3D.WriteDisp3DControl(value, mask); return;
                    default: _nds.EngineA.Write16(offset, value, mask); return;
                }
            }
            if (offset >= 0x1000 && offset < 0x1070)
            {
                _nds.EngineB.Write16(offset - 0x1000, value, mask);
                return;
            }

            switch (offset)
            {
                case 0x100: case 0x104: case 0x108: case 0x10C:
                {
                    int t = (int)(offset - 0x100) / 4;
                    if (mask == 0xFFFF) _nds.Timers9.WriteReload(t, value);
                    return;
                }
                case 0x102: case 0x106: case 0x10A: case 0x10E:
                {
                    int t = (int)(offset - 0x102) / 4;
                    _nds.Timers9.WriteControl(t, (ushort)((_nds.Timers9.ReadControl(t) & ~mask) | (value & mask)));
                    return;
                }
                case Keypad.REG_KEYCNT: _nds.Keypad.WriteKeyControl(0, value, mask); return;
                case IpcController.REG_IPCSYNC: _nds.Ipc.WriteSync(0, value, mask); return;
                case IpcController.REG_IPCFIFOCNT: _nds.Ipc.WriteFifoControl(0, value, mask); return;
                case CartridgeSlot.REG_AUXSPICNT: _nds.Cartridge.WriteSpiControl(value, mask); return;
                case CartridgeSlot.REG_AUXSPIDATA:
                    if ((mask & 0xFF) != 0) _nds.Cartridge.WriteSpiData((byte)value);
                    return;
                case 0x1A8: case 0x1AA: case 0x1AC: case 0x1AE:
                {
                    int i = (int)(offset - 0x1A8);
                    if ((mask & 0x00FF) != 0) _nds.Cartridge.WriteCommand(i, (byte)value);
                    if ((mask & 0xFF00) != 0) _nds.Cartridge.WriteCommand(i + 1, (byte)(value >> 8));
                    return;
                }
                case 0x1B0: case 0x1B2: case 0x1B4: case 0x1B6: case 0x1B8: case 0x1BA:
                    return; // KEY2 seeds: encryption is not simulated
                case SystemControl.REG_EXMEMCNT:
                    _nds.Control.WriteExMemControl(value, mask);
                    return;
                case 0x240: case 0x242: case 0x244: case 0x246: case 0x248:
                    if ((mask & 0x00FF) != 0) WriteIo8(0x04000000u | offset, (byte)value);
                    if ((mask & 0xFF00) != 0) WriteIo8(0x04000000u | (offset + 1), (byte)(value >> 8));
                    return;
                case MathUnit.REG_DIVCNT: _nds.Math.WriteDivControl((ushort)((_nds.Math.DivControl & ~mask) | (value & mask))); return;
                case MathUnit.REG_SQRTCNT: _nds.Math.WriteSqrtControl((ushort)((_nds.Math.SqrtControl & ~mask) | (value & mask))); return;
                case SystemControl.REG_POSTFLG:
                    if ((mask & 0xFF) != 0)
                        _nds.Control.PostFlag9 = (byte)((_nds.Control.PostFlag9 & 1) | (value & 3));
                    return;
                case SystemControl.REG_POWCNT: _nds.Control.WritePowerControl9(value, mask); return;
            }

            UnhandledIo?.Invoke(0x04000000u | offset, 16, true);
        }
    }
}
