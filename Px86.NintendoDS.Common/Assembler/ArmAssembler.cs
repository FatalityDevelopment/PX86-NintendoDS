using System.Numerics;

namespace Px86.NintendoDS.Common.Assembler
{
    /// <summary>
    /// ARM-state assembler (ARMv5TE, or ARMv4T for the ARM7): one instruction per call.
    ///
    /// It reads back what <see cref="Disassembler.ArmDisassembler"/> prints, so
    /// disassemble → edit → assemble works, and the usual hand-written forms:
    ///   suffix order    pre-UAL "addeqs", "ldmeqia", "ldreqb" and UAL "addseq", "ldmiaeq", "ldrbeq"
    ///   immediates      "#0x10", "#16", "#-4"; a value that doesn't fit is retried with the
    ///                   complementary instruction (mov↔mvn, add↔sub, and↔bic, cmp↔cmn, adc↔sbc)
    ///   targets         absolute addresses or labels for b/bl/blx/adr and "[label]" loads
    ///   aliases         push/pop, nop, lsl/lsr/asr/ror/rrx, ldm/stm fd/ed/fa/ea, svc, adr
    ///   literals        "ldr r0, =value": a pool word (<see cref="AssemblerContext.AllocateLiteral"/>)
    ///                   or, without a pool, a mov/mvn when the value fits
    ///   raw words       "undefined 0x12345678" (as the disassembler prints unknown words)
    ///
    /// PC-relative values are computed from the instruction's address + 8.
    ///
    /// Reference: ARM Architecture Reference Manual (ARMv5TE) A3–A5; GBATEK "ARM CPU Instruction Summary (ARM)".
    /// </summary>
    public static class ArmAssembler
    {
        private static readonly string[] AluNames =
        {
            "and", "eor", "sub", "rsb", "add", "adc", "sbc", "rsc",
            "tst", "teq", "cmp", "cmn", "orr", "mov", "bic", "mvn",
        };

        private static readonly string[] ShiftNames = { "lsl", "lsr", "asr", "ror" };

        public static AssembleResult Assemble(string line, uint address, AssemblerContext? context = null)
        {
            var ctx = context ?? AssemblerContext.Default;
            try
            {
                var (mnemonic, ops) = AsmSyntax.Split(line);
                if (mnemonic.Length == 0)
                    return AssembleResult.Fail("Empty line.");
                return AssembleResult.Words(Encode(mnemonic, ops, address, ctx));
            }
            catch (AsmException ex)
            {
                return AssembleResult.Fail(ex.Message);
            }
        }

        /// <summary>The 12-bit operand field for a rotated 8-bit immediate, or null if the value can't be encoded.</summary>
        public static uint? EncodeImmediate(uint value)
        {
            for (int rot = 0; rot < 16; rot++)
            {
                uint imm = BitOperations.RotateLeft(value, rot * 2);
                if (imm <= 0xFF)
                    return (uint)rot << 8 | imm;
            }
            return null;
        }

        // ── Mnemonics ─────────────────────────────────────────────────────────

        /// <summary>Does <paramref name="mnemonic"/> = base + condition + one of <paramref name="suffixes"/> (either order)?</summary>
        private static bool Match(string mnemonic, string name, out uint cond, out string suffix, params string[] suffixes)
        {
            cond = 14;
            suffix = "";
            if (!mnemonic.StartsWith(name, StringComparison.Ordinal))
                return false;
            string rest = mnemonic[name.Length..];
            foreach (string s in suffixes.Length == 0 ? new[] { "" } : suffixes)
            {
                if (rest.StartsWith(s, StringComparison.Ordinal) && AsmSyntax.Condition(rest[s.Length..]) is >= 0 and var c1)
                {
                    cond = (uint)c1;
                    suffix = s;
                    return true;
                }
                if (rest.EndsWith(s, StringComparison.Ordinal) && AsmSyntax.Condition(rest[..^s.Length]) is >= 0 and var c2)
                {
                    cond = (uint)c2;
                    suffix = s;
                    return true;
                }
            }
            return false;
        }

