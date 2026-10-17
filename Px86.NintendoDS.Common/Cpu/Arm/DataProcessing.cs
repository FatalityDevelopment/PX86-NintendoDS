using System.Numerics;

namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>
    /// ARM data-processing (ALU) instructions.
    ///
    ///   cond 00 I opcode S Rn Rd operand2
    ///
    ///   opcode  0 AND  1 EOR  2 SUB  3 RSB  4 ADD  5 ADC  6 SBC  7 RSC
    ///           8 TST  9 TEQ  A CMP  B CMN  C ORR  D MOV  E BIC  F MVN
    ///
    /// Operand 2 is either an 8-bit immediate rotated right by 2 × rot, or Rm
    /// shifted by an immediate or by the bottom byte of Rs. With a
    /// register-specified shift, reading R15 as Rn or Rm yields PC + 12 (one
    /// extra pipeline stage elapses).
    ///
    /// Writing R15 with S = 1 copies SPSR to CPSR (exception return). ALU writes
    /// to R15 never change ARM/Thumb state, on ARMv4 or ARMv5.
    ///
    /// Reference: GBATEK "ARM Opcodes: Data Processing (ALU)".
    /// </summary>
    public static class DataProcessing
    {
        public const int OP_AND = 0x0;
        public const int OP_EOR = 0x1;
        public const int OP_SUB = 0x2;
        public const int OP_RSB = 0x3;
        public const int OP_ADD = 0x4;
        public const int OP_ADC = 0x5;
        public const int OP_SBC = 0x6;
        public const int OP_RSC = 0x7;
        public const int OP_TST = 0x8;
        public const int OP_TEQ = 0x9;
        public const int OP_CMP = 0xA;
        public const int OP_CMN = 0xB;
        public const int OP_ORR = 0xC;
        public const int OP_MOV = 0xD;
        public const int OP_BIC = 0xE;
        public const int OP_MVN = 0xF;

        public static void Execute(ArmCpu cpu, uint op)
        {
            int opcode = (int)(op >> 21) & 0xF;
            bool setFlags = (op & (1u << 20)) != 0;
            int rn = (int)(op >> 16) & 0xF;
            int rd = (int)(op >> 12) & 0xF;

            bool carry = cpu.FlagC;
            uint operand2;
            uint rnValue = cpu.R[rn];

            if ((op & (1u << 25)) != 0)
            {
                int rotate = (int)((op >> 8) & 0xF) * 2;
                operand2 = BitOperations.RotateRight(op & 0xFF, rotate);
                if (rotate != 0)
                    carry = (operand2 >> 31) != 0;
            }
            else
            {
                int rm = (int)(op & 0xF);
                int shiftType = (int)(op >> 5) & 3;
                uint rmValue = cpu.R[rm];

                if ((op & (1u << 4)) != 0)
                {
                    uint amount = cpu.R[(int)(op >> 8) & 0xF] & 0xFF;
                    if (rm == 15) rmValue += 4;
                    if (rn == 15) rnValue += 4;
                    operand2 = Alu.ShiftRegister(rmValue, shiftType, amount, ref carry);
                    cpu.AddCycles(1);
                }
                else
                {
                    operand2 = Alu.ShiftImmediate(rmValue, shiftType, (int)(op >> 7) & 0x1F, ref carry);
                }
            }

            // With Rd = R15 and S = 1 the flags come from SPSR, not the result.
            bool flags = setFlags && rd != 15;
            uint result;

            switch (opcode)
            {
                case OP_AND:
                    result = rnValue & operand2;
                    if (flags) cpu.SetNZC(result, carry);
                    break;
                case OP_EOR:
                    result = rnValue ^ operand2;
                    if (flags) cpu.SetNZC(result, carry);
                    break;
                case OP_SUB:
                    result = Alu.Subtract(cpu, rnValue, operand2, flags);
                    break;
                case OP_RSB:
                    result = Alu.Subtract(cpu, operand2, rnValue, flags);
                    break;
                case OP_ADD:
                    result = Alu.Add(cpu, rnValue, operand2, flags);
                    break;
                case OP_ADC:
                    result = Alu.AddWithCarry(cpu, rnValue, operand2, flags);
                    break;
                case OP_SBC:
                    result = Alu.SubtractWithCarry(cpu, rnValue, operand2, flags);
                    break;
                case OP_RSC:
                    result = Alu.SubtractWithCarry(cpu, operand2, rnValue, flags);
                    break;
                case OP_TST:
                    cpu.SetNZC(rnValue & operand2, carry);
                    return;
                case OP_TEQ:
                    cpu.SetNZC(rnValue ^ operand2, carry);
                    return;
                case OP_CMP:
                    Alu.Subtract(cpu, rnValue, operand2, true);
                    return;
                case OP_CMN:
                    Alu.Add(cpu, rnValue, operand2, true);
                    return;
                case OP_ORR:
                    result = rnValue | operand2;
                    if (flags) cpu.SetNZC(result, carry);
                    break;
                case OP_MOV:
                    if (op == 0xE1A0C00Cu)
                        cpu.NoCashMessage?.Invoke(cpu); // mov r12,r12: debug message marker
                    result = operand2;
                    if (flags) cpu.SetNZC(result, carry);
                    break;
                case OP_BIC:
                    result = rnValue & ~operand2;
                    if (flags) cpu.SetNZC(result, carry);
                    break;
                default:
                    result = ~operand2;
                    if (flags) cpu.SetNZC(result, carry);
                    break;
            }

            if (rd == 15)
            {
                if (setFlags)
                    cpu.RestoreCpsrFromSpsr();
                cpu.Branch(result);
            }
            else
            {
                cpu.R[rd] = result;
            }
        }
    }
}
