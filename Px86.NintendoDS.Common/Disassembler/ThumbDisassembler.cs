namespace Px86.NintendoDS.Common.Disassembler
{
    /// <summary>
    /// Thumb-state disassembler. PC-relative targets resolve against
    /// <c>address + 4</c>. A BL/BLX pair needs both halves: pass the suffix
    /// in <paramref name="nextOpcode"/> when disassembling the prefix to get a
    /// resolved target.
    /// </summary>
    public static class ThumbDisassembler
    {
        private static readonly string[] AluNames =
        {
            "and", "eor", "lsl", "lsr", "asr", "adc", "sbc", "ror",
            "tst", "neg", "cmp", "cmn", "orr", "mul", "bic", "mvn",
        };

        private static readonly string[] Conditions =
        {
            "eq", "ne", "cs", "cc", "mi", "pl", "vs", "vc",
            "hi", "ls", "ge", "lt", "gt", "le", "", "",
        };

        private static string Reg(int r) => ArmDisassembler.Reg(r);

        public static string Disassemble(ushort op, uint address, ushort? nextOpcode = null)
        {
            int rd = op & 7;
            int rs = (op >> 3) & 7;
            int rn = (op >> 6) & 7;

            switch (op >> 13)
            {
                case 0:
                    if ((op & 0x1800) == 0x1800)
                    {
                        string name = (op & 0x200) != 0 ? "sub" : "add";
                        string operand = (op & 0x400) != 0 ? $"#{rn}" : Reg(rn);
                        return $"{name}s {Reg(rd)}, {Reg(rs)}, {operand}";
                    }
                    {
                        string[] shifts = { "lsl", "lsr", "asr" };
                        int amount = (op >> 6) & 0x1F;
                        if (amount == 0 && ((op >> 11) & 3) != 0) amount = 32;
                        return $"{shifts[(op >> 11) & 3]}s {Reg(rd)}, {Reg(rs)}, #{amount}";
                    }
                case 1:
                {
                    string[] names = { "movs", "cmp", "adds", "subs" };
                    return $"{names[(op >> 11) & 3]} {Reg((op >> 8) & 7)}, #0x{op & 0xFF:X}";
                }
                case 2:
                    if ((op & 0xFC00) == 0x4000)
                        return $"{AluNames[(op >> 6) & 0xF]}s {Reg(rd)}, {Reg(rs)}";
                    if ((op & 0xFC00) == 0x4400)
                    {
                        int hd = rd | ((op >> 4) & 8);
                        int hs = (op >> 3) & 0xF;
                        return ((op >> 8) & 3) switch
                        {
                            0 => $"add {Reg(hd)}, {Reg(hs)}",
                            1 => $"cmp {Reg(hd)}, {Reg(hs)}",
                            2 => hd == 8 && hs == 8 ? "nop" : $"mov {Reg(hd)}, {Reg(hs)}",
                            _ => $"{((op & 0x80) != 0 ? "blx" : "bx")} {Reg(hs)}",
                        };
                    }
                    if ((op & 0xF800) == 0x4800)
                    {
                        uint target = ((address + 4) & ~3u) + (uint)((op & 0xFF) << 2);
                        return $"ldr {Reg((op >> 8) & 7)}, [0x{target:X8}]";
                    }
                    if ((op & 0x0200) == 0)
                    {
                        string[] names = { "str", "strb", "ldr", "ldrb" };
                        return $"{names[(op >> 10) & 3]} {Reg(rd)}, [{Reg(rs)}, {Reg(rn)}]";
                    }
                    else
                    {
                        string[] names = { "strh", "ldrsb", "ldrh", "ldrsh" };
                        return $"{names[(op >> 10) & 3]} {Reg(rd)}, [{Reg(rs)}, {Reg(rn)}]";
                    }
                case 3:
                {
                    int imm = (op >> 6) & 0x1F;
                    bool isByte = (op & 0x1000) != 0;
                    string name = ((op & 0x800) != 0 ? "ldr" : "str") + (isByte ? "b" : "");
                    return $"{name} {Reg(rd)}, [{Reg(rs)}, #0x{(isByte ? imm : imm << 2):X}]";
                }
                case 4:
                    if ((op & 0x1000) == 0)
                        return $"{((op & 0x800) != 0 ? "ldrh" : "strh")} {Reg(rd)}, [{Reg(rs)}, #0x{((op >> 6) & 0x1F) << 1:X}]";
                    return $"{((op & 0x800) != 0 ? "ldr" : "str")} {Reg((op >> 8) & 7)}, [sp, #0x{(op & 0xFF) << 2:X}]";
                case 5:
                    if ((op & 0x1000) == 0)
                        return $"add {Reg((op >> 8) & 7)}, {((op & 0x800) != 0 ? "sp" : "pc")}, #0x{(op & 0xFF) << 2:X}";
                    if ((op & 0xFF00) == 0xB000)
                        return $"add sp, #{((op & 0x80) != 0 ? "-" : "")}0x{(op & 0x7F) << 2:X}";
                    if ((op & 0xF600) == 0xB400)
                    {
                        bool pop = (op & 0x800) != 0;
                        uint list = (uint)(op & 0xFF);
                        if ((op & 0x100) != 0) list |= pop ? 0x8000u : 0x4000u;
                        return $"{(pop ? "pop" : "push")} {ArmDisassembler.RegisterList(list)}";
                    }
                    if ((op & 0xFF00) == 0xBE00)
                        return $"bkpt 0x{op & 0xFF:X}";
                    return $"undefined 0x{op:X4}";
                case 6:
                    if ((op & 0x1000) == 0)
                        return $"{((op & 0x800) != 0 ? "ldmia" : "stmia")} {Reg((op >> 8) & 7)}!, {ArmDisassembler.RegisterList((uint)(op & 0xFF))}";
                    {
                        int cond = (op >> 8) & 0xF;
                        if (cond == 0xF) return $"swi 0x{op & 0xFF:X}";
                        if (cond == 0xE) return $"undefined 0x{op:X4}";
                        uint target = (uint)(address + 4 + (sbyte)(op & 0xFF) * 2);
                        return $"b{Conditions[cond]} 0x{target:X8}";
                    }
                default:
                    switch ((op >> 11) & 3)
                    {
                        case 0:
                        {
                            int offset = ((op << 21) >> 20);
                            return $"b 0x{(uint)(address + 4 + offset):X8}";
                        }
                        case 1:
                            return $"blx (suffix) 0x{(op & 0x7FF) << 1:X}";
                        case 2:
                            if (nextOpcode is ushort next && (next & 0xE800) == 0xE800)
                            {
                                int hi = ((op << 21) >> 9);
                                uint target = (uint)(address + 4 + hi) + (uint)((next & 0x7FF) << 1);
                                bool exchange = (next & 0x1000) == 0;
                                return exchange ? $"blx 0x{target & ~3u:X8}" : $"bl 0x{target:X8}";
                            }
                            return $"bl (prefix) 0x{op & 0x7FF:X}";
                        default:
                            return $"bl (suffix) 0x{(op & 0x7FF) << 1:X}";
                    }
            }
        }
    }
}