        private static uint[] Encode(string m, List<string> ops, uint address, AssemblerContext ctx)
        {
            uint cond;
            string sfx;

            if (m == "undefined")
            {
                Count(ops, 1, m);
                return new[] { AsmSyntax.Value(ops[0], address, ctx) };
            }
            if (m == "nop")
            {
                Count(ops, 0, m);
                return new[] { 0xE1A00000u };
            }

            // Branches first: "bl", "bls", "blt" and "blx" share prefixes.
            if (Match(m, "bx", out cond, out _))
                return new[] { cond << 28 | 0x012FFF10u | Reg(ops, 0, 1, m) };
            if (Match(m, "blx", out cond, out _))
            {
                Count(ops, 1, m);
                RequireV5(ctx, "blx");
                if (AsmSyntax.TryRegister(ops[0]) is int rm)
                    return new[] { cond << 28 | 0x012FFF30u | (uint)rm };
                if (cond != 14)
                    throw new AsmException("blx to an address can't be conditional.");
                uint target = AsmSyntax.Value(ops[0], address, ctx);
                int halves = AsmSyntax.BranchOffset(target, address + 8, 2, 25, ctx, "blx");
                return new[] { 0xFA000000u | (uint)(halves & 1) << 24 | (uint)(halves >> 1) & 0xFFFFFF };
            }
            if (Match(m, "bl", out cond, out _) || Match(m, "b", out cond, out _))
            {
                Count(ops, 1, m);
                bool link = m.StartsWith("bl") && Match(m, "bl", out _, out _);
                uint target = AsmSyntax.Value(ops[0], address, ctx);
                int words = AsmSyntax.BranchOffset(target, address + 8, 4, 24, ctx, link ? "bl" : "b");
                return new[] { cond << 28 | (link ? 0x0B000000u : 0x0A000000u) | (uint)words & 0xFFFFFF };
            }

            if (Match(m, "swi", out cond, out _) || Match(m, "svc", out cond, out _))
            {
                Count(ops, 1, m);
                uint n = AsmSyntax.Value(ops[0], address, ctx);
                if (n > 0xFFFFFF) throw new AsmException("swi number must fit in 24 bits.");
                return new[] { cond << 28 | 0x0F000000u | n };
            }
            if (m == "bkpt")
            {
                Count(ops, 1, m);
                RequireV5(ctx, "bkpt");
                uint n = AsmSyntax.Value(ops[0], address, ctx);
                if (n > 0xFFFF) throw new AsmException("bkpt number must fit in 16 bits.");
                return new[] { 0xE1200070u | (n >> 4) << 8 | n & 0xF };
            }
            if (Match(m, "clz", out cond, out _))
            {
                Count(ops, 2, m);
                RequireV5(ctx, "clz");
                return new[] { cond << 28 | 0x016F0F10u | Reg(ops, 0) << 12 | Reg(ops, 1) };
            }
            string[] qNames = { "qadd", "qsub", "qdadd", "qdsub" };
            for (int q = 3; q >= 0; q--)
            {
                if (!Match(m, qNames[q], out cond, out _)) continue;
                Count(ops, 3, m);
                RequireV5(ctx, qNames[q]);
                return new[] { cond << 28 | 0x01000050u | (uint)q << 21 | Reg(ops, 2) << 16 | Reg(ops, 0) << 12 | Reg(ops, 1) };
            }
            if (EncodeDspMultiply(m, ops, ctx) is uint dsp)
                return new[] { dsp };
            if (Match(m, "mla", out cond, out sfx, "", "s"))
            {
                Count(ops, 4, m);
                return new[] { cond << 28 | 0x00200090u | S(sfx) | Reg(ops, 0) << 16 | Reg(ops, 3) << 12 | Reg(ops, 2) << 8 | Reg(ops, 1) };
            }
            foreach (var (name, bits) in new[] { ("umull", 0u), ("umlal", 1u << 21), ("smull", 1u << 22), ("smlal", 3u << 21) })
            {
                if (!Match(m, name, out cond, out sfx, "", "s")) continue;
                Count(ops, 4, m);
                return new[] { cond << 28 | 0x00800090u | bits | S(sfx) | Reg(ops, 1) << 16 | Reg(ops, 0) << 12 | Reg(ops, 3) << 8 | Reg(ops, 2) };
            }
            if (Match(m, "mul", out cond, out sfx, "", "s"))
            {
                Count(ops, 3, m);
                return new[] { cond << 28 | 0x00000090u | S(sfx) | Reg(ops, 0) << 16 | Reg(ops, 2) << 8 | Reg(ops, 1) };
            }
            if (Match(m, "swp", out cond, out sfx, "b", ""))
            {
                Count(ops, 3, m);
                if (!ops[2].StartsWith('[') || !ops[2].EndsWith(']')) throw new AsmException("swp needs [rn] as its third operand.");
                return new[] { cond << 28 | 0x01000090u | (sfx == "b" ? 1u << 22 : 0) | (uint)AsmSyntax.Register(ops[2][1..^1]) << 16 | Reg(ops, 0) << 12 | Reg(ops, 1) };
            }
            if (Match(m, "mrs", out cond, out _))
            {
                Count(ops, 2, m);
                return new[] { cond << 28 | 0x010F0000u | Psr(ops[1], out _) << 22 | Reg(ops, 0) << 12 };
            }
            if (Match(m, "msr", out cond, out _))
            {
                Count(ops, 2, m);
                uint r = Psr(ops[0], out uint mask);
                if (AsmSyntax.TryRegister(ops[1]) is int rm)
                    return new[] { cond << 28 | 0x0120F000u | r << 22 | mask << 16 | (uint)rm };
                uint imm = EncodeImmediate(AsmSyntax.Value(ops[1], address, ctx)) ?? throw new AsmException($"{ops[1]} is not an 8-bit rotated immediate.");
                return new[] { cond << 28 | 0x0320F000u | r << 22 | mask << 16 | imm };
            }
            if (Match(m, "mrc", out cond, out _) || Match(m, "mcr", out cond, out _))
            {
                Count(ops, 6, m, 5);
                bool load = m.StartsWith("mrc");
                uint opc2 = ops.Count == 6 ? Small(ops[5], 7, address, ctx) : 0;
                return new[] { cond << 28 | 0x0E000010u | Small(ops[1], 7, address, ctx) << 21 | (load ? 1u << 20 : 0) | CopReg(ops[3], 'c') << 16
                               | Reg(ops, 2) << 12 | CopReg(ops[0], 'p') << 8 | opc2 << 5 | CopReg(ops[4], 'c') };
            }
            if (Match(m, "cdp", out cond, out _))
            {
                Count(ops, 6, m, 5);
                uint opc2 = ops.Count == 6 ? Small(ops[5], 7, address, ctx) : 0;
                return new[] { cond << 28 | 0x0E000000u | Small(ops[1], 15, address, ctx) << 20 | CopReg(ops[3], 'c') << 16 | CopReg(ops[2], 'c') << 12
                               | CopReg(ops[0], 'p') << 8 | opc2 << 5 | CopReg(ops[4], 'c') };
            }
            if (m == "pld")
            {
                Count(ops, 1, m);
                RequireV5(ctx, "pld");
                uint word = AddressingMode(ops[0], null, address, ctx, out bool post, out _);
                if (post || (word & (1u << 21)) != 0) throw new AsmException("pld takes a pre-indexed address without writeback.");
                return new[] { 0xF450F000u | word };
            }

            // ── Block transfers ──
            if (m is "push" or "pop" || Match(m, "push", out _, out _) || Match(m, "pop", out _, out _))
            {
                bool pop = m.StartsWith("pop");
                Match(m, pop ? "pop" : "push", out cond, out _);
                Count(ops, 1, m);
                uint list = AsmSyntax.RegisterList(ops[0].TrimEnd('^'));
                uint user = ops[0].EndsWith('^') ? 1u << 22 : 0;
                return new[] { cond << 28 | (pop ? 0x08BD0000u : 0x092D0000u) | user | list };
            }
            foreach (bool load in new[] { true, false })
            {
                string name = load ? "ldm" : "stm";
                string[] modes = { "ia", "ib", "da", "db", "fd", "ed", "fa", "ea", "" };
                if (!Match(m, name, out cond, out sfx, modes)) continue;
                string mode = sfx switch
                {
                    "" => "ia",
                    "fd" => load ? "ia" : "db",
                    "ed" => load ? "ib" : "da",
                    "fa" => load ? "da" : "ib",
                    "ea" => load ? "db" : "ia",
                    _ => sfx,
                };
                Count(ops, 2, m);
                string baseText = ops[0];
                bool writeBack = baseText.EndsWith('!');
                uint rn = (uint)AsmSyntax.Register(baseText.TrimEnd('!'));
                bool caret = ops[1].EndsWith('^');
                uint list = AsmSyntax.RegisterList(ops[1].TrimEnd('^'));
                uint pu = mode switch { "ia" => 0x00800000u, "ib" => 0x01800000u, "da" => 0u, _ => 0x01000000u };
                return new[] { cond << 28 | 0x08000000u | pu | (caret ? 1u << 22 : 0) | (writeBack ? 1u << 21 : 0) | (load ? 1u << 20 : 0) | rn << 16 | list };
            }

            // ── Loads and stores ──
            if (EncodeLoadStore(m, ops, address, ctx) is uint ls)
                return new[] { ls };

            // ── Shift aliases: lsl r0, r1, #2 = mov r0, r1, lsl #2 ──
            foreach (string shift in new[] { "lsl", "lsr", "asr", "ror", "rrx" })
            {
                if (!Match(m, shift, out cond, out sfx, "", "s")) continue;
                if (shift == "rrx")
                {
                    Count(ops, 2, m);
                    return new[] { cond << 28 | 0x01A00060u | S(sfx) | Reg(ops, 0) << 12 | Reg(ops, 1) };
                }
                Count(ops, 3, m, 2);
                string rd = ops[0], rm = ops.Count == 3 ? ops[1] : ops[0], amount = ops[^1];
                return new[] { cond << 28 | 0x01A00000u | S(sfx) | (uint)AsmSyntax.Register(rd) << 12 | ShiftedRegister(new List<string> { rm, $"{shift} {amount}" }, 0, address, ctx) };
            }

            if (Match(m, "adr", out cond, out _))
            {
                Count(ops, 2, m);
                uint target = AsmSyntax.Value(ops[1], address, ctx);
                int diff = ctx.AllowUnresolved ? 0 : (int)(target - (address + 8));
                uint rd = Reg(ops, 0);
                uint? imm = EncodeImmediate((uint)Math.Abs((long)diff));
                if (imm == null) throw new AsmException($"adr target 0x{target:X8} is too far or not encodable; use ldr {ops[0]}, ={ops[1]}.");
                return new[] { cond << 28 | (diff < 0 ? 0x024F0000u : 0x028F0000u) | rd << 12 | imm.Value };
            }

            // ── Data processing ──
            for (uint opcode = 0; opcode < 16; opcode++)
            {
                if (!Match(m, AluNames[opcode], out cond, out sfx, "", "s")) continue;
                return new[] { EncodeAlu(opcode, cond, sfx == "s", ops, address, ctx, m) };
            }

            if (m.StartsWith("ldc") || m.StartsWith("stc"))
                throw new AsmException("ldc/stc are not supported (the DS has no coprocessor that uses them).");
            throw new AsmException($"Unknown ARM instruction \"{m}\".");
        }

