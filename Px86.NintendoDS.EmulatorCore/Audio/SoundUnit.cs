using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Audio
{
    /// <summary>
    /// ARM7 sound registers (04000400h–0400051Fh). Mixing is not implemented
    /// yet; registers are stored and channel busy bits clear immediately.
    /// </summary>
    public sealed class SoundUnit
    {
        public const uint REG_BASE = 0x400;
        public const uint REG_END  = 0x520;

        private readonly byte[] _registers = new byte[0x120];

        public void Reset() => Array.Clear(_registers);

        public static bool Contains(uint offset) => offset >= REG_BASE && offset < REG_END;

        public uint Read32(uint offset)
        {
            uint value = MemoryAccess.Read32(_registers, (int)(offset - REG_BASE) & ~3);
            if (offset < 0x500 && (offset & 0xF) == 0)
                value &= 0x7FFFFFFFu; // channel never busy
            return value;
        }

        public void Write32(uint offset, uint value) =>
            MemoryAccess.Write32(_registers, (int)(offset - REG_BASE) & ~3, value);
    }
}
