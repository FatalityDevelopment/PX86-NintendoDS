namespace Px86.NintendoDS.Common.Cpu.Thumb
{
    /// <summary>Handler for one Thumb opcode.</summary>
    public delegate void ThumbHandler(ArmCpu cpu, uint opcode);

    /// <summary>
    /// Thumb decode table, indexed by opcode bits 15–6.
    ///
    ///   Fmt  Bits 15–11  Instruction
    ///    1   000xx       LSL/LSR/ASR Rd, Rs, #imm5
    ///    2   00011       ADD/SUB Rd, Rs, Rn|#imm3
    ///    3   001xx       MOV/CMP/ADD/SUB Rd, #imm8
    ///    4   010000      ALU operations (AND … MVN)
    ///    5   010001      Hi-register ADD/CMP/MOV, BX/BLX
    ///    6   01001       LDR Rd, [PC, #imm8×4]
    ///    7   0101 xx0    LDR/STR{B} Rd, [Rb, Ro]
    ///    8   0101 xx1    STRH/LDSB/LDRH/LDSH Rd, [Rb, Ro]
    ///    9   011xx       LDR/STR{B} Rd, [Rb, #imm5]
    ///   10   1000x       LDRH/STRH Rd, [Rb, #imm5×2]
    ///   11   1001x       LDR/STR Rd, [SP, #imm8×4]
    ///   12   1010x       ADD Rd, PC|SP, #imm8×4
    ///   13   10110000    ADD SP, #±imm7×4
    ///   14   1011x10x    PUSH/POP {Rlist, LR|PC}
    ///   15   1100x       LDMIA/STMIA Rb!, {Rlist}
    ///   16   1101        B{cond} label
    ///   17   11011111    SWI nn
    ///   18   11100       B label
    ///   19   11110/11111 BL prefix / suffix; 11101 BLX suffix (ARMv5)
    ///
    /// Reference: GBATEK "THUMB Instruction Summary" / "THUMB Binary Opcode Format".
    /// </summary>
    public static class ThumbInstructionTable
    {
        public static readonly ThumbHandler[] Handlers = Build();

        private static ThumbHandler[] Build()
        {
            var table = new ThumbHandler[1024];
            for (int i = 0; i < table.Length; i++)
                table[i] = Classify((uint)i);
            return table;
        }

        private static ThumbHandler Classify(uint i)
        {
            uint top5 = i >> 5;
            switch (top5)
            {
                case 0: case 1: case 2:
                    return ThumbAlu.ShiftImmediate;
                case 3:
                    return ThumbAlu.AddSubtract;
                case 4: case 5: case 6: case 7:
                    return ThumbAlu.Immediate;
                case 8:
                    return ((i >> 4) & 1) == 0 ? ThumbAlu.AluOperation : ThumbAlu.HiRegisterOperation;
                case 9:
                    return ThumbLoadStore.PcRelativeLoad;
                case 10: case 11:
                    return ((i >> 3) & 1) == 0 ? ThumbLoadStore.RegisterOffset : ThumbLoadStore.SignExtended;
                case 12: case 13: case 14: case 15:
                    return ThumbLoadStore.ImmediateOffset;
                case 16: case 17:
                    return ThumbLoadStore.Halfword;
                case 18: case 19:
                    return ThumbLoadStore.SpRelative;
                case 20: case 21:
                    return ThumbAlu.LoadAddress;
                case 22: case 23:
                {
                    uint top8 = i >> 2;
                    if (top8 == 0xB0) return ThumbAlu.AdjustStackPointer;
                    if (top8 == 0xB4 || top8 == 0xB5) return ThumbLoadStore.Push;
                    if (top8 == 0xBC || top8 == 0xBD) return ThumbLoadStore.Pop;
                    if (top8 == 0xBE) return ThumbBranches.Breakpoint;
                    return Undefined;
                }
                case 24: case 25:
                    return ThumbLoadStore.MultipleLoadStore;
                case 26: case 27:
                {
                    uint cond = (i >> 2) & 0xF;
                    if (cond == 0xF) return ThumbBranches.SoftwareInterrupt;
                    if (cond == 0xE) return Undefined;
                    return ThumbBranches.ConditionalBranch;
                }
                case 28:
                    return ThumbBranches.UnconditionalBranch;
                case 29:
                    return ThumbBranches.LongBranchExchangeSuffix;
                case 30:
                    return ThumbBranches.LongBranchPrefix;
                default:
                    return ThumbBranches.LongBranchSuffix;
            }
        }

        public static void Undefined(ArmCpu cpu, uint opcode) => cpu.UndefinedInstruction(opcode);
    }
}
