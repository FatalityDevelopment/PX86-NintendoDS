namespace Px86.NintendoDS.Common.Cpu
{
    /// <summary>
    /// ARM processor modes as encoded in CPSR bits 4–0.
    /// Reference: GBATEK "ARM CPU Flags &amp; Condition Field (cond)".
    /// </summary>
    public enum ProcessorMode : uint
    {
        User       = 0x10,
        Fiq        = 0x11,
        Irq        = 0x12,
        Supervisor = 0x13,
        Abort      = 0x17,
        Undefined  = 0x1B,
        System     = 0x1F,
    }

    /// <summary>
    /// Which ARM architecture revision a core implements.
    ///
    ///   ARMv4T  — ARM7TDMI (DS ARM7, 33.51 MHz)
    ///   ARMv5TE — ARM946E-S (DS ARM9, 67.03 MHz): adds BLX, CLZ, BKPT,
    ///             saturating QADD/QSUB, DSP multiplies (SMLAxy etc.), LDRD/STRD,
    ///             PLD, and LDR/LDM/POP-to-PC interworking.
    /// </summary>
    public enum ArmArchitecture
    {
        ARMv4T,
        ARMv5TE,
    }
}
