using System.Runtime.CompilerServices;

namespace Px86.NintendoDS.Common.Core
{
    /// <summary>
    /// Little-endian loads and stores on byte arrays.
    ///
    /// The DS is little-endian like every host this emulator targets (x86-64,
    /// ARM64), so these are plain unaligned reads. Callers pass an index that is
    /// already masked into the array; there is no bounds check beyond the CLR's.
    /// </summary>
    public static class MemoryAccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Read16(byte[] memory, int index) =>
            Unsafe.ReadUnaligned<ushort>(ref memory[index]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Read32(byte[] memory, int index) =>
            Unsafe.ReadUnaligned<uint>(ref memory[index]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write16(byte[] memory, int index, ushort value) =>
            Unsafe.WriteUnaligned(ref memory[index], value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write32(byte[] memory, int index, uint value) =>
            Unsafe.WriteUnaligned(ref memory[index], value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Read64(byte[] memory, int index) =>
            Unsafe.ReadUnaligned<ulong>(ref memory[index]);
    }
}
