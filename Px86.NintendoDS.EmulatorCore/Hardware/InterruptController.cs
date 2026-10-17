using Px86.NintendoDS.Common.Cpu;

namespace Px86.NintendoDS.EmulatorCore.Hardware
{
    /// <summary>
    /// Per-CPU interrupt controller (one instance for the ARM9, one for the ARM7).
    ///
    ///   04000208h  IME  bit 0: master enable
    ///   04000210h  IE   32-bit enable mask
    ///   04000214h  IF   32-bit request flags (write 1 to acknowledge)
    ///
    /// The CPU's IRQ line is IME &amp;&amp; (IE &amp; IF) != 0. A halted CPU wakes up
    /// when (IE &amp; IF) != 0 regardless of IME and CPSR.I.
    ///
    /// Bit assignments:
    ///   0 VBlank  1 HBlank  2 VCount  3–6 Timer 0–3  7 RTC (ARM7)  8–11 DMA 0–3
    ///   12 Keypad  13 GBA slot  16 IPC sync  17 IPC send FIFO empty
    ///   18 IPC recv FIFO not empty  19 Cart transfer done  20 Cart IREQ_MC
    ///   21 Geometry FIFO (ARM9)  22 Hinge open (ARM7)  23 SPI (ARM7)  24 Wifi (ARM7)
    ///
    /// Reference: GBATEK "DS Interrupts".
    /// </summary>
    public sealed class InterruptController
    {
        public const uint REG_IME = 0x208;
        public const uint REG_IE  = 0x210;
        public const uint REG_IF  = 0x214;

        public const int IRQ_VBLANK         = 0;
        public const int IRQ_HBLANK         = 1;
        public const int IRQ_VCOUNT         = 2;
        public const int IRQ_TIMER0         = 3;
        public const int IRQ_RTC            = 7;
        public const int IRQ_DMA0           = 8;
        public const int IRQ_KEYPAD         = 12;
        public const int IRQ_GBA_SLOT       = 13;
        public const int IRQ_IPC_SYNC       = 16;
        public const int IRQ_IPC_SEND_EMPTY = 17;
        public const int IRQ_IPC_RECV       = 18;
        public const int IRQ_CART_DONE      = 19;
        public const int IRQ_CART_IREQ      = 20;
        public const int IRQ_GXFIFO         = 21;
        public const int IRQ_HINGE          = 22;
        public const int IRQ_SPI            = 23;
        public const int IRQ_WIFI           = 24;

        private readonly ArmCpu _cpu;
        private readonly uint _ieMask;

        public InterruptController(ArmCpu cpu, bool isArm9)
        {
            _cpu = cpu;
            _ieMask = isArm9 ? 0x003F3F7Fu : 0x01DF3FFFu;
        }

        public uint Ime { get; private set; }
        public uint Ie { get; private set; }
        public uint If { get; private set; }

        public void Reset()
        {
            Ime = Ie = If = 0;
            Update();
        }

        public void Request(int bit)
        {
            If |= 1u << bit;
            Update();
        }

        public void WriteIme(uint value)
        {
            Ime = value & 1;
            Update();
        }

        public void WriteIe(uint value, uint mask = 0xFFFFFFFFu)
        {
            Ie = (Ie & ~mask) | (value & mask & _ieMask);
            Update();
        }

        /// <summary>Write-1-to-clear.</summary>
        public void AcknowledgeIf(uint value)
        {
            If &= ~value;
            Update();
        }

        private void Update()
        {
            bool pending = (Ie & If) != 0;
            if (pending)
                _cpu.Halted = false;
            _cpu.IrqLine = pending && Ime != 0;
        }
    }
}
