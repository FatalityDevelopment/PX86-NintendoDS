using Px86.NintendoDS.Common.Interfaces;

namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>
    /// Memory access for debuggers and cheat tools, as seen by one core (the
    /// ARM9 view includes its TCMs). Accesses bypass watchpoints.
    ///
    /// Writes behave like CPU writes, I/O side effects included. Reads skip
    /// the two registers whose reads consume data — IPCFIFORECV (04100000h)
    /// and the cartridge data port (04100010h) — and return 0 there instead,
    /// so a memory viewer can't steal a FIFO word or a ROM word from the game.
    /// </summary>
    public static class DebugMemory
    {
        public static IArmMemoryBus Bus(NdsEmulator nds, DebugCpu cpu) => cpu == DebugCpu.Arm9 ? nds.Arm9Bus : nds.Arm7Bus;

        private static bool ReadHasSideEffects(uint address)
        {
            uint word = address & ~3u;
            return word == 0x04100000u || word == 0x04100010u;
        }

        public static byte Read8(NdsEmulator nds, DebugCpu cpu, uint address) =>
            ReadHasSideEffects(address) ? (byte)0 : Bus(nds, cpu).Read8(address);

        public static ushort Read16(NdsEmulator nds, DebugCpu cpu, uint address) =>
            ReadHasSideEffects(address) ? (ushort)0 : Bus(nds, cpu).Read16(address);

        public static uint Read32(NdsEmulator nds, DebugCpu cpu, uint address) =>
            ReadHasSideEffects(address) ? 0 : Bus(nds, cpu).Read32(address);

        public static void Write8(NdsEmulator nds, DebugCpu cpu, uint address, byte value) => Bus(nds, cpu).Write8(address, value);

        public static void Write16(NdsEmulator nds, DebugCpu cpu, uint address, ushort value) => Bus(nds, cpu).Write16(address, value);

        public static void Write32(NdsEmulator nds, DebugCpu cpu, uint address, uint value) => Bus(nds, cpu).Write32(address, value);

        /// <summary>
        /// Read a block. Aligned words are read 32 bits at a time (main RAM
        /// and VRAM ignore byte writes, but byte reads are fine either way).
        /// </summary>
        public static void ReadBlock(NdsEmulator nds, DebugCpu cpu, uint address, Span<byte> destination)
        {
            int i = 0;
            while (i < destination.Length)
            {
                uint a = address + (uint)i;
                if ((a & 3) == 0 && destination.Length - i >= 4)
                {
                    uint v = Read32(nds, cpu, a);
                    destination[i] = (byte)v;
                    destination[i + 1] = (byte)(v >> 8);
                    destination[i + 2] = (byte)(v >> 16);
                    destination[i + 3] = (byte)(v >> 24);
                    i += 4;
                }
                else
                {
                    destination[i++] = Read8(nds, cpu, a);
                }
            }
        }

        /// <summary>
        /// Write a block, using 32-bit writes for aligned words and 16-bit
        /// writes for aligned halfwords, so palette, VRAM and OAM (which
        /// ignore 8-bit writes) can be patched too.
        /// </summary>
        public static void WriteBlock(NdsEmulator nds, DebugCpu cpu, uint address, ReadOnlySpan<byte> source)
        {
            int i = 0;
            while (i < source.Length)
            {
                uint a = address + (uint)i;
                int left = source.Length - i;
                if ((a & 3) == 0 && left >= 4)
                {
                    Write32(nds, cpu, a, (uint)(source[i] | (source[i + 1] << 8) | (source[i + 2] << 16) | (source[i + 3] << 24)));
                    i += 4;
                }
                else if ((a & 1) == 0 && left >= 2)
                {
                    Write16(nds, cpu, a, (ushort)(source[i] | (source[i + 1] << 8)));
                    i += 2;
                }
                else
                {
                    Write8(nds, cpu, a, source[i++]);
                }
            }
        }
    }
}
