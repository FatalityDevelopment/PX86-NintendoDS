namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>
    /// ARM branches.
    ///
    ///   B/BL        cond 101 L offset24          PC = PC+8 + offset*4; BL: LR = next instruction
    ///   BX          cond 0001 0010 1111 1111 1111 0001 Rm    bit 0 of Rm selects Thumb
    ///   BLX Rm      cond 0001 0010 1111 1111 1111 0011 Rm    (ARMv5)
    ///   BLX label   1111 101 H offset24                       (ARMv5) always switches to Thumb;
    ///                                                         H adds 2 to the target
    ///
    /// Reference: GBATEK "ARM Opcodes: Branch and Branch with Link (B, BL, BX, BLX, SWI, BKPT)".
    /// </summary>
    public static class Branches
    {
        public static void BranchWithLink(ArmCpu cpu, uint op)
        {
            int offset = ((int)(op << 8)) >> 6; // sign-extend 24 bits, × 4
            if ((op & (1u << 24)) != 0)
                cpu.R[14] = cpu.R[15] - 4;
            cpu.Branch((uint)(cpu.R[15] + offset));
        }

        public static void BranchExchange(ArmCpu cpu, uint op)
        {
            cpu.BranchExchange(cpu.R[(int)(op & 0xF)]);
        }

        public static void BranchLinkExchangeRegister(ArmCpu cpu, uint op)
        {
            if (!cpu.IsArmV5)
            {
                ArmInstructionTable.Undefined(cpu, op);
                return;
            }
            uint target = cpu.R[(int)(op & 0xF)];
            cpu.R[14] = cpu.R[15] - 4;
            cpu.BranchExchange(target);
        }

        public static void BranchLinkExchangeImmediate(ArmCpu cpu, uint op)
        {
            int offset = ((int)(op << 8)) >> 6;
            uint halfword = (op >> 23) & 2;
            cpu.R[14] = cpu.R[15] - 4;
            cpu.BranchExchange((uint)(cpu.R[15] + offset) + halfword + 1);
        }
    }
}
