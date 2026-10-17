using System.Numerics;

namespace Px86.NintendoDS.Common.Cpu.Thumb
{
    /// <summary>
    /// Thumb memory formats 6–11, 14 and 15.
    ///
    /// Misaligned accesses follow the same rules as ARM state (rotated LDR,
    /// ARMv4 rotated LDRH / LDRSH-as-LDRSB). POP {PC} interworks on ARMv5.
    ///
    /// LDMIA writes back unless Rb is in the list. STMIA with Rb in the list
    /// stores the old base if Rb is first (ARMv4) or always (ARMv5), otherwise
    /// the new base. An empty list transfers R15 on ARMv4 and moves Rb by 0x40.
    ///
    /// Reference: GBATEK "THUMB Opcodes: Memory Load/Store" and
    ///            "THUMB Opcodes: Memory Multiple Load/Store (PUSH/POP and LDM/STM)".
    /// </summary>
    public static class ThumbLoadStore
    {
        public static void PcRelativeLoad(ArmCpu cpu, uint op)
        {
            int rd = (int)(op >> 8) & 7;
            uint address = (cpu.R[15] & ~2u) + ((op & 0xFF) << 2);
            cpu.R[rd] = cpu.Bus.Read32(address & ~3u);
            cpu.AddCycles(2);
        }

        public static void RegisterOffset(ArmCpu cpu, uint op)
        {
            int rd = (int)(op & 7);
            uint address = cpu.R[(int)(op >> 3) & 7] + cpu.R[(int)(op >> 6) & 7];
            switch ((op >> 10) & 3)
            {
                case 0: cpu.Bus.Write32(address & ~3u, cpu.R[rd]); cpu.AddCycles(1); break;
                case 1: cpu.Bus.Write8(address, (byte)cpu.R[rd]); cpu.AddCycles(1); break;
                case 2: cpu.R[rd] = Alu.ReadWordRotated(cpu, address); cpu.AddCycles(2); break;
                default: cpu.R[rd] = cpu.Bus.Read8(address); cpu.AddCycles(2); break;
            }
        }

        public static void SignExtended(ArmCpu cpu, uint op)
        {
            int rd = (int)(op & 7);
            uint address = cpu.R[(int)(op >> 3) & 7] + cpu.R[(int)(op >> 6) & 7];
            switch ((op >> 10) & 3)
            {
                case 0: cpu.Bus.Write16(address & ~1u, (ushort)cpu.R[rd]); cpu.AddCycles(1); break;
                case 1: cpu.R[rd] = (uint)(sbyte)cpu.Bus.Read8(address); cpu.AddCycles(2); break;
                case 2: cpu.R[rd] = Alu.ReadHalfword(cpu, address); cpu.AddCycles(2); break;
                default: cpu.R[rd] = Alu.ReadSignedHalfword(cpu, address); cpu.AddCycles(2); break;
            }
        }

        public static void ImmediateOffset(ArmCpu cpu, uint op)
        {
            int rd = (int)(op & 7);
            uint baseAddress = cpu.R[(int)(op >> 3) & 7];
            uint imm = (op >> 6) & 0x1F;
            switch ((op >> 11) & 3)
            {
                case 0: cpu.Bus.Write32((baseAddress + (imm << 2)) & ~3u, cpu.R[rd]); cpu.AddCycles(1); break;
                case 1: cpu.R[rd] = Alu.ReadWordRotated(cpu, baseAddress + (imm << 2)); cpu.AddCycles(2); break;
                case 2: cpu.Bus.Write8(baseAddress + imm, (byte)cpu.R[rd]); cpu.AddCycles(1); break;
                default: cpu.R[rd] = cpu.Bus.Read8(baseAddress + imm); cpu.AddCycles(2); break;
            }
        }