        // ── Data processing ───────────────────────────────────────────────────

        private static uint EncodeAlu(uint opcode, uint cond, bool s, List<string> ops, uint address, AssemblerContext ctx, string m)
        {
            bool compare = opcode is >= 8 and <= 0xB;
            bool move = opcode is 0xD or 0xF;
            int rd = 0, rn = 0, first;
            if (compare)
            {
                if (ops.Count < 2) throw new AsmException($"{m} needs a register and an operand.");
                rn = AsmSyntax.Register(ops[0]);
                s = true;
                first = 1;
            }
            else if (move)
            {
                if (ops.Count < 2) throw new AsmException($"{m} needs a register and an operand.");
                rd = AsmSyntax.Register(ops[0]);
                first = 1;
            }
            else
            {
                if (ops.Count < 2) throw new AsmException($"{m} needs registers and an operand.");
                rd = AsmSyntax.Register(ops[0]);
                // "add r0, #1" / "add r0, r1" = "add r0, r0, ..."
                bool twoOperand = ops.Count == 2 || ops.Count == 3 && IsShift(ops[2]);
                rn = twoOperand ? rd : AsmSyntax.Register(ops[1]);
                first = twoOperand ? 1 : 2;
            }

            uint word = cond << 28 | (s ? 1u << 20 : 0) | (uint)rn << 16 | (uint)rd << 12;
            if (ops.Count == first + 1 && AsmSyntax.IsValue(ops[first]))
            {
                uint value = AsmSyntax.Value(ops[first], address, ctx);
                if (EncodeImmediate(value) is uint imm)
                    return word | 1u << 25 | opcode << 21 | imm;
                // The complementary instruction with the inverted or negated value.
                (uint alt, uint altValue) = opcode switch
                {
                    0xD => (0xFu, ~value), 0xF => (0xDu, ~value),
                    0x0 => (0xEu, ~value), 0xE => (0x0u, ~value),
                    0x4 => (0x2u, (uint)-(int)value), 0x2 => (0x4u, (uint)-(int)value),
                    0xA => (0xBu, (uint)-(int)value), 0xB => (0xAu, (uint)-(int)value),
                    0x5 => (0x6u, ~value), 0x6 => (0x5u, ~value),
                    _ => (99u, 0u),
                };
                if (alt != 99 && EncodeImmediate(altValue) is uint altImm)
                    return word | 1u << 25 | alt << 21 | altImm;
                if (ctx.AllowUnresolved)
                    return word | 1u << 25 | opcode << 21;
                throw new AsmException($"#0x{value:X} can't be encoded as an ARM immediate (an 8-bit value rotated by an even amount). " +
                                       "Use ldr rX, =value or build it with mov + orr.");
            }
            return word | opcode << 21 | ShiftedRegister(ops, first, address, ctx);
        }

