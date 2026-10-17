using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.TestProject.Emulator
{
    [TestClass]
    public class TestDirectBoot
    {
        // ARM9: power on, DISPCNT = VRAM display of bank A, VRAMCNT_A = LCDC,
        // fill 256×192 pixels of bank A with red (0x001F), then spin.
        private static readonly uint[] FillRedProgram =
        {
            0xE3A00301u, // mov r0, #0x04000000
            0xE3A01C82u, // mov r1, #0x8200
            0xE381100Fu, // orr r1, r1, #0x0F
            0xE5801304u, // str r1, [r0, #0x304]   POWCNT1
            0xE3A01802u, // mov r1, #0x00020000
            0xE5801000u, // str r1, [r0]           DISPCNT
            0xE3A01080u, // mov r1, #0x80
            0xE5C01240u, // strb r1, [r0, #0x240]  VRAMCNT_A
            0xE3A0251Au, // mov r2, #0x06800000
            0xE3A0301Fu, // mov r3, #0x1F
            0xE1833803u, // orr r3, r3, r3, lsl #16
            0xE3A04A06u, // mov r4, #0x6000
            0xE2544001u, // subs r4, r4, #1
            0xE4823004u, // str r3, [r2], #4
            0x1AFFFFFCu, // bne (subs)
            0xEAFFFFFEu, // b .
        };

        [TestMethod]
        public void DirectBoot_LoadsBinariesHeaderAndEntryState()
        {
            var nds = new NdsEmulator();
            var rom = TestRomBuilder.Build(FillRedProgram);
            nds.LoadRom(rom);
            nds.Boot();

            Assert.AreEqual(TestRomBuilder.Arm9Address, nds.Arm9.Pc, "ARM9 at its entry point");
            Assert.AreEqual(TestRomBuilder.Arm7Address, nds.Arm7.Pc, "ARM7 at its entry point");
            Assert.AreEqual(ProcessorMode.System, nds.Arm9.Mode);
            Assert.AreEqual(0xE3A00301u, nds.Arm9Bus.Read32(0x02000000), "ARM9 binary copied");
            Assert.AreEqual((byte)'P', nds.Arm9Bus.Read8(DirectBoot.HeaderAddress), "header copied to 027FFE00h");
            Assert.AreEqual(nds.Cartridge.ChipId, nds.Arm9Bus.Read32(0x027FF800), "chip ID in boot info");
            Assert.AreEqual(3, nds.Control.WramMode, "shared WRAM given to the ARM7");
            Assert.AreEqual(0x03000000u, nds.Cp15.DtcmBase);
            Assert.AreEqual(5, nds.Arm9Bus.Read16(DirectBoot.UserSettingsAddress), "firmware user settings version");
        }

        [TestMethod]
        public void RunFrame_VramDisplayModeShowsBankA()
        {
            var nds = new NdsEmulator();
            nds.LoadRom(TestRomBuilder.Build(FillRedProgram));
            nds.Boot();

            for (int i = 0; i < 3; i++)
                nds.RunFrame();

            Assert.AreEqual(3, nds.FrameCount);
            Assert.IsTrue(nds.Control.DisplaySwap, "POWCNT1 bit 15 puts engine A on the top screen");
            uint pixel = Gpu2DEngine.ToArgb(nds.TopScreen[100 * 256 + 128]);
            Assert.AreEqual(0xFFFF0000u, pixel, "top screen shows the red VRAM fill");
        }

        [TestMethod]
        public void RunFrame_TakesOneFrameOfLcdTime()
        {
            var nds = new NdsEmulator();
            nds.LoadRom(TestRomBuilder.Build(FillRedProgram));
            nds.Boot();
            nds.RunFrame(); // up to the first VBlank (line 192)
            long start = nds.Scheduler.Now;
            nds.RunFrame();
            long elapsed = nds.Scheduler.Now - start;
            Assert.IsTrue(System.Math.Abs(elapsed - LcdController.Arm9CyclesPerFrame) <= NdsEmulator.MaxSliceCycles,
                $"one frame = {LcdController.Arm9CyclesPerFrame} ARM9 cycles, got {elapsed}");
        }
    }
}
