using Px86.NintendoDS.FileFormats.Images;

namespace Px86.NintendoDS.EmulatorCore.Video
{
    /// <summary>Both screens stacked (top above bottom) as 256×384 host pixels.</summary>
    public static class ScreenCapture
    {
        public const int Width = 256;
        public const int Height = 384;

        /// <summary>Fill <paramref name="destination"/> (256×384, 0xAARRGGBB) with the current screens.</summary>
        public static void Compose(NdsEmulator nds, Span<uint> destination)
        {
            var top = nds.TopScreen;
            var bottom = nds.BottomScreen;
            for (int i = 0; i < 256 * 192; i++)
            {
                destination[i] = Gpu2DEngine.ToArgb(top[i]);
                destination[256 * 192 + i] = Gpu2DEngine.ToArgb(bottom[i]);
            }
        }

        public static uint[] Compose(NdsEmulator nds)
        {
            var pixels = new uint[Width * Height];
            Compose(nds, pixels);
            return pixels;
        }

        public static void SavePng(NdsEmulator nds, string path) =>
            PngWriter.Write(path, Width, Height, Compose(nds));
    }
}