        private static bool IsShift(string op)
        {
            string lower = op.Trim().ToLowerInvariant();
            return lower == "rrx" || ShiftNames.Any(s => lower.StartsWith(s + " "));
        }

        /// <summary>"rm", "rm, lsl #n", "rm, lsl rs", "rm, rrx" (operands from <paramref name="first"/>) as bits 11–0.</summary>
        private static uint ShiftedRegister(List<string> ops, int first, uint address, AssemblerContext ctx)
        {
            uint rm = (uint)AsmSyntax.Register(ops[first]);
            if (ops.Count == first + 1)
                return rm;
            if (ops.Count != first + 2)
                throw new AsmException("Too many operands.");
            string shift = ops[first + 1].Trim().ToLowerInvariant();
            if (shift == "rrx")
                return rm | 3u << 5;
            int type = Array.FindIndex(ShiftNames, n => shift.StartsWith(n));
            if (type < 0 || shift.Length < 4)
                throw new AsmException($"Expected a shift (lsl, lsr, asr, ror, rrx), got \"{ops[first + 1]}\".");
            string amountText = shift[4..].Trim();
            if (AsmSyntax.TryRegister(amountText) is int rs)
                return rm | (uint)type << 5 | 1u << 4 | (uint)rs << 8;
            uint amount = AsmSyntax.Value(amountText, address, ctx);
            if (amount == 0)
                return type == 0 ? rm : throw new AsmException($"{ShiftNames[type]} #0 is not valid; leave the shift out.");
            if (amount == 32 && type is 1 or 2)
                return rm | (uint)type << 5;
            if (amount > 31)
                throw new AsmException($"Shift amount {amount} is out of range.");
            return rm | (uint)type << 5 | amount << 7;
        }

