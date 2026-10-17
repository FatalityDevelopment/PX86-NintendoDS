using System.Numerics;

namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>
    /// ARM status register transfers.
    ///
    ///   MRS Rd, CPSR|SPSR        cond 00010 P 00 1111 Rd 0000 0000 0000
    ///   MSR PSR_fields, Rm       cond 00010 P 10 field 1111 0000 0000 Rm
    ///   MSR PSR_fields, #imm     cond 00110 P 10 field 1111 rot imm8
    ///
    ///   field bit 19 f = PSR[31:24]  flags
    ///         bit 18 s = PSR[23:16]  (reserved)
    ///         bit 17 x = PSR[15:8]   (reserved)
    ///         bit 16 c = PSR[7:0]    control (mode, I, F, T)
    ///
    /// User mode may only write the flag byte of CPSR. The T bit is never
    /// changed through MSR (use BX). Reserved bits stay zero; bit 27 (Q) only
    /// exists on ARMv5TE.
    ///
    /// Reference: GBATEK "ARM Opcodes: PSR Transfer (MRS, MSR)".
    /// </summary>
    public static class PsrTransfer
    {
        public static void MoveFromStatus(ArmCpu cpu, uint op)
        {
            int rd = (int)(op >> 12) & 0xF;
            cpu.R[rd] = (op & (1u << 22)) != 0 ? cpu.Spsr : cpu.Cpsr;
        }

        public static void MoveToStatus(ArmCpu cpu, uint op)
        {
            bool toSpsr = (op & (1u << 22)) != 0;
            uint value = (op & (1u << 25)) != 0
                ? BitOperations.RotateRight(op & 0xFF, (int)((op >> 8) & 0xF) * 2)
                : cpu.R[(int)(op & 0xF)];

            uint mask = 0;
            if ((op & (1u << 16)) != 0) mask |= 0x000000FFu;
            if ((op & (1u << 17)) != 0) mask |= 0x0000FF00u;
            if ((op & (1u << 18)) != 0) mask |= 0x00FF0000u;
            if ((op & (1u << 19)) != 0) mask |= 0xFF000000u;
            mask &= cpu.IsArmV5 ? 0xF80000FFu : 0xF00000FFu;

            if (toSpsr)
            {
                if (cpu.Mode != ProcessorMode.User && cpu.Mode != ProcessorMode.System)
                    cpu.Spsr = (cpu.Spsr & ~mask) | (value & mask);
                return;
            }

            if (cpu.Mode == ProcessorMode.User)
                mask &= 0xFF000000u;
            mask &= ~ArmCpu.FLAG_T;
            cpu.SetCpsr((cpu.Cpsr & ~mask) | (value & mask));
        }
    }
}
