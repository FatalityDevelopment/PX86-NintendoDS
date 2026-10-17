using System.Numerics;
using System.Runtime.CompilerServices;

namespace Px86.NintendoDS.Common.Cpu
{
    /// <summary>
    /// Barrel shifter and flag-setting arithmetic shared by the ARM and Thumb
    /// decoders.
    ///
    /// Shift types: 0 = LSL, 1 = LSR, 2 = ASR, 3 = ROR.
    ///
    /// Immediate shifts reuse amount 0 for special forms:
    ///   LSL #0 → no shift (carry unchanged), LSR #0 → LSR #32,
    ///   ASR #0 → ASR #32, ROR #0 → RRX (rotate right through carry).
    /// Register shifts use the bottom byte of Rs; amount 0 leaves value and
    /// carry unchanged, and amounts ≥ 32 saturate per shift type.
    ///
    /// Reference: GBATEK "ARM Opcodes: Data Processing (ALU)".
    /// </summary>
    public static class Alu
    {
        public const int SHIFT_LSL = 0;
        public const int SHIFT_LSR = 1;
        public const int SHIFT_ASR = 2;
        public const int SHIFT_ROR = 3;

        // ── Barrel shifter ────────────────────────────────────────────────────

        public static uint ShiftImmediate(uint value, int type, int amount, ref bool carry)
        {
            switch (type)
            {
                case SHIFT_LSL:
                    if (amount == 0)
                        return value;
                    carry = ((value >> (32 - amount)) & 1) != 0;
                    return value << amount;

                case SHIFT_LSR:
                    if (amount == 0)
                    {
                        carry = (value >> 31) != 0;
                        return 0;
                    }
                    carry = ((value >> (amount - 1)) & 1) != 0;
                    return value >> amount;

                case SHIFT_ASR:
                    if (amount == 0)
                    {
                        carry = (value >> 31) != 0;
                        return (uint)((int)value >> 31);
                    }
                    carry = ((value >> (amount - 1)) & 1) != 0;
                    return (uint)((int)value >> amount);

                default:
                    if (amount == 0)
                    {
                        bool oldCarry = carry;
                        carry = (value & 1) != 0;
                        return (value >> 1) | (oldCarry ? 0x80000000u : 0u);
                    }
                    carry = ((value >> (amount - 1)) & 1) != 0;
                    return BitOperations.RotateRight(value, amount);
            }
        }

        public static uint ShiftRegister(uint value, int type, uint amount, ref bool carry)
        {
            if (amount == 0)
                return value;

            switch (type)
            {
                case SHIFT_LSL:
                    if (amount < 32)
                    {
                        carry = ((value >> (32 - (int)amount)) & 1) != 0;
                        return value << (int)amount;
                    }
                    carry = amount == 32 && (value & 1) != 0;
                    return 0;

                case SHIFT_LSR:
                    if (amount < 32)
                    {
                        carry = ((value >> ((int)amount - 1)) & 1) != 0;
                        return value >> (int)amount;
                    }
                    carry = amount == 32 && (value >> 31) != 0;
                    return 0;

                case SHIFT_ASR:
                    if (amount < 32)
                    {
                        carry = ((value >> ((int)amount - 1)) & 1) != 0;
                        return (uint)((int)value >> (int)amount);
                    }
                    carry = (value >> 31) != 0;
                    return (uint)((int)value >> 31);

                default:
                    amount &= 31;
                    if (amount == 0)
                    {
                        carry = (value >> 31) != 0;
                        return value;
                    }
                    carry = ((value >> ((int)amount - 1)) & 1) != 0;
                    return BitOperations.RotateRight(value, (int)amount);
            }
        }

        // ── Arithmetic with flags ─────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Add(ArmCpu cpu, uint a, uint b, bool setFlags)
        {
            uint result = a + b;
            if (setFlags)
                cpu.SetNZCV(result, result < a, ((~(a ^ b) & (a ^ result)) >> 31) != 0);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint AddWithCarry(ArmCpu cpu, uint a, uint b, bool setFlags)
        {
            ulong wide = (ulong)a + b + (cpu.FlagC ? 1u : 0u);
            uint result = (uint)wide;
            if (setFlags)
                cpu.SetNZCV(result, wide > 0xFFFFFFFFul, ((~(a ^ b) & (a ^ result)) >> 31) != 0);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Subtract(ArmCpu cpu, uint a, uint b, bool setFlags)
        {
            uint result = a - b;
            if (setFlags)
                cpu.SetNZCV(result, a >= b, (((a ^ b) & (a ^ result)) >> 31) != 0);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint SubtractWithCarry(ArmCpu cpu, uint a, uint b, bool setFlags)
        {
            uint borrow = cpu.FlagC ? 0u : 1u;
            uint result = a - b - borrow;
            if (setFlags)
                cpu.SetNZCV(result, (ulong)a >= (ulong)b + borrow, (((a ^ b) & (a ^ result)) >> 31) != 0);
            return result;
        }

        // ── Saturation (ARMv5TE QADD family) ──────────────────────────────────

        public static uint SaturatingAdd(ArmCpu cpu, uint a, uint b)
        {
            long wide = (long)(int)a + (int)b;
            return Saturate(cpu, wide);
        }

        public static uint SaturatingSubtract(ArmCpu cpu, uint a, uint b)
        {
            long wide = (long)(int)a - (int)b;
            return Saturate(cpu, wide);
        }

        public static uint Saturate(ArmCpu cpu, long value)
        {
            if (value > int.MaxValue)
            {
                cpu.SetQ();
                return 0x7FFFFFFFu;
            }
            if (value < int.MinValue)
            {
                cpu.SetQ();
                return 0x80000000u;
            }
            return (uint)(int)value;
        }

        // ── Misaligned load rotation ──────────────────────────────────────────

        /// <summary>LDR from a misaligned address returns the aligned word rotated right by 8 × (addr &amp; 3).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadWordRotated(ArmCpu cpu, uint address)
        {
            uint value = cpu.Bus.Read32(address & ~3u);
            int rotate = (int)(address & 3) * 8;
            return rotate == 0 ? value : BitOperations.RotateRight(value, rotate);
        }

        /// <summary>
        /// LDRH: ARMv4 rotates a misaligned halfword by 8; ARMv5 reads the aligned halfword.
        /// </summary>
        public static uint ReadHalfword(ArmCpu cpu, uint address)
        {
            uint value = cpu.Bus.Read16(address & ~1u);
            if (!cpu.IsArmV5 && (address & 1) != 0)
                return BitOperations.RotateRight(value, 8);
            return value;
        }

        /// <summary>LDRSH: a misaligned LDRSH on ARMv4 behaves as LDRSB of the odd byte.</summary>
        public static uint ReadSignedHalfword(ArmCpu cpu, uint address)
        {
            if (!cpu.IsArmV5 && (address & 1) != 0)
                return (uint)(sbyte)cpu.Bus.Read8(address);
            return (uint)(short)cpu.Bus.Read16(address & ~1u);
        }
    }
}