        // ── Loads and stores ──────────────────────────────────────────────────

        private static uint? EncodeLoadStore(string m, List<string> ops, uint address, AssemblerContext ctx)
        {
            string[] halfSuffixes = { "sh", "sb", "h", "d" };
            foreach (bool load in new[] { true, false })
            {
                string name = load ? "ldr" : "str";
                if (!Match(m, name, out uint cond, out string sfx, "bt", "sh", "sb", "b", "t", "h", "d", ""))
                    continue;
                if (!load && sfx is "sh" or "sb")
                    throw new AsmException($"{m}: there is no signed store.");
                if (ops.Count < 2) throw new AsmException($"{m} needs a register and an address.");
                uint rd = Reg(ops, 0);
                // "ldrd r0, r1, [r2]": the second register is implied (rd + 1).
                if (sfx == "d" && AsmSyntax.TryRegister(ops[1]) is int second)
                {
                    if (second != rd + 1) throw new AsmException($"{m}: the second register must be r{rd + 1}.");
                    ops = ops.Where((_, i) => i != 1).ToList();
                }
                // A post-indexed offset ("[r1], #4", "[r1], -r2, lsl #2") was split at its commas.
                if (ops.Count > 2)
                    ops = new List<string> { ops[0], string.Join(", ", ops.Skip(1)) };

                if (halfSuffixes.Contains(sfx))
                {
                    if (sfx == "d")
                    {
                        RequireV5(ctx, m);
                        if ((rd & 1) != 0 || rd == 14) throw new AsmException($"{m} needs an even first register below lr.");
                    }
                    uint sh = sfx switch { "h" => 1u, "sb" => 2u, "sh" => 3u, _ => load ? 2u : 3u };
                    uint l = sfx == "d" ? 0 : load ? 1u : 0;
                    uint word = HalfwordAddressing(ops[1], address, ctx);
                    return cond << 28 | word | l << 20 | rd << 12 | sh << 5 | 0x90u;
                }

                string operand = ops[1];
                if (operand.StartsWith('='))
                {
                    if (!load || sfx != "") throw new AsmException("Only ldr can load a =literal.");
                    return Literal(cond, rd, AsmSyntax.Value(operand[1..], address, ctx), address, ctx);
                }
                uint mode = AddressingMode(operand, sfx.Contains('t'), address, ctx, out bool post, out _);
                return cond << 28 | 0x04000000u | mode | (sfx.Contains('b') ? 1u << 22 : 0) | (load ? 1u << 20 : 0) | rd << 12;
            }
            return null;
        }

