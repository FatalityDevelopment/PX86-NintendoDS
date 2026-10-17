using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Audio
{
    /// <summary>tools/testroms/src/sound7.c: PSG square wave + one-shot PCM16 on the SPU.</summary>
    [TestClass]
    public class TestSoundRom
    {
        [TestMethod]
        public void PsgAndOneShotPcm()
        {
            var nds = TestRomRunner.RunToCompletion("sound", waitForArm7: true);
            Assert.AreEqual(1u, TestRomRunner.Result7(nds, 1), "one-shot channel busy after start");
            Assert.AreEqual(0u, TestRomRunner.Result7(nds, 2), "one-shot channel stops by itself");
            Assert.AreEqual(1u, TestRomRunner.Result7(nds, 3), "PSG channel keeps playing");

            nds.Sound.ReadSamples(new short[65536]); // drop what was produced before the checks
            nds.RunFrame();
            var buffer = new short[4096];
            int frames = nds.Sound.ReadSamples(buffer);
            Assert.IsTrue(frames > 500, $"~547 samples per frame expected, got {frames}");

            int min = int.MaxValue, max = int.MinValue, transitions = 0;
            for (int i = 0; i < frames; i++)
            {
                int left = buffer[i * 2];
                min = Math.Min(min, left);
                max = Math.Max(max, left);
                if (i > 0 && Math.Sign(left) != Math.Sign(buffer[(i - 1) * 2]))
                    transitions++;
            }
            Assert.IsTrue(max > 4000 && min < -4000, $"square wave swings both ways (min {min}, max {max})");
            // 1 kHz square wave over one frame (~16.7 ms) ≈ 33 sign changes.
            Assert.IsTrue(transitions >= 25 && transitions <= 40, $"about 1 kHz, got {transitions} sign changes");
        }
    }
}
