using System.Buffers.Binary;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hardware.Spi;
using Px86.NintendoDS.FileFormats.Checksums;
using Px86.NintendoDS.FileFormats.Rom;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Hardware
{
    /// <summary>
    /// A tap must land where the mouse clicked once the game converts the TSC
    /// reading with the firmware calibration — for the generated firmware and
    /// for a real dump whose calibration differs from our default ADC mapping.
    /// </summary>
    [TestClass]
    public class TestTouchScreen
    {
        private static readonly (int X, int Y)[] Points = { (0, 0), (10, 20), (128, 96), (200, 150), (255, 191) };

        /// <summary>Calibration like a real console's (GBATEK-style values, not on our default line).</summary>
        private static FirmwareFlash FirmwareWithRealCalibration()
        {
            var data = FirmwareFlash.CreateDefault().Data.ToArray();
            for (int copy = 0; copy < 2; copy++)
            {
                int o = FirmwareFlash.UserSettingsOffset + copy * 0x100;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 0x58), 0x02A4);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 0x5A), 0x0342);
                data[o + 0x5C] = 0x20; data[o + 0x5D] = 0x20;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 0x5E), 0x0D3C);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 0x60), 0x0C42);
                data[o + 0x62] = 0xE0; data[o + 0x63] = 0xA0;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 0x72), Crc16.Compute(data.AsSpan(o, 0x70)));
            }
            return new FirmwareFlash(data);
        }

        private static void CheckTaps(NdsEmulator nds, string what)
        {
            nds.RunFrame();
            Assert.AreEqual(TestRomRunner.DoneMagic, TestRomRunner.Result7(nds, 0), $"{what}: ARM7 test running");
            foreach (var (x, y) in Points)
            {
                nds.Spi.Touch.SetTouch(x, y);
                nds.Keypad.SetPen(true);
                nds.RunFrame();
                nds.RunFrame();
                Assert.AreEqual(1u, TestRomRunner.Result7(nds, 3), $"{what}: pen down seen");
                int gx = (int)TestRomRunner.Result7(nds, 1), gy = (int)TestRomRunner.Result7(nds, 2);
                Assert.IsTrue(Math.Abs(gx - x) <= 1 && Math.Abs(gy - y) <= 1,
                    $"{what}: tap ({x},{y}) read back as ({gx},{gy}) [adc {TestRomRunner.Result7(nds, 5):X3},{TestRomRunner.Result7(nds, 6):X3}]");
            }
            nds.Spi.Touch.ReleaseTouch();
            nds.Keypad.SetPen(false);
            nds.RunFrame();
            Assert.AreEqual(0u, TestRomRunner.Result7(nds, 3), $"{what}: pen up seen");
        }

        [TestMethod]
        public void TapsLandOnTheClickedPixelWithTheGeneratedFirmware()
        {
            CheckTaps(TestRomRunner.Boot("touch"), "generated firmware");
        }

        [TestMethod]
        public void TapsLandOnTheClickedPixelWithARealFirmwareCalibration()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TestRoms", "touch.nds");
            var nds = new NdsEmulator();
            nds.LoadFirmware(FirmwareWithRealCalibration().Data.ToArray());
            nds.LoadRom(NdsRom.Load(path));
            nds.Boot();
            Assert.AreEqual(0x02A4, nds.Spi.Touch.CurrentCalibration.X.Adc1, "calibration taken from the firmware");
            CheckTaps(nds, "real calibration");
        }

        [TestMethod]
        public void GeneratedCalibrationIsValid()
        {
            var settings = FirmwareFlash.CreateDefault().UserSettings;
            Assert.IsTrue(settings[0x62] > settings[0x5C] && settings[0x63] > settings[0x5D],
                "scr2 > scr1 (no byte overflow at the far corner)");
            var c = TouchScreen.Calibration.FromUserSettings(settings);
            Assert.AreNotEqual(TouchScreen.Calibration.Default, default, "parsed");
            for (int x = 0; x < 256; x++)
                Assert.AreEqual(x, c.X.ToPixel(c.X.ToAdc(x)), $"x {x} round trip");
            for (int y = 0; y < 192; y++)
                Assert.AreEqual(y, c.Y.ToPixel(c.Y.ToAdc(y)), $"y {y} round trip");
        }
    }
}
