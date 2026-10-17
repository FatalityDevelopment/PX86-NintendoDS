using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Spi
{
    /// <summary>
    /// ARM7 SPI bus controller.
    ///
    ///   040001C0h  SPICNT   bits 0–1 baud, 7 busy, 8–9 device (0 power, 1 firmware, 2 touch),
    ///                       10 16-bit mode (bugged), 11 chip-select hold, 14 IRQ enable, 15 enable
    ///   040001C2h  SPIDATA  write starts an 8-bit exchange, read returns the received byte
    ///
    /// Transfers complete instantly; the busy bit always reads 0. When the hold
    /// bit is clear at the end of a transfer, the device is deselected.
    ///
    /// Reference: GBATEK "DS Serial Peripheral Interface Bus (SPI)".
    /// </summary>
    public sealed class SpiBus
    {
        public const uint REG_SPICNT  = 0x1C0;
        public const uint REG_SPIDATA = 0x1C2;

        private readonly InterruptController _irq;
        private readonly ISpiDevice?[] _devices = new ISpiDevice?[4];
        private ushort _control;
        private byte _data;
        private int _selected = -1;

        public SpiBus(InterruptController arm7Irq, PowerManager power, FirmwareFlash firmware, TouchScreen touch)
        {
            _irq = arm7Irq;
            Power = power;
            Firmware = firmware;
            Touch = touch;
            _devices[0] = power;
            _devices[1] = firmware;
            _devices[2] = touch;
        }

        public PowerManager Power { get; }
        public FirmwareFlash Firmware { get; private set; }
        public TouchScreen Touch { get; }

        public void ReplaceFirmware(FirmwareFlash firmware)
        {
            Firmware = firmware;
            _devices[1] = firmware;
        }

        public void Reset()
        {
            _control = 0;
            _data = 0;
            _selected = -1;
            Power.Reset();
        }

        public ushort ReadControl() => _control;

        public void WriteControl(ushort value, ushort mask = 0xFFFF)
        {
            ushort next = (ushort)((_control & ~mask) | (value & mask & 0xCF03));
            int device = (next >> 8) & 3;

            // Disabling the bus or switching devices drops the chip select.
            if (_selected >= 0 && ((next & 0x8000) == 0 || device != _selected))
            {
                _devices[_selected]?.Deselect();
                _selected = -1;
            }
            _control = next;
        }

        public byte ReadData() => _data;

        public void WriteData(byte value)
        {
            if ((_control & 0x8000) == 0)
                return;

            int device = (_control >> 8) & 3;
            _selected = device;
            _data = _devices[device]?.Transfer(value) ?? 0;

            if ((_control & 0x0800) == 0)
            {
                _devices[device]?.Deselect();
                _selected = -1;
            }

            if ((_control & 0x4000) != 0)
                _irq.Request(InterruptController.IRQ_SPI);
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("Spi");
            s.Sync(ref _control);
            s.Sync(ref _data);
            s.Sync(ref _selected);
            Firmware.Serialize(s);
            Touch.Serialize(s);
            Power.Serialize(s);
        }
    }
}
