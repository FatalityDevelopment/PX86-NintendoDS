using System.Numerics;
using System.Runtime.CompilerServices;

namespace Px86.NintendoDS.Common.Cpu
{
    /// <summary>
    /// Instruction bodies for compiled blocks. The JIT emits calls with the
    /// decoded fields as constants; .NET inlines these small methods and folds
    /// the constants, so each guest instruction becomes a few native ones.
    ///
    /// Every method does exactly what the matching interpreter handler does
    /// for that one encoding (same flags, same register writes). Operand values
    /// are passed in already read, so the caller can supply R15 as a constant.
    ///
    /// Reference: GBATEK "ARM Opcodes: Data Processing (ALU)" and
    ///            "THUMB Opcodes: Register Operations (ALU, BX)".
    /// </summary>
    public static class JitOps
    {
        private const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

        // ── Barrel shifter (immediate amount, carry not needed) ───────────────

        [MethodImpl(Inline)]
        public static uint ShiftImm(ArmCpu c, uint v, int type, int amount)
        {
            switch (type)
            {
                case Alu.SHIFT_LSL: return v << amount;
                case Alu.SHIFT_LSR: return amount == 0 ? 0 : v >> amount;
                case Alu.SHIFT_ASR: return (uint)((int)v >> (amount == 0 ? 31 : amount));
                default:
                    return amount == 0
                        ? (v >> 1) | (c.FlagC ? 0x80000000u : 0u)
                        : BitOperations.RotateRight(v, amount);
            }
        }

        // ── ARM data processing, flags not set ────────────────────────────────

