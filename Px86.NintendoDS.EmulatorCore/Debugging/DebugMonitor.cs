namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>Which core a debugger request is about. The numbers are the ones used on the PINE wire.</summary>
    public enum DebugCpu
    {
        Arm9 = 0,
        Arm7 = 1,
    }

    public enum BreakReason
    {
        Breakpoint,
        Watchpoint,
        Step,
    }

    /// <summary>
    /// Why execution stopped. <see cref="Pc"/> is the instruction about to run
    /// (breakpoint) or the instruction that made the access (watchpoint).
    /// </summary>
    public readonly record struct BreakInfo(DebugCpu Cpu, uint Pc, BreakReason Reason, uint Address = 0, bool IsWrite = false);

    /// <summary>Watch condition bits, as on the PINE wire.</summary>
    [Flags]
    public enum WatchCondition : byte
    {
        Read = 0x01,
        Write = 0x02,
        ReadWrite = Read | Write,
    }

    /// <summary>A watched address range: <see cref="Start"/> inclusive, <see cref="End"/> exclusive.</summary>
    public readonly record struct Watchpoint(DebugCpu Cpu, uint Start, uint End, WatchCondition Condition)
    {
        public bool Overlaps(uint address, int size) => address < End && address + (uint)size > Start;
    }

    /// <summary>
    /// Breakpoints and watchpoints for both cores.
    ///
    /// <see cref="NdsEmulator"/> asks <see cref="CheckBreakpoint"/> before every
    /// instruction while a monitor is attached, and stops the run as soon as
    /// <see cref="PendingBreak"/> is set. Watchpoints are checked by
    /// <see cref="WatchingMemoryBus"/>, which is only put in front of a core
    /// while it has watches, so an idle debugger costs one null check per
    /// instruction.
    ///
    /// Not thread-safe: <see cref="Hosting.EmulatorHost"/> serialises every
    /// access with its lock.
    /// </summary>
    public sealed class DebugMonitor
    {
        private const uint NoSkip = 0xFFFFFFFFu;

        private readonly HashSet<uint>[] _breakpoints = { new(), new() };
        private readonly HashSet<uint>[] _temporary = { new(), new() };
        private readonly List<Watchpoint> _watches = new();
        private readonly uint[] _skipOnce = { NoSkip, NoSkip };

        /// <summary>Raised when watchpoints are added or removed (the emulator re-installs its bus wrappers).</summary>
        public event Action? WatchesChanged;

        /// <summary>Set when a CPU should stop; cleared by whoever handles the break.</summary>
        public BreakInfo? PendingBreak { get; set; }

        // ── Breakpoints ───────────────────────────────────────────────────────

        public void AddBreakpoint(DebugCpu cpu, uint address) => _breakpoints[(int)cpu].Add(address & ~1u);

        public bool RemoveBreakpoint(DebugCpu cpu, uint address) => _breakpoints[(int)cpu].Remove(address & ~1u);

        public void ClearBreakpoints(DebugCpu cpu)
        {
            _breakpoints[(int)cpu].Clear();
            _temporary[(int)cpu].Clear();
        }

        public bool HasBreakpoint(DebugCpu cpu, uint address) => _breakpoints[(int)cpu].Contains(address & ~1u);

        public IEnumerable<(DebugCpu Cpu, uint Address)> Breakpoints =>
            _breakpoints[0].Select(a => (DebugCpu.Arm9, a)).Concat(_breakpoints[1].Select(a => (DebugCpu.Arm7, a)));

        /// <summary>A one-shot breakpoint (step over / step out). All of them are dropped at the next break.</summary>
        public void AddTemporaryBreakpoint(DebugCpu cpu, uint address) => _temporary[(int)cpu].Add(address & ~1u);

        /// <summary>Let the core run the instruction at <paramref name="pc"/> once even if it has a breakpoint (resume).</summary>
        public void SkipOnce(DebugCpu cpu, uint pc) => _skipOnce[(int)cpu] = pc & ~1u;

        /// <summary>Called by the emulator before each instruction. True means stop before executing it.</summary>
        public bool CheckBreakpoint(int cpu, uint pc)
        {
            if (_skipOnce[cpu] != NoSkip)
            {
                bool skip = _skipOnce[cpu] == pc;
                _skipOnce[cpu] = NoSkip;
                if (skip)
                    return false;
            }
            if (_breakpoints[cpu].Count == 0 && _temporary[cpu].Count == 0)
                return false;
            if (!_breakpoints[cpu].Contains(pc) && !_temporary[cpu].Contains(pc))
                return false;
            Break(new BreakInfo((DebugCpu)cpu, pc, BreakReason.Breakpoint));
            return true;
        }

        // ── Watchpoints ───────────────────────────────────────────────────────

        public IReadOnlyList<Watchpoint> Watches => _watches;

        public bool HasWatches(DebugCpu cpu) => _watches.Any(w => w.Cpu == cpu);

        public void AddWatch(Watchpoint watch)
        {
            if (watch.End <= watch.Start)
                throw new ArgumentException("A watch range needs End > Start.");
            if (!_watches.Contains(watch))
                _watches.Add(watch);
            WatchesChanged?.Invoke();
        }

        public bool RemoveWatch(DebugCpu cpu, uint start, uint end)
        {
            bool removed = _watches.RemoveAll(w => w.Cpu == cpu && w.Start == start && w.End == end) > 0;
            if (removed)
                WatchesChanged?.Invoke();
            return removed;
        }

        public void ClearWatches()
        {
            _watches.Clear();
            WatchesChanged?.Invoke();
        }

        /// <summary>Called by <see cref="WatchingMemoryBus"/> for each CPU data access.</summary>
        internal void OnAccess(DebugCpu cpu, uint pc, uint address, int size, bool write)
        {
            if (PendingBreak != null)
                return;
            var need = write ? WatchCondition.Write : WatchCondition.Read;
            foreach (var w in _watches)
            {
                if (w.Cpu == cpu && (w.Condition & need) != 0 && w.Overlaps(address, size))
                {
                    Break(new BreakInfo(cpu, pc, BreakReason.Watchpoint, address, write));
                    return;
                }
            }
        }

        private void Break(BreakInfo info)
        {
            PendingBreak = info;
            _temporary[0].Clear();
            _temporary[1].Clear();
        }
    }
}
