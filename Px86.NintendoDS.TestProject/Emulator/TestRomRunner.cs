using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.FileFormats.Rom;

namespace Px86.NintendoDS.TestProject.Emulator
{
    /// <summary>Runs a test ROM from TestRoms/ (built by tools/testroms/build.sh) until it reports done.</summary>
    internal static class TestRomRunner
    {
        public const uint ResultBase9 = 0x02300000;
        public const uint ResultBase7 = 0x02300100;
        public const uint DoneMagic = 0x600DF00D;

        public static NdsEmulator Boot(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestRoms", name + ".nds");
            var nds = new NdsEmulator();
            nds.LoadRom(NdsRom.Load(path));
            nds.Boot();
            return nds;
        }

        public static uint Result9(NdsEmulator nds, int index) => nds.Arm9Bus.BusRead32(ResultBase9 + (uint)index * 4);
        public static uint Result7(NdsEmulator nds, int index) => nds.Arm9Bus.BusRead32(ResultBase7 + (uint)index * 4);

        /// <summary>Run frames until RESULT9(0) (and optionally RESULT7(0)) hold the done marker.</summary>
        public static NdsEmulator RunToCompletion(string name, int maxFrames = 120, bool waitForArm7 = false)
        {
            var nds = Boot(name);
            for (int frame = 0; frame < maxFrames; frame++)
            {
                nds.RunFrame();
                bool done9 = Result9(nds, 0) == DoneMagic;
                bool done7 = !waitForArm7 || Result7(nds, 0) == DoneMagic;
                if (done9 && done7)
                    return nds;
            }
            Assert.Fail($"{name} did not finish in {maxFrames} frames. ARM9: {nds.Arm9}\nARM7: {nds.Arm7}");
            return nds;
        }
    }
}
