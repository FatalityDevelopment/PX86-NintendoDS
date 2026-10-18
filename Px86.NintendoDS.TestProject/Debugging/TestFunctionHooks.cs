using System.Text;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Jit;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Debugging
{
    /// <summary>
    /// The printf test ROM has its own formatter, like a retail game: printf and
    /// sprintf write to buffers nobody reads, so only the hooks can show the text.
    /// </summary>
    [TestClass]
    public class TestFunctionHooks
    {
        private static readonly string[] PrintfLines =
        {
            "[-7  ] [  ok] [+5] [c=Z] [50%]",
            "loop 0", "loop 1", "loop 2",
            "big=123456789012 after=9",
        };

        /// <summary>Boot, wait until the ROM published its function addresses, let <paramref name="setup"/> add hooks, then run it.</summary>
        private static (List<string> Arm9, List<string> Arm7) Run(CpuMode mode, bool autoScan, Action<NdsEmulator>? setup)
        {
            var nds = TestRomRunner.Boot("printf");
            nds.CpuMode = mode;
            nds.Hooks.AutoScan = autoScan;
            for (int frame = 0; frame < 30 && (TestRomRunner.Result9(nds, 1) == 0 || TestRomRunner.Result7(nds, 1) == 0); frame++)
                nds.RunFrame();
            Assert.AreNotEqual(0u, TestRomRunner.Result7(nds, 1), "ARM7 started");
            setup?.Invoke(nds);
            DebugMemory.Write32(nds, DebugCpu.Arm9, 0x02300014, 1); // RESULT9(5): go
            for (int frame = 0; frame < 60; frame++)
            {
                nds.RunFrame();
                if (TestRomRunner.Result9(nds, 0) == TestRomRunner.DoneMagic && TestRomRunner.Result7(nds, 0) == TestRomRunner.DoneMagic)
                    break;
            }
            Assert.AreEqual(TestRomRunner.DoneMagic, TestRomRunner.Result9(nds, 0), "ARM9 finished");
            Assert.AreEqual(TestRomRunner.DoneMagic, TestRomRunner.Result7(nds, 0), "ARM7 finished");
            var lines = nds.DebugOutput.Snapshot();
            return (lines.Where(l => l.Cpu == DebugCpu.Arm9).Select(l => l.Text).ToList(),
                    lines.Where(l => l.Cpu == DebugCpu.Arm7).Select(l => l.Text).ToList());
        }

        [DataTestMethod]
        [DataRow(CpuMode.Interpreter)]
        [DataRow(CpuMode.Jit)]
        public void ManualHooksPrintEachCallOnce(CpuMode mode)
        {
            var (arm9, arm7) = Run(mode, autoScan: false, nds =>
            {
                nds.Hooks.Add(DebugCpu.Arm9, TestRomRunner.Result9(nds, 2), HookKind.Printf, "game_printf");
                nds.Hooks.Add(DebugCpu.Arm9, TestRomRunner.Result9(nds, 3), HookKind.SPrintf, "game_sprintf");
                // The formatter both call: nested calls must not print a second time.
                nds.Hooks.Add(DebugCpu.Arm9, TestRomRunner.Result9(nds, 1), HookKind.VSNPrintf, "fmt_vsnprintf");
                nds.Hooks.Add(DebugCpu.Arm7, TestRomRunner.Result7(nds, 1), HookKind.Auto, "fmt_vsnprintf");
            });
            CollectionAssert.AreEqual(new[] { "[sprintf] score=42 name=Px86 hex=0000BEEF" }.Concat(PrintfLines).ToList(), arm9, string.Join(" | ", arm9));
            CollectionAssert.AreEqual(new[] { "[sprintf] arm7 v=-3 s=thumb" }, arm7, string.Join(" | ", arm7));
        }

        [DataTestMethod]
        [DataRow(CpuMode.Interpreter)]
        [DataRow(CpuMode.Jit)]
        public void TheScannerFindsAndHooksTheFormatter(CpuMode mode)
        {
            NdsEmulator? emulator = null;
            var (arm9, arm7) = Run(mode, autoScan: true, nds => emulator = nds);
            var hooks = emulator!.Hooks.List();
            Assert.IsTrue(hooks.Any(h => h.Cpu == DebugCpu.Arm9 && h.Address == TestRomRunner.Result9(emulator, 1) && h.Automatic),
                "ARM ARM9 formatter: " + string.Join(", ", hooks.Select(FunctionHooks.Format)));
            Assert.IsTrue(hooks.Any(h => h.Cpu == DebugCpu.Arm7 && h.Address == (TestRomRunner.Result7(emulator, 1) & ~1u)),
                "Thumb ARM7 formatter: " + string.Join(", ", hooks.Select(FunctionHooks.Format)));
            // Only the formatter is hooked, so every call reads as a sprintf.
            CollectionAssert.AreEqual(new[] { "score=42 name=Px86 hex=0000BEEF" }.Concat(PrintfLines).Select(l => "[sprintf] " + l).ToList(), arm9, string.Join(" | ", arm9));
            CollectionAssert.AreEqual(new[] { "[sprintf] arm7 v=-3 s=thumb" }, arm7);
        }

        [TestMethod]
        public void TheScannerIgnoresDataAndScanf()
        {
            var random = new byte[0x10000];
            new Random(1).NextBytes(random);
            Assert.AreEqual(0, PrintfSignatures.Scan(random, 0x02000000).Count);

            // push {r4, lr}; cmp r0, #'%'; then compares with d, x, s, '-' and '[' (a scanset: scanf).
            uint[] scanf = { 0xE92D4010, 0xE3500025, 0xE3500064, 0xE3500078, 0xE3500073, 0xE350002D, 0xE350005B };
            var code = scanf.SelectMany(BitConverter.GetBytes).ToArray();
            Assert.AreEqual(0, PrintfSignatures.Scan(code, 0x02000000).Count, "scanf");
            code = scanf[..^1].SelectMany(BitConverter.GetBytes).ToArray();
            CollectionAssert.AreEqual(new[] { 0x02000000u }, PrintfSignatures.Scan(code, 0x02000000), "printf");
        }

        // ── Formatter ─────────────────────────────────────────────────────────

        private static string Format(string format, params uint[] args)
        {
            var nds = TestRomRunner.Boot("printf");
            uint text = 0x02200000;
            foreach (var (s, i) in new[] { "hello", "abc" }.Select((s, i) => (s, i)))
                WriteString(nds, text + (uint)i * 0x10, s);
            // Registers r1–r3 then the stack at 02210000h.
            var r = new uint[16];
            r[13] = 0x02210000;
            for (int i = 0; i < args.Length; i++)
            {
                if (i < 3) r[i + 1] = args[i];
                else DebugMemory.Write32(nds, DebugCpu.Arm9, r[13] + (uint)(i - 3) * 4, args[i]);
            }
            return PrintfFormatter.Format(nds, DebugCpu.Arm9, format, PrintfArgs.FromRegisters(nds, DebugCpu.Arm9, r, 1));
        }

        private static void WriteString(NdsEmulator nds, uint address, string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s + "\0");
            for (int i = 0; i < bytes.Length; i++)
                DebugMemory.Write8(nds, DebugCpu.Arm9, address + (uint)i, bytes[i]);
        }

        [DataTestMethod]
        [DataRow("%d|%i|%u", "-5|7|4294967295", new uint[] { unchecked((uint)-5), 7, 0xFFFFFFFF })]
        [DataRow("%5d|%-5d|%05d|%+d|% d", "   42|42   |00042|+42| 42", new uint[] { 42, 42, 42, 42, 42 })]
        [DataRow("%x|%X|%#x|%08x|%o|%#o", "beef|BEEF|0xbeef|0000beef|17|017", new uint[] { 0xBEEF, 0xBEEF, 0xBEEF, 0xBEEF, 15, 15 })]
        [DataRow("%s|%.3s|%6s|%-6s|", "hello|hel| hello|hello |", new uint[] { 0x02200000, 0x02200000, 0x02200000, 0x02200000 })]
        [DataRow("%c%c|%%|%p|%s", "hi|%|0x02200010|(null)", new uint[] { 'h', 'i', 0x02200010, 0 })]
        [DataRow("%*d|%-*d|%.*d", "  7|7  |007", new uint[] { 3, 7, 3, 7, 3, 7 })]
        [DataRow("%hd|%hhu|%ld", "-1|255|9", new uint[] { 0xFFFF, 0x1FF, 9 })]
        [DataRow("%.0d|%.2d|%k|%", "|05|%k|%", new uint[] { 0, 5 })]
        public void FormatsLikeC(string format, string expected, uint[] args) =>
            Assert.AreEqual(expected, Format(format, args));

        [TestMethod]
        public void SixtyFourBitArgumentsFollowTheCallingConvention()
        {
            // printf(fmt, int, long long): the int is in r1, the long long in the even pair r2:r3.
            Assert.AreEqual("1 4294967298", Format("%d %lld", 1, 2, 1));
            // printf(fmt, long long, int): r1 is odd, so the long long goes to r2:r3 and the int to [sp].
            Assert.AreEqual("4294967298 9", Format("%lld %d", 0xDEAD, 2, 1, 9));
            // Three ints first: the long long is on the stack, aligned to 8 ([sp+4] is skipped).
            Assert.AreEqual("1 2 3 4 4294967298", Format("%d %d %d %d %lld", 1, 2, 3, 4, 0xBAD, 2, 1));
            // Doubles are 64-bit too.
            ulong pi = (ulong)BitConverter.DoubleToInt64Bits(3.25);
            Assert.AreEqual("3.250|3.25e+00", Format("%.3f|%.2e", 0, (uint)pi, (uint)(pi >> 32), (uint)pi, (uint)(pi >> 32)));
        }

        [TestMethod]
        public void VaListArgumentsAreReadFromMemory()
        {
            var nds = TestRomRunner.Boot("printf");
            WriteString(nds, 0x02200000, "abc");
            uint list = 0x02210000;
            uint[] slots = { 5, 0x02200000, 0x11, 0x22 }; // int, char*, long long low/high (offset 8: already aligned)
            for (int i = 0; i < slots.Length; i++)
                DebugMemory.Write32(nds, DebugCpu.Arm9, list + (uint)i * 4, slots[i]);
            Assert.AreEqual("5 abc 146028888081",
                PrintfFormatter.Format(nds, DebugCpu.Arm9, "%d %s %lld", PrintfArgs.FromVaList(nds, DebugCpu.Arm9, list)));
        }

        [TestMethod]
        public void HookTextParses()
        {
            Assert.IsTrue(FunctionHooks.TryParse("arm7:0x0380ABCD:sprintf", out var cpu, out uint address, out var kind));
            Assert.AreEqual((DebugCpu.Arm7, 0x0380ABCDu, HookKind.SPrintf), (cpu, address, kind));
            Assert.IsTrue(FunctionHooks.TryParse("02001234", out cpu, out address, out kind));
            Assert.AreEqual((DebugCpu.Arm9, 0x02001234u, HookKind.Printf), (cpu, address, kind));
            Assert.IsFalse(FunctionHooks.TryParse("arm9:zz", out _, out _, out _));
            Assert.IsFalse(FunctionHooks.TryParse("arm9:0x10:bogus", out _, out _, out _));
            Assert.AreEqual("arm7:0x0380ABCC:vsnprintf",
                FunctionHooks.Format(new FunctionHook(DebugCpu.Arm7, 0x0380ABCC, HookKind.VSNPrintf, "", false)));
        }
    }
}
