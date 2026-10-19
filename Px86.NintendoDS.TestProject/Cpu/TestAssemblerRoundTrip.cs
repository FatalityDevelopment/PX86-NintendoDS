using Px86.NintendoDS.Common.Assembler;
using Px86.NintendoDS.Common.Disassembler;

namespace Px86.NintendoDS.TestProject.Cpu
{
    /// <summary>
    /// Disassemble → assemble → disassemble gives the same text, for random ARM words and every
    /// Thumb halfword. Comparing text (not words) allows encodings the disassembler prints the
    /// same way (an unused field, a "#0" offset with either sign).
    /// </summary>
    [TestClass]
    public class TestAssemblerRoundTrip
    {
        // Printed in a form that can't be read back (coprocessor loads, lone BL halves), or
        // unpredictable encodings the assembler refuses (empty register lists, LDRD/STRD with
        // an odd first register).
        private static bool Skip(string text) =>
            text.StartsWith("ldc") || text.StartsWith("stc") || text.Contains("(prefix)") || text.Contains("(suffix)")
            || text.Contains("{}") || OddDoubleword(text);

        private static bool OddDoubleword(string text)
        {
            if (!text.StartsWith("ldrd") && !text.StartsWith("strd")) return false;
            int? r = AsmSyntax.TryRegister(text[(text.IndexOf(' ') + 1)..text.IndexOf(',')]);
            return r is not int n || (n & 1) != 0 || n == 14;
        }

        [TestMethod]
        public void ArmWordsRoundTrip()
        {
            var random = new Random(1234);
            var failures = new List<string>();
            const uint address = 0x02100000;
            for (int i = 0; i < 300000 && failures.Count < 30; i++)
            {
                uint op = (uint)random.NextInt64(0, 1L << 32);
                string text = ArmDisassembler.Disassemble(op, address);
                if (Skip(text)) continue;
                var result = ArmAssembler.Assemble(text, address);
                if (!result.Success)
                {
                    failures.Add($"{op:X8} \"{text}\": {result.Error}");
                    continue;
                }
                string again = ArmDisassembler.Disassemble(result.Word, address);
                if (again != text)
                    failures.Add($"{op:X8} \"{text}\" -> {result.Word:X8} \"{again}\"");
            }
            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
        }

        [TestMethod]
        public void ThumbHalfwordsRoundTrip()
        {
            var failures = new List<string>();
            const uint address = 0x02100002;
            for (int op = 0; op < 0x10000 && failures.Count < 30; op++)
            {
                ushort? next = (op & 0xF800) == 0xF000 ? (ushort)0xF812 : null;
                string text = ThumbDisassembler.Disassemble((ushort)op, address, next);
                if (Skip(text)) continue;
                // A high-register cmp of two low registers is unpredictable; it reads back as the ALU cmp.
                if ((op & 0xFF00) == 0x4500 && (op & 0xC0) == 0) continue;
                var result = ThumbAssembler.Assemble(text, address);
                if (!result.Success)
                {
                    failures.Add($"{op:X4} \"{text}\": {result.Error}");
                    continue;
                }
                ushort first = result.Half;
                ushort? second = result.Bytes.Length == 4 ? BitConverter.ToUInt16(result.Bytes, 2) : null;
                string again = ThumbDisassembler.Disassemble(first, address, second);
                if (again != text)
                    failures.Add($"{op:X4} \"{text}\" -> {Convert.ToHexString(result.Bytes)} \"{again}\"");
            }
            Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
        }
    }
}
