using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Video
{
    /// <summary>
    /// tools/testroms/src/gfx3d9.c: a Gouraud-shaded triangle in front of a white quad
    /// (drawn later, further away), rendered through the geometry engine and rasterizer.
    /// </summary>
    [TestClass]
    public class TestGpu3DRom
    {
        private static NdsEmulator? _nds;
        private static uint[] _screens = Array.Empty<uint>();

        [ClassInitialize]
        public static void Run(TestContext _)
        {
            _nds = TestRomRunner.RunToCompletion("gfx3d");
            _nds.RunFrame();
            _screens = ScreenCapture.Compose(_nds);
        }

        private static uint Top(int x, int y) => _screens[y * 256 + x];
        private static int Red(uint argb) => (int)(argb >> 16) & 0xFF;
        private static int Green(uint argb) => (int)(argb >> 8) & 0xFF;
        private static int Blue(uint argb) => (int)argb & 0xFF;

        [TestMethod]
        public void PolygonRamCountsAndSwap()
        {
            Assert.AreEqual(2u, TestRomRunner.Result9(_nds!, 2) & 0xFFF, "RAM_COUNT before the swap: 2 polygons");
            Assert.AreNotEqual(0u, TestRomRunner.Result9(_nds!, 1) & (1u << 27), "GXSTAT busy while the swap waits for VBlank");
            Assert.AreEqual(0u, TestRomRunner.Result9(_nds!, 3) & 0xFFF, "the swap hands the list to the renderer");
        }

        [TestMethod]
        public void RearPlaneShowsWhereNothingIsDrawn() =>
            Assert.AreEqual(0xFF000000u, Top(5, 5), "opaque black clear colour");

        [TestMethod]
        public void DepthTestKeepsNearerTriangleInFront()
        {
            uint centre = Top(128, 100);
            Assert.AreNotEqual(0xFFFFFFFFu, centre, "the triangle, not the white quad behind it");
            Assert.AreNotEqual(0xFF000000u, centre, "something was drawn");
            Assert.AreEqual(0xFFFFFFFFu, Top(240, 20), "the quad is visible outside the triangle");
        }

        [TestMethod]
        public void VertexColoursAreInterpolated()
        {
            uint nearRed = Top(70, 140);    // close to the red bottom-left vertex
            uint nearGreen = Top(186, 140); // close to the green bottom-right vertex
            uint nearBlue = Top(128, 52);   // close to the blue top vertex
            Assert.IsTrue(Red(nearRed) > 150 && Green(nearRed) < 100, $"reddish, got {nearRed:X8}");
            Assert.IsTrue(Green(nearGreen) > 150 && Red(nearGreen) < 100, $"greenish, got {nearGreen:X8}");
            Assert.IsTrue(Blue(nearBlue) > 150 && Red(nearBlue) < 100, $"blueish, got {nearBlue:X8}");
        }
    }
}

namespace Px86.NintendoDS.TestProject.Video
{
    /// <summary>tools/testroms/src/tex3d9.c: repeated direct-colour texture plus an alpha-blended quad.</summary>
    [TestClass]
    public class TestTexture3DRom
    {
        private static uint[] _screens = Array.Empty<uint>();

        [ClassInitialize]
        public static void Run(TestContext _)
        {
            var nds = TestRomRunner.RunToCompletion("tex3d");
            nds.RunFrame();
            _screens = ScreenCapture.Compose(nds);
        }

        private static uint Top(int x, int y) => _screens[y * 256 + x];

        [TestMethod]
        public void DirectColourTextureRepeatsAsChecker()
        {
            // Texture coordinates span 64 texels over 256 pixels: texels are 4 pixels wide.
            uint a = Top(130, 50), b = Top(134, 50);
            Assert.IsTrue(a is 0xFFFF0000u or 0xFF0000FFu, $"pure red or blue texel, got {a:X8}");
            Assert.IsTrue(b is 0xFFFF0000u or 0xFF0000FFu, $"pure red or blue texel, got {b:X8}");
            Assert.AreNotEqual(a, b, "neighbouring texels alternate");
        }

        [TestMethod]
        public void TranslucentPolygonIsAlphaBlended()
        {
            uint pixel = Top(60, 50);
            int r = (int)(pixel >> 16) & 0xFF, g = (int)(pixel >> 8) & 0xFF, bl = (int)pixel & 0xFF;
            Assert.IsTrue(g > 80 && g < 200, $"half-white blended over red/blue, got {pixel:X8}");
            Assert.IsTrue(r > 80 && bl > 80, $"both channels lifted towards white, got {pixel:X8}");
        }
    }
}

namespace Px86.NintendoDS.TestProject.Video
{
    /// <summary>Display capture of the 3D layer into VRAM bank B (end of gfx3d9.c).</summary>
    [TestClass]
    public class TestDisplayCapture
    {
        [TestMethod]
        public void CaptureWrites3DLayerToVramAndClearsEnableBit()
        {
            var nds = TestRomRunner.RunToCompletion("gfx3d");
            Assert.AreEqual(0u, TestRomRunner.Result9(nds, 4) & 0x80000000u, "DISPCAPCNT bit 31 clears after the frame");

            byte[] bankB = nds.Vram.Bank(Px86.NintendoDS.EmulatorCore.Video.VramController.BANK_B);
            ushort Pixel(int x, int y) => BitConverter.ToUInt16(bankB, (y * 256 + x) * 2);

            Assert.AreEqual(0x8000, Pixel(5, 5), "opaque black rear plane");
            ushort centre = Pixel(128, 100);
            Assert.AreNotEqual(0, centre & 0x8000, "triangle pixel is opaque");
            Assert.AreNotEqual(0x8000, centre, "triangle pixel has colour");
        }
    }
}
