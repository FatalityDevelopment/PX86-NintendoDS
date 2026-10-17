using Px86.NintendoDS.EmulatorCore.Hardware;
using Px86.NintendoDS.FileFormats.Rom;

namespace Px86.NintendoDS.EmulatorCore.Cartridge
{
    /// <summary>
    /// The NDS cartridge slot (slot-1): ROM command protocol plus the AUX SPI
    /// bus to the backup chip.
    ///
    ///   040001A0h  AUXSPICNT   0–1 SPI baud, 6 SPI hold, 7 SPI busy, 13 mode (0 ROM, 1 SPI),
    ///                          14 ROM transfer IRQ enable, 15 slot enable
    ///   040001A2h  AUXSPIDATA  SPI byte exchange
    ///   040001A4h  ROMCTRL     23 data-word ready (R), 24–26 block size (0 none, 1–6 = 100h &lt;&lt; n, 7 = 4),
    ///                          29 release reset, 31 start/busy
    ///   040001A8h  Command     8 bytes, MSB of the command at the lowest address
    ///   04100010h  Data in     32-bit data port (CPU or DMA in "DS cartridge" mode)
    ///
    /// Protocol state:
    ///   Raw   after reset: 9Fh dummy, 00h header, 90h chip ID, 3Ch → KEY1
    ///   Key1  Blowfish-encrypted commands used by the BIOS to read the secure area
    ///         (only needed when booting through a real BIOS; see <see cref="Key1Decryptor"/>)
    ///   Main  the state games run in: B7h aaaaaaaa = read 200h bytes, B8h = chip ID
    ///
    /// Direct boot starts in <see cref="ProtocolMode.Main"/>. KEY2 stream
    /// encryption is not simulated: data is served decrypted, which is what
    /// software sees once the hardware has removed KEY2.
    ///
    /// Reference: GBATEK "DS Cartridge I/O Ports", "DS Cartridge Protocol".
    /// </summary>
    public sealed class CartridgeSlot
    {
        public const uint REG_AUXSPICNT  = 0x1A0;
        public const uint REG_AUXSPIDATA = 0x1A2;
        public const uint REG_ROMCTRL    = 0x1A4;
        public const uint REG_COMMAND    = 0x1A8;
        public const uint REG_SEED       = 0x1B0;
        public const uint REG_DATA       = 0x100010;

        public enum ProtocolMode { Raw, Key1, Main }

        private readonly Scheduler _scheduler;
        private readonly InterruptController[] _irq;
        private readonly DmaController[] _dma;

        private readonly byte[] _command = new byte[8];
        private ushort _spiControl;
        private byte _spiData;
        private uint _romControl;

        private byte[] _transfer = Array.Empty<byte>();
        private int _transferLength;
        private int _transferPosition;
        private uint _dataLatch;

        public CartridgeSlot(Scheduler scheduler, InterruptController arm9Irq, InterruptController arm7Irq,
                             DmaController arm9Dma, DmaController arm7Dma)
        {
            _scheduler = scheduler;
            _irq = new[] { arm9Irq, arm7Irq };
            _dma = new[] { arm9Dma, arm7Dma };
            scheduler.SetHandler(SchedulerEvent.CartridgeTransfer, OnWordReady);
        }

        public NdsRom? Rom { get; private set; }

        public SaveMemory Save { get; private set; } = new(SaveType.None);

        public ProtocolMode Mode { get; set; } = ProtocolMode.Raw;

        /// <summary>Optional KEY1 support for real-BIOS boot.</summary>
        public Key1Decryptor? Key1 { get; set; }

        /// <summary>Which CPU owns the slot: 0 = ARM9, 1 = ARM7 (EXMEMCNT bit 11).</summary>
        public int Owner { get; set; }

        public uint ChipId
        {
            get
            {
                if (Rom == null) return 0xFFFFFFFFu;
                long megabytes = Rom.Header.Capacity >> 20;
                uint sizeByte = megabytes <= 128 ? (uint)Math.Max(0, megabytes - 1) : (uint)(0x100 - (megabytes >> 8));
                return 0xC2u | (sizeByte << 8);
            }
        }

        public void Insert(NdsRom rom, SaveMemory save)
        {
            Rom = rom;
            Save = save;
        }

        public void Reset()
        {
            Array.Clear(_command);
            _spiControl = 0;
            _spiData = 0;
            _romControl = 0;
            _transferLength = 0;
            _transferPosition = 0;
            Mode = ProtocolMode.Raw;
            _scheduler.Cancel(SchedulerEvent.CartridgeTransfer);
        }

        // ── AUX SPI ───────────────────────────────────────────────────────────

        public ushort ReadSpiControl() => _spiControl;

        public void WriteSpiControl(ushort value, ushort mask = 0xFFFF)
        {
            ushort next = (ushort)((_spiControl & ~mask) | (value & mask & 0xE043));
            // Leaving SPI mode, or dropping hold while idle, releases the chip select.
            if ((_spiControl & 0x2000) != 0 && (next & 0x2000) == 0)
                Save.Deselect();
            _spiControl = next;
        }

        public byte ReadSpiData() => _spiData;

        public void WriteSpiData(byte value)
        {
            if ((_spiControl & 0xA000) != 0xA000)
                return; // slot disabled or in ROM mode
            _spiData = Save.Transfer(value);
            if ((_spiControl & 0x0040) == 0)
                Save.Deselect();
        }

        // ── ROM protocol ──────────────────────────────────────────────────────

        public uint ReadRomControl() => _romControl;

        public byte ReadCommand(int index) => _command[index];