        private static uint Literal(uint cond, uint rd, uint value, uint address, AssemblerContext ctx)
        {
            if (ctx.AllocateLiteral == null)
            {
                if (EncodeImmediate(value) is uint imm) return cond << 28 | 0x03A00000u | rd << 12 | imm;
                if (EncodeImmediate(~value) is uint inv) return cond << 28 | 0x03E00000u | rd << 12 | inv;
                throw new AsmException($"=0x{value:X} needs a literal pool here.");
            }
            uint slot = ctx.AllocateLiteral(value);
            int diff = ctx.AllowUnresolved ? 0 : (int)(slot - (address + 8));
            if (diff is <= -4096 or >= 4096)
                throw new AsmException($"The literal pool at 0x{slot:X8} is more than 4 KB away; place a pool closer.");
            return cond << 28 | 0x051F0000u | (diff >= 0 ? 1u << 23 : 0) | rd << 12 | (uint)Math.Abs(diff);
        }

        /// <summary>
        /// Word/byte addressing (bits 25–16 and 11–0 with P/U/W): "[rn]", "[rn, #±imm]{!}",
        /// "[rn, ±rm{, shift}]{!}", "[rn], #±imm", "[rn], ±rm{, shift}", or "[address]" (PC-relative).
        /// <paramref name="translate"/> (ldrt/strt) forces post-indexing with W set.
        /// </summary>
        private static uint AddressingMode(string text, bool? translate, uint address, AssemblerContext ctx, out bool post, out uint rn)
        {
            text = text.Trim();
            post = false;
            rn = 15;
            if (!text.StartsWith('['))
                throw new AsmException($"Expected an address like [r0, #4], got \"{text}\".");
            int close = text.IndexOf(']');
            if (close < 0) throw new AsmException($"Missing ] in \"{text}\".");
            string inside = text[1..close];
            string after = text[(close + 1)..].Trim();
            bool writeBack = after == "!";
            var parts = AsmSyntax.Split("x " + inside).Operands;

            if (parts.Count == 1 && AsmSyntax.TryRegister(parts[0]) == null)
            {
                // [label]: PC-relative.
                if (after.Length != 0) throw new AsmException("A PC-relative address can't have writeback or a post offset.");
                uint target = AsmSyntax.Value(parts[0], address, ctx);
                int diff = ctx.AllowUnresolved ? 0 : (int)(target - (address + 8));
                if (diff is <= -4096 or >= 4096) throw new AsmException($"0x{target:X8} is more than 4 KB from this instruction; use ldr rX, =address first.");
                return 0x01000000u | (diff >= 0 ? 1u << 23 : 0) | 15u << 16 | (uint)Math.Abs(diff);
            }

            rn = (uint)AsmSyntax.Register(parts[0]);
            List<string> offset;
            if (after.StartsWith(','))
            {
                if (parts.Count != 1) throw new AsmException($"Bad address \"{text}\".");
                post = true;
                offset = AsmSyntax.Split("x " + after[1..]).Operands;
            }
            else if (after.Length == 0 || writeBack)
                offset = parts.Skip(1).ToList();
            else
                throw new AsmException($"Unexpected \"{after}\" after the address.");

            bool pre = !post;
            if (translate == true)
            {
                if (pre && offset.Count > 0) throw new AsmException("ldrt/strt only take post-indexed addresses: [rn], #offset.");
                pre = false;
                writeBack = true;
            }
            else if (post)
                writeBack = false;

            uint word = (pre ? 1u << 24 : 0) | (writeBack ? 1u << 21 : 0) | rn << 16;
            if (offset.Count == 0)
                return word | 1u << 23;
            string first = offset[0];
            bool minus = first.StartsWith('-');
            string reg = first.TrimStart('-', '+').Trim();
            if (AsmSyntax.TryRegister(reg) is int rm)
            {
                var shiftOps = new List<string> { reg };
                shiftOps.AddRange(offset.Skip(1));
                uint shifted = ShiftedRegister(shiftOps, 0, address, ctx);
                if ((shifted & 0x10) != 0) throw new AsmException("Address offsets can't be shifted by a register.");
                return word | 1u << 25 | (minus ? 0 : 1u << 23) | shifted;
            }
            if (offset.Count != 1) throw new AsmException($"Bad address offset in \"{text}\".");
            int value = (int)AsmSyntax.Value(first, address, ctx);
            if (value is <= -4096 or >= 4096) throw new AsmException($"Offset {value} is out of range (-4095..4095).");
            return word | (value >= 0 ? 1u << 23 : 0) | (uint)Math.Abs(value);
        }

