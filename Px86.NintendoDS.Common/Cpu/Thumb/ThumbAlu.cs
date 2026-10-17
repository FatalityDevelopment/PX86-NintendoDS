namespace Px86.NintendoDS.Common.Cpu.Thumb
{
    /// <summary>
    /// Thumb arithmetic/logic formats 1–5, 12 and 13.
    ///
    /// All low-register ALU forms set flags (Thumb has no S bit). Hi-register
    /// ADD/MOV leave flags alone; CMP sets them. Format 4 opcodes:
    ///
    ///   0 AND  1 EOR  2 LSL  3 LSR  4 ASR  5 ADC  6 SBC  7 ROR
    ///   8 TST  9 NEG  A CMP  B CMN  C ORR  D MUL  E BIC  F MVN
    ///
    /// Reference: GBATEK "THUMB Opcodes: Register Operations (ALU, BX)".
    /// </summary>
    public static class ThumbAlu
    {
        public static void ShiftImmediate(ArmCpu cpu, uint op)
        {
            int rd = (int)(op & 7);
            int rs = (int)(op >> 3) & 7;
            bool carry = cpu.FlagC;
            uint result = Alu.ShiftImmediate(cpu.R[rs], (int)(op >> 11) & 3, (int)(op >> 6) & 0x1F, ref carry);
            cpu.R[rd] = result;
            cpu.SetNZC(result, carry);
        }

        public static void AddSubtract(ArmCpu cpu, uint op)
        {
            int rd = (int)(op & 7);
            int rs = (int)(op >> 3) & 7;
            uint field = (op >> 6) & 7;
            uint operand = (op & (1u << 10)) != 0 ? field : cpu.R[(int)field];
            cpu.R[rd] = (op & (1u << 9)) != 0
                ? Alu.Subtract(cpu, cpu.R[rs], operand, true)
                : Alu.Add(cpu, cpu.R[rs], operand, true);
        }

        public static void Immediate(ArmCpu cpu, uint op)
        {
            int rd = (int)(op >> 8) & 7;
            uint imm = op & 0xFF;
            switch ((op >> 11) & 3)
            {
                case 0:
                    cpu.R[rd] = imm;
                    cpu.SetNZ(imm);
                    break;
                case 1:
                    Alu.Subtract(cpu, cpu.R[rd], imm, true);
                    break;
                case 2:
                    cpu.R[rd] = Alu.Add(cpu, cpu.R[rd], imm, true);
                    break;
                default:
                    cpu.R[rd] = Alu.Subtract(cpu, cpu.R[rd], imm, true);
                    break;
            }
        }

        public static void AluOperation(ArmCpu cpu, uint op)
        {
            int rd = (int)(op & 7);
            int rs = (int)(op >> 3) & 7;
            uint a = cpu.R[rd];
            uint b = cpu.R[rs];
            bool carry = cpu.FlagC;
            uint result;

            switch ((op >> 6) & 0xF)
            {
                case 0x0: result = a & b; cpu.SetNZ(result); break;
                case 0x1: result = a ^ b; cpu.SetNZ(result); break;
                case 0x2:
                    result = Alu.ShiftRegister(a, Alu.SHIFT_LSL, b & 0xFF, ref carry);
                    cpu.SetNZC(result, carry);
                    cpu.AddCycles(1);
                    break;
                case 0x3:
                    result = Alu.ShiftRegister(a, Alu.SHIFT_LSR, b & 0xFF, ref carry);
                    cpu.SetNZC(result, carry);
                    cpu.AddCycles(1);
                    break;
                case 0x4:
                    result = Alu.ShiftRegister(a, Alu.SHIFT_ASR, b & 0xFF, ref carry);
                    cpu.SetNZC(result, carry);
                    cpu.AddCycles(1);
                    break;
                case 0x5: result = Alu.AddWithCarry(cpu, a, b, true); break;
                case 0x6: result = Alu.SubtractWithCarry(cpu, a, b, true); break;
                case 0x7:
                    result = Alu.ShiftRegister(a, Alu.SHIFT_ROR, b & 0xFF, ref carry);
                    cpu.SetNZC(result, carry);
                    cpu.AddCycles(1);
                    break;
                case 0x8: cpu.SetNZ(a & b); return;
                case 0x9: result = Alu.Subtract(cpu, 0, b, true); break;
                case 0xA: Alu.Subtract(cpu, a, b, true); return;
                case 0xB: Alu.Add(cpu, a, b, true); return;
                case 0xC: result = a | b; cpu.SetNZ(result); break;
                case 0xD:
                    result = a * b;
                    cpu.SetNZ(result);
                    cpu.AddCycles(Arm.Multiply.MultiplierCycles(a));
                    break;
                case 0xE: result = a & ~b; cpu.SetNZ(result); break;
                default: result = ~b; cpu.SetNZ(result); break;
            }
            cpu.R[rd] = result;
        }

        public static void HiRegisterOperation(ArmCpu cpu, uint op)
        {
            int rd = (int)(op & 7) | (int)((op >> 4) & 8);
            int rs = (int)((op >> 3) & 0xF);
            uint value = cpu.R[rs];

            switch ((op >> 8) & 3)
            {
                case 0:
                {
                    uint result = cpu.R[rd] + value;
                    if (rd == 15) cpu.Branch(result);
                    else cpu.R[rd] = result;
                    break;
                }
                case 1:
                    Alu.Subtract(cpu, cpu.R[rd], value, true);
                    break;
                case 2:
                    if (op == 0x46E4)
                        cpu.NoCashMessage?.Invoke(cpu); // mov r12,r12: debug message marker
                    if (rd == 15) cpu.Branch(value);
                    else cpu.R[rd] = value;
                    break;
                default:
                    if ((op & 0x80) != 0)
                    {
                        if (!cpu.IsArmV5)
                        {
                            ThumbInstructionTable.Undefined(cpu, op);
                            return;
                        }
                        cpu.R[14] = (cpu.Pc + 2) | 1;
                    }
                    cpu.BranchExchange(value);
                    break;
            }
        }

        public static void LoadAddress(ArmCpu cpu, uint op)
        {
            int rd = (int)(op >> 8) & 7;
            uint offset = (op & 0xFF) << 2;
            cpu.R[rd] = ((op & (1u << 11)) != 0 ? cpu.R[13] : cpu.R[15] & ~2u) + offset;
        }

        public static void AdjustStackPointer(ArmCpu cpu, uint op)
        {
            uint offset = (op & 0x7F) << 2;
            cpu.R[13] = (op & 0x80) != 0 ? cpu.R[13] - offset : cpu.R[13] + offset;
        }
    }
}
