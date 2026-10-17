using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hardware.Spi;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Hardware
{
    /// <summary>
    /// The mic test ROM reads TSC channel 6 over SPI like the SDK: silence
    /// while the amplifier is off, the input scaled around 800h by the
    /// amplifier gain, and the 8-bit readout's own bit layout.
    /// </summary>
    [TestClass]
    public class TestMicrophone
    {
        private sealed class Constant : IMicrophoneSource
        {
            public short Level;
            public short Sample(long arm9Cycles) => Level;
        }

        private static NdsEmulator Run(IMicrophoneSource source)
        {
            var nds = TestRomRunner.Boot("mic");
            nds.Spi.Touch.Microphone = source;
            for (int frame = 0; frame < 60 && TestRomRunner.Result7(nds, 0) != TestRomRunner.DoneMagic; frame++)
                nds.RunFrame();
            Assert.AreEqual(TestRomRunner.DoneMagic, TestRomRunner.Result7(nds, 0), "ARM7 finished");
            return nds;
        }

        [TestMethod]
        public void AmplifierGainAndBothResolutions()
        {
            var nds = Run(new Constant { Level = 16000 });
            uint R(int i) => TestRomRunner.Result7(nds, i);
            Assert.AreEqual(0x800u, R(1), "amplifier off: silence");
            Assert.AreEqual(0x800u + 16000 / 32, R(2), "gain 0: 12-bit value around 800h");
            Assert.AreEqual(R(2), R(3));
            Assert.AreEqual(R(2), R(4));
            Assert.AreEqual((0x800u + 16000 / 32) >> 4, R(5), "8-bit readout (dummy bit, 8 bits, MSB first)");
            Assert.AreEqual(Math.Min(0xFFFu, 0x800u + (16000u << 2) / 32), R(6), "gain 2 = 4x louder");
        }

        [TestMethod]
        public void BlowingIsLoudNoise()
        {
            var nds = Run(new MicrophoneInput { Blowing = true });
            Assert.IsTrue(TestRomRunner.Result7(nds, 3) < 0x700 && TestRomRunner.Result7(nds, 4) > 0x900,
                $"noise spans the range: {TestRomRunner.Result7(nds, 3):X}..{TestRomRunner.Result7(nds, 4):X}");
        }

        [TestMethod]
        public void CapturedSamplesPlayAtEmulatedSpeed()
        {
            var mic = new MicrophoneInput { SampleRate = 16000 };
            var ramp = new short[16000];
            for (int i = 0; i < ramp.Length; i++) ramp[i] = (short)(i - 8000);
            mic.Push(ramp);
            long cyclesPerSample = Scheduler.Arm9ClockHz / 16000;
            short first = mic.Sample(0);
            Assert.AreEqual(16000 - mic.Latency - 8000, first, "starts a fixed latency behind the newest sample");
            short later = mic.Sample(cyclesPerSample * 100);
            Assert.AreEqual(first + 100, later, 1, "100 samples of emulated time later");
            mic.Push(new short[20000]); // the emulator stalled: capture ran far ahead
            Assert.AreEqual(0, mic.Sample(cyclesPerSample * 101), "resyncs to the newest samples instead of lagging");
        }
    }
}
