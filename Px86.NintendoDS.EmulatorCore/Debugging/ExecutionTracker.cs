using Px86.NintendoDS.Common.Cpu;

namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>
    /// One call on the shadow call stack. <see cref="CallSite"/> is the instruction that made the
    /// call (for a Thumb BL pair, its second half); <see cref="Target"/> has bit 0 set for Thumb code.
    /// </summary>
    public readonly record struct CallFrame(uint CallSite, uint Target, uint ReturnAddress, uint StackPointer, bool Thumb, bool Interrupt);

    /// <summary>
    /// Watches the CPUs run, for code analysis tools (CodeDesigner): what executed, the real
    /// call stack, and how often each function was called. Off by default; while it is on
    /// the CPUs are interpreted (like with a debugger attached) so every instruction is seen.
    ///
    ///   Coverage     one bit per halfword that started an instruction, per CPU, in 4 KB pages
    ///   Call stack   a shadow stack per CPU: a call is an instruction that set LR to the
    ///                address after itself and jumped (BL, BLX, the second half of a Thumb BL
    ///                pair); an exception entry (IRQ, SWI…) is an interrupt frame. A frame is
    ///                popped when the PC reaches its return address, when the stack pointer
    ///                rises above the frame's (longjmp, a pop that skipped the return), or for
    ///                an interrupt frame when the CPU is back in the mode it came from.
    ///   Call counts  calls per target address
    ///
    /// Reads from other threads go through <c>EmulatorHost.Invoke</c>, like all debugger state.
    /// </summary>
    public sealed class ExecutionTracker
    {
        public const int MaxDepth = 256;
        private const int PageShift = 12;
        private const int WordsPerPage = (1 << PageShift) / 2 / 64; // halfwords per page / 64

        private readonly Dictionary<uint, ulong[]>[] _coverage = { new(), new() };
        private readonly List<(CallFrame Frame, ProcessorMode CallerMode)>[] _stacks = { new(), new() };
        private readonly Dictionary<uint, long>[] _calls = { new(), new() };
        private readonly uint[] _pc = new uint[2];
        private readonly ProcessorMode[] _mode = new ProcessorMode[2];
        private readonly bool[] _thumb = new bool[2];

        /// <summary>Tracking on (makes the emulator interpret instead of running cached blocks).</summary>
        public bool Enabled { get; set; }

        public void Clear()
        {
            for (int i = 0; i < 2; i++)
            {
                _coverage[i].Clear();
                _stacks[i].Clear();
                _calls[i].Clear();
            }
        }

        // ── Run loop ──────────────────────────────────────────────────────────

        /// <summary>Before the CPU executes the instruction at its PC.</summary>
        public void Before(int cpu, ArmCpu core)
        {
            uint pc = core.Pc;
            _pc[cpu] = pc;
            _mode[cpu] = core.Mode;
            _thumb[cpu] = core.IsThumb;
            Mark(cpu, pc);
        }

        /// <summary>After the instruction (or an exception entry) ran.</summary>
        public void After(int cpu, ArmCpu core)
        {
            uint pc = _pc[cpu], now = core.Pc, lr = core.R[14];
            var stack = _stacks[cpu];
            bool thumb = _thumb[cpu];

            if (core.Mode != _mode[cpu] && IsExceptionEntry(now))
            {
                // Exception entry: LR is the banked one and points after the interrupted code.
                Push(cpu, new CallFrame(pc, now, lr, core.R[13], thumb, Interrupt: true), _mode[cpu]);
                return;
            }

            uint size = thumb ? 2u : 4u;
            bool jumped = now != pc + size;
            uint link = thumb ? (pc + 2) | 1 : pc + 4;
            // LR may already hold this value (recursion from the same call site), so only its final value counts.
            if (jumped && lr == link)
            {
                Push(cpu, new CallFrame(pc, now | (core.IsThumb ? 1u : 0), lr & ~1u, core.R[13], thumb, Interrupt: false), core.Mode);
                return;
            }

            // Returns: pop every frame we have come back out of.
            while (stack.Count > 0)
            {
                var (frame, callerMode) = stack[^1];
                bool returned = frame.Interrupt
                    ? core.Mode == callerMode
                    : now == frame.ReturnAddress || core.Mode == callerMode && core.R[13] > frame.StackPointer;
                if (!returned) break;
                stack.RemoveAt(stack.Count - 1);
            }
        }

        private static bool IsExceptionEntry(uint pc) => pc < 0x20 || pc >= 0xFFFF0000 && pc < 0xFFFF0020;

        private void Push(int cpu, CallFrame frame, ProcessorMode callerMode)
        {
            var stack = _stacks[cpu];
            if (stack.Count == MaxDepth)
                stack.RemoveAt(0); // keep the innermost calls
            stack.Add((frame, callerMode));
            if (!frame.Interrupt)
            {
                uint target = frame.Target & ~1u;
                _calls[cpu][target] = _calls[cpu].GetValueOrDefault(target) + 1;
            }
        }

        private void Mark(int cpu, uint address)
        {
            uint page = address >> PageShift;
            if (!_coverage[cpu].TryGetValue(page, out var bits))
                _coverage[cpu][page] = bits = new ulong[WordsPerPage];
            int index = (int)((address & ((1u << PageShift) - 1)) >> 1);
            bits[index >> 6] |= 1UL << (index & 63);
        }

        // ── Queries ───────────────────────────────────────────────────────────

        /// <summary>Was an instruction started at this address?</summary>
        public bool Executed(DebugCpu cpu, uint address) =>
            _coverage[(int)cpu].TryGetValue(address >> PageShift, out var bits)
            && (bits[(int)((address & ((1u << PageShift) - 1)) >> 1) >> 6] & (1UL << (int)((address >> 1) & 63))) != 0;

        /// <summary>Coverage of [start, start + length): one bit per halfword, LSB first (bit i = start + 2i).</summary>
        public byte[] Coverage(DebugCpu cpu, uint start, int length)
        {
            start &= ~1u;
            int halves = (length + 1) / 2;
            var result = new byte[(halves + 7) / 8];
            for (int i = 0; i < halves; i++)
                if (Executed(cpu, start + (uint)i * 2))
                    result[i >> 3] |= (byte)(1 << (i & 7));
            return result;
        }

        /// <summary>Executed halfword count per 4 KB page (what ran, at a glance).</summary>
        public IReadOnlyList<(uint Page, int Halfwords)> Pages(DebugCpu cpu) =>
            _coverage[(int)cpu].OrderBy(p => p.Key)
                .Select(p => (p.Key << PageShift, p.Value.Sum(w => System.Numerics.BitOperations.PopCount(w))))
                .ToList();

        /// <summary>Innermost call first.</summary>
        public IReadOnlyList<CallFrame> CallStack(DebugCpu cpu) =>
            _stacks[(int)cpu].Select(s => s.Frame).Reverse().ToList();

        /// <summary>The most called targets, most first.</summary>
        public IReadOnlyList<(uint Target, long Calls)> CallCounts(DebugCpu cpu, int max = 100) =>
            _calls[(int)cpu].OrderByDescending(c => c.Value).ThenBy(c => c.Key).Take(max).Select(c => (c.Key, c.Value)).ToList();
    }
}
