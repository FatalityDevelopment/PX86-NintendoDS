using System.Globalization;

namespace Px86.NintendoDS.Common.Assembler
{
    /// <summary>What the assembler needs from its caller (labels, literal pools, the target CPU).</summary>
    public sealed class AssemblerContext
    {
        /// <summary>The value of a label or constant, or null when it is unknown.</summary>
        public Func<string, uint?>? ResolveSymbol { get; init; }

        /// <summary>
        /// Size pass: unknown symbols count as the instruction's own address and range checks
        /// are skipped, so every instruction gets its final size before labels are known.
        /// </summary>
        public bool AllowUnresolved { get; init; }

        /// <summary>
        /// "ldr rX, =value": returns the address of the pool word that will hold the value.
        /// Without it, the value must fit a mov/mvn immediate (ARM) or 0–255 (Thumb).
        /// </summary>
        public Func<uint, uint>? AllocateLiteral { get; init; }

        /// <summary>ARM946E-S (ARMv5TE). False for the ARM7TDMI (ARMv4T): BLX, CLZ, Q*, SMLA*, LDRD/STRD, PLD and BKPT are refused.</summary>
        public bool IsArmV5 { get; init; } = true;

        public static AssemblerContext Default { get; } = new();
    }

    /// <summary>The bytes of one assembled instruction (little-endian), or why it failed.</summary>
    public readonly record struct AssembleResult(byte[] Bytes, string? Error)
    {
        public bool Success => Error == null;

        public static AssembleResult Fail(string error) => new(Array.Empty<byte>(), error);

        public static AssembleResult Words(params uint[] words)
        {
            var bytes = new byte[words.Length * 4];
            for (int i = 0; i < words.Length; i++)
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
            return new(bytes, null);
        }

        public static AssembleResult Halves(params ushort[] halves)
        {
            var bytes = new byte[halves.Length * 2];
            for (int i = 0; i < halves.Length; i++)
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), halves[i]);
            return new(bytes, null);
        }

        /// <summary>The first word (ARM) — tests and single-instruction callers.</summary>
        public uint Word => BitConverter.ToUInt32(Bytes, 0);

        /// <summary>The first halfword (Thumb).</summary>
        public ushort Half => BitConverter.ToUInt16(Bytes, 0);
    }

    /// <summary>An assembly error with a message for the user.</summary>
    public sealed class AsmException : Exception
    {
        public AsmException(string message) : base(message) { }
    }

    /// <summary>
    /// Operand syntax shared by the ARM and Thumb assemblers. It accepts what the Px86
    /// disassemblers print (registers r0–r15/sp/lr/pc, "#0x10", absolute targets
    /// "0x02001234", "[0x02001234]" for PC-relative loads, "{r0-r3, lr}") and the usual
    /// assembler spellings: decimal and $hex numbers, "#-4", ip/fp/sl/sb, label names
    /// (":name" or "name"), and "+"/"-" between terms.
    /// </summary>
    public static class AsmSyntax
    {
        public static readonly string[] Conditions =
        {
            "eq", "ne", "cs", "cc", "mi", "pl", "vs", "vc",
            "hi", "ls", "ge", "lt", "gt", "le", "al",
        };

        /// <summary>Condition code of a suffix ("", "al" = 14; "hs"/"lo" accepted), or -1.</summary>
        public static int Condition(string s) => s switch
        {
            "" or "al" => 14,
            "hs" => 2,
            "lo" => 3,
            _ => Array.IndexOf(Conditions, s),
        };

        /// <summary>Split "mnemonic operands" and the operands at top-level commas (not inside [] or {}).</summary>
        public static (string Mnemonic, List<string> Operands) Split(string line)
        {
            line = line.Trim();
            int space = 0;
            while (space < line.Length && !char.IsWhiteSpace(line[space]))
                space++;
            string mnemonic = line[..space].ToLowerInvariant();
            string rest = line[space..].Trim();
            var operands = new List<string>();
            if (rest.Length == 0)
                return (mnemonic, operands);
            int depth = 0, start = 0;
            for (int i = 0; i < rest.Length; i++)
            {
                char c = rest[i];
                if (c is '[' or '{' or '(') depth++;
                else if (c is ']' or '}' or ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    operands.Add(rest[start..i].Trim());
                    start = i + 1;
                }
            }
            operands.Add(rest[start..].Trim());
            if (operands.Any(o => o.Length == 0))
                throw new AsmException("Empty operand.");
            return (mnemonic, operands);
        }

        public static int? TryRegister(string s) => s.Trim().ToLowerInvariant() switch
        {
            "sp" => 13,
            "lr" => 14,
            "pc" => 15,
            "ip" => 12,
            "fp" => 11,
            "sl" => 10,
            "sb" => 9,
            var r when r.Length is 2 or 3 && r[0] == 'r' && int.TryParse(r.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n < 16 => n,
            _ => null,
        };

        public static int Register(string s) =>
            TryRegister(s) ?? throw new AsmException($"Expected a register, got \"{s}\".");

        public static int LowRegister(string s)
        {
            int r = Register(s);
            if (r > 7)
                throw new AsmException($"Thumb needs a low register (r0-r7) here, got {s}.");
            return r;
        }

        /// <summary>"{r0-r3, lr}" as a bit mask.</summary>
        public static uint RegisterList(string s)
        {
            s = s.Trim();
            if (!s.StartsWith('{') || !s.EndsWith('}'))
                throw new AsmException($"Expected a register list like {{r0-r3, lr}}, got \"{s}\".");
            uint mask = 0;
            foreach (string part in s[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int dash = part.IndexOf('-');
                if (dash < 0)
                {
                    mask |= 1u << Register(part);
                    continue;
                }
                int from = Register(part[..dash]), to = Register(part[(dash + 1)..]);
                if (to < from)
                    throw new AsmException($"Register range {part} goes backwards.");
                for (int r = from; r <= to; r++)
                    mask |= 1u << r;
            }
            if (mask == 0)
                throw new AsmException("Empty register list.");
            return mask;
        }

        /// <summary>A value: "#" optional; numbers (0x, $, decimal, 0b), symbols, and "+"/"-" between terms.</summary>
        public static uint Value(string s, uint address, AssemblerContext ctx)
        {
            s = s.Trim();
            if (s.StartsWith('#'))
                s = s[1..].Trim();
            if (s.Length == 0)
                throw new AsmException("Missing value.");
            long total = 0;
            int i = 0, sign = 1;
            bool expectTerm = true;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c is '+' or '-')
                {
                    if (expectTerm) sign = c == '-' ? -sign : sign;
                    else { sign = c == '-' ? -1 : 1; expectTerm = true; }
                    i++;
                    continue;
                }
                if (!expectTerm)
                    throw new AsmException($"Unexpected \"{s[i..]}\" in \"{s}\".");
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not '+' and not '-')
                    i++;
                total += sign * (long)Term(s[start..i], address, ctx);
                sign = 1;
                expectTerm = false;
            }
            if (expectTerm)
                throw new AsmException($"\"{s}\" ends with an operator.");
            return (uint)total;
        }

        private static uint Term(string t, uint address, AssemblerContext ctx)
        {
            string lower = t.ToLowerInvariant();
            if (lower.StartsWith("0x") && uint.TryParse(lower.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex)) return hex;
            if (lower.StartsWith('$') && uint.TryParse(lower.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex)) return hex;
            if (lower.StartsWith("0b"))
            {
                try { return Convert.ToUInt32(lower[2..], 2); }
                catch (FormatException) { throw new AsmException($"Bad binary number \"{t}\"."); }
            }
            if (char.IsAsciiDigit(lower[0]))
            {
                if (ulong.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out ulong dec) && dec <= uint.MaxValue)
                    return (uint)dec;
                throw new AsmException($"Bad number \"{t}\".");
            }
            if (lower.Length == 3 && lower[0] == '\'' && lower[2] == '\'')
                return t[1];
            if (lower == ".")
                return address;
            if (ctx.ResolveSymbol?.Invoke(t) is uint value)
                return value;
            if (ctx.AllowUnresolved)
                return address;
            throw new AsmException($"Unknown label or value \"{t}\".");
        }

        /// <summary>Is this operand a plain value (not a register or memory form)?</summary>
        public static bool IsValue(string s) => !s.StartsWith('[') && !s.StartsWith('{') && TryRegister(s) == null;

        /// <summary>Signed difference that must fit in <paramref name="bits"/> bits after dividing by <paramref name="scale"/>.</summary>
        public static int BranchOffset(uint target, uint origin, int scale, int bits, AssemblerContext ctx, string what)
        {
            long diff = (long)(int)(target - origin);
            if (ctx.AllowUnresolved)
                return 0;
            if (diff % scale != 0)
                throw new AsmException($"{what} target 0x{target:X8} must be {scale}-byte aligned.");
            long units = diff / scale;
            long limit = 1L << (bits - 1);
            if (units < -limit || units >= limit)
                throw new AsmException($"{what} target 0x{target:X8} is out of range ({-limit * scale}..{(limit - 1) * scale} bytes from 0x{origin:X8}).");
            return (int)units;
        }
    }
}
