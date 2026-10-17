namespace Px86.NintendoDS.Common.Cpu.Thumb
{
    /// <summary>
    /// Thumb branches, SWI and BKPT (formats 16–19).
    ///
    /// BL/BLX is a two-halfword pair:
    ///   prefix  11110 offHi   LR = PC + 4 + (signext(offHi) &lt;&lt; 12)
    ///   suffix  11111 offLo   PC = LR + (offLo &lt;&lt; 1);          LR = next | 1   (BL)
    ///           11101 offLo   PC = (LR + (offLo &lt;&lt; 1)) &amp; ~3;  LR = next | 1   (BLX, ARMv5 → ARM)
    ///
    /// Reference: GBATEK "THUMB Opcodes: Jumps and Calls".
    /// </summary>
    public static class ThumbBranches
    {
        public static void ConditionalBranch(ArmCpu cpu, uint op)
        {
            if (!cpu.ConditionPassed((op >> 8) & 0xF))
                return;
            int offset = (sbyte)(op & 0xFF) * 2;
            cpu.Branch((uint)(cpu.R[15] + offset));
        }

        public static void UnconditionalBranch(ArmCpu cpu, uint op)
        {
            int offset = ((int)(op << 21)) >> 20;
            cpu.Branch((uint)(cpu.R[15] + offset));
        }

        public static void LongBranchPrefix(ArmCpu cpu, uint op)
        {
            int offset = ((int)(op << 21)) >> 9;
            cpu.R[14] = (uint)(cpu.R[15] + offset);
        }

        public static void LongBranchSuffix(ArmCpu cpu, uint op)
        {
            uint target = cpu.R[14] + ((op & 0x7FF) << 1);
            cpu.R[14] = (cpu.Pc + 2) | 1;
            cpu.Branch(target);
        }

        public static void LongBranchExchangeSuffix(ArmCpu cpu, uint op)
        {
            if (!cpu.IsArmV5)
            {
                ThumbInstructionTable.Undefined(cpu, op);
                return;
            }
            uint target = (cpu.R[14] + ((op & 0x7FF) << 1)) & ~3u;
            cpu.R[14] = (cpu.Pc + 2) | 1;
            cpu.BranchExchange(target);
        }

        public static void SoftwareInterrupt(ArmCpu cpu, uint op)
        {
            cpu.SoftwareInterrupt(op & 0xFF);
        }

        public static void Breakpoint(ArmCpu cpu, uint op)
        {
            if (!cpu.IsArmV5)
            {
                ThumbInstructionTable.Undefined(cpu, op);
                return;
            }
            cpu.RaiseException(ProcessorMode.Abort, ArmCpu.VECTOR_PREFETCH, cpu.Pc + 4, disableFiq: false);
        }
    }
}
