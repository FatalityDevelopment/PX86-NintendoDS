using System.Numerics;

namespace Px86.NintendoDS.Common.Cpu.Arm
{
    /// <summary>
    /// ARM block transfers (LDM/STM).
    ///
    ///   cond 100 P U S W L Rn Rlist
    ///
    ///   P/U  IA (P=0 U=1), IB (P=1 U=1), DA (P=0 U=0), DB (P=1 U=0)
    ///   S    STM: store the User-bank registers.
    ///        LDM with R15 in Rlist: CPSR = SPSR after the load (exception return).
    ///        LDM without R15: load the User-bank registers.
    ///
    /// Registers always transfer lowest-numbered to lowest address.
    ///
    /// Edge cases (GBATEK "Strange Effects on Invalid Rlist's"):
    ///   • Empty Rlist: R15 is transferred on ARMv4 only; Rn moves by ±0x40 on both.
    ///   • STM with Rn in Rlist and writeback: ARMv4 stores the old base if Rn is
    ///     the first register, otherwise the new base. ARMv5 always stores the old base.
    ///   • LDM with Rn in Rlist and writeback: ARMv4 never writes back. ARMv5
    ///     writes back if Rn is the only register or is not the last register.
    ///   • LDM into R15 interworks on ARMv5.
    ///
    /// Reference: GBATEK "ARM Opcodes: Memory: Block Data Transfer (LDM, STM)".
    /// </summary>
    public static class BlockDataTransfer
    {
        public static void Execute(ArmCpu cpu, uint op)
        {
            int rn = (int)(op >> 16) & 0xF;
            bool pre = (op & (1u << 24)) != 0;
            bool up = (op & (1u << 23)) != 0;
            bool psrOrUser = (op & (1u << 22)) != 0;
            bool writeBack = (op & (1u << 21)) != 0;
            bool load = (op & (1u << 20)) != 0;
            uint list = op & 0xFFFF;

            uint baseAddress = cpu.R[rn];
            uint bytes;

            if (list == 0)
            {
                bytes = 0x40;
                if (!cpu.IsArmV5)
                    list = 0x8000;
            }
            else
            {
                bytes = (uint)BitOperations.PopCount(list) * 4;
            }

            uint start = up
                ? (pre ? baseAddress + 4 : baseAddress)
                : (pre ? baseAddress - bytes : baseAddress - bytes + 4);
            uint newBase = up ? baseAddress + bytes : baseAddress - bytes;

            bool hasPc = (list & 0x8000) != 0;
            bool userBank = psrOrUser && !(load && hasPc);
            uint address = start;

            if (load)
            {
                uint pcValue = 0;
                for (int i = 0; i < 16; i++)
                {
                    if ((list & (1u << i)) == 0)
                        continue;
                    uint value = cpu.Bus.Read32(address & ~3u);
                    address += 4;
                    if (i == 15)
                        pcValue = value;
                    else if (userBank)
                        cpu.SetUserRegister(i, value);
                    else
                        cpu.R[i] = value;
                }

                if (writeBack)
                {
                    if ((list & (1u << rn)) == 0)
                    {
                        cpu.R[rn] = newBase;
                    }
                    else if (cpu.IsArmV5)
                    {
                        bool onlyRn = list == (1u << rn);
                        bool rnIsLast = (31 - BitOperations.LeadingZeroCount(list)) == rn;
                        if (onlyRn || !rnIsLast)
                            cpu.R[rn] = newBase;
                    }
                }

                if (hasPc)
                {
                    if (psrOrUser)
                    {
                        cpu.RestoreCpsrFromSpsr();
                        cpu.Branch(pcValue);
                    }
                    else
                    {
                        cpu.LoadPc(pcValue);
                    }
                }

                cpu.AddCycles(BitOperations.PopCount(list) + 1);
            }
            else
            {
                bool first = true;
                for (int i = 0; i < 16; i++)
                {
                    if ((list & (1u << i)) == 0)
                        continue;

                    uint value;
                    if (i == 15)
                        value = cpu.R[15] + 4;
                    else if (i == rn && writeBack && !cpu.IsArmV5 && !first)
                        value = newBase;
                    else if (userBank)
                        value = cpu.GetUserRegister(i);
                    else
                        value = cpu.R[i];

                    cpu.Bus.Write32(address & ~3u, value);
                    address += 4;
                    first = false;
                }

                if (writeBack)
                    cpu.R[rn] = newBase;

                cpu.AddCycles(BitOperations.PopCount(list));
            }
        }
    }
}
