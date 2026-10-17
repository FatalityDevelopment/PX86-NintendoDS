using System.Numerics;
using System.Text;

namespace Px86.NintendoDS.Common.Disassembler
{
    /// <summary>
    /// ARM-state disassembler (ARMv5TE) producing GBATEK/ARM-style syntax,
    /// e.g. <c>ldreq r0, [r1, #0x4]!</c>. Used by trace logging and tests.
    ///
    /// <c>address</c> is the address of the instruction itself; PC-relative
    /// targets are resolved against <c>address + 8</c>.
    /// </summary>
    public static class ArmDisassembler
    {
        private static readonly string[] Conditions =
        {
            "eq", "ne", "cs", "cc", "mi", "pl", "vs", "vc",
            "hi", "ls", "ge", "lt", "gt", "le", "", "nv",
        };

        private static readonly string[] AluNames =
        {
            "and", "eor", "sub", "rsb", "add", "adc", "sbc", "rsc",
            "tst", "teq", "cmp", "cmn", "orr", "mov", "bic", "mvn",
        };

        private static readonly string[] ShiftNames = { "lsl", "lsr", "asr", "ror" };

        public static string Reg(int r) => r switch
        {
            13 => "sp",
            14 => "lr",
            15 => "pc",
            _ => "r" + r,
        };

        public static string Disassemble(uint op, uint address)
        {
            uint condField = op >> 28;
            string cond = Conditions[condField];

            if (condField == 0xF)
            {
                if ((op & 0x0E000000u) == 0x0A000000u)
                {
                    int offset = ((int)(op << 8)) >> 6;
                    uint target = (uint)(address + 8 + offset) + ((op >> 23) & 2);
                    return $"blx 0x{target:X8}";
                }
                if ((op & 0x0D70F000u) == 0x0550F000u)
                    return "pld " + AddressMode(op, address);
                return $"undefined 0x{op:X8}";
            }

            uint cls = (op >> 25) & 7;
            switch (cls)
            {
                case 0:
                case 1:
                    return DisassembleClass0(op, cond, address);
                case 2:
                case 3:
                    if (cls == 3 && (op & 0x10) != 0)
                        return $"undefined 0x{op:X8}";
                    return $"{((op & (1u << 20)) != 0 ? "ldr" : "str")}{cond}{((op & (1u << 22)) != 0 ? "b" : "")}{((op & 0x01200000u) == 0x00200000u ? "t" : "")} {Reg((int)(op >> 12) & 0xF)}, {AddressMode(op, address)}";
                case 4:
                    return DisassembleBlock(op, cond);
                case 5:
                {
                    int offset = ((int)(op << 8)) >> 6;
                    uint target = (uint)(address + 8 + offset);
                    return $"b{((op & (1u << 24)) != 0 ? "l" : "")}{cond} 0x{target:X8}";
                }
                case 6:
                    return $"{((op & (1u << 20)) != 0 ? "ldc" : "stc")}{cond} p{(op >> 8) & 0xF}, c{(op >> 12) & 0xF}, ...";
                default:
                    if ((op & (1u << 24)) != 0)
                        return $"swi{cond} 0x{op & 0xFFFFFF:X}";
                    if ((op & 0x10) != 0)
                        return $"{((op & (1u << 20)) != 0 ? "mrc" : "mcr")}{cond} p{(op >> 8) & 0xF}, {(op >> 21) & 7}, {Reg((int)(op >> 12) & 0xF)}, c{(op >> 16) & 0xF}, c{op & 0xF}, {(op >> 5) & 7}";
                    return $"cdp{cond} p{(op >> 8) & 0xF}, {(op >> 20) & 0xF}, c{(op >> 12) & 0xF}, c{(op >> 16) & 0xF}, c{op & 0xF}, {(op >> 5) & 7}";
            }
        }

