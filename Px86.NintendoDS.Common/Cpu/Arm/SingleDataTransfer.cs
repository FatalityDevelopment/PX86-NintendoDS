namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>
    /// ARM single-register loads and stores.
    ///
    ///   LDR/STR{B}   cond 01 I P U B W L Rn Rd offset12 | shifted Rm
    ///   LDRH/STRH,   cond 000 P U I W L Rn Rd offHi 1 S H 1 offLo | Rm
    ///   LDRSB/LDRSH
    ///   LDRD/STRD    (ARMv5TE: L = 0 with SH = 10 / 11)
    ///   SWP{B}       cond 00010 B 00 Rn Rd 0000 1001 Rm
    ///
    ///   P  1 = pre-index (offset applied before the access), 0 = post-index
    ///   U  1 = add offset, 0 = subtract
    ///   W  write back (always implied for post-index)
    ///
    /// Behaviour notes
    ///   • Misaligned LDR rotates the aligned word (both cores).
    ///   • STR of R15 stores PC + 12.
    ///   • A load into Rn wins over the base writeback.
    ///   • LDR into R15 interworks on ARMv5 (bit 0 → Thumb).
    ///
    /// Reference: GBATEK "ARM Opcodes: Memory: Single Data Transfer (LDR, STR, PLD)"
    ///            and "Halfword, Doubleword, and Signed Data Transfer".
    /// </summary>
    public static class SingleDataTransfer
    {
        public static void WordByteTransfer(ArmCpu cpu, uint op)
        {
            int rn = (int)(op >> 16) & 0xF;
            int rd = (int)(op >> 12) & 0xF;
            bool pre = (op & (1u << 24)) != 0;
            bool up = (op & (1u << 23)) != 0;
            bool isByte = (op & (1u << 22)) != 0;
            bool writeBack = (op & (1u << 21)) != 0 || !pre;
            bool load = (op & (1u << 20)) != 0;

            uint offset;
            if ((op & (1u << 25)) == 0)
            {
                offset = op & 0xFFF;
            }
            else
            {
                bool carry = cpu.FlagC;
                offset = Alu.ShiftImmediate(cpu.R[(int)(op & 0xF)], (int)(op >> 5) & 3, (int)(op >> 7) & 0x1F, ref carry);
            }

            uint baseAddress = cpu.R[rn];
            uint offsetAddress = up ? baseAddress + offset : baseAddress - offset;
            uint address = pre ? offsetAddress : baseAddress;

            if (load)
            {
                uint value = isByte ? cpu.Bus.Read8(address) : Alu.ReadWordRotated(cpu, address);
                if (writeBack)
                    cpu.R[rn] = offsetAddress;
                cpu.AddCycles(2);
                if (rd == 15)
                    cpu.LoadPc(value);
                else
                    cpu.R[rd] = value;
            }
            else
            {
                uint value = rd == 15 ? cpu.R[15] + 4 : cpu.R[rd];
                if (isByte)
                    cpu.Bus.Write8(address, (byte)value);
                else
                    cpu.Bus.Write32(address & ~3u, value);
                if (writeBack)
                    cpu.R[rn] = offsetAddress;
                cpu.AddCycles(1);
            }
        }

        public static void HalfwordTransfer(ArmCpu cpu, uint op)
        {
            int rn = (int)(op >> 16) & 0xF;
            int rd = (int)(op >> 12) & 0xF;
            bool pre = (op & (1u << 24)) != 0;
            bool up = (op & (1u << 23)) != 0;
            bool immediate = (op & (1u << 22)) != 0;
            bool writeBack = (op & (1u << 21)) != 0 || !pre;
            bool load = (op & (1u << 20)) != 0;
            int sh = (int)(op >> 5) & 3;

            uint offset = immediate ? ((op >> 4) & 0xF0) | (op & 0xF) : cpu.R[(int)(op & 0xF)];
            uint baseAddress = cpu.R[rn];
            uint offsetAddress = up ? baseAddress + offset : baseAddress - offset;
            uint address = pre ? offsetAddress : baseAddress;

            if (load)
            {
                uint value = sh switch
                {
                    1 => Alu.ReadHalfword(cpu, address),
                    2 => (uint)(sbyte)cpu.Bus.Read8(address),
                    _ => Alu.ReadSignedHalfword(cpu, address),
                };
                if (writeBack)
                    cpu.R[rn] = offsetAddress;
                cpu.AddCycles(2);
                if (rd == 15)
                    cpu.LoadPc(value);
                else
                    cpu.R[rd] = value;
                return;
            }

            switch (sh)
            {
                case 1: // STRH
                {
                    uint value = rd == 15 ? cpu.R[15] + 4 : cpu.R[rd];
                    cpu.Bus.Write16(address & ~1u, (ushort)value);
                    if (writeBack)
                        cpu.R[rn] = offsetAddress;
                    cpu.AddCycles(1);
                    break;
                }
                case 2: // LDRD
                {
                    if (!cpu.IsArmV5)
                    {
                        ArmInstructionTable.Undefined(cpu, op);
                        return;
                    }
                    rd &= ~1;
                    uint lo = cpu.Bus.Read32(address & ~3u);
                    uint hi = cpu.Bus.Read32((address + 4) & ~3u);
                    if (writeBack)
                        cpu.R[rn] = offsetAddress;
                    cpu.R[rd] = lo;
                    if (rd + 1 == 15)
                        cpu.LoadPc(hi);
                    else
                        cpu.R[rd + 1] = hi;
                    cpu.AddCycles(3);
                    break;
                }
                default: // STRD
                {
                    if (!cpu.IsArmV5)
                    {
                        ArmInstructionTable.Undefined(cpu, op);
                        return;
                    }
                    rd &= ~1;
                    cpu.Bus.Write32(address & ~3u, cpu.R[rd]);
                    cpu.Bus.Write32((address + 4) & ~3u, rd + 1 == 15 ? cpu.R[15] + 4 : cpu.R[rd + 1]);
                    if (writeBack)
                        cpu.R[rn] = offsetAddress;
                    cpu.AddCycles(2);
                    break;
                }
            }
        }

        public static void Swap(ArmCpu cpu, uint op)
        {
            int rn = (int)(op >> 16) & 0xF;
            int rd = (int)(op >> 12) & 0xF;
            int rm = (int)(op & 0xF);
            uint address = cpu.R[rn];
            uint source = cpu.R[rm];

            if ((op & (1u << 22)) != 0)
            {
                uint value = cpu.Bus.Read8(address);
                cpu.Bus.Write8(address, (byte)source);
                cpu.R[rd] = value;
            }
            else
            {
                uint value = Alu.ReadWordRotated(cpu, address);
                cpu.Bus.Write32(address & ~3u, source);
                cpu.R[rd] = value;
            }
            cpu.AddCycles(3);
        }
    }
}
