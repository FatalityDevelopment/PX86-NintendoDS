namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>Handler for one ARM-state opcode. The condition has already passed.</summary>
    public delegate void ArmHandler(ArmCpu cpu, uint opcode);

    /// <summary>
    /// ARM-state decode table.
    ///
    /// Index = (opcode bits 27–20) &lt;&lt; 4 | (opcode bits 7–4). Those 12 bits are
    /// enough to tell every ARMv5TE instruction class apart; the handlers decode
    /// the remaining fields themselves.
    ///
    ///   27–25  Class
    ///   000    Data processing (register), multiply, SWP, halfword/doubleword
    ///          transfers, and the "miscellaneous" space (MRS/MSR/BX/BLX/CLZ/
    ///          QADD/BKPT/SMLAxy) where opcode = TST/TEQ/CMP/CMN with S = 0
    ///   001    Data processing (immediate), MSR immediate
    ///   010    LDR/STR immediate offset
    ///   011    LDR/STR register offset (bit 4 = 1 is undefined)
    ///   100    LDM/STM
    ///   101    B/BL
    ///   110    LDC/STC (no coprocessor on the DS uses these)
    ///   111    CDP / MCR / MRC (bit 24 = 0), SWI (bit 24 = 1)
    ///
    /// Opcodes with condition 1111 go to <see cref="ExecuteUnconditional"/>
    /// (ARMv5 BLX immediate and PLD; "never" on ARMv4).
    ///
    /// Reference: GBATEK "ARM Binary Opcode Format".
    /// </summary>
    public static class ArmInstructionTable
    {
        public static readonly ArmHandler[] Handlers = Build();

        private static ArmHandler[] Build()
        {
            var table = new ArmHandler[4096];
            for (int i = 0; i < table.Length; i++)
                table[i] = Classify((uint)(i >> 4), (uint)(i & 0xF));
            return table;
        }

        private static ArmHandler Classify(uint hi, uint lo)
        {
            switch (hi >> 5)
            {
                case 0:
                    if ((lo & 0x9) == 0x9)
                    {
                        if (lo == 0x9)
                        {
                            if ((hi & 0x1C) == 0x00) return Multiply.MultiplyAccumulate;
                            if ((hi & 0x18) == 0x08) return Multiply.MultiplyLong;
                            if ((hi & 0x1B) == 0x10) return SingleDataTransfer.Swap;
                            return Undefined;
                        }
                        return SingleDataTransfer.HalfwordTransfer;
                    }
                    if ((hi & 0x19) == 0x10)
                        return ClassifyMiscellaneous(hi, lo);
                    return DataProcessing.Execute;

                case 1:
                    if ((hi & 0x19) == 0x10)
                        return (hi & 0x02) != 0 ? PsrTransfer.MoveToStatus : Undefined;
                    return DataProcessing.Execute;

                case 2:
                    return SingleDataTransfer.WordByteTransfer;

                case 3:
                    return (lo & 1) != 0 ? Undefined : SingleDataTransfer.WordByteTransfer;

                case 4:
                    return BlockDataTransfer.Execute;

                case 5:
                    return Branches.BranchWithLink;

                case 6:
                    return Coprocessor.DataTransfer;

                default:
                    if ((hi & 0x10) != 0) return Miscellaneous.SoftwareInterrupt;
                    return (lo & 1) != 0 ? Coprocessor.RegisterTransfer : Coprocessor.DataOperation;
            }
        }

        /// <summary>Opcode field = 10xx, S = 0: the ARMv5 miscellaneous instruction space.</summary>
        private static ArmHandler ClassifyMiscellaneous(uint hi, uint lo)
        {
            uint op = (hi >> 1) & 0x3; // bits 22–21
            switch (lo)
            {
                case 0x0:
                    return (op & 1) != 0 ? PsrTransfer.MoveToStatus : PsrTransfer.MoveFromStatus;
                case 0x1:
                    if (op == 1) return Branches.BranchExchange;
                    if (op == 3) return Miscellaneous.CountLeadingZeros;
                    return Undefined;
                case 0x3:
                    return op == 1 ? Branches.BranchLinkExchangeRegister : Undefined;
                case 0x5:
                    return Miscellaneous.SaturatingArithmetic;
                case 0x7:
                    return op == 1 ? Miscellaneous.Breakpoint : Undefined;
                case 0x8:
                case 0xA:
                case 0xC:
                case 0xE:
                    return Multiply.SignedHalfwordMultiply;
                default:
                    return Undefined;
            }
        }

        /// <summary>Condition field 1111.</summary>
        public static void ExecuteUnconditional(ArmCpu cpu, uint opcode)
        {
            if (!cpu.IsArmV5)
                return; // ARMv4: "never"

            if ((opcode & 0x0E000000u) == 0x0A000000u)
            {
                Branches.BranchLinkExchangeImmediate(cpu, opcode);
                return;
            }

            if ((opcode & 0x0D70F000u) == 0x0550F000u)
                return; // PLD — cache preload hint, no-op

            Undefined(cpu, opcode);
        }

        public static void Undefined(ArmCpu cpu, uint opcode)
        {
            // ARMv5-only instructions in the misc space are undefined on ARMv4 too,
            // so there is nothing architecture-specific to do here.
            cpu.UndefinedInstruction(opcode);
        }
    }
}
