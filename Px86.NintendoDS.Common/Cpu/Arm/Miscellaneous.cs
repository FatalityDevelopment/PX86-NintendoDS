using System.Numerics;

namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>
    /// SWI and the ARMv5TE miscellaneous instructions.
    ///
    ///   SWI      cond 1111 comment24   — DS BIOS function number is bits 23–16
    ///   CLZ      cond 0001 0110 1111 Rd 1111 0001 Rm               (ARMv5)
    ///   QADD     cond 0001 0000 Rn Rd 0000 0101 Rm  Rd = sat(Rm + Rn)          (ARMv5)
    ///   QSUB     cond 0001 0010 …                   Rd = sat(Rm − Rn)
    ///   QDADD    cond 0001 0100 …                   Rd = sat(Rm + sat(Rn × 2))
    ///   QDSUB    cond 0001 0110 …                   Rd = sat(Rm − sat(Rn × 2))
    ///   BKPT     1110 0001 0010 imm12 0111 imm4                    (ARMv5) prefetch abort
    ///
    /// Saturating operations set the sticky Q flag when they clamp.
    ///
    /// Reference: GBATEK "ARM Opcodes: Special ARM9 Instructions (CLZ, BKPT, LDRD, STRD, PLD)"
    ///            and "ARM Opcodes: Qadd/Qsub".
    /// </summary>
    public static class Miscellaneous
    {
        public static void SoftwareInterrupt(ArmCpu cpu, uint op)
        {
            cpu.SoftwareInterrupt((op >> 16) & 0xFF);
        }

        public static void CountLeadingZeros(ArmCpu cpu, uint op)
        {
            if (!cpu.IsArmV5)
            {
                ArmInstructionTable.Undefined(cpu, op);
                return;
            }
            int rd = (int)(op >> 12) & 0xF;
            cpu.R[rd] = (uint)BitOperations.LeadingZeroCount(cpu.R[(int)(op & 0xF)]);
        }

        public static void SaturatingArithmetic(ArmCpu cpu, uint op)
        {
            if (!cpu.IsArmV5)
            {
                ArmInstructionTable.Undefined(cpu, op);
                return;
            }
            int rn = (int)(op >> 16) & 0xF;
            int rd = (int)(op >> 12) & 0xF;
            int rm = (int)(op & 0xF);
            uint a = cpu.R[rm];
            uint b = cpu.R[rn];

            switch ((op >> 21) & 3)
            {
                case 0: cpu.R[rd] = Alu.SaturatingAdd(cpu, a, b); break;
                case 1: cpu.R[rd] = Alu.SaturatingSubtract(cpu, a, b); break;
                case 2: cpu.R[rd] = Alu.SaturatingAdd(cpu, a, Alu.SaturatingAdd(cpu, b, b)); break;
                default: cpu.R[rd] = Alu.SaturatingSubtract(cpu, a, Alu.SaturatingAdd(cpu, b, b)); break;
            }
        }

        public static void Breakpoint(ArmCpu cpu, uint op)
        {
            if (!cpu.IsArmV5)
            {
                ArmInstructionTable.Undefined(cpu, op);
                return;
            }
            cpu.RaiseException(ProcessorMode.Abort, ArmCpu.VECTOR_PREFETCH, cpu.Pc + 4, disableFiq: false);
        }
    }
}
