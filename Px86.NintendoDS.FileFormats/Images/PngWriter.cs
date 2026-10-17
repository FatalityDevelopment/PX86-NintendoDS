using System.Buffers.Binary;
using System.IO.Compression;

namespace Px86.NintendoDS.FileFormats.Images
{
    /// <summary>
    /// Minimal PNG encoder (8-bit RGBA, no filtering) for screenshots and the
    /// headless test mode. Dependency-free on purpose.
    /// </summary>
    public static class PngWriter
    {
        /// <summary>Write <paramref name="pixels"/> (0xAARRGGBB, row-major) as a PNG.</summary>
        public static void Write(Stream output, int width, int height, ReadOnlySpan<uint> pixels)
        {
            output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // colour type: RGBA
            WriteChunk(output, "IHDR", ihdr);

            var raw = new byte[height * (width * 4 + 1)];
            int pos = 0;
            for (int y = 0; y < height; y++)
            {
                raw[pos++] = 0; // filter: none
                for (int x = 0; x < width; x++)
                {
                    uint p = pixels[y * width + x];
                    raw[pos++] = (byte)(p >> 16);
                    raw[pos++] = (byte)(p >> 8);
                    raw[pos++] = (byte)p;
                    raw[pos++] = (byte)(p >> 24);
                }
            }

            using (var compressed = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                    zlib.Write(raw);
                WriteChunk(output, "IDAT", compressed.ToArray());
            }

            WriteChunk(output, "IEND", Array.Empty<byte>());
        }

        public static void Write(string path, int width, int height, ReadOnlySpan<uint> pixels)
        {
            using var file = File.Create(path);
            Write(file, width, height, pixels);
        }

        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);

            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes);
            output.Write(data);

            uint crc = Crc32(typeBytes, data);
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            output.Write(crcBytes);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] type, byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
