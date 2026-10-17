namespace Px86.NintendoDS.EmulatorCore.Hardware
{
    /// <summary>
    /// Miscellaneous system control registers.
    ///
    ///   ARM9 04000204h  EXMEMCNT  0–6 GBA slot timing, 7 GBA slot owner (1 = ARM7),
    ///                             11 NDS slot owner (1 = ARM7), 14 main RAM mode, 15 main RAM priority
    ///   ARM7 04000204h  EXMEMSTAT bits 0–6 own, bits 7–15 mirror EXMEMCNT
    ///   ARM9 04000247h  WRAMCNT   shared WRAM split (see <see cref="WramMode"/>)
    ///   ARM7 04000241h  WRAMSTAT  read-only mirror of WRAMCNT
    ///   ARM9 04000304h  POWCNT1   0 LCDs, 1 2D engine A, 2 3D render, 3 3D geometry,
    ///                             9 2D engine B, 15 display swap (1 = engine A on the top screen)
    ///   ARM7 04000304h  POWCNT2   0 sound, 1 wifi
    ///   both 04000300h  POSTFLG   bit 0 boot done (ARM9 also bit 1)
    ///   ARM7 04000301h  HALTCNT   bits 6–7: 2 = halt, 3 = sleep
    ///
    /// Reference: GBATEK "DS Memory Control - Cartridges and Main RAM",
    ///            "DS Memory Control - WRAM", "DS Power Control".
    /// </summary>
    public sealed class SystemControl
    {
        public const uint REG_EXMEMCNT = 0x204;
        public const uint REG_WRAMCNT  = 0x247;
        public const uint REG_WRAMSTAT = 0x241;
        public const uint REG_VRAMSTAT = 0x240;
        public const uint REG_POSTFLG  = 0x300;
        public const uint REG_HALTCNT  = 0x301;
        public const uint REG_POWCNT   = 0x304;
        public const uint REG_BIOSPROT = 0x308;

        public ushort ExMemControl { get; private set; } = 0x6000;
        public ushort ExMemStatus7 { get; private set; }
        public byte WramMode { get; private set; }
        public ushort PowerControl9 { get; private set; }
        public ushort PowerControl7 { get; private set; }
        public byte PostFlag9 { get; set; }
        public byte PostFlag7 { get; set; }

        /// <summary>Raised when WRAMCNT changes (buses rebuild their WRAM views).</summary>
        public event Action? WramMappingChanged;

        /// <summary>Raised when EXMEMCNT changes (cartridge ownership).</summary>
        public event Action? ExMemChanged;

        public bool DisplaySwap => (PowerControl9 & 0x8000) != 0;
        public bool EngineAEnabled => (PowerControl9 & 0x0002) != 0;
        public bool EngineBEnabled => (PowerControl9 & 0x0200) != 0;
        public bool LcdEnabled => (PowerControl9 & 0x0001) != 0;
        public bool SoundEnabled => (PowerControl7 & 0x0001) != 0;

        /// <summary>NDS slot owner: 0 = ARM9, 1 = ARM7.</summary>
        public int NdsSlotOwner => (ExMemControl >> 11) & 1;

        public void Reset()
        {
            ExMemControl = 0x6000;
            ExMemStatus7 = 0;
            WramMode = 0;
            PowerControl9 = 0;
            PowerControl7 = 0;
            PostFlag9 = PostFlag7 = 0;
        }

        public void WriteExMemControl(ushort value, ushort mask = 0xFFFF)
        {
            ExMemControl = (ushort)(((ExMemControl & ~mask) | (value & mask) & 0xE8FF) | 0x6000);
            ExMemChanged?.Invoke();
        }

        public ushort ReadExMemStatus7() => (ushort)((ExMemControl & 0xFF80) | (ExMemStatus7 & 0x7F));

        public void WriteExMemStatus7(ushort value, ushort mask = 0xFFFF) =>
            ExMemStatus7 = (ushort)((ExMemStatus7 & ~mask) | (value & mask & 0x7F));

        public void WriteWramMode(byte value)
        {
            byte mode = (byte)(value & 3);
            if (mode == WramMode)
                return;
            WramMode = mode;
            WramMappingChanged?.Invoke();
        }

        public void WritePowerControl9(ushort value, ushort mask = 0xFFFF) =>
            PowerControl9 = (ushort)((PowerControl9 & ~mask) | (value & mask & 0x820F));

        public void WritePowerControl7(ushort value, ushort mask = 0xFFFF) =>
            PowerControl7 = (ushort)((PowerControl7 & ~mask) | (value & mask & 0x0003));
    }
}