        /// <summary>Halfword/signed/doubleword addressing: an 8-bit immediate or a register, no shifts (bits 24–16, 11–8, 3–0).</summary>
        private static uint HalfwordAddressing(string text, uint address, AssemblerContext ctx)
        {
            text = text.Trim();
            if (!text.StartsWith('['))
            {
                throw new AsmException($"Expected an address like [r0, #4], got \"{text}\".");
            }
            int close = text.IndexOf(']');
            if (close < 0) throw new AsmException($"Missing ] in \"{text}\".");
            var parts = AsmSyntax.Split("x " + text[1..close]).Operands;
            string after = text[(close + 1)..].Trim();
            bool writeBack = after == "!";
            if (parts.Count == 1 && AsmSyntax.TryRegister(parts[0]) == null)
            {
                uint target = AsmSyntax.Value(parts[0], address, ctx);
                int diff = ctx.AllowUnresolved ? 0 : (int)(target - (address + 8));
                if (diff is <= -256 or >= 256) throw new AsmException($"0x{target:X8} is more than 255 bytes away.");
                return 0x01400000u | (diff >= 0 ? 1u << 23 : 0) | 15u << 16 | ((uint)Math.Abs(diff) & 0xF0) << 4 | (uint)Math.Abs(diff) & 0xF;
            }
            uint rn = (uint)AsmSyntax.Register(parts[0]);
            bool post = after.StartsWith(',');
            List<string> offset = post ? AsmSyntax.Split("x " + after[1..]).Operands : parts.Skip(1).ToList();
            if (!post && after.Length != 0 && !writeBack) throw new AsmException($"Unexpected \"{after}\" after the address.");
            uint word = (post ? 0 : 1u << 24) | (writeBack && !post ? 1u << 21 : 0) | rn << 16;
            if (offset.Count == 0)
                return word | 1u << 23 | 1u << 22;
            if (offset.Count != 1) throw new AsmException("Halfword addresses can't use a shifted register.");
            string first = offset[0];
            bool minus = first.StartsWith('-');
            if (AsmSyntax.TryRegister(first.TrimStart('-', '+').Trim()) is int rm)
                return word | (minus ? 0 : 1u << 23) | (uint)rm;
            int value = (int)AsmSyntax.Value(first, address, ctx);
            if (value is <= -256 or >= 256) throw new AsmException($"Offset {value} is out of range (-255..255).");
            uint abs = (uint)Math.Abs(value);
            return word | (value >= 0 ? 1u << 23 : 0) | 1u << 22 | (abs & 0xF0) << 4 | abs & 0xF;
        }

        // ── DSP multiplies (ARMv5TE) ──────────────────────────────────────────

