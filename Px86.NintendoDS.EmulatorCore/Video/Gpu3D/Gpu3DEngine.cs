using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Video.Gpu3D
{
    /// <summary>
    /// The 3D engine's register window (ARM9 04000060h DISP3DCNT and 04000320h–040006A3h).
    /// Geometry and rendering are not implemented yet: registers are stored and
    /// GXSTAT reports an empty, idle FIFO so software doesn't stall.
    /// </summary>
    public sealed class Gpu3DEngine
    {
        public const uint REG_DISP3DCNT = 0x060;
        public const uint REG_GXFIFO    = 0x400;
        public const uint REG_GXSTAT    = 0x600;

        private readonly byte[] _registers = new byte[0x400];

        public ushort Disp3DControl { get; private set; }

        public void Reset()
        {
            Array.Clear(_registers);
            Disp3DControl = 0;
        }

        public ushort ReadDisp3DControl() => Disp3DControl;

        public void WriteDisp3DControl(ushort value, ushort mask = 0xFFFF) =>
            Disp3DControl = (ushort)((Disp3DControl & ~mask) | (value & mask));

        public static bool Contains(uint offset) => offset >= 0x320 && offset < 0x6A4;

        public uint Read32(uint offset)
        {
            if (offset == REG_GXSTAT)
                return 0x06000002u; // FIFO empty + less than half full, geometry idle
            return MemoryAccess.Read32(_registers, (int)(offset - 0x300) & 0x3FC);
        }

        public void Write32(uint offset, uint value) =>
            MemoryAccess.Write32(_registers, (int)(offset - 0x300) & 0x3FC, value);

        /// <summary>Line of 3D output for engine A's BG0 (null until the renderer exists).</summary>
        public ushort[]? GetLine(int line) => null;
    }
}
