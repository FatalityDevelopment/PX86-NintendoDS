namespace Px86.NintendoDS.Common.Interfaces
{
    /// <summary>
    /// The memory bus seen by one ARM core.
    ///
    /// The Nintendo DS has two independent buses — one for the ARM946E-S (ARM9)
    /// and one for the ARM7TDMI (ARM7) — that share main RAM, shared WRAM and
    /// some VRAM banks but otherwise map different hardware at the same
    /// addresses. Each core is handed its own bus implementation.
    ///
    /// Addresses passed in are the raw 32-bit virtual addresses issued by the
    /// CPU. Implementations are responsible for alignment (the DS forces
    /// 16-bit accesses to 2-byte boundaries and 32-bit accesses to 4-byte
    /// boundaries; the CPU applies any ARM-specific rotation on misaligned
    /// loads itself).
    /// </summary>
    public interface IArmMemoryBus
    {
        byte Read8(uint address);
        ushort Read16(uint address);
        uint Read32(uint address);

        void Write8(uint address, byte value);
        void Write16(uint address, ushort value);
        void Write32(uint address, uint value);

        /// <summary>
        /// Opcode fetch. Separate from <see cref="Read32"/> so the ARM9 bus can
        /// route fetches through ITCM only (DTCM is data-only) and so buses can
        /// skip side effects reserved for data reads.
        /// </summary>
        uint Fetch32(uint address);

        /// <summary>Thumb opcode fetch. See <see cref="Fetch32"/>.</summary>
        ushort Fetch16(uint address);
    }
}