        private static string DisassembleClass0(uint op, string cond, uint address)
        {
            bool immediate = (op & (1u << 25)) != 0;
            int rn = (int)(op >> 16) & 0xF;
            int rd = (int)(op >> 12) & 0xF;
            int rs = (int)(op >> 8) & 0xF;
            int rm = (int)(op & 0xF);

            if (!immediate)
            {
                if ((op & 0x0FFFFFF0u) == 0x012FFF10u) return $"bx{cond} {Reg(rm)}";
                if ((op & 0x0FFFFFF0u) == 0x012FFF30u) return $"blx{cond} {Reg(rm)}";
                if ((op & 0x0FFF0FF0u) == 0x016F0F10u) return $"clz{cond} {Reg(rd)}, {Reg(rm)}";
                if ((op & 0xFFF000F0u) == 0xE1200070u) return $"bkpt 0x{((op >> 4) & 0xFFF0) | (op & 0xF):X}";

                if ((op & 0x0F9000F0u) == 0x01000050u)
                {
                    string[] names = { "qadd", "qsub", "qdadd", "qdsub" };
                    return $"{names[(op >> 21) & 3]}{cond} {Reg(rd)}, {Reg(rm)}, {Reg(rn)}";
                }

                if ((op & 0x0F900090u) == 0x01000080u)
                {
                    string x = (op & 0x20) != 0 ? "t" : "b";
                    string y = (op & 0x40) != 0 ? "t" : "b";
                    return ((op >> 21) & 3) switch
                    {
                        // These encodings put Rd in bits 19–16 and the accumulator in bits 15–12.
                        0 => $"smla{x}{y}{cond} {Reg(rn)}, {Reg(rm)}, {Reg(rs)}, {Reg(rd)}",
                        1 => (op & 0x20) == 0
                            ? $"smlaw{y}{cond} {Reg(rn)}, {Reg(rm)}, {Reg(rs)}, {Reg(rd)}"
                            : $"smulw{y}{cond} {Reg(rn)}, {Reg(rm)}, {Reg(rs)}",
                        2 => $"smlal{x}{y}{cond} {Reg(rd)}, {Reg(rn)}, {Reg(rm)}, {Reg(rs)}",
                        _ => $"smul{x}{y}{cond} {Reg(rn)}, {Reg(rm)}, {Reg(rs)}",
                    };
                }

                if ((op & 0x0FC000F0u) == 0x00000090u)
                {
                    string s = (op & (1u << 20)) != 0 ? "s" : "";
                    return (op & (1u << 21)) != 0
                        ? $"mla{cond}{s} {Reg(rn)}, {Reg(rm)}, {Reg(rs)}, {Reg(rd)}"
                        : $"mul{cond}{s} {Reg(rn)}, {Reg(rm)}, {Reg(rs)}";
                }

                if ((op & 0x0F8000F0u) == 0x00800090u)
                {
                    string name = ((op & (1u << 22)) != 0 ? "s" : "u") + ((op & (1u << 21)) != 0 ? "mlal" : "mull");
                    string s = (op & (1u << 20)) != 0 ? "s" : "";
                    return $"{name}{cond}{s} {Reg(rd)}, {Reg(rn)}, {Reg(rm)}, {Reg(rs)}";
                }

                if ((op & 0x0FB00FF0u) == 0x01000090u)
                    return $"swp{cond}{((op & (1u << 22)) != 0 ? "b" : "")} {Reg(rd)}, {Reg(rm)}, [{Reg(rn)}]";

                if ((op & 0x0E000090u) == 0x00000090u && (op & 0x60) != 0)
                    return DisassembleHalfword(op, cond);
            }

            uint opcode = (op >> 21) & 0xF;
            bool setFlags = (op & (1u << 20)) != 0;

            // MRS / MSR live where TST/TEQ/CMP/CMN have S = 0.
            if (opcode >= 8 && opcode <= 0xB && !setFlags)
            {
                string psr = (op & (1u << 22)) != 0 ? "spsr" : "cpsr";
                if ((op & (1u << 21)) == 0)
                    return $"mrs{cond} {Reg(rd)}, {psr}";

                var fields = new StringBuilder();
                if ((op & (1u << 19)) != 0) fields.Append('f');
                if ((op & (1u << 18)) != 0) fields.Append('s');
                if ((op & (1u << 17)) != 0) fields.Append('x');
                if ((op & (1u << 16)) != 0) fields.Append('c');
                string source = immediate
                    ? $"#0x{BitOperations.RotateRight(op & 0xFF, (int)((op >> 8) & 0xF) * 2):X}"
                    : Reg(rm);
                return $"msr{cond} {psr}_{fields}, {source}";
            }

            string operand2 = immediate
                ? $"#0x{BitOperations.RotateRight(op & 0xFF, (int)((op >> 8) & 0xF) * 2):X}"
                : ShiftedRegister(op);
            string sfx = setFlags && (opcode < 8 || opcode > 0xB) ? "s" : "";
            string name2 = AluNames[opcode] + cond + sfx;

            return opcode switch
            {
                >= 8 and <= 0xB => $"{name2} {Reg(rn)}, {operand2}",
                0xD or 0xF => $"{name2} {Reg(rd)}, {operand2}",
                _ => $"{name2} {Reg(rd)}, {Reg(rn)}, {operand2}",
            };
        }