        private static uint? EncodeDspMultiply(string m, List<string> ops, AssemblerContext ctx)
        {
            string[] xy = { "bb", "tb", "bt", "tt" };
            uint XY(string s) => (s[0] == 't' ? 1u << 5 : 0) | (s[1] == 't' ? 1u << 6 : 0);
            uint cond;
            string sfx;
            if (Match(m, "smlal", out cond, out sfx, xy))
            {
                Count(ops, 4, m);
                RequireV5(ctx, m);
                return cond << 28 | 0x01400080u | Reg(ops, 1) << 16 | Reg(ops, 0) << 12 | Reg(ops, 3) << 8 | XY(sfx) | Reg(ops, 2);
            }
            if (Match(m, "smla", out cond, out sfx, xy))
            {
                Count(ops, 4, m);
                RequireV5(ctx, m);
                return cond << 28 | 0x01000080u | Reg(ops, 0) << 16 | Reg(ops, 3) << 12 | Reg(ops, 2) << 8 | XY(sfx) | Reg(ops, 1);
            }
            if (Match(m, "smlaw", out cond, out sfx, "b", "t"))
            {
                Count(ops, 4, m);
                RequireV5(ctx, m);
                return cond << 28 | 0x01200080u | Reg(ops, 0) << 16 | Reg(ops, 3) << 12 | Reg(ops, 2) << 8 | (sfx == "t" ? 1u << 6 : 0) | Reg(ops, 1);
            }
            if (Match(m, "smulw", out cond, out sfx, "b", "t"))
            {
                Count(ops, 3, m);
                RequireV5(ctx, m);
                return cond << 28 | 0x012000A0u | Reg(ops, 0) << 16 | Reg(ops, 2) << 8 | (sfx == "t" ? 1u << 6 : 0) | Reg(ops, 1);
            }
            if (Match(m, "smul", out cond, out sfx, xy))
            {
                Count(ops, 3, m);
                RequireV5(ctx, m);
                return cond << 28 | 0x01600080u | Reg(ops, 0) << 16 | Reg(ops, 2) << 8 | XY(sfx) | Reg(ops, 1);
            }
            return null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void Count(List<string> ops, int count, string m, int min = -1)
        {
            if (ops.Count != count && ops.Count != (min < 0 ? count : min) && !(min >= 0 && ops.Count >= min && ops.Count <= count))
                throw new AsmException($"{m} takes {(min >= 0 ? $"{min}-{count}" : count.ToString())} operand{(count == 1 ? "" : "s")}, got {ops.Count}.");
        }

        private static uint Reg(List<string> ops, int index, int count = -1, string m = "")
        {
            if (count >= 0) Count(ops, count, m);
            if (index >= ops.Count) throw new AsmException("Missing operand.");
            return (uint)AsmSyntax.Register(ops[index]);
        }

        private static uint S(string sfx) => sfx == "s" ? 1u << 20 : 0;

        private static void RequireV5(AssemblerContext ctx, string what)
        {
            if (!ctx.IsArmV5) throw new AsmException($"{what} needs ARMv5 (the ARM9); the ARM7 is ARMv4T.");
        }

        private static uint Small(string text, uint max, uint address, AssemblerContext ctx)
        {
            uint v = AsmSyntax.Value(text, address, ctx);
            if (v > max) throw new AsmException($"{text} must be 0-{max}.");
            return v;
        }

        /// <summary>"p15" / "c7" (also a bare number).</summary>
        private static uint CopReg(string text, char prefix)
        {
            string t = text.Trim().ToLowerInvariant();
            if (t.Length > 1 && t[0] == prefix && uint.TryParse(t.AsSpan(1), out uint n) && n < 16) return n;
            if (uint.TryParse(t, out n) && n < 16) return n;
            throw new AsmException($"Expected {prefix}0-{prefix}15, got \"{text}\".");
        }

        /// <summary>"cpsr", "spsr", "cpsr_fc", "cpsr_all", "cpsr_flg": R bit and field mask (fsxc).</summary>
        private static uint Psr(string text, out uint mask)
        {
            string t = text.Trim().ToLowerInvariant();
            uint r = t.StartsWith("spsr") ? 1u : t.StartsWith("cpsr") ? 0u : throw new AsmException($"Expected cpsr or spsr, got \"{text}\".");
            if (t.Length == 4)
            {
                mask = 9; // "cpsr" alone means the control and flags fields
                return r;
            }
            string fields = t[4..].TrimStart('_'); // "cpsr_" (no fields) is a valid, if useless, encoding
            mask = fields switch
            {
                "all" => 9,
                "flg" => 8,
                "ctl" => 1,
                _ => 0,
            };
            if (mask == 0)
            {
                foreach (char c in fields)
                {
                    uint bit = c switch { 'f' => 8u, 's' => 4u, 'x' => 2u, 'c' => 1u, _ => throw new AsmException($"Bad PSR field \"{text}\".") };
                    mask |= bit;
                }
            }
            return r;
        }
    }
}
