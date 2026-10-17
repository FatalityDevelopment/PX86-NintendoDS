using Px86.NintendoDS.EmulatorCore.Debugging;

namespace Px86.NintendoDS.EmulatorCore.Remote
{
    /// <summary>
    /// Register access for PINE GetRegister/SetRegister.
    ///
    ///   Category 0 (GPR)   num 0–15  r0–r15 of the current mode (r15 = address of the next instruction)
    ///                      num 16    CPSR (writing it can switch mode)
    ///                      num 17    SPSR of the current mode (User/System read CPSR, writes are ignored)
    ///   Category 1 (CP15)  ARM9 only: num 0 control (C1,C0,0), 1 DTCM region (C9,C1,0),
    ///                      2 ITCM region (C9,C1,1), 3 main ID (C0,C0,0, read-only)
    /// </summary>
    public static class PineRegisters
    {
        private static readonly (int Crn, int Crm, int Opc2)[] Cp15Map =
        {
            (1, 0, 0),
            (9, 1, 0),
            (9, 1, 1),
            (0, 0, 0),
        };

        public static uint? Read(NdsEmulator nds, DebugCpu cpu, byte category, byte number)
        {
            var core = nds.Cpu(cpu);
            switch (category)
            {
                case PineOpcodes.CategoryGpr:
                    return number switch
                    {
                        < 15 => core.R[number],
                        15 => core.Pc,
                        16 => core.Cpsr,
                        17 => core.Spsr,
                        _ => null,
                    };
                case PineOpcodes.CategoryCp15:
                    if (cpu != DebugCpu.Arm9 || number >= Cp15Map.Length)
                        return null;
                    var (crn, crm, opc2) = Cp15Map[number];
                    return nds.Cp15.Read(0, crn, crm, opc2);
                default:
                    return null;
            }
        }

        public static bool Write(NdsEmulator nds, DebugCpu cpu, byte category, byte number, uint value)
        {
            var core = nds.Cpu(cpu);
            switch (category)
            {
                case PineOpcodes.CategoryGpr:
                    switch (number)
                    {
                        case < 15:
                            core.R[number] = value;
                            return true;
                        case 15:
                            core.SetPc((value & ~1u) | (core.IsThumb ? 1u : 0u));
                            return true;
                        case 16:
                            core.SetCpsr(value);
                            return true;
                        case 17:
                            core.Spsr = value;
                            return true;
                        default:
                            return false;
                    }
                case PineOpcodes.CategoryCp15:
                    if (cpu != DebugCpu.Arm9 || number >= 3)
                        return false;
                    var (crn, crm, opc2) = Cp15Map[number];
                    nds.Cp15.Write(0, crn, crm, opc2, value);
                    return true;
                default:
                    return false;
            }
        }
    }
}
