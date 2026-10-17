namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>
    /// Coprocessor instructions.
    ///
    ///   MCR/MRC  cond 1110 opc1 L CRn Rd cp# opc2 1 CRm
    ///   CDP      cond 1110 opc1 CRn CRd cp# opc2 0 CRm
    ///   LDC/STC  cond 110 …
    ///
    /// Only CP15 on the ARM9 exists. MRC with Rd = R15 copies bits 31–28 of
    /// the result into the N/Z/C/V flags. The ARM7's CP14 (debug) reads as 0
    /// and ignores writes; everything else is an undefined instruction.
    ///
    /// Reference: GBATEK "ARM Opcodes: Coprocessor Instructions (MRC/MCR, LDC/STC, CDP, MCRR/MRRC)".
    /// </summary>
    public static class Coprocessor
    {
        public const int CP14 = 14;
        public const int CP15 = 15;

        public static void RegisterTransfer(ArmCpu cpu, uint op)
        {
            int cp = (int)(op >> 8) & 0xF;
            int opcode1 = (int)(op >> 21) & 7;
            int crn = (int)(op >> 16) & 0xF;
            int rd = (int)(op >> 12) & 0xF;
            int opcode2 = (int)(op >> 5) & 7;
            int crm = (int)(op & 0xF);
            bool read = (op & (1u << 20)) != 0;

            if (cp == CP15 && cpu.Cp15 != null)
            {
                if (read)
                {
                    uint value = cpu.Cp15.Read(opcode1, crn, crm, opcode2);
                    if (rd == 15)
                        cpu.SetFlagsFromBits(value);
                    else
                        cpu.R[rd] = value;
                }
                else
                {
                    cpu.Cp15.Write(opcode1, crn, crm, opcode2, rd == 15 ? cpu.R[15] + 4 : cpu.R[rd]);
                }
                cpu.AddCycles(1);
                return;
            }

            if (cp == CP14 && !cpu.IsArmV5)
            {
                if (read && rd != 15)
                    cpu.R[rd] = 0;
                return;
            }

            ArmInstructionTable.Undefined(cpu, op);
        }

        public static void DataOperation(ArmCpu cpu, uint op) => ArmInstructionTable.Undefined(cpu, op);

        public static void DataTransfer(ArmCpu cpu, uint op) => ArmInstructionTable.Undefined(cpu, op);
    }
}
