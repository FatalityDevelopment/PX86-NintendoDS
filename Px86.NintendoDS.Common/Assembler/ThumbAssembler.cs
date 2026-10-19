namespace Px86.NintendoDS.Common.Assembler
{
    /// <summary>
    /// Thumb-state assembler (ARMv5TE Thumb, or ARMv4T for the ARM7): one instruction per call,
    /// 2 bytes, or 4 for a BL/BLX pair.
    ///
    /// It reads back what <see cref="Disassembler.ThumbDisassembler"/> prints ("adds r0, r1, #3",
    /// "ldr r0, [0x02001234]", "add sp, #-0x10", "nop") and the usual spellings: the "s" suffix
    /// is optional on instructions that always set flags, "sub sp, #16", "mov/add/cmp" with a
    /// high register, "neg"/"rsbs rd, rm, #0", "muls rd, rm, rd", "ldr r0, =value", "adr".
    ///
    /// Register-to-register mov/add without "s" assemble to the high-register form (no flags),
    /// as the disassembler prints them; "movs r0, r1" is lsls r0, r1, #0 and "cmp r0, r1" with
    /// two low registers is the ALU compare.
    ///
    /// PC-relative values are computed from the instruction's address + 4 (word-aligned for loads,
    /// adr and blx).
    ///
    /// Reference: ARM Architecture Reference Manual (ARMv5TE) A6–A7; GBATEK "THUMB Instruction Set".
    /// </summary>
    public static class ThumbAssembler
    {
        private static readonly string[] AluNames =
        {
            "and", "eor", "lsl", "lsr", "asr", "adc", "sbc", "ror",
            "tst", "neg", "cmp", "cmn", "orr", "mul", "bic", "mvn",
        };

        private static readonly string[] Conditions =
        {
            "eq", "ne", "cs", "cc", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le",
        };

        public static AssembleResult Assemble(string line, uint address, AssemblerContext? context = null)
        {
            var ctx = context ?? AssemblerContext.Default;
            try
            {
                var (mnemonic, ops) = AsmSyntax.Split(line);
                if (mnemonic.Length == 0)
                    return AssembleResult.Fail("Empty line.");
                return AssembleResult.Halves(Encode(mnemonic, ops, address, ctx));
            }
            catch (AsmException ex)
            {
                return AssembleResult.Fail(ex.Message);
            }
        }

        private static ushort[] One(int value) => new[] { (ushort)value };

        private static ushort[] Encode(string m, List<string> ops, uint address, AssemblerContext ctx)
        {
            switch (m)
            {
                case "undefined":
                    Count(ops, 1, m);
                    return One((int)(AsmSyntax.Value(ops[0], address, ctx) & 0xFFFF));
                case "nop":
                    Count(ops, 0, m);
                    return One(0x46C0);
                case "swi":
                case "svc":
                    Count(ops, 1, m);
                    return One(0xDF00 | Byte(ops[0], address, ctx, m));
                case "bkpt":
                    Count(ops, 1, m);
                    RequireV5(ctx, m);
                    return One(0xBE00 | Byte(ops[0], address, ctx, m));
                case "bx":
                case "blx" when AsmSyntax.TryRegister(ops.FirstOrDefault() ?? "") != null:
                    Count(ops, 1, m);
                    if (m == "blx") RequireV5(ctx, m);
                    return One((m == "blx" ? 0x4780 : 0x4700) | AsmSyntax.Register(ops[0]) << 3);
                case "bl":
                case "blx":
                    return LongBranch(m == "blx", ops, address, ctx);
                case "b":
                {
                    Count(ops, 1, m);
                    uint target = AsmSyntax.Value(ops[0], address, ctx);
                    int offset = AsmSyntax.BranchOffset(target, address + 4, 2, 11, ctx, "b");
                    return One(0xE000 | offset & 0x7FF);
                }
                case "push":
                case "pop":
                {
                    Count(ops, 1, m);
                    bool pop = m == "pop";
                    uint list = AsmSyntax.RegisterList(ops[0]);
                    uint extra = pop ? 0x8000u : 0x4000u;
                    if ((list & ~(0xFFu | extra)) != 0)
                        throw new AsmException($"Thumb {m} takes r0-r7{(pop ? " and pc" : " and lr")}.");
                    return One((pop ? 0xBC00 : 0xB400) | ((list & extra) != 0 ? 0x100 : 0) | (int)(list & 0xFF));
                }
                case "ldmia":
                case "stmia":
                case "ldm":
                case "stm":
                case "ldmfd":
                case "stmea":
                {
                    Count(ops, 2, m);
                    bool load = m.StartsWith("ld");
                    int rb = AsmSyntax.LowRegister(ops[0].TrimEnd('!'));
                    uint list = AsmSyntax.RegisterList(ops[1]);
                    if (list > 0xFF) throw new AsmException($"Thumb {m} takes r0-r7.");
                    return One((load ? 0xC800 : 0xC000) | rb << 8 | (int)list);
                }
                case "adr":
                {
                    Count(ops, 2, m);
                    int rd = AsmSyntax.LowRegister(ops[0]);
                    uint target = AsmSyntax.Value(ops[1], address, ctx);
                    return One(0xA000 | rd << 8 | PcRelative(target, address, ctx, m));
                }
            }

            if (m.Length == 3 && m[0] == 'b' && Array.IndexOf(Conditions, m[1..]) is var cond and >= 0)
            {
                Count(ops, 1, m);
                uint target = AsmSyntax.Value(ops[0], address, ctx);
                int offset = AsmSyntax.BranchOffset(target, address + 4, 2, 8, ctx, m);
                return One(0xD000 | cond << 8 | offset & 0xFF);
            }

            if (EncodeLoadStore(m, ops, address, ctx) is int ls)
                return One(ls);
            return One(EncodeArithmetic(m, ops, address, ctx));
        }

        // ── Arithmetic ────────────────────────────────────────────────────────

        private static int EncodeArithmetic(string m, List<string> ops, uint address, AssemblerContext ctx)
        {
            bool s = m.EndsWith('s') && m.Length == 4 || m is "adds" or "subs" or "movs" or "negs" or "rsbs" or "muls";
            string name = s ? m[..^1] : m;

            // Special registers: sp/pc arithmetic and the high-register forms.
            if (name is "add" or "sub" && ops.Count >= 2 && AsmSyntax.TryRegister(ops[0]) == 13 && (ops.Count == 2 || AsmSyntax.TryRegister(ops[1]) == 13) && AsmSyntax.IsValue(ops[^1]))
            {
                int value = (int)AsmSyntax.Value(ops[^1], address, ctx);
                if (name == "sub") value = -value;
                int magnitude = Math.Abs(value);
                if (magnitude % 4 != 0 || magnitude > 508) throw new AsmException("add sp needs a multiple of 4 from -508 to 508.");
                bool negative = value < 0 || value == 0 && (name == "sub") != ops[^1].Contains('-'); // "#-0x0" keeps its sign bit
                return 0xB000 | (negative ? 0x80 : 0) | magnitude >> 2;
            }
            if (name == "add" && ops.Count == 3 && AsmSyntax.TryRegister(ops[1]) is 13 or 15 && AsmSyntax.IsValue(ops[2]))
            {
                int rd = AsmSyntax.LowRegister(ops[0]);
                bool sp = AsmSyntax.Register(ops[1]) == 13;
                uint value = AsmSyntax.Value(ops[2], address, ctx);
                if (value % 4 != 0 || value > 1020) throw new AsmException($"add {ops[1]} needs a multiple of 4 from 0 to 1020.");
                return (sp ? 0xA800 : 0xA000) | rd << 8 | (int)(value >> 2);
            }
            if (!s && name is "add" or "mov" or "cmp" && ops.Count == 2 && AsmSyntax.TryRegister(ops[0]) is int hd && AsmSyntax.TryRegister(ops[1]) is int hs
                && (name != "cmp" || hd > 7 || hs > 7))
            {
                int op = name switch { "add" => 0, "cmp" => 1, _ => 2 };
                return 0x4400 | op << 8 | (hd & 8) << 4 | hs << 3 | hd & 7;
            }

            // Shifts by an immediate: lsl/lsr/asr rd, rs, #n (and movs rd, rs = lsls #0).
            if (name is "lsl" or "lsr" or "asr" && ops.Count == 3 && AsmSyntax.IsValue(ops[2]))
            {
                int type = name switch { "lsl" => 0, "lsr" => 1, _ => 2 };
                uint amount = AsmSyntax.Value(ops[2], address, ctx);
                if (type == 0 ? amount > 31 : amount is 0 or > 32) throw new AsmException($"{m} #{amount} is out of range.");
                return type << 11 | (int)(amount & 0x1F) << 6 | AsmSyntax.LowRegister(ops[1]) << 3 | AsmSyntax.LowRegister(ops[0]);
            }
            if (name == "mov" && ops.Count == 2 && AsmSyntax.TryRegister(ops[1]) != null)
                return AsmSyntax.LowRegister(ops[1]) << 3 | AsmSyntax.LowRegister(ops[0]);

            // Immediate forms.
            if (name is "add" or "sub" or "mov" or "cmp" && AsmSyntax.IsValue(ops[^1]))
            {
                int rd = AsmSyntax.LowRegister(ops[0]);
                uint value = AsmSyntax.Value(ops[^1], address, ctx);
                if (ops.Count == 3 && name is "add" or "sub")
                {
                    int rs = AsmSyntax.LowRegister(ops[1]);
                    if (value < 8)
                        return 0x1C00 | (name == "sub" ? 0x200 : 0) | (int)value << 6 | rs << 3 | rd;
                    if (rs != rd) throw new AsmException($"{m} {ops[0]}, {ops[1]}, #{value}: a 3-register-style immediate must be 0-7.");
                }
                else if (ops.Count != 2)
                    throw new AsmException($"{m} takes 2 operands.");
                if (value > 0xFF) throw new AsmException($"#{value} doesn't fit in 8 bits.");
                int op = name switch { "mov" => 0, "cmp" => 1, "add" => 2, _ => 3 };
                return 0x2000 | op << 11 | rd << 8 | (int)value;
            }

            // add/sub rd, rs, rn.
            if (name is "add" or "sub" && ops.Count == 3)
                return 0x1800 | (name == "sub" ? 0x200 : 0) | AsmSyntax.LowRegister(ops[2]) << 6 | AsmSyntax.LowRegister(ops[1]) << 3 | AsmSyntax.LowRegister(ops[0]);
            if (name is "add" && s && ops.Count == 2)
                return 0x1800 | AsmSyntax.LowRegister(ops[1]) << 6 | AsmSyntax.LowRegister(ops[0]) << 3 | AsmSyntax.LowRegister(ops[0]);

            // "rsbs rd, rm, #0" = neg.
            if (name == "rsb" && ops.Count == 3 && AsmSyntax.IsValue(ops[2]) && AsmSyntax.Value(ops[2], address, ctx) == 0)
            {
                name = "neg";
                ops = ops.Take(2).ToList();
            }
            // "muls rd, rm, rd".
            if (name == "mul" && ops.Count == 3)
            {
                if (AsmSyntax.Register(ops[2]) != AsmSyntax.Register(ops[0])) throw new AsmException("Thumb muls needs rd, rm, rd.");
                ops = ops.Take(2).ToList();
            }

            int alu = Array.IndexOf(AluNames, name);
            if (alu < 0) throw new AsmException($"Unknown Thumb instruction \"{m}\".");
            Count(ops, 2, m);
            return 0x4000 | alu << 6 | AsmSyntax.LowRegister(ops[1]) << 3 | AsmSyntax.LowRegister(ops[0]);
        }

        // ── Loads and stores ──────────────────────────────────────────────────

        private static int? EncodeLoadStore(string m, List<string> ops, uint address, AssemblerContext ctx)
        {
            string[] names = { "str", "strb", "strh", "ldr", "ldrb", "ldrh", "ldrsb", "ldrsh" };
            if (!names.Contains(m))
                return null;
            Count(ops, 2, m);
            int rd = AsmSyntax.LowRegister(ops[0]);
            string a = ops[1].Trim();

            if (a.StartsWith('='))
            {
                if (m != "ldr") throw new AsmException("Only ldr can load a =literal.");
                uint value = AsmSyntax.Value(a[1..], address, ctx);
                if (ctx.AllocateLiteral == null)
                {
                    if (value <= 0xFF) return 0x2000 | rd << 8 | (int)value;
                    throw new AsmException($"=0x{value:X} needs a literal pool here.");
                }
                return 0x4800 | rd << 8 | PcRelative(ctx.AllocateLiteral(value), address, ctx, "ldr =");
            }
            if (!a.StartsWith('[') || !a.EndsWith(']'))
                throw new AsmException($"Expected an address like [r0, #4], got \"{a}\".");
            var parts = AsmSyntax.Split("x " + a[1..^1]).Operands;

            if (parts.Count == 1 && AsmSyntax.TryRegister(parts[0]) == null)
            {
                if (m != "ldr") throw new AsmException("Only ldr can load from a PC-relative address.");
                return 0x4800 | rd << 8 | PcRelative(AsmSyntax.Value(parts[0], address, ctx), address, ctx, m);
            }
            int rb = AsmSyntax.Register(parts[0]);
            if (parts.Count == 2 && AsmSyntax.TryRegister(parts[1]) is int ro)
            {
                if (rb > 7 || ro > 7) throw new AsmException("Register offsets need low registers.");
                int op = Array.IndexOf(new[] { "str", "strh", "strb", "ldrsb", "ldr", "ldrh", "ldrb", "ldrsh" }, m);
                return 0x5000 | op << 9 | ro << 6 | rb << 3 | rd;
            }
            uint offset = parts.Count == 2 ? AsmSyntax.Value(parts[1], address, ctx) : 0;
            if (parts.Count > 2) throw new AsmException($"Bad address \"{a}\".");
            if (rb is 13 or 15)
            {
                if (m is not ("ldr" or "str") || rb == 15 && m == "str")
                    throw new AsmException($"{m} can't use [{parts[0]}, #imm].");
                if (offset % 4 != 0 || offset > 1020) throw new AsmException("The offset must be a multiple of 4 from 0 to 1020.");
                return (rb == 13 ? (m == "ldr" ? 0x9800 : 0x9000) : 0x4800) | rd << 8 | (int)(offset >> 2);
            }
            if (rb > 7) throw new AsmException("Thumb loads and stores need a low base register (or sp/pc).");
            if (m is "ldrsb" or "ldrsh") throw new AsmException($"{m} only has a register offset: [rb, ro].");
            int scale = m switch { "ldr" or "str" => 4, "ldrh" or "strh" => 2, _ => 1 };
            if (offset % scale != 0 || offset / scale > 31) throw new AsmException($"{m} offset must be a multiple of {scale} from 0 to {31 * scale}.");
            int baseOp = m switch { "str" => 0x6000, "ldr" => 0x6800, "strb" => 0x7000, "ldrb" => 0x7800, "strh" => 0x8000, _ => 0x8800 };
            return baseOp | (int)(offset / scale) << 6 | rb << 3 | rd;
        }

        // ── Branches ──────────────────────────────────────────────────────────

        private static ushort[] LongBranch(bool exchange, List<string> ops, uint address, AssemblerContext ctx)
        {
            Count(ops, 1, exchange ? "blx" : "bl");
            if (exchange) RequireV5(ctx, "blx");
            uint target = AsmSyntax.Value(ops[0], address, ctx);
            // BLX clears bit 1 of the result, so count from the word-aligned PC.
            uint origin = exchange ? (address + 4) & ~3u : address + 4;
            if (exchange && !ctx.AllowUnresolved && (target & 3) != 0)
                throw new AsmException($"blx target 0x{target:X8} must be word-aligned (ARM code).");
            int halves = AsmSyntax.BranchOffset(target, origin, 2, 22, ctx, exchange ? "blx" : "bl");
            return new[] { (ushort)(0xF000 | halves >> 11 & 0x7FF), (ushort)((exchange ? 0xE800 : 0xF800) | halves & 0x7FF) };
        }

        private static int PcRelative(uint target, uint address, AssemblerContext ctx, string what)
        {
            if (ctx.AllowUnresolved) return 0;
            uint origin = (address + 4) & ~3u;
            long diff = (long)target - origin;
            if (diff < 0 || diff > 1020 || diff % 4 != 0)
                throw new AsmException($"{what}: 0x{target:X8} must be word-aligned and 0-1020 bytes after 0x{origin:X8}.");
            return (int)(diff >> 2);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void Count(List<string> ops, int count, string m)
        {
            if (ops.Count != count)
                throw new AsmException($"{m} takes {count} operand{(count == 1 ? "" : "s")}, got {ops.Count}.");
        }

        private static int Byte(string text, uint address, AssemblerContext ctx, string m)
        {
            uint v = AsmSyntax.Value(text, address, ctx);
            if (v > 0xFF) throw new AsmException($"{m} number must fit in 8 bits.");
            return (int)v;
        }

        private static void RequireV5(AssemblerContext ctx, string what)
        {
            if (!ctx.IsArmV5) throw new AsmException($"{what} needs ARMv5 (the ARM9); the ARM7 is ARMv4T.");
        }
    }
}
