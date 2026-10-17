using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Video
{
    /// <summary>Renders tools/testroms/src/gfxtest9.c and checks pixels on both screens.</summary>
    [TestClass]
    public class TestGraphicsRom
    {
        private const uint Red = 0xFFFF0000u;
        private const uint Green = 0xFF00FF00u;
        private const uint Blue = 0xFF0000FFu;

        private static NdsEmulator? _nds;
        private static uint[] _screens = Array.Empty<uint>();

        [ClassInitialize]
        public static void Run(TestContext context)
        {
            _nds = TestRomRunner.RunToCompletion("gfxtest");
            _nds.RunFrame(); // one full frame after setup
            _screens = ScreenCapture.Compose(_nds);
            ScreenCapture.SavePng(_nds, Path.Combine(context.TestRunDirectory ?? Path.GetTempPath(), "gfxtest.png"));
        }

        private static uint Top(int x, int y) => _screens[y * 256 + x];
        private static uint Bottom(int x, int y) => _screens[(192 + y) * 256 + x];

        [TestMethod]
        public void TextBackground_Checkerboard()
        {
            Assert.AreEqual(Green, Top(0, 0), "map entry (0,0) = tile 2 (green)");
            Assert.AreEqual(Red, Top(8, 0), "map entry (1,0) = tile 1 (red)");
            Assert.AreEqual(Red, Top(0, 8), "map entry (0,1) = tile 1 (red)");
            Assert.AreEqual(Green, Top(15, 15), "map entry (1,1) = tile 2 (green)");
        }

        [TestMethod]
        public void Sprite_DrawnOverBackground()
        {
            Assert.AreEqual(Blue, Top(100, 50), "sprite top-left");
            Assert.AreEqual(Blue, Top(115, 65), "sprite bottom-right");
            Assert.AreNotEqual(Blue, Top(116, 66), "outside the 16x16 sprite");
        }

        [TestMethod]
        public void DirectColorBitmapBackground_OnEngineB()
        {
            Assert.AreEqual(0xFF000000u, Bottom(0, 10), "x = 0 → red 0");
            Assert.AreEqual(Red, Bottom(255, 10), "x = 255 → red 31 (full red)");
        }
    }
}
