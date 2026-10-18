using System.Buffers.Binary;

namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>
    /// Finds printf-style formatters (the Nitro SDK's OS_VSNPrintf, a C library's
    /// vsnprintf, a game's own) in loaded ARM or Thumb code, without symbols.
    ///
    /// A formatter's main loop compares each format character with '%', then the
    /// character after it with conversion letters and flags. So a candidate is a
    /// "cmp rN, #25h" followed, within <see cref="Window"/> instructions, by compares
    /// with at least <see cref="MinConversions"/> different characters from "dioxXucsp",
    /// at least one flag character from "-+# 0" (which printf parses and scanf
    /// doesn't), and no compare with '[' (scanf's scanset). The function start is the
    /// nearest "push {..., lr}" before it: ARM "stmdb sp!, {...lr}" (E92D4xxxh) or
    /// Thumb "push {...lr}" (B5xxh).
    ///
    /// These are heuristics: the scanner finds formatters built by common compilers,
    /// not every possible one, so hooks can also be added by hand.
    /// </summary>
    public static class PrintfSignatures
    {
        public const int Window = 160;
        public const int MinConversions = 3;
        private const int MaxFunctionBytes = 1024;
        private const string Conversions = "dioxXucsp";
        private const string Flags = "-+# 0";

        /// <summary>Function starts found in <paramref name="code"/> (loaded at <paramref name="baseAddress"/>); bit 0 set = Thumb.</summary>
        public static List<uint> Scan(ReadOnlySpan<byte> code, uint baseAddress)
        {
            var found = new SortedSet<uint>();
            for (int i = 0; i + 4 <= code.Length; i += 4)
            {
                uint op = BinaryPrimitives.ReadUInt32LittleEndian(code[i..]);
                if (ArmCompareImmediate(op) == '%' && LooksLikeFormatter(code, i, thumb: false)
                    && ArmFunctionStart(code, i) is int start)
                    found.Add(baseAddress + (uint)start);
            }
            for (int i = 0; i + 2 <= code.Length; i += 2)
            {
                ushort op = BinaryPrimitives.ReadUInt16LittleEndian(code[i..]);
                if (ThumbCompareImmediate(op) == '%' && LooksLikeFormatter(code, i, thumb: true)
                    && ThumbFunctionStart(code, i) is int start)
                    found.Add(baseAddress + (uint)start | 1);
            }
            return found.ToList();
        }

        // ── Instruction patterns ──────────────────────────────────────────────

        /// <summary>The immediate of "cmp rN, #imm8" (any condition, no rotation), or -1.</summary>
        private static int ArmCompareImmediate(uint op) =>
            (op & 0x0FF0FF00) == 0x03500000 && op >> 28 != 0xF ? (int)(op & 0xFF) : -1;

        /// <summary>The immediate of Thumb "cmp rN, #imm8", or -1.</summary>
        private static int ThumbCompareImmediate(ushort op) => (op & 0xF800) == 0x2800 ? op & 0xFF : -1;

        private static bool LooksLikeFormatter(ReadOnlySpan<byte> code, int at, bool thumb)
        {
            int size = thumb ? 2 : 4;
            var conversions = new HashSet<char>();
            bool flag = false;
            for (int n = 1; n <= Window; n++)
            {
                int i = at + n * size;
                if (i + size > code.Length)
                    break;
                int imm = thumb
                    ? ThumbCompareImmediate(BinaryPrimitives.ReadUInt16LittleEndian(code[i..]))
                    : ArmCompareImmediate(BinaryPrimitives.ReadUInt32LittleEndian(code[i..]));
                if (imm < 0)
                    continue;
                char c = (char)imm;
                if (c == '[')
                    return false;
                if (Conversions.Contains(c))
                    conversions.Add(c);
                else if (Flags.Contains(c))
                    flag = true;
            }
            return flag && conversions.Count >= MinConversions;
        }

        private static int? ArmFunctionStart(ReadOnlySpan<byte> code, int at)
        {
            for (int i = at; i >= 0 && at - i <= MaxFunctionBytes; i -= 4)
            {
                uint op = BinaryPrimitives.ReadUInt32LittleEndian(code[i..]);
                if ((op & 0xFFFF4000) == 0xE92D4000)
                    return i;
            }
            return null;
        }

        private static int? ThumbFunctionStart(ReadOnlySpan<byte> code, int at)
        {
            for (int i = at; i >= 0 && at - i <= MaxFunctionBytes; i -= 2)
            {
                ushort op = BinaryPrimitives.ReadUInt16LittleEndian(code[i..]);
                if ((op & 0xFF00) == 0xB500)
                    return i;
            }
            return null;
        }
    }
}
