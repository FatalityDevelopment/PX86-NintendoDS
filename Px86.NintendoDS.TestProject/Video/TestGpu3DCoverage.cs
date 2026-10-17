using Px86.NintendoDS.EmulatorCore;

namespace Px86.NintendoDS.TestProject.Video
{
    /// <summary>
    /// Adjacent polygons must cover the screen without cracks: a jittered
    /// triangle mesh drawn in white over a red rear plane must leave no red
    /// pixels where the mesh is. Cracks showed up in games as black specks.
    /// </summary>
    [TestClass]
    public class TestGpu3DCoverage
    {
        private const uint Gx = 0x04000000;

        private static NdsEmulator NewGpu()
        {
            var nds = new NdsEmulator();
            nds.Reset();
            nds.Arm9Bus.Write32(0x04000304, 0x820F);        // POWCNT1: 3D on
            nds.Arm9Bus.Write32(Gx + 0x350, 0x001F001F);    // CLEAR_COLOR red, alpha 31
            nds.Arm9Bus.Write32(Gx + 0x354, 0x7FFF);        // CLEAR_DEPTH
            nds.Arm9Bus.Write32(Gx + 0x580, 0xBFFF0000);    // VIEWPORT 0,0 - 255,191
            foreach (uint mode in new uint[] { 0, 2 })
            {
                nds.Arm9Bus.Write32(Gx + 0x440, mode);      // MTX_MODE
                nds.Arm9Bus.Write32(Gx + 0x454, 0);         // MTX_IDENTITY
            }
            nds.Arm9Bus.Write32(Gx + 0x4A4, 0x001F00C0);    // POLYGON_ATTR: alpha 31, both faces
            nds.Arm9Bus.Write32(Gx + 0x480, 0x7FFF);        // COLOR white
            return nds;
        }

        private static void Vertex(NdsEmulator nds, double x, double y)
        {
            uint fx = (ushort)(short)Math.Round(x * 4096), fy = (ushort)(short)Math.Round(y * 4096);
            nds.Arm9Bus.Write32(Gx + 0x48C, fx | (fy << 16)); // VTX_16
            nds.Arm9Bus.Write32(Gx + 0x48C, 0);
        }

        /// <summary>Draw a jittered grid of triangles spanning [-extent, extent] in clip space.</summary>
        private static void DrawMesh(NdsEmulator nds, double extent, int columns, int rows, int seed)
        {
            var random = new Random(seed);
            var px = new double[columns + 1, rows + 1];
            var py = new double[columns + 1, rows + 1];
            for (int i = 0; i <= columns; i++)
                for (int j = 0; j <= rows; j++)
                {
                    bool border = i == 0 || j == 0 || i == columns || j == rows;
                    double jitter = border ? 0 : 0.35;
                    px[i, j] = -extent + 2 * extent * (i + (random.NextDouble() - 0.5) * jitter) / columns;
                    py[i, j] = -extent + 2 * extent * (j + (random.NextDouble() - 0.5) * jitter) / rows;
                }

            nds.Arm9Bus.Write32(Gx + 0x500, 0); // BEGIN_VTXS: separate triangles
            for (int i = 0; i < columns; i++)
                for (int j = 0; j < rows; j++)
                {
                    // Alternate the diagonal so edges run in every direction.
                    if (((i + j) & 1) == 0)
                    {
                        Vertex(nds, px[i, j], py[i, j]); Vertex(nds, px[i + 1, j], py[i + 1, j]); Vertex(nds, px[i + 1, j + 1], py[i + 1, j + 1]);
                        Vertex(nds, px[i, j], py[i, j]); Vertex(nds, px[i + 1, j + 1], py[i + 1, j + 1]); Vertex(nds, px[i, j + 1], py[i, j + 1]);
                    }
                    else
                    {
                        Vertex(nds, px[i, j], py[i, j]); Vertex(nds, px[i + 1, j], py[i + 1, j]); Vertex(nds, px[i, j + 1], py[i, j + 1]);
                        Vertex(nds, px[i + 1, j], py[i + 1, j]); Vertex(nds, px[i + 1, j + 1], py[i + 1, j + 1]); Vertex(nds, px[i, j + 1], py[i, j + 1]);
                    }
                }
            nds.Arm9Bus.Write32(Gx + 0x540, 0); // SWAP_BUFFERS
            nds.Gpu3D.OnVBlank(renderEnabled: true);
        }

        private static List<(int X, int Y)> RedPixels(NdsEmulator nds, int x0, int y0, int x1, int y1)
        {
            var red = new List<(int, int)>();
            for (int y = y0; y < y1; y++)
            {
                var line = nds.Gpu3D.GetLine(y)!;
                for (int x = x0; x < x1; x++)
                    if ((line[x] & 0x7FFF) != 0x7FFF)
                        red.Add((x, y));
            }
            return red;
        }

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void MeshInsideTheScreenHasNoCracks(int seed)
        {
            var nds = NewGpu();
            DrawMesh(nds, 0.8, 24, 18, seed);
            Assert.IsTrue(nds.Gpu3D.HasOutput, "rendered");
            // The mesh covers x 25.6–230.4, y 19.2–172.8; check well inside its border.
            var red = RedPixels(nds, 28, 22, 228, 170);
            Assert.AreEqual(0, red.Count, $"cracks at {string.Join(" ", red.Take(10))}");
        }

        [DataTestMethod]
        [DataRow(4)]
        [DataRow(5)]
        public void MeshLargerThanTheScreenHasNoCracks(int seed)
        {
            var nds = NewGpu();
            DrawMesh(nds, 1.6, 20, 16, seed); // half the polygons get clipped by the screen edges
            var red = RedPixels(nds, 0, 0, 256, 192);
            Assert.AreEqual(0, red.Count, $"cracks at {string.Join(" ", red.Take(10))}");
        }

        [TestMethod]
        public void NopBytesInAPackedCommandAreSkipped()
        {
            var nds = NewGpu();
            // Packed word: NOP, BEGIN_VTXS, NOP, COLOR — both real commands must run.
            nds.Arm9Bus.Write32(Gx + 0x400, 0x20004000);
            nds.Arm9Bus.Write32(Gx + 0x400, 0);        // BEGIN_VTXS parameter: triangles
            nds.Arm9Bus.Write32(Gx + 0x400, 0x001F);   // COLOR parameter: red
            Vertex(nds, -0.5, -0.5); Vertex(nds, 0.5, -0.5); Vertex(nds, 0, 0.5);
            nds.Arm9Bus.Write32(Gx + 0x350, 0x7C00001F); // clear: blue would be 0x7C00; keep red but alpha 31 below
            nds.Arm9Bus.Write32(Gx + 0x350, 0x001F7C00); // CLEAR_COLOR blue, alpha 31
            nds.Arm9Bus.Write32(Gx + 0x540, 0);
            nds.Gpu3D.OnVBlank(renderEnabled: true);
            Assert.AreEqual(0x001F, nds.Gpu3D.GetLine(96)![128] & 0x7FFF, "the triangle was drawn, in the packed COLOR");
        }
    }
}
