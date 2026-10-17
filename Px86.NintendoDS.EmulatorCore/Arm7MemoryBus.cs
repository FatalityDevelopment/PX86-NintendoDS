using System.Runtime.CompilerServices;
using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.Common.Interfaces;
using Px86.NintendoDS.EmulatorCore.Audio;
using Px86.NintendoDS.EmulatorCore.Cartridge;
using Px86.NintendoDS.EmulatorCore.Hardware;
using Px86.NintendoDS.EmulatorCore.Hardware.Spi;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.EmulatorCore
{
    /// <summary>
    /// ARM7TDMI memory bus.
    ///
    ///   00000000h  ARM7 BIOS 16 KB
    ///   02000000h  Main RAM 4 MB (shared with the ARM9)
    ///   03000000h  Shared WRAM, ARM7 view (WRAMCNT: none → mirrors ARM7 WRAM, 1st 16K, 2nd 16K, 32K)
    ///   03800000h  ARM7 WRAM 64 KB (mirrored to 03FFFFFFh)
    ///   04000000h  I/O ports
    ///   04800000h  Wifi registers and 8 KB Wifi RAM (stubbed)
    ///   06000000h  VRAM banks C/D when mapped to the ARM7 (MST 2)
    ///   08000000h  GBA slot (open bus)
    ///
    /// Reference: GBATEK "DS Memory Maps", "DS I/O Maps".
    /// </summary>
    public sealed class Arm7MemoryBus : IArmMemoryBus
    {
        private readonly NdsEmulator _nds;
        private readonly byte[] _wifiRam = new byte[0x2000];
        private readonly ushort[] _wifiRegisters = new ushort[0x800];

        public Arm7MemoryBus(NdsEmulator nds)
        {
            _nds = nds;
        }

        public event Action<uint, int, bool>? UnhandledIo;

        public void Reset()
        {
            Array.Clear(_wifiRam);
            Array.Clear(_wifiRegisters);
            _wifiRegisters[0] = 0xC340; // W_ID: DS-Lite wifi chip
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("Arm7Bus");
            s.Sync(_wifiRam);
            s.Sync(_wifiRegisters);
        }

        // ── Shared WRAM view ──────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>Where code at <paramref name="address"/> lives, for the block cache (null = don't cache).</summary>
        public byte[]? LocateCode(uint address, out int index)
        {
            switch (address >> 24)
            {
                case 0x00 when address < 0x4000:
                    index = (int)address;
                    return _nds.Bios7;
                case 0x02:
                    index = (int)(address & NdsEmulator.MainRamMask);
                    return _nds.MainRam;
                case 0x03:
                    return WramFor(address, out index);
                default:
                    index = 0;
                    return null;
            }
        }

        private byte[] WramFor(uint address, out int index)
        {
            if ((address & 0x00800000u) != 0)
            {
                index = (int)(address & 0xFFFF);
                return _nds.Arm7Wram;
            }
            switch (_nds.Control.WramMode)
            {
                case 0:
                    index = (int)(address & 0xFFFF);
                    return _nds.Arm7Wram;
                case 1:
                    index = (int)(address & 0x3FFF);
                    return _nds.SharedWram;
                case 2:
                    index = 0x4000 + (int)(address & 0x3FFF);
                    return _nds.SharedWram;
                default:
                    index = (int)(address & 0x7FFF);
                    return _nds.SharedWram;
            }
        }

        // ── CPU interface ─────────────────────────────────────────────────────

        public uint Fetch32(uint address) => Read32(address);
        public ushort Fetch16(uint address) => Read16(address);

        public uint Read32(uint address)
        {
            address &= ~3u;
            switch (address >> 24)
            {
                case 0x00: return address < 0x4000 ? MemoryAccess.Read32(_nds.Bios7, (int)address) : 0xFFFFFFFFu;
                case 0x02: return MemoryAccess.Read32(_nds.MainRam, (int)(address & NdsEmulator.MainRamMask));
                case 0x03:
                {
                    var memory = WramFor(address, out int i);
                    return MemoryAccess.Read32(memory, i);
                }
                case 0x04:
                    if (address >= 0x04800000u)
                        return ReadWifi16(address) | ((uint)ReadWifi16(address + 2) << 16);
                    return ReadIo32(address);
                case 0x06: return _nds.Vram.Arm7.Read32(address);
                case 0x08:
                case 0x09: return ((address >> 1) & 0xFFFF) | ((((address + 2) >> 1) & 0xFFFF) << 16);
                case 0x0A: return 0xFFFFFFFFu;
                default: return 0;
            }
        }

        public ushort Read16(uint address)
        {
            address &= ~1u;
            switch (address >> 24)
            {
                case 0x00: return address < 0x4000 ? MemoryAccess.Read16(_nds.Bios7, (int)address) : (ushort)0xFFFF;
                case 0x02: return MemoryAccess.Read16(_nds.MainRam, (int)(address & NdsEmulator.MainRamMask));
                case 0x03:
                {
                    var memory = WramFor(address, out int i);
                    return MemoryAccess.Read16(memory, i);
                }
                case 0x04:
                    if (address >= 0x04800000u)
                        return ReadWifi16(address);
                    return ReadIo16(address);
                case 0x06: return _nds.Vram.Arm7.Read16(address);
                case 0x08:
                case 0x09: return (ushort)((address >> 1) & 0xFFFF);
                case 0x0A: return 0xFFFF;
                default: return 0;
            }
        }

        public byte Read8(uint address)
        {
            switch (address >> 24)
            {
                case 0x00: return address < 0x4000 ? _nds.Bios7[address] : (byte)0xFF;
                case 0x02: return _nds.MainRam[address & NdsEmulator.MainRamMask];
                case 0x03:
                {
                    var memory = WramFor(address, out int i);
                    return memory[i];
                }
                case 0x04:
                    if (address >= 0x04800000u)
                        return (byte)(ReadWifi16(address & ~1u) >> (int)((address & 1) * 8));
                    return ReadIo8(address);
                case 0x06: return _nds.Vram.Arm7.Read8(address);
                case 0x0A: return 0xFF;
                default: return 0;
            }
        }

        public void Write32(uint address, uint value)
        {
            address &= ~3u;
            switch (address >> 24)
            {
                case 0x02:
                {
                    int i = (int)(address & NdsEmulator.MainRamMask);
                    MemoryAccess.Write32(_nds.MainRam, i, value);
                    if (_nds.Code.MainRamPageHasCode(i)) _nds.Code.OnWrite(_nds.MainRam, i);
                    break;
                }
                case 0x03:
                {
                    var memory = WramFor(address, out int i);
                    MemoryAccess.Write32(memory, i, value);
                    _nds.Code.OnWrite(memory, i);
                    break;
                }
                case 0x04:
                    if (address >= 0x04800000u)
                    {
                        WriteWifi16(address, (ushort)value);
                        WriteWifi16(address + 2, (ushort)(value >> 16));
                    }
                    else
                    {
                        WriteIo32(address, value);
                    }
                    break;
                case 0x06: _nds.Vram.Arm7.Write32(address, value); break;
            }
        }

        public void Write16(uint address, ushort value)
        {
            address &= ~1u;
            switch (address >> 24)
            {
                case 0x02:
                {
                    int i = (int)(address & NdsEmulator.MainRamMask);
                    MemoryAccess.Write16(_nds.MainRam, i, value);
                    if (_nds.Code.MainRamPageHasCode(i)) _nds.Code.OnWrite(_nds.MainRam, i);
                    break;
                }
                case 0x03:
                {
                    var memory = WramFor(address, out int i);
                    MemoryAccess.Write16(memory, i, value);
                    _nds.Code.OnWrite(memory, i);
                    break;
                }
                case 0x04:
                    if (address >= 0x04800000u)
                        WriteWifi16(address, value);
                    else
                        WriteIo16(address, value, 0xFFFF);
                    break;
                case 0x06: _nds.Vram.Arm7.Write16(address, value); break;
            }
        }

        public void Write8(uint address, byte value)
        {
            switch (address >> 24)
            {
                case 0x02:
                {
                    int i = (int)(address & NdsEmulator.MainRamMask);
                    _nds.MainRam[i] = value;
                    if (_nds.Code.MainRamPageHasCode(i)) _nds.Code.OnWrite(_nds.MainRam, i);
                    break;
                }
                case 0x03:
                {
                    var memory = WramFor(address, out int i);
                    memory[i] = value;
                    _nds.Code.OnWrite(memory, i);
                    break;
                }
                case 0x04:
                    if (address < 0x04800000u)
                        WriteIo8(address, value);
                    break;
                case 0x06: _nds.Vram.Arm7.Write8(address, value); break; // ARM7 may byte-write its VRAM
            }
        }

        // ── Wifi (stub) ───────────────────────────────────────────────────────

        private ushort ReadWifi16(uint address)
        {
            uint offset = address & 0x7FFF;
            if (offset >= 0x4000 && offset < 0x6000)
                return MemoryAccess.Read16(_wifiRam, (int)(offset - 0x4000));
            if (offset < 0x1000)
                return _wifiRegisters[offset >> 1];
            return 0;
        }

        private void WriteWifi16(uint address, ushort value)
        {
            uint offset = address & 0x7FFF;
            if (offset >= 0x4000 && offset < 0x6000)
                MemoryAccess.Write16(_wifiRam, (int)(offset - 0x4000), value);
            else if (offset > 0 && offset < 0x1000)
                _wifiRegisters[offset >> 1] = value;
        }

        // ── I/O ───────────────────────────────────────────────────────────────

        private static uint Offset(uint address) => address & 0x00FFFFFFu;

        private bool TryReadIo32(uint offset, out uint value)
        {
            switch (offset)
            {
                case 0x0B0: case 0x0BC: case 0x0C8: case 0x0D4:
                    value = _nds.Dma7.ReadSource((int)(offset - 0x0B0) / 12); return true;
                case 0x0B4: case 0x0C0: case 0x0CC: case 0x0D8:
                    value = _nds.Dma7.ReadDestination((int)(offset - 0x0B4) / 12); return true;
                case 0x0B8: case 0x0C4: case 0x0D0: case 0x0DC:
                    value = _nds.Dma7.ReadControl((int)(offset - 0x0B8) / 12); return true;
                case 0x1A4: value = _nds.Cartridge.ReadRomControl(); return true;
                case 0x208: value = _nds.Irq7.Ime; return true;
                case 0x210: value = _nds.Irq7.Ie; return true;
                case 0x214: value = _nds.Irq7.If; return true;
                case 0x100000: value = _nds.Ipc.Receive(1); return true;
                case 0x100010: value = _nds.Cartridge.ReadData(); return true;
            }
            if (SoundUnit.Contains(offset))
            {
                value = _nds.Sound.Read32(offset);
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
            switch (offset)
            {
                case SystemControl.REG_VRAMSTAT: return _nds.Vram.Vramstat;
                case SystemControl.REG_WRAMSTAT: return _nds.Control.WramMode;
                case SystemControl.REG_POSTFLG: return _nds.Control.PostFlag7;
                case SystemControl.REG_HALTCNT: return 0;
            }
            return (byte)(ReadIo16(address & ~1u) >> (int)((address & 1) * 8));
        }

        private ushort ReadIo16Core(uint offset)
        {
            switch (offset)
            {
                case LcdController.REG_DISPSTAT: return _nds.Lcd.ReadDispstat(1);
                case LcdController.REG_VCOUNT: return (ushort)_nds.Lcd.VCount;
                case 0x100: case 0x104: case 0x108: case 0x10C:
                    return _nds.Timers7.ReadCounter((int)(offset - 0x100) / 4);
                case 0x102: case 0x106: case 0x10A: case 0x10E:
                    return _nds.Timers7.ReadControl((int)(offset - 0x102) / 4);
                case Keypad.REG_KEYINPUT: return _nds.Keypad.KeyInput;
                case Keypad.REG_KEYCNT: return _nds.Keypad.ReadKeyControl(1);
                case 0x134: return 0x8000; // RCNT: GPIO mode, as left by the BIOS
                case Keypad.REG_EXTKEYIN: return _nds.Keypad.ExtKeyIn;
                case RealTimeClock.REG_RTC: return _nds.Rtc.Read();
                case IpcController.REG_IPCSYNC: return _nds.Ipc.ReadSync(1);
                case IpcController.REG_IPCFIFOCNT: return _nds.Ipc.ReadFifoControl(1);
                case CartridgeSlot.REG_AUXSPICNT: return _nds.Cartridge.ReadSpiControl();
                case CartridgeSlot.REG_AUXSPIDATA: return _nds.Cartridge.ReadSpiData();
                case 0x1A8: case 0x1AA: case 0x1AC: case 0x1AE:
                {
                    int i = (int)(offset - 0x1A8);
                    return (ushort)(_nds.Cartridge.ReadCommand(i) | (_nds.Cartridge.ReadCommand(i + 1) << 8));
                }
                case SpiBus.REG_SPICNT: return _nds.Spi.ReadControl();
                case SpiBus.REG_SPIDATA: return _nds.Spi.ReadData();
                case SystemControl.REG_EXMEMCNT: return _nds.Control.ReadExMemStatus7();
                case 0x206: return 0; // WIFIWAITCNT
                case 0x240: return (ushort)(_nds.Vram.Vramstat | (_nds.Control.WramMode << 8));
                case SystemControl.REG_POSTFLG: return _nds.Control.PostFlag7;
                case SystemControl.REG_POWCNT: return _nds.Control.PowerControl7;
                case 0x4700: return 0; // DSi SNDEXCNT (mapped in DS mode on DSi only)
            }

            UnhandledIo?.Invoke(0x04000000u | offset, 16, false);
            return 0;
        }

        private bool TryWriteIo32(uint offset, uint value, uint mask)
        {
            switch (offset)
            {
                case 0x0B0: case 0x0BC: case 0x0C8: case 0x0D4:
                    _nds.Dma7.WriteSource((int)(offset - 0x0B0) / 12, value, mask); return true;
                case 0x0B4: case 0x0C0: case 0x0CC: case 0x0D8:
                    _nds.Dma7.WriteDestination((int)(offset - 0x0B4) / 12, value, mask); return true;
                case 0x0B8: case 0x0C4: case 0x0D0: case 0x0DC:
                    _nds.Dma7.WriteControl((int)(offset - 0x0B8) / 12, value, mask); return true;
                case IpcController.REG_IPCFIFOSEND:
                    _nds.Ipc.Send(1, value); return true;
                case CartridgeSlot.REG_ROMCTRL:
                    _nds.Cartridge.WriteRomControl(value, mask); return true;
                case 0x208: _nds.Irq7.WriteIme(value); return true;
                case 0x210: _nds.Irq7.WriteIe(value, mask); return true;
                case 0x214: _nds.Irq7.AcknowledgeIf(value & mask); return true;
                case 0x100010: return true;
            }
            if (SoundUnit.Contains(offset))
            {
                _nds.Sound.Write32(offset, value, mask);
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
            switch (offset)
            {
                case SystemControl.REG_HALTCNT:
                    if ((value >> 6) >= 2)
                        _nds.Arm7.Halted = true;
                    return;
                case SystemControl.REG_POSTFLG:
                    _nds.Control.PostFlag7 = (byte)((_nds.Control.PostFlag7 & 1) | (value & 1));
                    return;
            }
            if (offset >= 0x1A8 && offset < 0x1B0)
            {
                _nds.Cartridge.WriteCommand((int)(offset - 0x1A8), value);
                return;
            }
            int shift = (int)(offset & 1) * 8;
            WriteIo16(address & ~1u, (ushort)(value << shift), (ushort)(0xFF << shift));
        }

        private void WriteIo16Core(uint offset, ushort value, ushort mask)
        {
            switch (offset)
            {
                case LcdController.REG_DISPSTAT: _nds.Lcd.WriteDispstat(1, value, mask); return;
                case LcdController.REG_VCOUNT: _nds.Lcd.WriteVCount(value); return;
                case 0x100: case 0x104: case 0x108: case 0x10C:
                {
                    int t = (int)(offset - 0x100) / 4;
                    if (mask == 0xFFFF) _nds.Timers7.WriteReload(t, value);
                    return;
                }
                case 0x102: case 0x106: case 0x10A: case 0x10E:
                {
                    int t = (int)(offset - 0x102) / 4;
                    _nds.Timers7.WriteControl(t, (ushort)((_nds.Timers7.ReadControl(t) & ~mask) | (value & mask)));
                    return;
                }
                case Keypad.REG_KEYCNT: _nds.Keypad.WriteKeyControl(1, value, mask); return;
                case 0x134: return; // RCNT
                case RealTimeClock.REG_RTC:
                    if ((mask & 0xFF) != 0)
                        _nds.Rtc.Write((ushort)((_nds.Rtc.Read() & ~mask) | (value & mask)));
                    return;
                case IpcController.REG_IPCSYNC: _nds.Ipc.WriteSync(1, value, mask); return;
                case IpcController.REG_IPCFIFOCNT: _nds.Ipc.WriteFifoControl(1, value, mask); return;
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
                    return;
                case SpiBus.REG_SPICNT: _nds.Spi.WriteControl(value, mask); return;
                case SpiBus.REG_SPIDATA:
                    if ((mask & 0xFF) != 0) _nds.Spi.WriteData((byte)value);
                    return;
                case SystemControl.REG_EXMEMCNT: _nds.Control.WriteExMemStatus7(value, mask); return;
                case 0x206: return;
                case SystemControl.REG_POSTFLG:
                    if ((mask & 0x00FF) != 0) WriteIo8(0x04000300u, (byte)value);
                    if ((mask & 0xFF00) != 0) WriteIo8(0x04000301u, (byte)(value >> 8));
                    return;
                case SystemControl.REG_POWCNT: _nds.Control.WritePowerControl7(value, mask); return;
                case SystemControl.REG_BIOSPROT: case SystemControl.REG_BIOSPROT + 2: return;
                case 0x4700: return;
            }

            UnhandledIo?.Invoke(0x04000000u | offset, 16, true);
        }
    }
}
