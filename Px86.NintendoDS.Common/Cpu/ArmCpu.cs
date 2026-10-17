using System.Runtime.CompilerServices;
using Px86.NintendoDS.Common.Cpu.Arm;
using Px86.NintendoDS.Common.Cpu.Thumb;
using Px86.NintendoDS.Common.Interfaces;

namespace Px86.NintendoDS.Common.Cpu
{
    /// <summary>
    /// Interpreter for the two ARM cores in the Nintendo DS.
    ///
    /// One class covers both cores; <see cref="Architecture"/> selects the
    /// behaviour differences:
    ///
    ///   ARM9  ARM946E-S  ARMv5TE  67.03 MHz  CP15, vectors at 0xFFFF0000 (high)
    ///   ARM7  ARM7TDMI   ARMv4T   33.51 MHz  no CP15, vectors at 0x00000000
    ///
    /// Pipeline model
    ///   <see cref="Pc"/> is the address of the next instruction to execute.
    ///   While an instruction runs, <c>R[15]</c> reads as that address + 8 (ARM)
    ///   or + 4 (Thumb), which is what the real 3-stage pipeline exposes.
    ///   Any instruction that writes R15 calls <see cref="Branch"/> /
    ///   <see cref="BranchExchange"/>, which set the new <see cref="Pc"/>.
    ///
    /// Register banking
    ///   R0–R15 in <see cref="R"/> always hold the registers visible in the
    ///   current mode. R8–R12 have a FIQ bank, R13–R14 have a bank per
    ///   privileged mode, and each exception mode has an SPSR. Mode switches go
    ///   through <see cref="SwitchMode"/>.
    ///
    /// Decoding
    ///   ARM opcodes dispatch through a 4096-entry table indexed by bits 27–20
    ///   and 7–4; Thumb opcodes through a 1024-entry table indexed by bits 15–6.
    ///   Both tables are built once (<see cref="ArmInstructionTable"/>,
    ///   <see cref="ThumbInstructionTable"/>) and shared by every core.
    ///
    /// Timing
    ///   Each instruction costs one cycle plus whatever the handlers add with
    ///   <see cref="AddCycles"/> (memory accesses, multiplies, pipeline refills).
    ///   This is an approximation; the DS has no cycle-exact commercial
    ///   dependencies that the event scheduler can't absorb.
    ///
    /// Reference: GBATEK "ARM CPU Reference"; ARM Architecture Reference Manual (ARMv5TE).
    /// </summary>
    public sealed class ArmCpu
    {
        // ── CPSR bits ─────────────────────────────────────────────────────────
        public const uint FLAG_N = 1u << 31;
        public const uint FLAG_Z = 1u << 30;
        public const uint FLAG_C = 1u << 29;
        public const uint FLAG_V = 1u << 28;
        public const uint FLAG_Q = 1u << 27;
        public const uint FLAG_I = 1u << 7;
        public const uint FLAG_F = 1u << 6;
        public const uint FLAG_T = 1u << 5;
        public const uint MODE_MASK = 0x1Fu;

        // ── Exception vector offsets ──────────────────────────────────────────
        public const uint VECTOR_RESET     = 0x00;
        public const uint VECTOR_UNDEFINED = 0x04;
        public const uint VECTOR_SWI       = 0x08;
        public const uint VECTOR_PREFETCH  = 0x0C;
        public const uint VECTOR_DATA      = 0x10;
        public const uint VECTOR_IRQ       = 0x18;
        public const uint VECTOR_FIQ       = 0x1C;

        // ── Register banks ────────────────────────────────────────────────────
        private const int BANK_USR = 0;
        private const int BANK_FIQ = 1;
        private const int BANK_IRQ = 2;
        private const int BANK_SVC = 3;
        private const int BANK_ABT = 4;
        private const int BANK_UND = 5;

        private readonly uint[] _bankR13 = new uint[6];
        private readonly uint[] _bankR14 = new uint[6];
        private readonly uint[] _bankSpsr = new uint[6];
        private readonly uint[] _usrR8to12 = new uint[5];
        private readonly uint[] _fiqR8to12 = new uint[5];

        /// <summary>The 16 registers visible in the current mode.</summary>
        public readonly uint[] R = new uint[16];

        private uint _cpsr;
        private uint _pc;
        private int _cycles;

        /// <summary>Set by <see cref="Branch"/> so the step loop doesn't advance PC.</summary>
        internal bool Branched;

        public ArmCpu(ArmArchitecture architecture, IArmMemoryBus bus)
        {
            Architecture = architecture;
            Bus = bus;
            Reset();
        }

        // ── Configuration ─────────────────────────────────────────────────────

        public ArmArchitecture Architecture { get; }

        public bool IsArmV5 => Architecture == ArmArchitecture.ARMv5TE;

        public IArmMemoryBus Bus { get; }

