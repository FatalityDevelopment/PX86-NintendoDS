using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.TestProject.Emulator
{
    /// <summary>
    /// Save states must capture everything: after loading a state into a fresh
    /// emulator, running the same frames has to give bit-identical results.
    /// </summary>
    [TestClass]
    public class TestSaveStates
    {
        [DataTestMethod]
        [DataRow("bench", 20, 30)]
        [DataRow("gfx3d", 3, 10)]
        [DataRow("tex3d", 3, 5)]
        [DataRow("sound", 2, 10)]
        [DataRow("systest", 1, 10)]
        public void LoadedStateContinuesIdentically(string rom, int framesBefore, int framesAfter)
        {
            var original = TestRomRunner.Boot(rom);
            for (int i = 0; i < framesBefore; i++)
                original.RunFrame();

            using var state = new MemoryStream();
            original.SaveState(state);

            for (int i = 0; i < framesAfter; i++)
                original.RunFrame();

            var restored = TestRomRunner.Boot(rom);
            restored.RunFrame(); // diverge first, so the load really has to overwrite everything
            state.Position = 0;
            restored.LoadState(state);
            for (int i = 0; i < framesAfter; i++)
                restored.RunFrame();

            Assert.AreEqual(original.FrameCount, restored.FrameCount, "frame counter");
            Assert.AreEqual(original.Scheduler.Now, restored.Scheduler.Now, "scheduler time");
            Assert.AreEqual(original.Arm9.ToString(), restored.Arm9.ToString(), "ARM9 registers");
            Assert.AreEqual(original.Arm7.ToString(), restored.Arm7.ToString(), "ARM7 registers");
            CollectionAssert.AreEqual(original.MainRam, restored.MainRam, "main RAM");
            CollectionAssert.AreEqual(original.Arm7Wram, restored.Arm7Wram, "ARM7 WRAM");
            CollectionAssert.AreEqual(ScreenCapture.Compose(original), ScreenCapture.Compose(restored), "both screens");
        }

        [TestMethod]
        public void StateFromAnotherGameIsRejected()
        {
            var a = TestRomRunner.Boot("gfxtest");
            using var state = new MemoryStream();
            a.SaveState(state);

            var other = new NdsEmulator();
            other.LoadRom(TestRomBuilder.Build(TestRomBuilder.Arm7IdleLoop, gameCode: "XXXX"));
            other.Boot();
            state.Position = 0;
            Assert.ThrowsException<InvalidDataException>(() => other.LoadState(state));
        }

        [TestMethod]
        public void SessionSlotsRoundTripAndSurviveABadFile()
        {
            string folder = Path.Combine(Path.GetTempPath(), "px86-states-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string romPath = Path.Combine(folder, "gfx3d.nds");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "TestRoms", "gfx3d.nds"), romPath);
                using var session = GameSession.Start(new GameLoadOptions { RomPath = romPath });
                var nds = session.Emulator;

                Assert.IsFalse(session.LoadState(3), "an empty slot loads nothing");

                for (int i = 0; i < 3; i++) nds.RunFrame();
                session.SaveState(3);
                Assert.IsTrue(File.Exists(Path.Combine(folder, "gfx3d.st3")), "state file next to the ROM");
                long savedFrame = nds.FrameCount;
                var savedScreens = ScreenCapture.Compose(nds);

                for (int i = 0; i < 5; i++) nds.RunFrame();
                Assert.IsTrue(session.LoadState(3), "slot 3 loads");
                Assert.AreEqual(savedFrame, nds.FrameCount, "frame counter restored");
                CollectionAssert.AreEqual(savedScreens, ScreenCapture.Compose(nds), "screens restored");

                // A truncated file must fail without leaving the machine half-loaded.
                var bytes = File.ReadAllBytes(session.StatePath(3));
                File.WriteAllBytes(session.StatePath(4), bytes[..(bytes.Length / 2)]);
                for (int i = 0; i < 2; i++) nds.RunFrame();
                long before = nds.FrameCount;
                var mainRam = (byte[])nds.MainRam.Clone();
                Assert.ThrowsException<EndOfStreamException>(() => session.LoadState(4));
                Assert.AreEqual(before, nds.FrameCount, "frame counter untouched after a failed load");
                CollectionAssert.AreEqual(mainRam, nds.MainRam, "main RAM untouched after a failed load");
                nds.RunFrame();
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