        private static string ShiftedRegister(uint op)
        {
            int rm = (int)(op & 0xF);
            int type = (int)(op >> 5) & 3;

            if ((op & 0x10) != 0)
                return $"{Reg(rm)}, {ShiftNames[type]} {Reg((int)(op >> 8) & 0xF)}";

            int amount = (int)(op >> 7) & 0x1F;
            if (amount == 0)
            {
                return type switch
                {
                    0 => Reg(rm),
                    3 => $"{Reg(rm)}, rrx",
                    _ => $"{Reg(rm)}, {ShiftNames[type]} #32",
                };
            }
            return $"{Reg(rm)}, {ShiftNames[type]} #{amount}";
        }

        private static string AddressMode(uint op, uint address)
        {
            int rn = (int)(op >> 16) & 0xF;
            bool pre = (op & (1u << 24)) != 0;
            bool up = (op & (1u << 23)) != 0;
            bool writeBack = (op & (1u << 21)) != 0;
            string sign = up ? "" : "-";

            string offset;
            if ((op & (1u << 25)) == 0)
            {
                uint imm = op & 0xFFF;
                if (rn == 15 && pre && !writeBack)
                {
                    uint target = up ? address + 8 + imm : address + 8 - imm;
                    return $"[0x{target:X8}]";
                }
                offset = imm == 0 ? "" : $", #{sign}0x{imm:X}";
            }
            else
            {
                offset = $", {sign}{ShiftedRegister(op & ~0x10u)}";
            }

            if (pre)
                return $"[{Reg(rn)}{offset}]{(writeBack ? "!" : "")}";
            return $"[{Reg(rn)}]{offset}";
        }

        private static string DisassembleHalfword(uint op, string cond)
        {
            int rn = (int)(op >> 16) & 0xF;
            int rd = (int)(op >> 12) & 0xF;
            bool pre = (op & (1u << 24)) != 0;
            bool up = (op & (1u << 23)) != 0;
            bool writeBack = (op & (1u << 21)) != 0;
            bool load = (op & (1u << 20)) != 0;
            int sh = (int)(op >> 5) & 3;

            string name = load
                ? sh switch { 1 => "ldrh", 2 => "ldrsb", _ => "ldrsh" }
                : sh switch { 1 => "strh", 2 => "ldrd", _ => "strd" };

            string sign = up ? "" : "-";
            string offset = (op & (1u << 22)) != 0
                ? ((((op >> 4) & 0xF0) | (op & 0xF)) is var imm && imm != 0 ? $", #{sign}0x{imm:X}" : "")
                : $", {sign}{Reg((int)(op & 0xF))}";

            string addr = pre
                ? $"[{Reg(rn)}{offset}]{(writeBack ? "!" : "")}"
                : $"[{Reg(rn)}]{offset}";
            return $"{name}{cond} {Reg(rd)}, {addr}";
        }

        private static string DisassembleBlock(uint op, string cond)
        {
            int rn = (int)(op >> 16) & 0xF;
            bool load = (op & (1u << 20)) != 0;
            bool pre = (op & (1u << 24)) != 0;
            bool up = (op & (1u << 23)) != 0;
            string mode = (up ? "i" : "d") + (pre ? "b" : "a");

            string name;
            if (rn == 13 && load && mode == "ia" && (op & (1u << 21)) != 0) name = "pop";
            else if (rn == 13 && !load && mode == "db" && (op & (1u << 21)) != 0) name = "push";
            else name = (load ? "ldm" : "stm") + mode;

            string list = RegisterList(op & 0xFFFF);
            string caret = (op & (1u << 22)) != 0 ? "^" : "";
            if (name == "pop" || name == "push")
                return $"{name}{cond} {list}{caret}";
            return $"{name}{cond} {Reg(rn)}{((op & (1u << 21)) != 0 ? "!" : "")}, {list}{caret}";
        }

        public static string RegisterList(uint list)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < 16)
            {
                if ((list & (1u << i)) == 0)
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < 16 && (list & (1u << i)) != 0)
                    i++;
                int end = i - 1;
                parts.Add(end - start >= 2 ? $"{Reg(start)}-{Reg(end)}" : end == start ? Reg(start) : $"{Reg(start)}, {Reg(end)}");
            }
            return "{" + string.Join(", ", parts) + "}";
        }
    }
}