        /// <summary>CP15 (ARM9 only). Null means MCR/MRC p15 is undefined.</summary>
        public ICoprocessor? Cp15 { get; set; }

        /// <summary>Exception vector base: 0x00000000 or 0xFFFF0000 (CP15 control bit 13).</summary>
        public uint ExceptionBase { get; set; }

        /// <summary>
        /// High-level emulation hook for SWI. Called with the SWI number
        /// (bits 23–16 of the ARM opcode, bits 7–0 of the Thumb opcode).
        /// Return true when handled; the CPU then continues with the next
        /// instruction instead of entering the BIOS.
        /// </summary>
        public Func<ArmCpu, uint, bool>? SwiHandler { get; set; }

        /// <summary>Raised for opcodes the interpreter doesn't implement.</summary>
        public event Action<ArmCpu, uint, uint>? UnknownInstruction;

        // ── State ─────────────────────────────────────────────────────────────

        /// <summary>Address of the next instruction to execute.</summary>
        public uint Pc => _pc;

        public uint Cpsr => _cpsr;

        public ProcessorMode Mode => (ProcessorMode)(_cpsr & MODE_MASK);

        public bool IsThumb => (_cpsr & FLAG_T) != 0;

        /// <summary>
        /// Halted by HALTCNT (ARM7) or CP15 wait-for-interrupt (ARM9). The
        /// interrupt controller clears it when (IE &amp; IF) != 0.
        /// </summary>
        public bool Halted { get; set; }

        /// <summary>IRQ line level: IME &amp;&amp; (IE &amp; IF) != 0. Taken when CPSR.I is clear.</summary>
        public bool IrqLine { get; set; }

        /// <summary>Total cycles executed since reset (in this core's own clock).</summary>
        public long TotalCycles { get; private set; }

        public bool FlagN => (_cpsr & FLAG_N) != 0;
        public bool FlagZ => (_cpsr & FLAG_Z) != 0;
        public bool FlagC => (_cpsr & FLAG_C) != 0;
        public bool FlagV => (_cpsr & FLAG_V) != 0;
        public bool FlagQ => (_cpsr & FLAG_Q) != 0;

        /// <summary>SPSR of the current mode (User/System have none and read CPSR).</summary>
        public uint Spsr
        {
            get
            {
                int bank = BankOf(Mode);
                return bank == BANK_USR ? _cpsr : _bankSpsr[bank];
            }
            set
            {
                int bank = BankOf(Mode);
                if (bank != BANK_USR)
                    _bankSpsr[bank] = value;
            }
        }

        // ── Reset / boot helpers ──────────────────────────────────────────────

        /// <summary>Power-on state: Supervisor mode, IRQ+FIQ disabled, ARM state, PC at the reset vector.</summary>
        public void Reset()
        {
            Array.Clear(R);
            Array.Clear(_bankR13);
            Array.Clear(_bankR14);
            Array.Clear(_bankSpsr);
            Array.Clear(_usrR8to12);
            Array.Clear(_fiqR8to12);
            _cpsr = (uint)ProcessorMode.Supervisor | FLAG_I | FLAG_F;
            Halted = false;
            IrqLine = false;
            TotalCycles = 0;
            _pc = ExceptionBase + VECTOR_RESET;
        }

        /// <summary>Set PC directly (boot loaders, debuggers). Bit 0 selects Thumb.</summary>
        public void SetPc(uint address)
        {
            if ((address & 1) != 0)
            {
                _cpsr |= FLAG_T;
                _pc = address & ~1u;
            }
            else
            {
                _cpsr &= ~FLAG_T;
                _pc = address & ~3u;
            }
        }

        /// <summary>Set R13 for a given mode without switching to it (used by direct boot).</summary>
        public void SetBankedStackPointer(ProcessorMode mode, uint value)
        {
            int bank = BankOf(mode);
            if (bank == BankOf(Mode))
                R[13] = value;
            else
                _bankR13[bank] = value;
        }

        // ── Execution ─────────────────────────────────────────────────────────

        /// <summary>
        /// Execute one instruction (or take a pending IRQ).
        /// Returns the number of cycles consumed; a halted core returns 1.
        /// </summary>
        public int Step()
        {
            if (IrqLine && (_cpsr & FLAG_I) == 0)
            {
                Halted = false;
                RaiseException(ProcessorMode.Irq, VECTOR_IRQ, _pc + 4, disableFiq: false);
                TotalCycles += 3;
                return 3;
            }

            if (Halted)
            {
                TotalCycles++;
                return 1;
            }

            _cycles = 1;
            uint pc = _pc;

            if ((_cpsr & FLAG_T) != 0)
            {
                ushort op = Bus.Fetch16(pc);
                R[15] = pc + 4;
                ThumbInstructionTable.Handlers[op >> 6](this, op);
                if (!Branched)
                    _pc = pc + 2;
            }
            else
            {
                uint op = Bus.Fetch32(pc);
                R[15] = pc + 8;
                uint cond = op >> 28;
                if (cond == 0xE || ConditionPassed(cond))
                    ArmInstructionTable.Handlers[((op >> 16) & 0xFF0) | ((op >> 4) & 0xF)](this, op);
                else if (cond == 0xF)
                    ArmInstructionTable.ExecuteUnconditional(this, op);
                if (!Branched)
                    _pc = pc + 4;
            }

            Branched = false;
            TotalCycles += _cycles;
            return _cycles;
        }