        public void WriteCommand(int index, byte value) => _command[index] = value;

        public void WriteRomControl(uint value, uint mask = 0xFFFFFFFFu)
        {
            bool wasBusy = (_romControl & 0x80000000u) != 0;
            uint writable = 0xFF7FFFFFu & ~0x00008000u; // bit 23 read-only, bit 15 write-only
            uint next = (_romControl & ~(mask & writable)) | (value & mask & writable);
            if ((_romControl & 0x20000000u) != 0)
                next |= 0x20000000u; // RESB cannot be cleared
            _romControl = next;

            if (!wasBusy && (next & 0x80000000u) != 0)
                StartTransfer();
        }

        private void StartTransfer()
        {
            int sizeCode = (int)(_romControl >> 24) & 7;
            _transferLength = sizeCode switch
            {
                0 => 0,
                7 => 4,
                _ => 0x100 << sizeCode,
            };
            _transferPosition = 0;
            if (_transfer.Length < _transferLength)
                _transfer = new byte[Math.Max(_transferLength, 0x4000)];

            byte[] command = _command;
            if (Mode == ProtocolMode.Key1 && Key1 != null)
                command = Key1.DecryptCommand(_command);

            FillTransfer(command);

            if (_transferLength == 0)
            {
                FinishTransfer();
                return;
            }

            _romControl &= ~0x00800000u;
            // ~8 system cycles per byte at 6.7 MHz; schedule the first word's arrival.
            _scheduler.ScheduleFromNow(SchedulerEvent.CartridgeTransfer, 64);
        }

        private void FillTransfer(byte[] command)
        {
            var span = _transfer.AsSpan(0, _transferLength);
            if (Rom == null)
            {
                span.Fill(0xFF);
                return;
            }

            switch (Mode)
            {
                case ProtocolMode.Raw:
                    switch (command[0])
                    {
                        case 0x00: // header, repeated every 1000h bytes
                            for (int i = 0; i < span.Length; i++)
                                span[i] = Rom.Data[i & 0xFFF];
                            break;
                        case 0x90:
                            FillChipId(span);
                            break;
                        case 0x3C:
                            Mode = ProtocolMode.Key1;
                            span.Fill(0xFF);
                            break;
                        default:
                            span.Fill(0xFF);
                            break;
                    }
                    break;

                case ProtocolMode.Key1:
                    switch (command[0] >> 4)
                    {
                        case 0x1: // chip ID
                            FillChipId(span);
                            break;
                        case 0x2: // secure area block: bits 59–44 of the command = address / 1000h
                        {
                            uint block = (uint)(((command[0] & 0xF) << 12) | (command[1] << 4) | (command[2] >> 4)) * 0x1000;
                            for (int i = 0; i < span.Length; i++)
                                span[i] = Rom.Data[(block + i) % Rom.Data.Length];
                            Key1?.EncryptSecureArea(span, block);
                            break;
                        }
                        case 0xA: // enter main data mode
                            Mode = ProtocolMode.Main;
                            span.Fill(0);
                            break;
                        default:
                            span.Fill(0);
                            break;
                    }
                    break;

                case ProtocolMode.Main:
                    switch (command[0])
                    {
                        case 0xB7:
                        {
                            uint address = (uint)(command[1] << 24 | command[2] << 16 | command[3] << 8 | command[4]);
                            address %= (uint)Math.Max(Rom.Data.Length, 1);
                            // The secure area can't be read in main data mode; it redirects to 8000h+.
                            if (address < 0x8000)
                                address = 0x8000 + (address & 0x1FF);
                            for (int i = 0; i < span.Length; i++)
                            {
                                // Retail ROM chips wrap reads at 1000h-byte boundaries.
                                uint a = (address & ~0xFFFu) | ((address + (uint)i) & 0xFFF);
                                span[i] = Rom.Data[a % (uint)Rom.Data.Length];
                            }
                            break;
                        }
                        case 0xB8:
                            FillChipId(span);
                            break;
                        default:
                            span.Fill(0xFF);
                            break;
                    }
                    break;
            }
        }

        private void FillChipId(Span<byte> span)
        {
            uint id = ChipId;
            for (int i = 0; i < span.Length; i++)
                span[i] = (byte)(id >> ((i & 3) * 8));
        }

        private void OnWordReady()
        {
            if (_transferPosition >= _transferLength)
                return;

            _dataLatch = BitConverter.ToUInt32(_transfer, _transferPosition);
            _romControl |= 0x00800000u;
            _dma[Owner].Trigger(DmaTiming.Cartridge);
        }

        /// <summary>Read the 32-bit data port (04100010h).</summary>
        public uint ReadData()
        {
            if ((_romControl & 0x00800000u) == 0)
                return _dataLatch;

            uint value = _dataLatch;
            _romControl &= ~0x00800000u;
            _transferPosition += 4;

            if (_transferPosition >= _transferLength)
                FinishTransfer();
            else
                _scheduler.ScheduleFromNow(SchedulerEvent.CartridgeTransfer, 16);
            return value;
        }

        private void FinishTransfer()
        {
            _romControl &= ~(0x80000000u | 0x00800000u);
            if ((_spiControl & 0x4000) != 0)
                _irq[Owner].Request(InterruptController.IRQ_CART_DONE);
        }

        /// <summary>Called when a cartridge DMA channel is enabled: deliver a word that's already waiting.</summary>
        public void OnDmaStarted(int cpu)
        {
            if (cpu == Owner && (_romControl & 0x00800000u) != 0)
                _dma[Owner].Trigger(DmaTiming.Cartridge);
        }
    }
}
