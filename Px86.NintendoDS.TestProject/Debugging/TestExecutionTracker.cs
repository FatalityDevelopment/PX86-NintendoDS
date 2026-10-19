using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Jit;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Debugging
{
    /// <summary>The calltest ROM: nested calls into Thumb code, then a loop and a recursion with known call counts.</summary>
    [TestClass]
    public class TestExecutionTracker
    {
        [DataTestMethod]
        [DataRow(CpuMode.Interpreter)]
        [DataRow(CpuMode.Jit)]
        public void CallStackCoverageAndCallCounts(CpuMode mode)
        {
            var nds = TestRomRunner.Boot("calltest");
            nds.CpuMode = mode;
            nds.Tracker.Enabled = true;
            for (int frame = 0; frame < 5; frame++)
                nds.RunFrame();

            uint level1 = TestRomRunner.Result9(nds, 1), level2 = TestRomRunner.Result9(nds, 2);
            uint thumb3 = TestRomRunner.Result9(nds, 3), wait = TestRomRunner.Result9(nds, 4);
            var stack = nds.Tracker.CallStack(DebugCpu.Arm9).Where(f => !f.Interrupt).ToList();
            var targets = stack.Select(f => f.Target).ToList();
            string shown = string.Join(" < ", targets.Select(t => t.ToString("X8")));
            CollectionAssert.AreEqual(new[] { wait, thumb3, level2, level1 }, targets.Take(4).ToArray(), shown);
            Assert.IsTrue((thumb3 & 1) != 0, "the Thumb function's address has bit 0 set");
            Assert.IsTrue(stack[1].Thumb == false && stack[0].Thumb, "level2 (ARM) called thumb_level3; thumb_level3 (Thumb) called wait_here");
            Assert.IsTrue(nds.Tracker.Executed(DebugCpu.Arm9, level1 & ~1u), "coverage of level1");
            Assert.IsFalse(nds.Tracker.Executed(DebugCpu.Arm9, TestRomRunner.Result9(nds, 7)), "fact hasn't run yet");

            Px86.NintendoDS.EmulatorCore.Debugging.DebugMemory.Write32(nds, DebugCpu.Arm9, 0x02300014, 1);
            for (int frame = 0; frame < 5 && TestRomRunner.Result9(nds, 0) != TestRomRunner.DoneMagic; frame++)
                nds.RunFrame();
            Assert.AreEqual(TestRomRunner.DoneMagic, TestRomRunner.Result9(nds, 0));
            Assert.AreEqual(120u, TestRomRunner.Result9(nds, 8));
            Assert.AreEqual(1, nds.Tracker.CallStack(DebugCpu.Arm9).Count(f => !f.Interrupt), "back in main: only crt0's call to main (mov lr, pc; bx r0) is left");

            var counts = nds.Tracker.CallCounts(DebugCpu.Arm9).ToDictionary(c => c.Target, c => c.Calls);
            Assert.AreEqual(10, counts[TestRomRunner.Result9(nds, 6) & ~1u], "leaf");
            Assert.AreEqual(5, counts[TestRomRunner.Result9(nds, 7) & ~1u], "fact(5) calls itself 4 times + 1 from main");
            Assert.IsTrue(nds.Tracker.Executed(DebugCpu.Arm9, TestRomRunner.Result9(nds, 7)), "fact ran");

            var bits = nds.Tracker.Coverage(DebugCpu.Arm9, level1 & ~1u, 4);
            Assert.AreEqual(1, bits[0] & 1, "coverage bitmap: first halfword of level1");
            Assert.IsTrue(nds.Tracker.Pages(DebugCpu.Arm9).Any(p => p.Halfwords > 0));
        }

        [TestMethod]
        public void TrackingIsOffByDefaultAndClears()
        {
            var nds = TestRomRunner.Boot("calltest");
            nds.RunFrame();
            Assert.IsFalse(nds.Tracker.Enabled);
            Assert.AreEqual(0, nds.Tracker.Pages(DebugCpu.Arm9).Count);
            nds.Tracker.Enabled = true;
            nds.RunFrame();
            Assert.IsTrue(nds.Tracker.Pages(DebugCpu.Arm9).Count > 0);
            nds.Tracker.Clear();
            Assert.AreEqual(0, nds.Tracker.Pages(DebugCpu.Arm9).Count);
        }
    }
}
