using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.Common.Interfaces;

namespace Px86.NintendoDS.TestProject.Cpu
{
    /// <summary>A flat 64 KB little-endian RAM mirrored across the address space.</summary>
    internal sealed class FlatMemoryBus : IArmMemoryBus
    {
        public const int Size = 0x10000;
        public readonly byte[] Memory = new byte[Size];

        private static int Index(uint address) => (int)(address & (Size - 1));

        public byte Read8(uint address) => Memory[Index(address)];
        public ushort Read16(uint address) => BitConverter.ToUInt16(Memory, Index(address & ~1u));
        public uint Read32(uint address) => BitConverter.ToUInt32(Memory, Index(address & ~3u));
        public void Write8(uint address, byte value) => Memory[Index(address)] = value;
        public void Write16(uint address, ushort value) => BitConverter.TryWriteBytes(Memory.AsSpan(Index(address & ~1u)), value);
        public void Write32(uint address, uint value) => BitConverter.TryWriteBytes(Memory.AsSpan(Index(address & ~3u)), value);
        public uint Fetch32(uint address) => Read32(address);
        public ushort Fetch16(uint address) => Read16(address);
    }

    /// <summary>
    /// Builds a CPU on a <see cref="FlatMemoryBus"/> with a program at address 0.
    /// Opcodes in the tests were produced with <c>llvm-mc -triple=armv5te|thumbv5te -show-encoding</c>.
    /// </summary>
    internal static class CpuTestHarness
    {
        public static ArmCpu CreateArm(ArmArchitecture architecture, params uint[] program)
        {
            var bus = new FlatMemoryBus();
            for (int i = 0; i < program.Length; i++)
                bus.Write32((uint)(i * 4), program[i]);
            var cpu = new ArmCpu(architecture, bus);
            cpu.SetPc(0);
            return cpu;
        }

        public static ArmCpu CreateThumb(ArmArchitecture architecture, params ushort[] program)
        {
            var bus = new FlatMemoryBus();
            for (int i = 0; i < program.Length; i++)
                bus.Write16((uint)(i * 2), program[i]);
            var cpu = new ArmCpu(architecture, bus);
            cpu.SetPc(1);
            return cpu;
        }

        public static void Run(ArmCpu cpu, int instructions)
        {
            for (int i = 0; i < instructions; i++)
                cpu.Step();
        }

        public static FlatMemoryBus Memory(ArmCpu cpu) => (FlatMemoryBus)cpu.Bus;
    }
}
