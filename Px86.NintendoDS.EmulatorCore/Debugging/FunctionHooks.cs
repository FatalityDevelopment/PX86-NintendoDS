using System.Buffers.Binary;
using Px86.NintendoDS.Common.Cpu;

namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>The argument layout of a hooked printf-family function.</summary>
    public enum HookKind
    {
        /// <summary>printf(fmt, ...): format in r0.</summary>
        Printf,
        /// <summary>vprintf(fmt, va_list): r0, r1.</summary>
        VPrintf,
        /// <summary>sprintf(buf, fmt, ...): format in r1.</summary>
        SPrintf,
        /// <summary>vsprintf(buf, fmt, va_list): r1, r2.</summary>
        VSPrintf,
        /// <summary>snprintf(buf, size, fmt, ...): format in r2.</summary>
        SNPrintf,
        /// <summary>vsnprintf(buf, size, fmt, va_list): r2, r3.</summary>
        VSNPrintf,
        /// <summary>puts-style output of a plain string in r0.</summary>
        PutString,
        /// <summary>A formatter found by the scanner: vsnprintf, vsprintf or vprintf, told apart per call by which register holds a string.</summary>
        Auto,
    }

    /// <summary>A function to report when a CPU starts executing it.</summary>
    public sealed record FunctionHook(DebugCpu Cpu, uint Address, HookKind Kind, string Name, bool Automatic);

    /// <summary>
    /// Captures what a game prints through its own printf/sprintf functions, as PCSX2
    /// does for PS2 games. Retail games are built without debug output, but their
    /// sprintf-style formatters still run: when a CPU reaches the first instruction of
    /// a hooked function, the format string and arguments are read from r0–r3, the
    /// stack or the va_list, formatted (<see cref="PrintfFormatter"/>) and printed to
    /// <see cref="DebugOutput"/>. The game is not changed or slowed down otherwise.
    ///
    /// Hooks are added by hand (an address and a <see cref="HookKind"/> per CPU), or by
    /// <see cref="ScanNow"/>, which looks for printf-style formatters in the loaded code
    /// (<see cref="PrintfSignatures"/>) and runs again every few seconds to catch
    /// overlays. A hooked address whose instruction changed (another overlay was loaded
    /// there) is not reported.
    ///
    /// A call that reaches several hooks (sprintf → vsprintf → vsnprintf) prints once:
    /// a hook is skipped when the same CPU passed the same format string to a hook
    /// a moment before, from higher up the stack.
    /// </summary>
    public sealed class FunctionHooks
    {
        /// <summary>Frames between scans for code loaded later (overlays).</summary>
        public const int RescanFrames = 300;
        private const long NestedCallCycles = 4096;

        private readonly NdsEmulator _nds;
        private readonly Dictionary<uint, Entry>[] _hooks = { new(), new() };
        private readonly uint[] _lastFormat = new uint[2];
        private readonly long[] _lastTime = { long.MinValue, long.MinValue };
        private readonly uint[] _lastSp = new uint[2];
        private long _scannedFrame = -RescanFrames;

        private sealed class Entry
        {
            public required FunctionHook Hook;
            public uint Opcode;
        }

        public FunctionHooks(NdsEmulator nds) => _nds = nds;

        /// <summary>Hooks are enabled (manual and automatic).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Scan the loaded code for printf-style formatters.</summary>
        public bool AutoScan { get; set; } = true;

        /// <summary>Start sprintf-family lines with "[sprintf] ".</summary>
        public bool PrefixSPrintf { get; set; } = true;

        /// <summary>Whether the run loop must check hooks (cheap test).</summary>
        public bool Active => Enabled && (_hooks[0].Count | _hooks[1].Count) != 0;

        /// <summary>The set of hooked addresses changed: cached blocks must be rebuilt so they end before a hook.</summary>
        public event Action? Changed;

        /// <summary>A hook was added or removed by the scanner (for the emulator log).</summary>
        public event Action<string>? Log;

        public IReadOnlyList<FunctionHook> List()
        {
            var all = new List<FunctionHook>();
            foreach (var cpu in _hooks)
                all.AddRange(cpu.Values.Select(e => e.Hook));
            return all.OrderBy(h => h.Cpu).ThenBy(h => h.Address).ToList();
        }

        public void Add(DebugCpu cpu, uint address, HookKind kind, string? name = null, bool automatic = false)
        {
            address &= ~1u;
            name ??= kind.ToString().ToLowerInvariant();
            _hooks[(int)cpu][address] = new Entry
            {
                Hook = new FunctionHook(cpu, address, kind, name, automatic),
                Opcode = DebugMemory.Read32(_nds, cpu, address),
            };
            Changed?.Invoke();
        }

        public bool Remove(DebugCpu cpu, uint address)
        {
            bool removed = _hooks[(int)cpu].Remove(address & ~1u);
            if (removed)
                Changed?.Invoke();
            return removed;
        }

        /// <summary>Remove every hook (both kinds), e.g. when a new game is loaded.</summary>
        public void Clear()
        {
            _hooks[0].Clear();
            _hooks[1].Clear();
            _scannedFrame = -RescanFrames;
            Changed?.Invoke();
        }

        /// <summary>Is there a hook at this address (block building ends before one)?</summary>
        public bool IsHooked(int cpu, uint address) => Enabled && _hooks[cpu].ContainsKey(address & ~1u);

        // ── Execution ─────────────────────────────────────────────────────────

        /// <summary>The CPU is about to execute the instruction at its PC (only called while <see cref="Active"/>).</summary>
        public void OnExecute(int index, ArmCpu core)
        {
            if (!_hooks[index].TryGetValue(core.Pc & ~1u, out var entry))
                return;
            var cpu = (DebugCpu)index;
            if (DebugMemory.Read32(_nds, cpu, entry.Hook.Address) != entry.Opcode)
                return; // other code was loaded there
            Report(cpu, entry.Hook.Kind, core.R, core.TotalCycles);
        }

        /// <summary>Format a call with the registers as they are on entry, and print it.</summary>
        public void Report(DebugCpu cpu, HookKind kind, uint[] r, long now = long.MinValue / 2)
        {
            var nds = _nds;
            if (kind == HookKind.Auto)
                kind = PrintfFormatter.LooksLikeString(nds, cpu, r[2]) ? HookKind.VSNPrintf
                    : PrintfFormatter.LooksLikeString(nds, cpu, r[1]) ? HookKind.VSPrintf
                    : HookKind.VPrintf;
            int formatReg = kind switch
            {
                HookKind.SPrintf or HookKind.VSPrintf => 1,
                HookKind.SNPrintf or HookKind.VSNPrintf => 2,
                _ => 0,
            };
            uint format = r[formatReg];
            if (!PrintfFormatter.LooksLikeString(nds, cpu, format))
                return;

            int c = (int)cpu;
            // A wrapper calling the next formatter runs deeper in the stack; a loop
            // printing with the same format calls from the same depth and is kept.
            bool nested = _lastFormat[c] == format && r[13] < _lastSp[c] && now - _lastTime[c] < NestedCallCycles;
            _lastFormat[c] = format;
            _lastTime[c] = now;
            _lastSp[c] = r[13];
            if (nested)
                return;

            string fmt = PrintfFormatter.ReadString(nds, cpu, format);
            string text = kind switch
            {
                HookKind.PutString => fmt,
                HookKind.VPrintf or HookKind.VSPrintf or HookKind.VSNPrintf =>
                    PrintfFormatter.Format(nds, cpu, fmt, PrintfArgs.FromVaList(nds, cpu, r[formatReg + 1])),
                _ => PrintfFormatter.Format(nds, cpu, fmt, PrintfArgs.FromRegisters(nds, cpu, r, formatReg + 1)),
            };
            if (kind is HookKind.Printf or HookKind.VPrintf or HookKind.PutString)
                nds.DebugOutput.Print(cpu, text);
            else
                nds.DebugOutput.PrintLine(cpu, (PrefixSPrintf ? "[sprintf] " : "") + text.TrimEnd('\n'));
        }

        // ── Scanning ──────────────────────────────────────────────────────────

        /// <summary>Called once a frame: scan when it's time.</summary>
        public void OnFrame()
        {
            if (!Enabled || !AutoScan || _nds.FrameCount - _scannedFrame < RescanFrames)
                return;
            ScanNow();
        }

        /// <summary>
        /// Look for formatters in main RAM and ITCM (ARM9) and in the ARM7 binary's area
        /// and WRAM (ARM7). The ARM7 binary's main-RAM range comes from the cartridge
        /// header copy at 027FFE00h.
        /// </summary>
        public int ScanNow()
        {
            _scannedFrame = _nds.FrameCount;
            int added = 0;
            var main = _nds.MainRam;
            var header = main.AsSpan(0x3FFE00, 0x170);
            uint arm7Start = BinaryPrimitives.ReadUInt32LittleEndian(header[0x38..]);
            uint arm7Size = BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]);
            bool arm7InMain = arm7Start >= 0x02000000 && arm7Start < 0x02400000 && arm7Size is > 0 and <= 0x400000;

            foreach (uint found in PrintfSignatures.Scan(main, 0x02000000))
            {
                var cpu = arm7InMain && found >= arm7Start && found < arm7Start + arm7Size ? DebugCpu.Arm7 : DebugCpu.Arm9;
                added += AddFound(cpu, found);
            }
            var itcm = new byte[0x8000];
            DebugMemory.ReadBlock(_nds, DebugCpu.Arm9, 0x01FF8000, itcm);
            foreach (uint found in PrintfSignatures.Scan(itcm, 0x01FF8000))
                added += AddFound(DebugCpu.Arm9, found);
            var wram = new byte[0x18000];
            DebugMemory.ReadBlock(_nds, DebugCpu.Arm7, 0x037F8000, wram);
            foreach (uint found in PrintfSignatures.Scan(wram, 0x037F8000))
                added += AddFound(DebugCpu.Arm7, found);
            return added;
        }

        private int AddFound(DebugCpu cpu, uint found)
        {
            uint address = found & ~1u;
            if (_hooks[(int)cpu].TryGetValue(address, out var existing)
                && DebugMemory.Read32(_nds, cpu, address) == existing.Opcode)
                return 0;
            Add(cpu, address, HookKind.Auto, "printf-style formatter", automatic: true);
            Log?.Invoke($"Hooked a printf-style formatter on the {(cpu == DebugCpu.Arm9 ? "ARM9" : "ARM7")} at 0x{address:X8} ({((found & 1) != 0 ? "Thumb" : "ARM")}).");
            return 1;
        }

        // ── Text form (settings, command line) ────────────────────────────────

        /// <summary>Parse "arm9:0x02001234:sprintf" (CPU optional, ARM9 by default; kind optional, printf by default).</summary>
        public static bool TryParse(string text, out DebugCpu cpu, out uint address, out HookKind kind)
        {
            cpu = DebugCpu.Arm9;
            address = 0;
            kind = HookKind.Printf;
            var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries);
            int i = 0;
            if (parts.Length > 1 && parts[0].ToLowerInvariant() is "arm9" or "arm7")
                cpu = parts[i++].ToLowerInvariant() == "arm7" ? DebugCpu.Arm7 : DebugCpu.Arm9;
            if (i >= parts.Length)
                return false;
            string hex = parts[i++];
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out address))
                return false;
            if (i < parts.Length && !Enum.TryParse(parts[i++], ignoreCase: true, out kind))
                return false;
            return i == parts.Length;
        }

        public static string Format(FunctionHook hook) =>
            $"{(hook.Cpu == DebugCpu.Arm9 ? "arm9" : "arm7")}:0x{hook.Address:X8}:{hook.Kind.ToString().ToLowerInvariant()}";
    }
}