        /// <summary>Run until at least <paramref name="cycles"/> cycles have elapsed. Returns the cycles actually run.</summary>
        public int Run(int cycles)
        {
            int ran = 0;
            while (ran < cycles)
            {
                if (Halted && !(IrqLine && (_cpsr & FLAG_I) == 0))
                {
                    TotalCycles += cycles - ran;
                    return cycles;
                }
                ran += Step();
            }
            return ran;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddCycles(int cycles) => _cycles += cycles;

        // ── Condition codes ───────────────────────────────────────────────────

        // ConditionTable[cond << 4 | NZCV] — precomputed so the hot path is one lookup.
        private static readonly bool[] ConditionTable = BuildConditionTable();

        private static bool[] BuildConditionTable()
        {
            var table = new bool[256];
            for (int cond = 0; cond < 16; cond++)
            {
                for (int flags = 0; flags < 16; flags++)
                {
                    bool n = (flags & 8) != 0, z = (flags & 4) != 0, c = (flags & 2) != 0, v = (flags & 1) != 0;
                    table[(cond << 4) | flags] = cond switch
                    {
                        0x0 => z,              // EQ
                        0x1 => !z,             // NE
                        0x2 => c,              // CS/HS
                        0x3 => !c,             // CC/LO
                        0x4 => n,              // MI
                        0x5 => !n,             // PL
                        0x6 => v,              // VS
                        0x7 => !v,             // VC
                        0x8 => c && !z,        // HI
                        0x9 => !c || z,        // LS
                        0xA => n == v,         // GE
                        0xB => n != v,         // LT
                        0xC => !z && n == v,   // GT
                        0xD => z || n != v,    // LE
                        0xE => true,           // AL
                        _   => false,          // NV — handled separately (ARMv5 unconditional space)
                    };
                }
            }
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConditionPassed(uint cond) => ConditionTable[(cond << 4) | (_cpsr >> 28)];

        // ── Flag helpers ──────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetNZ(uint result)
        {
            _cpsr = (_cpsr & ~(FLAG_N | FLAG_Z)) | (result & FLAG_N) | (result == 0 ? FLAG_Z : 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetNZC(uint result, bool carry)
        {
            _cpsr = (_cpsr & ~(FLAG_N | FLAG_Z | FLAG_C)) | (result & FLAG_N) | (result == 0 ? FLAG_Z : 0) | (carry ? FLAG_C : 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetNZCV(uint result, bool carry, bool overflow)
        {
            _cpsr = (_cpsr & 0x0FFFFFFFu) | (result & FLAG_N) | (result == 0 ? FLAG_Z : 0) | (carry ? FLAG_C : 0) | (overflow ? FLAG_V : 0);
        }

        internal void SetQ() => _cpsr |= FLAG_Q;

        /// <summary>Set N/Z/C/V from bits 31–28 of a value (MRC to R15, MSR flags).</summary>
        internal void SetFlagsFromBits(uint value) => _cpsr = (_cpsr & 0x0FFFFFFFu) | (value & 0xF0000000u);

        // ── PC writes ─────────────────────────────────────────────────────────

        /// <summary>Jump within the current instruction set state.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Branch(uint address)
        {
            _pc = (_cpsr & FLAG_T) != 0 ? address & ~1u : address & ~3u;
            Branched = true;
            _cycles += 2;
        }

        /// <summary>Jump and select ARM/Thumb from bit 0 (BX semantics).</summary>
        public void BranchExchange(uint address)
        {
            if ((address & 1) != 0)
            {
                _cpsr |= FLAG_T;
                _pc = address & ~1u;
            }
            else
            {
                _cpsr &= ~FLAG_T;
                _pc = address & ~3u;
            }
            Branched = true;
            _cycles += 2;
        }

        /// <summary>
        /// Write a value loaded from memory to PC: ARMv5 interworks (bit 0
        /// selects Thumb), ARMv4 stays in the current state.
        /// </summary>
        internal void LoadPc(uint value)
        {
            if (IsArmV5)
                BranchExchange(value);
            else
                Branch(value);
        }

        // ── Mode switching / PSR writes ───────────────────────────────────────

        private static int BankOf(ProcessorMode mode) => mode switch
        {
            ProcessorMode.Fiq        => BANK_FIQ,
            ProcessorMode.Irq        => BANK_IRQ,
            ProcessorMode.Supervisor => BANK_SVC,
            ProcessorMode.Abort      => BANK_ABT,
            ProcessorMode.Undefined  => BANK_UND,
            _                        => BANK_USR,
        };

        /// <summary>Swap the banked registers for a new mode and update CPSR's mode bits.</summary>
        public void SwitchMode(ProcessorMode newMode)
        {
            int oldBank = BankOf(Mode);
            int newBank = BankOf(newMode);

            if (oldBank != newBank)
            {
                _bankR13[oldBank] = R[13];
                _bankR14[oldBank] = R[14];
                R[13] = _bankR13[newBank];
                R[14] = _bankR14[newBank];

                if (oldBank == BANK_FIQ)
                {
                    for (int i = 0; i < 5; i++) { _fiqR8to12[i] = R[8 + i]; R[8 + i] = _usrR8to12[i]; }
                }
                else if (newBank == BANK_FIQ)
                {
                    for (int i = 0; i < 5; i++) { _usrR8to12[i] = R[8 + i]; R[8 + i] = _fiqR8to12[i]; }
                }
            }

            _cpsr = (_cpsr & ~MODE_MASK) | (uint)newMode;
        }

        /// <summary>Replace CPSR, switching register banks if the mode field changes.</summary>
        public void SetCpsr(uint value)
        {
            if ((value & MODE_MASK) != (_cpsr & MODE_MASK))
                SwitchMode((ProcessorMode)(value & MODE_MASK));
            _cpsr = value;
        }

        /// <summary>CPSR = SPSR (exception return: MOVS PC / SUBS PC / LDM ^ with PC).</summary>
        internal void RestoreCpsrFromSpsr()
        {
            if (BankOf(Mode) != BANK_USR)
                SetCpsr(Spsr);
        }

        // ── User-bank access (LDM/STM with S bit) ─────────────────────────────

        internal uint GetUserRegister(int index)
        {
            if (index >= 8 && index <= 12 && BankOf(Mode) == BANK_FIQ)
                return _usrR8to12[index - 8];
            if (index >= 13 && index <= 14 && BankOf(Mode) != BANK_USR)
                return index == 13 ? _bankR13[BANK_USR] : _bankR14[BANK_USR];
            return R[index];
        }

        internal void SetUserRegister(int index, uint value)
        {
            if (index >= 8 && index <= 12 && BankOf(Mode) == BANK_FIQ)
                _usrR8to12[index - 8] = value;
            else if (index >= 13 && index <= 14 && BankOf(Mode) != BANK_USR)
            {
                if (index == 13) _bankR13[BANK_USR] = value;
                else _bankR14[BANK_USR] = value;
            }
            else
                R[index] = value;
        }

        // ── Exceptions ────────────────────────────────────────────────────────

        /// <summary>Enter an exception: bank switch, SPSR = old CPSR, LR = return address, ARM state, IRQs off.</summary>
        public void RaiseException(ProcessorMode mode, uint vectorOffset, uint returnAddress, bool disableFiq)
        {
            uint oldCpsr = _cpsr;
            SwitchMode(mode);
            Spsr = oldCpsr;
            R[14] = returnAddress;
            _cpsr = (_cpsr & ~FLAG_T) | FLAG_I | (disableFiq ? FLAG_F : 0);
            _pc = ExceptionBase + vectorOffset;
            Branched = true;
        }

        /// <summary>SWI from the current instruction. Tries the HLE hook first.</summary>
        internal void SoftwareInterrupt(uint number)
        {
            uint next = _pc + (IsThumb ? 2u : 4u);
            if (SwiHandler != null && SwiHandler(this, number))
                return;
            RaiseException(ProcessorMode.Supervisor, VECTOR_SWI, next, disableFiq: false);
            _cycles += 2;
        }

        /// <summary>Undefined instruction: report it, then take the undefined exception.</summary>
        internal void UndefinedInstruction(uint opcode)
        {
            UnknownInstruction?.Invoke(this, _pc, opcode);
            uint next = _pc + (IsThumb ? 2u : 4u);
            RaiseException(ProcessorMode.Undefined, VECTOR_UNDEFINED, next, disableFiq: false);
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 16; i++)
                sb.Append($"R{i}={R[i]:X8}{(i % 4 == 3 ? "\n" : " ")}");
            sb.Append($"PC={_pc:X8} CPSR={_cpsr:X8} [{(FlagN ? 'N' : '-')}{(FlagZ ? 'Z' : '-')}{(FlagC ? 'C' : '-')}{(FlagV ? 'V' : '-')}] {Mode}{(IsThumb ? " Thumb" : " ARM")}");
            return sb.ToString();
        }
    }
}
