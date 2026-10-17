using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Debugging
{
    [TestClass]
    public class TestDebugger
    {
        private static NdsEmulator BootWithDebugger(string rom, out DebugMonitor debug, int frames = 2)
        {
            var nds = TestRomRunner.Boot(rom);
            for (int i = 0; i < frames; i++)
                nds.RunFrame();
            debug = new DebugMonitor();
            nds.Debugger = debug;
            return nds;
        }

        [TestMethod]
        public void BreakpointStopsBeforeTheInstruction()
        {
            var nds = BootWithDebugger("bench", out var debug);
            uint loopPc = nds.Arm9.Pc;
            debug.AddBreakpoint(DebugCpu.Arm9, loopPc);
            nds.RunCycles(10); // move off the breakpoint first

            Assert.AreEqual(RunResult.Break, nds.Run(EmulatorHostTimings.Frame * 4), "bench loops forever, so it must come back to the breakpoint");
            Assert.AreEqual(loopPc, nds.Arm9.Pc, "stopped at the breakpoint, before running it");
            Assert.AreEqual(new BreakInfo(DebugCpu.Arm9, loopPc, BreakReason.Breakpoint), debug.PendingBreak, "break info");
        }

        [TestMethod]
        public void SkipOnceRunsPastTheBreakpointThenStopsAgain()
        {
            var nds = BootWithDebugger("bench", out var debug);
            uint loopPc = nds.Arm9.Pc;
            debug.AddBreakpoint(DebugCpu.Arm9, loopPc);
            nds.RunCycles(10);
            Assert.AreEqual(RunResult.Break, nds.Run(EmulatorHostTimings.Frame * 4), "first hit");
            long cycles = nds.Arm9.TotalCycles;

            debug.PendingBreak = null;
            debug.SkipOnce(DebugCpu.Arm9, nds.Arm9.Pc);
            Assert.AreEqual(RunResult.Break, nds.Run(EmulatorHostTimings.Frame * 4), "second hit");
            Assert.AreEqual(loopPc, nds.Arm9.Pc, "same breakpoint");
            Assert.IsTrue(nds.Arm9.TotalCycles > cycles, "the core ran in between");
        }

        [TestMethod]
        public void WriteWatchpointReportsTheAccess()
        {
            var nds = BootWithDebugger("bench", out var debug);
            uint target = TestRomRunner.ResultBase9 + 4; // RESULT9(1) = round, once per round
            debug.AddWatch(new Watchpoint(DebugCpu.Arm9, target, target + 4, WatchCondition.Write));
            Assert.IsInstanceOfType(nds.Arm9.Bus, typeof(WatchingMemoryBus), "watch bus installed");

            RunResult result = RunResult.BudgetUsed;
            for (int i = 0; i < 600 && result != RunResult.Break; i++)
                result = nds.Run(EmulatorHostTimings.Frame);
            Assert.AreEqual(RunResult.Break, result, "the round counter is written");
            var hit = debug.PendingBreak!.Value;
            Assert.AreEqual(BreakReason.Watchpoint, hit.Reason, "reason");
            Assert.AreEqual(target, hit.Address, "address");
            Assert.IsTrue(hit.IsWrite, "write");

            debug.ClearWatches();
            Assert.AreSame(nds.Arm9Bus, nds.Arm9.Bus, "watch bus removed again");
        }

        [TestMethod]
        public void StepRunsOneInstructionAndKeepsTheCoresTogether()
        {
            var nds = BootWithDebugger("bench", out _);
            for (int i = 0; i < 20; i++)
            {
                long before = nds.Arm9.TotalCycles;
                Assert.IsTrue(nds.StepInstruction(DebugCpu.Arm9), "step");
                Assert.IsTrue(nds.Arm9.TotalCycles > before, "the ARM9 executed something");
            }
            long arm7 = nds.Arm7.TotalCycles;
            for (int i = 0; i < 20; i++)
                Assert.IsTrue(nds.StepInstruction(DebugCpu.Arm7), "ARM7 step");
            Assert.IsTrue(nds.Arm7.TotalCycles > arm7, "the ARM7 executed (or idled) forward");

            // Execution afterwards is still sane.
            nds.RunFrame();
            nds.RunFrame();
            Assert.AreEqual(TestRomRunner.DoneMagic, TestRomRunner.Result9(nds, 0), "bench still runs");
        }

        [TestMethod]
        public void AnIdleDebuggerDoesNotChangeExecution()
        {
            var plain = TestRomRunner.Boot("systest");
            var watched = TestRomRunner.Boot("systest");
            watched.Debugger = new DebugMonitor();
            for (int i = 0; i < 20; i++)
            {
                plain.RunFrame();
                watched.RunFrame();
            }
            CollectionAssert.AreEqual(plain.MainRam, watched.MainRam, "main RAM");
            Assert.AreEqual(plain.Scheduler.Now, watched.Scheduler.Now, "time");
        }

        [TestMethod]
        public void DebugReadsDoNotPopTheIpcFifo()
        {
            var nds = TestRomRunner.Boot("systest");
            Assert.AreEqual(0u, DebugMemory.Read32(nds, DebugCpu.Arm9, 0x04100000), "IPCFIFORECV is not read");
            DebugMemory.WriteBlock(nds, DebugCpu.Arm9, 0x05000000, new byte[] { 0x1F, 0x00, 0xE0, 0x03 });
            Assert.AreEqual(0x03E0001Fu, DebugMemory.Read32(nds, DebugCpu.Arm9, 0x05000000), "palette patched with wide writes");
        }
    }

    internal static class EmulatorHostTimings
    {
        public const long Frame = EmulatorCore.Video.LcdController.Arm9CyclesPerFrame;
    }
}
