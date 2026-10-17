using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware
{
    /// <summary>Buttons on the DS, as a host-side bit set.</summary>
    [Flags]
    public enum NdsButton
    {
        None   = 0,
        A      = 1 << 0,
        B      = 1 << 1,
        Select = 1 << 2,
        Start  = 1 << 3,
        Right  = 1 << 4,
        Left   = 1 << 5,
        Up     = 1 << 6,
        Down   = 1 << 7,
        R      = 1 << 8,
        L      = 1 << 9,
        X      = 1 << 10,
        Y      = 1 << 11,
        Debug  = 1 << 12,
    }

    /// <summary>
    /// Button, pen and hinge inputs.
    ///
    ///   04000130h  KEYINPUT  (both CPUs) bits 0–9: A B Select Start Right Left Up Down R L; 0 = pressed
    ///   04000132h  KEYCNT    (both CPUs) bits 0–9 IRQ mask, 14 IRQ enable, 15 condition (0 = OR, 1 = AND)
    ///   04000136h  EXTKEYIN  (ARM7)      bit 0 X, 1 Y, 3 debug, 6 pen down, 7 hinge closed; 0 = pressed
    ///                                    bits 2, 4, 5 read as 1
    ///
    /// Reference: GBATEK "DS Keypad" / "GBA Keypad Input".
    /// </summary>
    public sealed class Keypad
    {
        public const uint REG_KEYINPUT = 0x130;
        public const uint REG_KEYCNT   = 0x132;
        public const uint REG_EXTKEYIN = 0x136;

        private NdsButton _pressed;
        private readonly ushort[] _keyControl = new ushort[2];
        private readonly InterruptController[] _irq;

        public Keypad(InterruptController arm9Irq, InterruptController arm7Irq)
        {
            _irq = new[] { arm9Irq, arm7Irq };
        }

        public bool PenDown { get; private set; }

        public bool HingeClosed { get; set; }

        public void Reset()
        {
            _pressed = NdsButton.None;
            PenDown = false;
            HingeClosed = false;
            Array.Clear(_keyControl);
        }

        public ushort KeyInput => (ushort)(~(int)_pressed & 0x3FF);

        public ushort ExtKeyIn
        {
            get
            {
                int value = 0x34;
                if ((_pressed & NdsButton.X) == 0) value |= 0x01;
                if ((_pressed & NdsButton.Y) == 0) value |= 0x02;
                if ((_pressed & NdsButton.Debug) == 0) value |= 0x08;
                if (!PenDown) value |= 0x40;
                if (HingeClosed) value |= 0x80;
                return (ushort)value;
            }
        }

        public ushort ReadKeyControl(int side) => _keyControl[side];

        public void WriteKeyControl(int side, ushort value, ushort mask = 0xFFFF)
        {
            _keyControl[side] = (ushort)((_keyControl[side] & ~mask) | (value & mask & 0xC3FF));
            CheckIrq();
        }

        public void SetButtons(NdsButton pressed)
        {
            _pressed = pressed;
            CheckIrq();
        }

        public void SetPen(bool down) => PenDown = down;

        private void CheckIrq()
        {
            int pressed = (int)_pressed & 0x3FF;
            for (int side = 0; side < 2; side++)
            {
                ushort control = _keyControl[side];
                if ((control & 0x4000) == 0)
                    continue;
                int mask = control & 0x3FF;
                bool fire = (control & 0x8000) != 0 ? (pressed & mask) == mask && mask != 0 : (pressed & mask) != 0;
                if (fire)
                    _irq[side].Request(InterruptController.IRQ_KEYPAD);
            }
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("Keypad");
            s.Sync(_keyControl);
            bool hinge = HingeClosed;
            s.Sync(ref hinge);
            HingeClosed = hinge;
        }
    }
}
