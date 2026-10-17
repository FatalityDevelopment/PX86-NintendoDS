namespace Px86.NintendoDS.FileFormats.Images
{
    /// <summary>
    /// Minimal baseline JPEG encoder (JFIF, 8-bit YCbCr, 4:4:4, standard
    /// Huffman tables) for the GS frame stream. Dependency-free on purpose.
    /// 4:4:4 keeps the colours of small pixel-art sprites from bleeding.
    ///
    /// Quantisation and Huffman tables are the example tables of ITU-T T.81
    /// Annex K, scaled for quality the same way as libjpeg.
    /// Reference: ITU-T T.81 (JPEG baseline sequential DCT).
    /// </summary>
    public sealed class JpegWriter
    {
        private static readonly byte[] ZigZag =
        {
             0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        };

        private static readonly byte[] LumaQuant =
        {
            16, 11, 10, 16,  24,  40,  51,  61,
            12, 12, 14, 19,  26,  58,  60,  55,
            14, 13, 16, 24,  40,  57,  69,  56,
            14, 17, 22, 29,  51,  87,  80,  62,
            18, 22, 37, 56,  68, 109, 103,  77,
            24, 35, 55, 64,  81, 104, 113,  92,
            49, 64, 78, 87, 103, 121, 120, 101,
            72, 92, 95, 98, 112, 100, 103,  99,
        };

        private static readonly byte[] ChromaQuant =
        {
            17, 18, 24, 47, 99, 99, 99, 99,
            18, 21, 26, 66, 99, 99, 99, 99,
            24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
        };

        private static readonly byte[] DcLumaBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] DcChromaBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        private static readonly byte[] DcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        private static readonly byte[] AcLumaBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D };
        private static readonly byte[] AcLumaValues =
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        };

        private static readonly byte[] AcChromaBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
        private static readonly byte[] AcChromaValues =
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
            0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
            0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
            0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
            0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        };

        private static readonly HuffmanTable DcLuma = new(DcLumaBits, DcValues);
        private static readonly HuffmanTable DcChroma = new(DcChromaBits, DcValues);
        private static readonly HuffmanTable AcLuma = new(AcLumaBits, AcLumaValues);
        private static readonly HuffmanTable AcChroma = new(AcChromaBits, AcChromaValues);

        /// <summary>DCT basis: Cos[u * 8 + x] = C(u)/2 · cos((2x+1)uπ/16).</summary>
        private static readonly float[] Cos = BuildCosTable();

        private readonly byte[] _lumaQ = new byte[64];
        private readonly byte[] _chromaQ = new byte[64];
        private readonly float[] _lumaDivisor = new float[64];
        private readonly float[] _chromaDivisor = new float[64];

        /// <param name="quality">1 (smallest) – 100 (best), like libjpeg.</param>
        public JpegWriter(int quality = 85)
        {
            Quality = Math.Clamp(quality, 1, 100);
            int scale = Quality < 50 ? 5000 / Quality : 200 - Quality * 2;
            for (int i = 0; i < 64; i++)
            {
                _lumaQ[i] = (byte)Math.Clamp((LumaQuant[i] * scale + 50) / 100, 1, 255);
                _chromaQ[i] = (byte)Math.Clamp((ChromaQuant[i] * scale + 50) / 100, 1, 255);
                _lumaDivisor[i] = 1f / _lumaQ[i];
                _chromaDivisor[i] = 1f / _chromaQ[i];
            }
        }

        public int Quality { get; }

        /// <summary>Encode <paramref name="pixels"/> (0xAARRGGBB, row-major; alpha ignored).</summary>
        public byte[] Encode(int width, int height, ReadOnlySpan<uint> pixels)
        {
            if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (pixels.Length < width * height)
                throw new ArgumentException("Not enough pixels.", nameof(pixels));

            var output = new MemoryStream(width * height / 2);
            WriteHeaders(output, width, height);

            var bits = new BitWriter(output);
            Span<float> y = stackalloc float[64];
            Span<float> cb = stackalloc float[64];
            Span<float> cr = stackalloc float[64];
            Span<int> coefficients = stackalloc int[64];
            int dcY = 0, dcCb = 0, dcCr = 0;

            for (int by = 0; by < height; by += 8)
            {
                for (int bx = 0; bx < width; bx += 8)
                {
                    for (int row = 0; row < 8; row++)
                    {
                        int sy = Math.Min(by + row, height - 1);
                        for (int col = 0; col < 8; col++)
                        {
                            int sx = Math.Min(bx + col, width - 1);
                            uint p = pixels[sy * width + sx];
                            float r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                            int i = row * 8 + col;
                            y[i] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                            cb[i] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                            cr[i] = 0.5f * r - 0.418688f * g - 0.081312f * b;
                        }
                    }

                    Transform(y, _lumaDivisor, coefficients);
                    dcY = EncodeBlock(bits, coefficients, dcY, DcLuma, AcLuma);
                    Transform(cb, _chromaDivisor, coefficients);
                    dcCb = EncodeBlock(bits, coefficients, dcCb, DcChroma, AcChroma);
                    Transform(cr, _chromaDivisor, coefficients);
                    dcCr = EncodeBlock(bits, coefficients, dcCr, DcChroma, AcChroma);
                }
            }

            bits.Flush();
            output.WriteByte(0xFF);
            output.WriteByte(0xD9); // EOI
            return output.ToArray();
        }

        // ── Headers ───────────────────────────────────────────────────────────

        private void WriteHeaders(Stream o, int width, int height)
        {
            o.Write(new byte[] { 0xFF, 0xD8 }); // SOI
            o.Write(new byte[] { 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });

            // DQT: both tables, 8-bit, zig-zag order.
            o.Write(new byte[] { 0xFF, 0xDB, 0x00, 0x84 });
            o.WriteByte(0x00);
            for (int k = 0; k < 64; k++) o.WriteByte(_lumaQ[ZigZag[k]]);
            o.WriteByte(0x01);
            for (int k = 0; k < 64; k++) o.WriteByte(_chromaQ[ZigZag[k]]);

            // SOF0: 3 components, no subsampling.
            o.Write(new byte[]
            {
                0xFF, 0xC0, 0x00, 0x11, 0x08,
                (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
                0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            });

            WriteHuffman(o, 0x00, DcLumaBits, DcValues);
            WriteHuffman(o, 0x10, AcLumaBits, AcLumaValues);
            WriteHuffman(o, 0x01, DcChromaBits, DcValues);
            WriteHuffman(o, 0x11, AcChromaBits, AcChromaValues);

            // SOS
            o.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00 });
        }

        private static void WriteHuffman(Stream o, byte classAndId, byte[] bits, byte[] values)
        {
            int length = 2 + 1 + 16 + values.Length;
            o.WriteByte(0xFF);
            o.WriteByte(0xC4);
            o.WriteByte((byte)(length >> 8));
            o.WriteByte((byte)length);
            o.WriteByte(classAndId);
            o.Write(bits);
            o.Write(values);
        }

        // ── Block coding ──────────────────────────────────────────────────────

        private static float[] BuildCosTable()
        {
            var table = new float[64];
            for (int u = 0; u < 8; u++)
            {
                double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                for (int x = 0; x < 8; x++)
                    table[u * 8 + x] = (float)(cu / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
            }
            return table;
        }

        /// <summary>2-D forward DCT (separable), then quantise into <paramref name="output"/> (natural order).</summary>
        private static void Transform(Span<float> block, float[] divisor, Span<int> output)
        {
            Span<float> temp = stackalloc float[64];
            // Rows: temp[y][u] = Σx Cos[u][x] · block[y][x]
            for (int y = 0; y < 8; y++)
            {
                for (int u = 0; u < 8; u++)
                {
                    float sum = 0;
                    for (int x = 0; x < 8; x++)
                        sum += Cos[u * 8 + x] * block[y * 8 + x];
                    temp[y * 8 + u] = sum;
                }
            }
            // Columns: F[v][u] = Σy Cos[v][y] · temp[y][u]
            for (int u = 0; u < 8; u++)
            {
                for (int v = 0; v < 8; v++)
                {
                    float sum = 0;
                    for (int y = 0; y < 8; y++)
                        sum += Cos[v * 8 + y] * temp[y * 8 + u];
                    int i = v * 8 + u;
                    output[i] = (int)MathF.Round(sum * divisor[i]);
                }
            }
        }

        private static int EncodeBlock(BitWriter bits, Span<int> coefficients, int previousDc, HuffmanTable dc, HuffmanTable ac)
        {
            int dcValue = coefficients[0];
            int diff = dcValue - previousDc;
            int size = BitLength(diff);
            dc.Write(bits, size);
            if (size > 0)
                bits.Write(Magnitude(diff, size), size);

            int run = 0;
            for (int k = 1; k < 64; k++)
            {
                int c = coefficients[ZigZag[k]];
                if (c == 0)
                {
                    run++;
                    continue;
                }
                while (run > 15)
                {
                    ac.Write(bits, 0xF0); // ZRL
                    run -= 16;
                }
                size = BitLength(c);
                ac.Write(bits, (run << 4) | size);
                bits.Write(Magnitude(c, size), size);
                run = 0;
            }
            if (run > 0)
                ac.Write(bits, 0x00); // EOB
            return dcValue;
        }

        private static int BitLength(int value)
        {
            uint v = (uint)Math.Abs(value);
            return v == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(v);
        }

        /// <summary>Value bits: positive as-is, negative as one's complement of |value| (T.81 F.1.2.1).</summary>
        private static int Magnitude(int value, int size) => value >= 0 ? value : value + (1 << size) - 1;

        private sealed class HuffmanTable
        {
            private readonly ushort[] _codes = new ushort[256];
            private readonly byte[] _sizes = new byte[256];

            public HuffmanTable(byte[] bits, byte[] values)
            {
                int code = 0, k = 0;
                for (int length = 1; length <= 16; length++)
                {
                    for (int i = 0; i < bits[length - 1]; i++)
                    {
                        _codes[values[k]] = (ushort)code;
                        _sizes[values[k]] = (byte)length;
                        k++;
                        code++;
                    }
                    code <<= 1;
                }
            }

            public void Write(BitWriter bits, int symbol) => bits.Write(_codes[symbol], _sizes[symbol]);
        }

        private sealed class BitWriter
        {
            private readonly Stream _output;
            private uint _buffer;
            private int _count;

            public BitWriter(Stream output) => _output = output;

            public void Write(int value, int size)
            {
                for (int i = size - 1; i >= 0; i--)
                {
                    _buffer = (_buffer << 1) | (uint)((value >> i) & 1);
                    if (++_count == 8)
                        Emit();
                }
            }

            private void Emit()
            {
                byte b = (byte)_buffer;
                _output.WriteByte(b);
                if (b == 0xFF)
                    _output.WriteByte(0x00); // byte stuffing
                _buffer = 0;
                _count = 0;
            }

            /// <summary>Pad the last byte with 1 bits.</summary>
            public void Flush()
            {
                while (_count != 0)
                    Write(1, 1);
            }
        }
    }
}
