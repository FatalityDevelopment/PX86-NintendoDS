using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Video
{
    /// <summary>
    /// One of the two 2D graphics engines (A at 04000000h, B at 04001000h).
    ///
    /// Register window (offsets from the engine base):
    ///   00h DISPCNT (32)   08h–0Eh BG0–3CNT   10h–1Eh BGxHOFS/VOFS (W)
    ///   20h–2Eh BG2 PA/PB/PC/PD, BG2X, BG2Y (W)   30h–3Eh BG3 affine (W)
    ///   40h–46h WIN0H/WIN1H/WIN0V/WIN1V (W)   48h WININ   4Ah WINOUT   4Ch MOSAIC (W)
    ///   50h BLDCNT   52h BLDALPHA   54h BLDY (W)   6Ch MASTER_BRIGHT
    ///   Engine A only: 60h DISP3DCNT (owned by the 3D engine), 64h DISPCAPCNT, 68h DISP_MMEM_FIFO
    ///
    /// DISPCNT display mode (bits 16–17): 0 = off (white), 1 = normal BG/OBJ,
    /// 2 = VRAM display (engine A: LCDC bank selected by bits 18–19),
    /// 3 = main-memory display FIFO (engine A).
    ///
    /// Output is one RGB555 + 3D-bit line per scanline into <see cref="Framebuffer"/>
    /// as 0x00BBGGRR with 6-bit channels after master brightness.
    ///
    /// Reference: GBATEK "DS Video BG Modes / Control", "GBA LCD Video Controller".
    /// </summary>
    public sealed partial class Gpu2DEngine
    {
        public const uint REG_DISPCNT       = 0x00;
        public const uint REG_BG0CNT        = 0x08;
        public const uint REG_BG0HOFS       = 0x10;
        public const uint REG_BG2PA         = 0x20;
        public const uint REG_BG2X          = 0x28;
        public const uint REG_BG2Y          = 0x2C;
        public const uint REG_BG3PA         = 0x30;
        public const uint REG_BG3X          = 0x38;
        public const uint REG_BG3Y          = 0x3C;
        public const uint REG_WIN0H         = 0x40;
        public const uint REG_WIN1H         = 0x42;
        public const uint REG_WIN0V         = 0x44;
        public const uint REG_WIN1V         = 0x46;
        public const uint REG_WININ         = 0x48;
        public const uint REG_WINOUT        = 0x4A;
        public const uint REG_MOSAIC        = 0x4C;
        public const uint REG_BLDCNT        = 0x50;
        public const uint REG_BLDALPHA      = 0x52;
        public const uint REG_BLDY          = 0x54;
        public const uint REG_DISPCAPCNT    = 0x64;
        public const uint REG_MASTER_BRIGHT = 0x6C;

        private readonly ushort[] _regs = new ushort[0x38];
        private readonly VramController _vram;
        private readonly byte[] _palette;
        private readonly byte[] _oam;
        private readonly int _paletteBase;
        private readonly int _oamBase;

        // Internal affine reference points (latched from BGxX/BGxY, advanced by PB/PD per line).
        private readonly int[] _affineX = new int[2];
        private readonly int[] _affineY = new int[2];

        public Gpu2DEngine(bool isEngineA, VramController vram, byte[] palette, byte[] oam)
        {
            IsEngineA = isEngineA;
            _vram = vram;
            _palette = palette;
            _oam = oam;
            _paletteBase = isEngineA ? 0 : 0x400;
            _oamBase = isEngineA ? 0 : 0x400;
            BgVram = isEngineA ? vram.BgA : vram.BgB;
            ObjVram = isEngineA ? vram.ObjA : vram.ObjB;
            BgExtPalette = isEngineA ? vram.BgExtPaletteA : vram.BgExtPaletteB;
            ObjExtPalette = isEngineA ? vram.ObjExtPaletteA : vram.ObjExtPaletteB;
        }

        public bool IsEngineA { get; }

        public VramRegion BgVram { get; }
        public VramRegion ObjVram { get; }
        public VramRegion BgExtPalette { get; }
        public VramRegion ObjExtPalette { get; }

        /// <summary>256×192 output, 0x00BBGGRR with 6-bit channels (after master brightness).</summary>
        public uint[] Framebuffer { get; } = new uint[256 * 192];

        /// <summary>Optional 3D layer (engine A BG0 when DISPCNT bit 3 is set): 256 pixels, RGB555 + alpha in bits 15+.</summary>
        public Func<int, ushort[]?>? ThreeDLineProvider { get; set; }

        /// <summary>Optional main-memory display FIFO source (engine A, display mode 3).</summary>
        public Func<ushort>? MainMemoryFifo { get; set; }

        public uint DisplayControl => (uint)(_regs[0] | (_regs[1] << 16));

        public void Reset()
        {
            Array.Clear(_regs);
            Array.Clear(_affineX);
            Array.Clear(_affineY);
            Array.Clear(Framebuffer);
        }

        // ── Register access ───────────────────────────────────────────────────

        private static bool IsReadable(uint offset) => offset switch
        {
            < 0x08 => true,                         // DISPCNT
            >= 0x08 and < 0x10 => true,             // BGxCNT
            0x48 or 0x4A => true,                   // WININ/WINOUT
            0x50 or 0x52 => true,                   // BLDCNT/BLDALPHA
            0x64 or 0x66 => true,                   // DISPCAPCNT
            0x6C => true,                           // MASTER_BRIGHT
            _ => false,
        };

        public ushort Read16(uint offset)
        {
            offset &= 0x7E;
            if (offset >= 0x70 || !IsReadable(offset))
                return 0;
            if (!IsEngineA && offset is 0x64 or 0x66)
                return 0;
            return _regs[offset >> 1];
        }

        public void Write16(uint offset, ushort value, ushort mask = 0xFFFF)
        {
            offset &= 0x7E;
            if (offset >= 0x70)
                return;
            int index = (int)(offset >> 1);
            _regs[index] = (ushort)((_regs[index] & ~mask) | (value & mask));

            // Writing BGxX/BGxY reloads the internal reference point immediately.
            switch (offset)
            {
                case 0x28: case 0x2A: _affineX[0] = SignExtend28(Reg32(0x28)); break;
                case 0x2C: case 0x2E: _affineY[0] = SignExtend28(Reg32(0x2C)); break;
                case 0x38: case 0x3A: _affineX[1] = SignExtend28(Reg32(0x38)); break;
                case 0x3C: case 0x3E: _affineY[1] = SignExtend28(Reg32(0x3C)); break;
            }
        }

        private uint Reg32(uint offset) => (uint)(_regs[offset >> 1] | (_regs[(offset >> 1) + 1] << 16));

        private ushort Reg(uint offset) => _regs[offset >> 1];

        private static int SignExtend28(uint value) => ((int)(value << 4)) >> 4;

        // ── Frame hooks ───────────────────────────────────────────────────────

        /// <summary>VBlank start: affine reference points reload from BGxX/BGxY.</summary>
        public void OnVBlank()
        {
            _affineX[0] = SignExtend28(Reg32(0x28));
            _affineY[0] = SignExtend28(Reg32(0x2C));
            _affineX[1] = SignExtend28(Reg32(0x38));
            _affineY[1] = SignExtend28(Reg32(0x3C));
        }

        /// <summary>Render scanline <paramref name="line"/> into <see cref="Framebuffer"/>.</summary>
        public void RenderScanline(int line, bool powered)
        {
            var output = Framebuffer.AsSpan(line * 256, 256);

            if (!powered)
            {
                output.Fill(0);
                return;
            }

            uint dispcnt = DisplayControl;
            int mode = (int)(dispcnt >> 16) & (IsEngineA ? 3 : 1);
            Span<ushort> rgb = stackalloc ushort[256];
            Span<ushort> graphics = stackalloc ushort[256];

            if (IsEngineA && line == 0)
                _capturing = (Reg32(REG_DISPCAPCNT) & 0x80000000u) != 0;

            // The layered BG/OBJ output is needed for display mode 1 and as capture source A.
            if (mode == 1 || _capturing)
                RenderNormal(line, graphics);

            switch (mode)
            {
                case 0:
                    rgb.Fill(0x7FFF);
                    break;
                case 1:
                    graphics.CopyTo(rgb);
                    break;
                case 2:
                {
                    int bank = (int)(dispcnt >> 18) & 3;
                    uint baseAddress = (uint)(bank * 0x20000 + line * 512);
                    for (int x = 0; x < 256; x++)
                        rgb[x] = _vram.Lcdc.Read16(baseAddress + (uint)(x * 2));
                    break;
                }
                default:
                    for (int x = 0; x < 256; x++)
                        rgb[x] = MainMemoryFifo?.Invoke() ?? 0;
                    break;
            }

            if (_capturing)
                Capture(line, graphics, mode);

            ApplyMasterBrightness(rgb, output);
            AdvanceAffine();
        }

        private void AdvanceAffine()
        {
            _affineX[0] += (short)Reg(0x22);
            _affineY[0] += (short)Reg(0x26);
            _affineX[1] += (short)Reg(0x32);
            _affineY[1] += (short)Reg(0x36);
        }

        private void ApplyMasterBrightness(ReadOnlySpan<ushort> rgb, Span<uint> output)
        {
            ushort bright = Reg(REG_MASTER_BRIGHT);
            int brightMode = bright >> 14;
            int factor = Math.Min(bright & 0x1F, 16);

            for (int x = 0; x < 256; x++)
            {
                ushort c = rgb[x];
                int r = (c & 0x1F) << 1, g = ((c >> 5) & 0x1F) << 1, b = ((c >> 10) & 0x1F) << 1;
                if (r != 0) r |= 1;
                if (g != 0) g |= 1;
                if (b != 0) b |= 1;

                if (brightMode == 1 && factor != 0)
                {
                    r += (63 - r) * factor / 16;
                    g += (63 - g) * factor / 16;
                    b += (63 - b) * factor / 16;
                }
                else if (brightMode == 2 && factor != 0)
                {
                    r -= r * factor / 16;
                    g -= g * factor / 16;
                    b -= b * factor / 16;
                }
                output[x] = (uint)(r | (g << 8) | (b << 16));
            }
        }

        /// <summary>Convert a framebuffer pixel (6-bit channels) to host 0xAARRGGBB.</summary>
        public static uint ToArgb(uint pixel)
        {
            uint r = pixel & 0x3F, g = (pixel >> 8) & 0x3F, b = (pixel >> 16) & 0x3F;
            r = (r << 2) | (r >> 4);
            g = (g << 2) | (g >> 4);
            b = (b << 2) | (b >> 4);
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        public void Serialize(StateSerializer s)
        {
            s.Section(IsEngineA ? "Engine A" : "Engine B");
            s.Sync(_regs);
            s.Sync(_affineX);
            s.Sync(_affineY);
            s.Sync(ref _capturing);
            s.Sync(Framebuffer);
        }

        // ── Display capture (engine A) ────────────────────────────────────────

        private bool _capturing;

        /// <summary>
        /// DISPCAPCNT (04000064h): capture source A (graphics or 3D), source B
        /// (VRAM or main-memory FIFO), or a blend of both, into an LCDC bank.
        /// Starts at line 0 when bit 31 is set; bit 31 clears at line 192.
        /// Reference: GBATEK "DS Video Capture and Main Memory Display Mode".
        /// </summary>
        private void Capture(int line, ReadOnlySpan<ushort> graphics, int displayMode)
        {
            uint control = Reg32(REG_DISPCAPCNT);
            (int width, int height) = ((control >> 20) & 3) switch
            {
                0 => (128, 128),
                1 => (256, 64),
                2 => (256, 128),
                _ => (256, 192),
            };

            if (line < height)
            {
                int eva = Math.Min((int)(control & 0x1F), 16);
                int evb = Math.Min((int)((control >> 8) & 0x1F), 16);
                int writeBank = (int)(control >> 16) & 3;
                uint writeOffset = ((control >> 18) & 3) * 0x8000;
                uint readOffset = displayMode == 2 ? 0 : ((control >> 26) & 3) * 0x8000;
                int readBank = (int)(DisplayControl >> 18) & 3;
                bool sourceA3D = (control & (1u << 24)) != 0;
                bool sourceBFifo = (control & (1u << 25)) != 0;
                int source = (int)(control >> 29) & 3;
                ushort[]? line3D = sourceA3D ? ThreeDLineProvider?.Invoke(line) : null;

                for (int x = 0; x < width; x++)
                {
                    ushort a;
                    if (sourceA3D)
                        a = line3D?[x] ?? 0;
                    else
                        a = (ushort)(graphics[x] | 0x8000);

                    ushort b = 0;
                    if (source != 0)
                    {
                        if (sourceBFifo)
                            b = (ushort)((MainMemoryFifo?.Invoke() ?? 0) | 0x8000);
                        else
                            b = _vram.Lcdc.Read16((uint)(readBank * 0x20000) + ((readOffset + (uint)((line * width + x) * 2)) & 0x1FFFF));
                    }

                    ushort result = source switch
                    {
                        0 => a,
                        1 => b,
                        _ => BlendCapture(a, b, eva, evb),
                    };
                    _vram.Lcdc.Write16((uint)(writeBank * 0x20000) + ((writeOffset + (uint)((line * width + x) * 2)) & 0x1FFFF), result);
                }
            }

            if (line == 191)
            {
                _capturing = false;
                _regs[(REG_DISPCAPCNT >> 1) + 1] &= 0x7FFF;
            }
        }

        private static ushort BlendCapture(ushort a, ushort b, int eva, int evb)
        {
            int alphaA = (a >> 15) & 1, alphaB = (b >> 15) & 1;
            int r = Math.Min(31, ((a & 0x1F) * alphaA * eva + (b & 0x1F) * alphaB * evb) >> 4);
            int g = Math.Min(31, (((a >> 5) & 0x1F) * alphaA * eva + ((b >> 5) & 0x1F) * alphaB * evb) >> 4);
            int bl = Math.Min(31, (((a >> 10) & 0x1F) * alphaA * eva + ((b >> 10) & 0x1F) * alphaB * evb) >> 4);
            bool alpha = (alphaA != 0 && eva > 0) || (alphaB != 0 && evb > 0);
            return (ushort)(r | (g << 5) | (bl << 10) | (alpha ? 0x8000 : 0));
        }

        private ushort PaletteColor(int index) => MemoryAccess.Read16(_palette, _paletteBase + index * 2);
    }
}