        [MethodImpl(Inline)] public static void And(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = a & b;
        [MethodImpl(Inline)] public static void Eor(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = a ^ b;
        [MethodImpl(Inline)] public static void Sub(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = a - b;
        [MethodImpl(Inline)] public static void Rsb(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = b - a;
        [MethodImpl(Inline)] public static void Add(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = a + b;
        [MethodImpl(Inline)] public static void Adc(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = a + b + (c.FlagC ? 1u : 0u);
        [MethodImpl(Inline)] public static void Sbc(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = a - b - (c.FlagC ? 0u : 1u);
        [MethodImpl(Inline)] public static void Rsc(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = b - a - (c.FlagC ? 0u : 1u);
        [MethodImpl(Inline)] public static void Orr(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = a | b;
        [MethodImpl(Inline)] public static void Mov(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = b;
        [MethodImpl(Inline)] public static void Bic(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = a & ~b;
        [MethodImpl(Inline)] public static void Mvn(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = ~b;

        // ── ARM data processing, flags set (logical ops take the shifter carry) ─

        [MethodImpl(Inline)] public static void Ands(ArmCpu c, int rd, uint a, uint b, bool carry) { uint r = a & b; c.R[rd] = r; c.SetNZC(r, carry); }
        [MethodImpl(Inline)] public static void Eors(ArmCpu c, int rd, uint a, uint b, bool carry) { uint r = a ^ b; c.R[rd] = r; c.SetNZC(r, carry); }
        [MethodImpl(Inline)] public static void Orrs(ArmCpu c, int rd, uint a, uint b, bool carry) { uint r = a | b; c.R[rd] = r; c.SetNZC(r, carry); }
        [MethodImpl(Inline)] public static void Movs(ArmCpu c, int rd, uint a, uint b, bool carry) { c.R[rd] = b; c.SetNZC(b, carry); }
        [MethodImpl(Inline)] public static void Bics(ArmCpu c, int rd, uint a, uint b, bool carry) { uint r = a & ~b; c.R[rd] = r; c.SetNZC(r, carry); }
        [MethodImpl(Inline)] public static void Mvns(ArmCpu c, int rd, uint a, uint b, bool carry) { uint r = ~b; c.R[rd] = r; c.SetNZC(r, carry); }
        [MethodImpl(Inline)] public static void Tst(ArmCpu c, int rd, uint a, uint b, bool carry) => c.SetNZC(a & b, carry);
        [MethodImpl(Inline)] public static void Teq(ArmCpu c, int rd, uint a, uint b, bool carry) => c.SetNZC(a ^ b, carry);

        [MethodImpl(Inline)] public static void Subs(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.Subtract(c, a, b, true);
        [MethodImpl(Inline)] public static void Rsbs(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.Subtract(c, b, a, true);
        [MethodImpl(Inline)] public static void Adds(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.Add(c, a, b, true);
        [MethodImpl(Inline)] public static void Adcs(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.AddWithCarry(c, a, b, true);
        [MethodImpl(Inline)] public static void Sbcs(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.SubtractWithCarry(c, a, b, true);
        [MethodImpl(Inline)] public static void Rscs(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.SubtractWithCarry(c, b, a, true);
        [MethodImpl(Inline)] public static void Cmp(ArmCpu c, int rd, uint a, uint b) => Alu.Subtract(c, a, b, true);
        [MethodImpl(Inline)] public static void Cmn(ArmCpu c, int rd, uint a, uint b) => Alu.Add(c, a, b, true);

        // ── Loads and stores (ARM and Thumb single transfers) ─────────────────

        public const int LoadWord = 0, LoadByte = 1, LoadHalf = 2, LoadSignedByte = 3, LoadSignedHalf = 4;
        public const int StoreWord = 0, StoreByte = 1, StoreHalf = 2;

        /// <summary>LDR/LDRB/LDRH/LDRSB/LDRSH: the load wins over the base writeback when Rd = Rn.</summary>
        [MethodImpl(Inline)]
        public static void Load(ArmCpu c, int kind, int rd, int rn, uint baseValue, uint offset, bool up, bool pre, bool writeBack)
        {
            uint offsetAddress = up ? baseValue + offset : baseValue - offset;
            uint address = pre ? offsetAddress : baseValue;
            uint value = kind switch
            {
                LoadWord => Alu.ReadWordRotated(c, address),
                LoadByte => c.Bus.Read8(address),
                LoadHalf => Alu.ReadHalfword(c, address),
                LoadSignedByte => (uint)(sbyte)c.Bus.Read8(address),
                _ => Alu.ReadSignedHalfword(c, address),
            };
            if (writeBack)
                c.R[rn] = offsetAddress;
            c.R[rd] = value;
        }

        /// <summary>STR/STRB/STRH: the value is read before the base writeback.</summary>
        [MethodImpl(Inline)]
        public static void Store(ArmCpu c, int kind, uint value, int rn, uint baseValue, uint offset, bool up, bool pre, bool writeBack)
        {
            uint offsetAddress = up ? baseValue + offset : baseValue - offset;
            uint address = pre ? offsetAddress : baseValue;
            switch (kind)
            {
                case StoreWord: c.Bus.Write32(address & ~3u, value); break;
                case StoreByte: c.Bus.Write8(address, (byte)value); break;
                default: c.Bus.Write16(address & ~1u, (ushort)value); break;
            }
            if (writeBack)
                c.R[rn] = offsetAddress;
        }

        // ── Thumb ALU (formats 1–5, 12, 13) ───────────────────────────────────

        [MethodImpl(Inline)] public static void Set(ArmCpu c, int rd, uint value) => c.R[rd] = value;

        [MethodImpl(Inline)]
        public static void ThumbShift(ArmCpu c, int rd, uint v, int type, int amount)
        {
            bool carry = c.FlagC;
            uint r = Alu.ShiftImmediate(v, type, amount, ref carry);
            c.R[rd] = r;
            c.SetNZC(r, carry);
        }

        [MethodImpl(Inline)] public static void ThumbMov(ArmCpu c, int rd, uint value) { c.R[rd] = value; c.SetNZ(value); }

        [MethodImpl(Inline)] public static void TAnd(ArmCpu c, int rd, uint a, uint b) { uint r = a & b; c.R[rd] = r; c.SetNZ(r); }
        [MethodImpl(Inline)] public static void TEor(ArmCpu c, int rd, uint a, uint b) { uint r = a ^ b; c.R[rd] = r; c.SetNZ(r); }
        [MethodImpl(Inline)] public static void TOrr(ArmCpu c, int rd, uint a, uint b) { uint r = a | b; c.R[rd] = r; c.SetNZ(r); }
        [MethodImpl(Inline)] public static void TBic(ArmCpu c, int rd, uint a, uint b) { uint r = a & ~b; c.R[rd] = r; c.SetNZ(r); }
        [MethodImpl(Inline)] public static void TMvn(ArmCpu c, int rd, uint a, uint b) { uint r = ~b; c.R[rd] = r; c.SetNZ(r); }
        [MethodImpl(Inline)] public static void TTst(ArmCpu c, int rd, uint a, uint b) => c.SetNZ(a & b);
        [MethodImpl(Inline)] public static void TNeg(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.Subtract(c, 0, b, true);
        [MethodImpl(Inline)] public static void TAdc(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.AddWithCarry(c, a, b, true);
        [MethodImpl(Inline)] public static void TSbc(ArmCpu c, int rd, uint a, uint b) => c.R[rd] = Alu.SubtractWithCarry(c, a, b, true);

        [MethodImpl(Inline)]
        public static void TShiftReg(ArmCpu c, int rd, uint a, uint b, int type)
        {
            bool carry = c.FlagC;
            uint r = Alu.ShiftRegister(a, type, b & 0xFF, ref carry);
            c.R[rd] = r;
            c.SetNZC(r, carry);
        }
    }
}
