using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.Common.Cpu.Arm;
using Px86.NintendoDS.Common.Cpu.Thumb;

namespace Px86.NintendoDS.EmulatorCore.Jit
{
    /// <summary>How the CPUs run code.</summary>
    public enum CpuMode
    {
        /// <summary>Fetch and decode every instruction (simplest; always used while debugging).</summary>
        Interpreter,

        /// <summary>Decode straight-line runs of code once and reuse them (same results, faster).</summary>
        Cached,
    }

    /// <summary>
    /// A straight-line run of decoded instructions inside one 256-byte page.
    /// It ends after an unconditional branch, at the page end or after
    /// <see cref="BlockCache.MaxInstructions"/>.
    /// </summary>
    public sealed class CachedBlock
    {
        public uint Address;
        public bool Thumb;
        public int Count;
        public uint[] Opcodes = Array.Empty<uint>();
        public ArmHandler[]? ArmHandlers;
        public ThumbHandler[]? ThumbHandlers;

        /// <summary>Cleared when the memory it was built from is written.</summary>
        public bool Valid = true;
    }

    /// <summary>
    /// Decoded-block cache for one CPU (a "cached interpreter", the first step
    /// of a JIT). Executes exactly like <see cref="ArmCpu.Step"/>, instruction
    /// by instruction with the same timing, IRQ, halt and DMA checks between
    /// instructions — it only skips the repeated fetch and decode.
    /// </summary>
    public sealed class BlockCache
    {
        public const int MaxInstructions = 64;

        /// <summary>Locates an address in memory: the backing array and offset, or null when code there must not be cached.</summary>
        public delegate byte[]? Locator(uint address, out int index);

        private readonly ArmCpu _cpu;
        private readonly CodeTracker _tracker;
        private readonly Locator _locate;
        private readonly Dictionary<uint, CachedBlock?> _blocks = new();

        public BlockCache(ArmCpu cpu, CodeTracker tracker, Locator locate)
        {
            _cpu = cpu;
            _tracker = tracker;
            _locate = locate;
        }

        public int BlockCount => _blocks.Count;
        public long Compiles { get; private set; }

        public void Clear() => _blocks.Clear();

        /// <summary>The block starting at the CPU's PC, built if needed; null if that code can't be cached.</summary>
        public CachedBlock? Get()
        {
            uint pc = _cpu.Pc;
            bool thumb = _cpu.IsThumb;
            uint key = pc | (thumb ? 1u : 0u);
            if (_blocks.TryGetValue(key, out var block) && (block == null || block.Valid))
                return block;
            block = Compile(pc, thumb);
            _blocks[key] = block;
            return block;
        }

        private CachedBlock? Compile(uint pc, bool thumb)
        {
            var memory = _locate(pc, out int index);
            if (memory == null)
                return null;
            Compiles++;

            int size = thumb ? 2 : 4;
            int room = (CodeTracker.PageSize - (index & (CodeTracker.PageSize - 1))) / size;
            int max = Math.Min(MaxInstructions, room);
            var opcodes = new uint[max];
            var arm = thumb ? null : new ArmHandler[max];
            var thumbHandlers = thumb ? new ThumbHandler[max] : null;
            int count = 0;
            var bus = _cpu.Bus;
            for (uint address = pc; count < max; address += (uint)size)
            {
                if (thumb)
                {
                    ushort op = bus.Fetch16(address);
                    opcodes[count] = op;
                    thumbHandlers![count] = ThumbInstructionTable.Handlers[op >> 6];
                    count++;
                    if (EndsBlockThumb(op))
                        break;
                }
                else
                {
                    uint op = bus.Fetch32(address);
                    opcodes[count] = op;
                    arm![count] = ArmInstructionTable.Handlers[((op >> 16) & 0xFF0) | ((op >> 4) & 0xF)];
                    count++;
                    if (EndsBlockArm(op))
                        break;
                }
            }

            var block = new CachedBlock
            {
                Address = pc,
                Thumb = thumb,
                Count = count,
                Opcodes = opcodes,
                ArmHandlers = arm,
                ThumbHandlers = thumbHandlers,
            };
            _tracker.Register(memory, index, block);
            return block;
        }

        /// <summary>Unconditional control transfers; anything after them is never reached in sequence.</summary>
        private static bool EndsBlockArm(uint op)
        {
            uint cond = op >> 28;
            if (cond != 0xE)
                return cond == 0xF && (op & 0x0E000000) == 0x0A000000; // BLX imm
            if ((op & 0x0E000000) == 0x0A000000) return true;          // B / BL
            if ((op & 0x0FFFFFD0) == 0x012FFF10) return true;          // BX / BLX reg
            if ((op & 0x0F000000) == 0x0F000000) return true;          // SWI
            if ((op & 0x0E108000) == 0x08108000) return true;          // LDM with PC
            if ((op & 0x0C10F000) == 0x0410F000) return true;          // LDR PC
            if ((op & 0x0C00F000) == 0x0000F000 && (op & 0x01900000) != 0x01000000) return true; // ALU writing PC
            return false;
        }

        private static bool EndsBlockThumb(ushort op)
        {
            if ((op & 0xF800) == 0xE000) return true;  // B
            if ((op & 0xE800) == 0xE800) return true;  // BL/BLX suffix
            if ((op & 0xFF00) == 0x4700) return true;  // BX/BLX reg
            if ((op & 0xFF00) == 0xBD00) return true;  // POP {…, pc}
            if ((op & 0xFF00) == 0xDF00) return true;  // SWI
            if ((op & 0xFF87) == 0x4687) return true;  // MOV pc, rX
            return false;
        }
    }
}