        public static void Halfword(ArmCpu cpu, uint op)
        {
            int rd = (int)(op & 7);
            uint address = cpu.R[(int)(op >> 3) & 7] + (((op >> 6) & 0x1F) << 1);
            if ((op & (1u << 11)) != 0)
            {
                cpu.R[rd] = Alu.ReadHalfword(cpu, address);
                cpu.AddCycles(2);
            }
            else
            {
                cpu.Bus.Write16(address & ~1u, (ushort)cpu.R[rd]);
                cpu.AddCycles(1);
            }
        }

        public static void SpRelative(ArmCpu cpu, uint op)
        {
            int rd = (int)(op >> 8) & 7;
            uint address = cpu.R[13] + ((op & 0xFF) << 2);
            if ((op & (1u << 11)) != 0)
            {
                cpu.R[rd] = Alu.ReadWordRotated(cpu, address);
                cpu.AddCycles(2);
            }
            else
            {
                cpu.Bus.Write32(address & ~3u, cpu.R[rd]);
                cpu.AddCycles(1);
            }
        }

        public static void Push(ArmCpu cpu, uint op)
        {
            uint list = op & 0xFF;
            bool lr = (op & 0x100) != 0;
            int count = BitOperations.PopCount(list) + (lr ? 1 : 0);
            uint address = cpu.R[13] - (uint)(count * 4);
            cpu.R[13] = address;

            for (int i = 0; i < 8; i++)
            {
                if ((list & (1u << i)) == 0) continue;
                cpu.Bus.Write32(address, cpu.R[i]);
                address += 4;
            }
            if (lr)
                cpu.Bus.Write32(address, cpu.R[14]);
            cpu.AddCycles(count);
        }

        public static void Pop(ArmCpu cpu, uint op)
        {
            uint list = op & 0xFF;
            bool pc = (op & 0x100) != 0;
            uint address = cpu.R[13];

            for (int i = 0; i < 8; i++)
            {
                if ((list & (1u << i)) == 0) continue;
                cpu.R[i] = cpu.Bus.Read32(address & ~3u);
                address += 4;
            }

            if (pc)
            {
                uint value = cpu.Bus.Read32(address & ~3u);
                address += 4;
                cpu.R[13] = address;
                if (cpu.IsArmV5)
                    cpu.BranchExchange(value);
                else
                    cpu.Branch(value);
            }
            else
            {
                cpu.R[13] = address;
            }
            cpu.AddCycles(BitOperations.PopCount(list) + 1);
        }

        public static void MultipleLoadStore(ArmCpu cpu, uint op)
        {
            int rb = (int)(op >> 8) & 7;
            uint list = op & 0xFF;
            uint address = cpu.R[rb];
            bool load = (op & (1u << 11)) != 0;

            if (list == 0)
            {
                if (!cpu.IsArmV5)
                {
                    if (load)
                        cpu.Branch(cpu.Bus.Read32(address & ~3u));
                    else
                        cpu.Bus.Write32(address & ~3u, cpu.R[15] + 2);
                }
                cpu.R[rb] = address + 0x40;
                return;
            }

            uint newBase = address + (uint)BitOperations.PopCount(list) * 4;

            if (load)
            {
                for (int i = 0; i < 8; i++)
                {
                    if ((list & (1u << i)) == 0) continue;
                    cpu.R[i] = cpu.Bus.Read32(address & ~3u);
                    address += 4;
                }
                if ((list & (1u << rb)) == 0)
                    cpu.R[rb] = newBase;
                cpu.AddCycles(BitOperations.PopCount(list) + 1);
            }
            else
            {
                bool first = true;
                for (int i = 0; i < 8; i++)
                {
                    if ((list & (1u << i)) == 0) continue;
                    uint value = i == rb && !first && !cpu.IsArmV5 ? newBase : cpu.R[i];
                    cpu.Bus.Write32(address & ~3u, value);
                    address += 4;
                    first = false;
                }
                cpu.R[rb] = newBase;
                cpu.AddCycles(BitOperations.PopCount(list));
            }
        }
    }
}
