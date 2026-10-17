using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Jit;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Debugging
{
    /// <summary>
    /// The debugmsg test ROM prints through every emulator debug channel
    /// (no$gba string/char ports, %param% expansion, Ensata char port, and
    /// in-code "mov r12,r12" messages in ARM and Thumb) on both CPUs.
    /// </summary>
    [TestClass]
    public class TestDebugOutput
    {
        private static NdsEmulator Run(CpuMode mode)
        {
            var nds = TestRomRunner.Boot("debugmsg");
            nds.CpuMode = mode;
            for (int frame = 0; frame < 60; frame++)
            {
                nds.RunFrame();
                if (TestRomRunner.Result9(nds, 0) == TestRomRunner.DoneMagic && TestRomRunner.Result7(nds, 0) == TestRomRunner.DoneMagic)
                    break;
            }
            Assert.AreEqual(TestRomRunner.DoneMagic, TestRomRunner.Result9(nds, 0), "ARM9 finished");
            Assert.AreEqual(TestRomRunner.DoneMagic, TestRomRunner.Result7(nds, 0), "ARM7 finished");
            return nds;
        }

        [DataTestMethod]
        [DataRow(CpuMode.Interpreter)]
        [DataRow(CpuMode.Jit)]
        public void EveryChannelReachesTheLog(CpuMode mode)
        {
            var nds = Run(mode);
            var lines = nds.DebugOutput.Snapshot();
            var arm9 = lines.Where(l => l.Cpu == DebugCpu.Arm9).Select(l => l.Text).ToList();
            var arm7 = lines.Where(l => l.Cpu == DebugCpu.Arm7).Select(l => l.Text).ToList();

            Assert.AreEqual(7, arm9.Count, string.Join(" | ", arm9));
            Assert.AreEqual("raw string", arm9[0], "string port, two writes, line feed in the second");
            Assert.AreEqual("r0=1234ABCD", arm9[1], "%r0% expanded, line feed added");
            Assert.AreEqual("chars OK", arm9[2], "char port, byte writes");
            Assert.AreEqual("W", arm9[3], "char port, 32-bit writes");
            Assert.AreEqual("ensata", arm9[4], "Ensata char port");
            Assert.AreEqual("arm msg r1=00000007", arm9[5], "ARM mov r12,r12 message");
            StringAssert.Matches(arm9[6], new System.Text.RegularExpressions.Regex("^thumb pc=02[0-9A-F]{6}$"), "Thumb mov r12,r12 message");
            CollectionAssert.AreEqual(new[] { "hello from arm7" }, arm7, "the ARM7 has the ports too");

            Assert.AreEqual(0x67246F6Eu, TestRomRunner.Result9(nds, 1), "ID starts with \"no$g\"");
            Assert.AreEqual(1u, TestRomRunner.Result9(nds, 2), "cycle counter advances");
        }

        [TestMethod]
        public void HalfWrittenLinesSurviveSaveStates()
        {
            var nds = TestRomRunner.Boot("touch");
            nds.RunFrame();
            foreach (char c in "partial") nds.Arm9Bus.Write8(0x04FFFA1C, (byte)c);
            using var state = new MemoryStream();
            nds.SaveState(state);
            var other = TestRomRunner.Boot("touch");
            state.Position = 0;
            other.LoadState(state);
            Assert.AreEqual("partial", other.DebugOutput.PendingText(DebugCpu.Arm9));
            other.Arm9Bus.Write8(0x04FFFA1C, (byte)'\n');
            Assert.AreEqual("partial", other.DebugOutput.Snapshot().Single().Text);
        }

        [TestMethod]
        public void LastClocksMeasuresBetweenMarks()
        {
            var nds = TestRomRunner.Boot("touch");
            Assert.AreEqual("", nds.DebugOutput.Expand(DebugCpu.Arm9, "%zeroclks%"));
            nds.RunFrame();
            long elapsed = long.Parse(nds.DebugOutput.Expand(DebugCpu.Arm9, "%lastclks%"));
            Assert.IsTrue(elapsed > 100_000, $"a frame of ARM9 cycles, got {elapsed}");
            Assert.AreEqual("100%done %nope%", nds.DebugOutput.Expand(DebugCpu.Arm9, "100%done %nope%"), "unknown fields stay as written");
        }
    }
}
