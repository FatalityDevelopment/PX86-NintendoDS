using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Jit;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Jit
{
    /// <summary>
    /// The block cache must not change anything: every test ROM run with
    /// cached blocks must stay bit-identical to the interpreter, frame by frame.
    /// </summary>
    [TestClass]
    public class TestBlockCache
    {
        private static NdsEmulator Boot(string rom, CpuMode mode)
        {
            var nds = TestRomRunner.Boot(rom);
            nds.CpuMode = mode;
            return nds;
        }

        private static void AssertSame(NdsEmulator a, NdsEmulator b, string what)
        {
            Assert.AreEqual(a.Scheduler.Now, b.Scheduler.Now, $"{what}: time");
            Assert.AreEqual(a.Arm9.ToString(), b.Arm9.ToString(), $"{what}: ARM9");
            Assert.AreEqual(a.Arm7.ToString(), b.Arm7.ToString(), $"{what}: ARM7");
            Assert.AreEqual(a.Arm9.TotalCycles, b.Arm9.TotalCycles, $"{what}: ARM9 cycles");
            Assert.AreEqual(a.Arm7.TotalCycles, b.Arm7.TotalCycles, $"{what}: ARM7 cycles");
            CollectionAssert.AreEqual(a.MainRam, b.MainRam, $"{what}: main RAM");
            CollectionAssert.AreEqual(a.Arm7Wram, b.Arm7Wram, $"{what}: ARM7 WRAM");
        }

        [DataTestMethod]
        [DataRow("systest", 30)]
        [DataRow("gfxtest", 10)]
        [DataRow("gfx3d", 10)]
        [DataRow("tex3d", 10)]
        [DataRow("sound", 20)]
        [DataRow("touch", 10)]
        [DataRow("bench", 20)]
        public void CachedRunsMatchTheInterpreter(string rom, int frames)
        {
            var interpreter = Boot(rom, CpuMode.Interpreter);
            var cached = Boot(rom, CpuMode.Cached);
            for (int frame = 0; frame < frames; frame++)
            {
                interpreter.RunFrame();
                cached.RunFrame();
                AssertSame(interpreter, cached, $"{rom} frame {frame}");
            }
            CollectionAssert.AreEqual(ScreenCapture.Compose(interpreter), ScreenCapture.Compose(cached), $"{rom}: screens");
            Assert.IsTrue(cached.BlocksCompiled > 0, "blocks were actually used");
            Assert.AreEqual(0, interpreter.BlocksCompiled, "the interpreter built none");
        }

        [TestMethod]
        public void OverwrittenCodeIsRebuilt()
        {
            // Let bench run so its loop gets cached, then patch the instruction under the PC
            // ("mov r0, #42; b ." in ARM state) and check the patched code is what runs.
            foreach (var mode in new[] { CpuMode.Interpreter, CpuMode.Cached })
            {
                var nds = Boot("bench", mode);
                for (int i = 0; i < 3; i++) nds.RunFrame();
                const uint patch = 0x023F8000;
                nds.Arm9Bus.BusWrite32(patch, 0xE3A0002A);       // mov r0, #42
                nds.Arm9Bus.BusWrite32(patch + 4, 0xEAFFFFFE);   // b .
                nds.RunCycles(100);                               // cache the patch area? not yet executed
                nds.Arm9.SetPc(patch);
                nds.RunFrame();
                Assert.AreEqual(42u, nds.Arm9.R[0], $"{mode}: first version ran");

                nds.Arm9Bus.BusWrite32(patch, 0xE3A00007);       // overwrite: mov r0, #7
                nds.Arm9.SetPc(patch);
                nds.RunFrame();
                Assert.AreEqual(7u, nds.Arm9.R[0], $"{mode}: the overwritten instruction is the one that runs");
            }
        }

        [TestMethod]
        public void SaveStatesWorkWithCachedBlocks()
        {
            var a = Boot("systest", CpuMode.Cached);
            for (int i = 0; i < 5; i++) a.RunFrame();
            using var state = new MemoryStream();
            a.SaveState(state);
            for (int i = 0; i < 5; i++) a.RunFrame();

            var b = Boot("systest", CpuMode.Cached);
            b.RunFrame();
            state.Position = 0;
            b.LoadState(state);
            for (int i = 0; i < 5; i++) b.RunFrame();
            AssertSame(a, b, "after load");
        }
    }
}
