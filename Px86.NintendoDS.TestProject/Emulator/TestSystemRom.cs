using static Px86.NintendoDS.TestProject.Emulator.TestRomRunner;

namespace Px86.NintendoDS.TestProject.Emulator
{
    /// <summary>
    /// End-to-end checks with tools/testroms/src/systest9.c and systest7.c:
    /// both CPUs run real compiled code through direct boot.
    /// </summary>
    [TestClass]
    public class TestSystemRom
    {
        private static Px86.NintendoDS.EmulatorCore.NdsEmulator? _nds;

        [ClassInitialize]
        public static void Run(TestContext _) => _nds = RunToCompletion("systest", waitForArm7: true);

        [TestMethod]
        public void VBlankIrqAndIntrWait() =>
            Assert.AreEqual(3u, Result9(_nds!, 1), "three VBlankIntrWait calls see three VBlank IRQs");

        [TestMethod]
        public void HardwareDivider()
        {
            Assert.AreEqual(unchecked((uint)-14), Result9(_nds!, 2), "-100 / 7");
            Assert.AreEqual(unchecked((uint)-2), Result9(_nds!, 3), "-100 % 7");
        }

        [TestMethod]
        public void HardwareSquareRoot() => Assert.AreEqual(1000u, Result9(_nds!, 4));

        [TestMethod]
        public void TimerOverflowIrqs()
        {
            // F/64 = 523.7 kHz, 1000 ticks per overflow → ~8.75 overflows per frame, 2 frames.
            uint overflows = Result9(_nds!, 5);
            Assert.IsTrue(overflows >= 16 && overflows <= 19, $"expected ~17 overflows, got {overflows}");
        }

        [TestMethod]
        public void ImmediateDma() => Assert.AreEqual(0x100Fu, Result9(_nds!, 6));

        [TestMethod]
        public void HleSwiDivCpuSetLz77() => Assert.AreEqual(7u, Result9(_nds!, 7), "bit 0 Div, bit 1 CpuSet, bit 2 LZ77");

        [TestMethod]
        public void IpcFifoAndSync()
        {
            Assert.AreEqual(0x1234u, Result7(_nds!, 1), "ARM7 received the word");
            Assert.AreEqual(0x1235u, Result9(_nds!, 8), "ARM9 received the echo");
            Assert.AreEqual(7u, Result9(_nds!, 9), "IPCSYNC input = ARM7 output");
        }

        [TestMethod]
        public void CartridgeB7Read()
        {
            Assert.AreEqual(128u, Result9(_nds!, 11), "0x200-byte block = 128 words");
            uint expected = BitConverter.ToUInt32(_nds!.Rom!.Data, (int)_nds.Rom.Header.Arm7RomOffset);
            Assert.AreEqual(expected, Result9(_nds, 10), "first word at ROM 8000h");
        }
    }
}
