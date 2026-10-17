using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.EmulatorCore.Video.Gpu3D;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Video
{
    [TestClass]
    public class TestGxCapture
    {
        [DataTestMethod]
        [DataRow("gfx3d")]
        [DataRow("tex3d")]
        public void ReplayRendersTheSameFrame(string rom)
        {
            // These ROMs draw one frame and swap once, so capture from boot.
            var nds = TestRomRunner.Boot(rom);

            byte[]? capture = null;
            var recorder = new GxCaptureRecorder(nds);
            recorder.Completed += bytes => capture = bytes;
            recorder.Start();
            for (int i = 0; i < 10 && capture == null; i++)
                nds.RunFrame();
            Assert.IsNotNull(capture, "capture finished within a few frames");
            Assert.IsFalse(recorder.IsBusy, "recorder idle again");

            var live = new uint[256 * 192];
            for (int y = 0; y < 192; y++)
            {
                var line = nds.Gpu3D.GetLine(y)!;
                for (int x = 0; x < 256; x++)
                {
                    ushort c = line[x];
                    live[y * 256 + x] = (c & 0x8000) == 0 ? 0xFF000000
                        : Gpu2DEngine.ToArgb((uint)((c & 0x1F) * 2 | ((c >> 5) & 0x1F) * 2 << 8 | ((c >> 10) & 0x1F) * 2 << 16));
                }
            }

            var result = GxCapture.Replay(capture);
            Assert.AreEqual("PXTS", result.GameCode, "game code stored");
            Assert.IsTrue(result.Polygons.Count > 0, "polygons rebuilt from the commands");
            Assert.IsTrue(result.Commands.Count > 0, "commands recorded");
            CollectionAssert.AreEqual(live, result.Pixels, "replay matches the live 3D output");
            StringAssert.Contains(result.Describe(), "BEGIN_VTXS", "description lists commands by name");
        }
    }
}
