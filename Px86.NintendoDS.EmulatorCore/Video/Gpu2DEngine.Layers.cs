using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Video
{
    /// <summary>
    /// Scanline renderer for display mode 1 (BG + OBJ layers).
    ///
    /// Pipeline per line:
    ///   1. Window masks (WIN0, WIN1, OBJ window, outside) → per-pixel layer enables
    ///   2. Up to four BG layers: text, affine, extended affine (16-bit map,
    ///      256-colour bitmap, direct-colour bitmap), large bitmap (mode 6), or 3D (BG0)
    ///   3. OBJ layer: normal/affine sprites, 4/8bpp and bitmap OBJs, OBJ window
    ///   4. Priority sort per pixel (OBJ beats BG at equal priority, lower BG
    ///      number beats higher), then colour special effects (BLDCNT)
    ///
    /// Layer pixels are stored as ushort: RGB555 in bits 0–14 and bit 15 set
    /// for an opaque pixel (0 = transparent).
    ///
    /// Reference: GBATEK "LCD VRAM Character Data / BG Map", "LCD OBJ - OAM Attributes",
    /// "LCD I/O Window Feature", "LCD I/O Color Special Effects", "DS Video BG Modes / Control",
    /// "DS Video OBJs", "DS Video Extended Palettes".
    /// </summary>
    public sealed partial class Gpu2DEngine
    {
        private const ushort Opaque = 0x8000;

        private const int LayerBg0 = 0;
        private const int LayerObj = 4;
        private const int LayerBackdrop = 5;

        private readonly ushort[][] _bgLines = { new ushort[256], new ushort[256], new ushort[256], new ushort[256] };
        private readonly ushort[] _objLine = new ushort[256];
        private readonly byte[] _objPriority = new byte[256];
        private readonly byte[] _objFlags = new byte[256];   // bit 0 semi-transparent, bit 1 bitmap alpha present
        private readonly byte[] _objAlpha = new byte[256];   // bitmap OBJ alpha 0–15 (+1)
        private readonly bool[] _objWindow = new bool[256];
        private readonly byte[] _windowMask = new byte[256];
        private readonly byte[] _bg0Alpha3D = new byte[256]; // 3D pixel alpha 0–31

        private void RenderNormal(int line, Span<ushort> rgb)
        {
            uint dispcnt = DisplayControl;
            int bgMode = (int)(dispcnt & 7);

            for (int i = 0; i < 4; i++)
                Array.Clear(_bgLines[i]);

            for (int bg = 0; bg < 4; bg++)
            {
                if ((dispcnt & (0x100u << bg)) == 0)
                    continue;
                RenderBackground(bg, bgMode, line);
            }

            Array.Clear(_objLine);
            Array.Clear(_objWindow);
            if ((dispcnt & 0x1000) != 0)
                RenderObjects(line);

            BuildWindowMask(line, dispcnt);
            Compose(rgb, dispcnt);
        }

        // ── Backgrounds ───────────────────────────────────────────────────────

        private void RenderBackground(int bg, int bgMode, int line)
        {
            uint dispcnt = DisplayControl;
            var output = _bgLines[bg];

            if (bg == 0 && IsEngineA && ((dispcnt & 8) != 0 || bgMode == 6))
            {
                Render3DLayer(line, output);
                return;
            }

            switch (bg)
            {
                case 0:
                case 1:
                    if (bgMode == 6 && bg == 1) return;
                    RenderText(bg, line, output);
                    return;
                case 2:
                    switch (bgMode)
                    {
                        case 0: case 1: case 3: RenderText(bg, line, output); return;
                        case 2: case 4: RenderAffine(bg, output); return;
                        case 5: RenderExtended(bg, output); return;
                        case 6: if (IsEngineA) RenderLargeBitmap(output); return;
                        default: return;
                    }
                default:
                    switch (bgMode)
                    {
                        case 0: RenderText(bg, line, output); return;
                        case 1: case 2: RenderAffine(bg, output); return;
                        case 3: case 4: case 5: RenderExtended(bg, output); return;
                        default: return;
                    }
            }
        }

        private void Render3DLayer(int line, ushort[] output)
        {
            var pixels = ThreeDLineProvider?.Invoke(line);
            if (pixels == null)
                return;
            int scroll = (short)(Reg(REG_BG0HOFS) << 7) >> 7; // 9-bit signed
            for (int x = 0; x < 256; x++)
            {
                int sx = x + scroll;
                if (sx < 0 || sx > 255)
                {
                    output[x] = 0;
                    continue;
                }
                // The 3D line uses bit 15 = opaque; partial alpha comes from Alpha3DProvider.
                ushort p = pixels[sx];
                output[x] = p;
                _bg0Alpha3D[x] = (p & Opaque) != 0 ? (byte)(Alpha3DProvider?.Invoke(line, sx) ?? 31) : (byte)0;
            }
        }

        /// <summary>Per-pixel 3D alpha (0–31) for blending the 3D layer; null means fully opaque.</summary>
        public Func<int, int, int>? Alpha3DProvider { get; set; }

        private int CharBase(ushort bgcnt)
        {
            int value = ((bgcnt >> 2) & 0xF) * 0x4000;
            if (IsEngineA) value += (int)((DisplayControl >> 24) & 7) * 0x10000;
            return value;
        }

        private int ScreenBase(ushort bgcnt)
        {
            int value = ((bgcnt >> 8) & 0x1F) * 0x800;
            if (IsEngineA) value += (int)((DisplayControl >> 27) & 7) * 0x10000;
            return value;
        }

        private ushort BgControl(int bg) => Reg(REG_BG0CNT + (uint)bg * 2);

        private int MosaicH => (Reg(REG_MOSAIC) & 0xF) + 1;
        private int MosaicV => ((Reg(REG_MOSAIC) >> 4) & 0xF) + 1;

        private ushort ExtendedPaletteColor(int bg, int palette, int index)
        {
            int slot = bg;
            if (bg < 2 && (BgControl(bg) & 0x2000) != 0)
                slot += 2;
            return BgExtPalette.Read16((uint)(slot * 0x2000 + palette * 512 + index * 2));
        }

        private void RenderText(int bg, int line, ushort[] output)
        {
            ushort bgcnt = BgControl(bg);
            int hofs = Reg(REG_BG0HOFS + (uint)bg * 4) & 0x1FF;
            int vofs = Reg(REG_BG0HOFS + (uint)bg * 4 + 2) & 0x1FF;
            bool mosaic = (bgcnt & 0x40) != 0;
            int y = mosaic ? line - line % MosaicV : line;
            int size = bgcnt >> 14;
            int widthMask = (size & 1) != 0 ? 511 : 255;
            int heightMask = (size & 2) != 0 ? 511 : 255;
            bool color256 = (bgcnt & 0x80) != 0;
            bool extPalette = color256 && (DisplayControl & 0x40000000u) != 0;
            int charBase = CharBase(bgcnt);
            int screenBase = ScreenBase(bgcnt);

            int sy = (y + vofs) & heightMask;
            int mosaicH = mosaic ? MosaicH : 1;

            for (int x = 0; x < 256; x++)
            {
                int px = mosaic ? x - x % mosaicH : x;
                int sx = (px + hofs) & widthMask;

                // Screen blocks are 32×32 entries (2 KB); 512-wide maps put the right half next.
                int block = (sx >> 8) + ((sy >> 8) * ((size & 1) != 0 ? 2 : 1));
                int entryAddress = screenBase + block * 0x800 + ((sy & 0xFF) >> 3) * 64 + ((sx & 0xFF) >> 3) * 2;
                ushort entry = BgVram.Read16((uint)entryAddress);

                int tile = entry & 0x3FF;
                int tx = sx & 7, ty = sy & 7;
                if ((entry & 0x400) != 0) tx = 7 - tx;
                if ((entry & 0x800) != 0) ty = 7 - ty;
                int palette = entry >> 12;

                if (color256)
                {
                    int index = BgVram.Read8((uint)(charBase + tile * 64 + ty * 8 + tx));
                    if (index == 0) continue;
                    output[x] = (ushort)((extPalette ? ExtendedPaletteColor(bg, palette, index) : PaletteColor(index)) | Opaque);
                }
                else
                {
                    int b = BgVram.Read8((uint)(charBase + tile * 32 + ty * 4 + (tx >> 1)));
                    int index = (tx & 1) != 0 ? b >> 4 : b & 0xF;
                    if (index == 0) continue;
                    output[x] = (ushort)(PaletteColor(palette * 16 + index) | Opaque);
                }
            }
        }

        private (int pa, int pb, int pc, int pd) AffineParams(int bg)
        {
            uint baseReg = bg == 2 ? REG_BG2PA : REG_BG3PA;
            return ((short)Reg(baseReg), (short)Reg(baseReg + 2), (short)Reg(baseReg + 4), (short)Reg(baseReg + 6));
        }

        private void RenderAffine(int bg, ushort[] output)
        {
            ushort bgcnt = BgControl(bg);
            int size = 128 << (bgcnt >> 14);
            bool wrap = (bgcnt & 0x2000) != 0;
            int charBase = CharBase(bgcnt);
            int screenBase = ScreenBase(bgcnt);
            var (pa, _, pc, _) = AffineParams(bg);
            int refX = _affineX[bg - 2], refY = _affineY[bg - 2];
            int tilesPerRow = size >> 3;

            for (int x = 0; x < 256; x++)
            {
                int tx = (refX + pa * x) >> 8;
                int ty = (refY + pc * x) >> 8;
                if (!WrapOrClip(ref tx, ref ty, size, size, wrap))
                    continue;
                int tile = BgVram.Read8((uint)(screenBase + (ty >> 3) * tilesPerRow + (tx >> 3)));
                int index = BgVram.Read8((uint)(charBase + tile * 64 + (ty & 7) * 8 + (tx & 7)));
                if (index != 0)
                    output[x] = (ushort)(PaletteColor(index) | Opaque);
            }
        }

        private void RenderExtended(int bg, ushort[] output)
        {
            ushort bgcnt = BgControl(bg);
            var (pa, _, pc, _) = AffineParams(bg);
            int refX = _affineX[bg - 2], refY = _affineY[bg - 2];
            bool wrap = (bgcnt & 0x2000) != 0;
            int sizeCode = bgcnt >> 14;

            if ((bgcnt & 0x80) == 0)
            {
                // Affine with 16-bit text-style map entries and 256-colour tiles.
                int size = 128 << sizeCode;
                int charBase = CharBase(bgcnt);
                int screenBase = ScreenBase(bgcnt);
                bool extPalette = (DisplayControl & 0x40000000u) != 0;
                int tilesPerRow = size >> 3;
                for (int x = 0; x < 256; x++)
                {
                    int tx = (refX + pa * x) >> 8;
                    int ty = (refY + pc * x) >> 8;
                    if (!WrapOrClip(ref tx, ref ty, size, size, wrap))
                        continue;
                    ushort entry = BgVram.Read16((uint)(screenBase + ((ty >> 3) * tilesPerRow + (tx >> 3)) * 2));
                    int px = tx & 7, py = ty & 7;
                    if ((entry & 0x400) != 0) px = 7 - px;
                    if ((entry & 0x800) != 0) py = 7 - py;
                    int index = BgVram.Read8((uint)(charBase + (entry & 0x3FF) * 64 + py * 8 + px));
                    if (index == 0) continue;
                    ushort color = extPalette ? ExtendedPaletteColor(bg, entry >> 12, index) : PaletteColor(index);
                    output[x] = (ushort)(color | Opaque);
                }
                return;
            }

            // Bitmaps: base = BGxCNT screen base × 16 KB (no DISPCNT offset).
            int bitmapBase = ((bgcnt >> 8) & 0x1F) * 0x4000;
            (int width, int height) = sizeCode switch
            {
                0 => (128, 128),
                1 => (256, 256),
                2 => (512, 256),
                _ => (512, 512),
            };
            bool direct = (bgcnt & 0x04) != 0;

            for (int x = 0; x < 256; x++)
            {
                int tx = (refX + pa * x) >> 8;
                int ty = (refY + pc * x) >> 8;
                if (!WrapOrClip(ref tx, ref ty, width, height, wrap))
                    continue;
                if (direct)
                {
                    ushort color = BgVram.Read16((uint)(bitmapBase + (ty * width + tx) * 2));
                    if ((color & 0x8000) != 0)
                        output[x] = color;
                }
                else
                {
                    int index = BgVram.Read8((uint)(bitmapBase + ty * width + tx));
                    if (index != 0)
                        output[x] = (ushort)(PaletteColor(index) | Opaque);
                }
            }
        }

        private void RenderLargeBitmap(ushort[] output)
        {
            ushort bgcnt = BgControl(2);
            (int width, int height) = (bgcnt >> 14) == 1 ? (1024, 512) : (512, 1024);
            var (pa, _, pc, _) = AffineParams(2);
            int refX = _affineX[0], refY = _affineY[0];
            bool wrap = (bgcnt & 0x2000) != 0;
            for (int x = 0; x < 256; x++)
            {
                int tx = (refX + pa * x) >> 8;
                int ty = (refY + pc * x) >> 8;
                if (!WrapOrClip(ref tx, ref ty, width, height, wrap))
                    continue;
                int index = BgVram.Read8((uint)(ty * width + tx));
                if (index != 0)
                    output[x] = (ushort)(PaletteColor(index) | Opaque);
            }
        }

        private static bool WrapOrClip(ref int x, ref int y, int width, int height, bool wrap)
        {
            if (wrap)
            {
                x &= width - 1;
                y &= height - 1;
                return true;
            }
            return x >= 0 && x < width && y >= 0 && y < height;
        }

        // ── Objects ───────────────────────────────────────────────────────────

        private static readonly (int W, int H)[,] ObjSizes =
        {
            { (8, 8), (16, 16), (32, 32), (64, 64) },   // square
            { (16, 8), (32, 8), (32, 16), (64, 32) },   // horizontal
            { (8, 16), (8, 32), (16, 32), (32, 64) },   // vertical
            { (8, 8), (8, 8), (8, 8), (8, 8) },         // prohibited
        };

        private void RenderObjects(int line)
        {
            uint dispcnt = DisplayControl;
            bool oneDimensional = (dispcnt & 0x10) != 0;
            int tileBoundaryShift = oneDimensional ? 5 + (int)((dispcnt >> 20) & 3) : 5;
            bool extPalette = (dispcnt & 0x80000000u) != 0;
            bool bitmap1D = (dispcnt & 0x40) != 0;
            int bitmapBoundary = 128 << (IsEngineA ? (int)((dispcnt >> 22) & 1) : 0);
            bool bitmapWide2D = (dispcnt & 0x20) != 0;

            for (int i = 0; i < 128; i++)
            {
                int oamAddress = _oamBase + i * 8;
                ushort attr0 = MemoryAccess.Read16(_oam, oamAddress);
                ushort attr1 = MemoryAccess.Read16(_oam, oamAddress + 2);
                ushort attr2 = MemoryAccess.Read16(_oam, oamAddress + 4);

                bool affine = (attr0 & 0x100) != 0;
                if (!affine && (attr0 & 0x200) != 0)
                    continue; // disabled

                int mode = (attr0 >> 10) & 3;
                var (width, height) = ObjSizes[attr0 >> 14, attr1 >> 14];
                bool doubleSize = affine && (attr0 & 0x200) != 0;
                int boundsW = doubleSize ? width * 2 : width;
                int boundsH = doubleSize ? height * 2 : height;

                int y = attr0 & 0xFF;
                int row = (line - y) & 0xFF;
                if (row >= boundsH)
                    continue;

                int x0 = attr1 & 0x1FF;
                if (x0 >= 256) x0 -= 512;

                int pa = 0x100, pb = 0, pc = 0, pd = 0x100;
                if (affine)
                {
                    int group = (attr1 >> 9) & 0x1F;
                    int paramBase = _oamBase + group * 32;
                    pa = (short)MemoryAccess.Read16(_oam, paramBase + 6);
                    pb = (short)MemoryAccess.Read16(_oam, paramBase + 14);
                    pc = (short)MemoryAccess.Read16(_oam, paramBase + 22);
                    pd = (short)MemoryAccess.Read16(_oam, paramBase + 30);
                }

                bool hflip = !affine && (attr1 & 0x1000) != 0;
                bool vflip = !affine && (attr1 & 0x2000) != 0;
                int tile = attr2 & 0x3FF;
                int priority = (attr2 >> 10) & 3;
                int palette = attr2 >> 12;
                bool color256 = (attr0 & 0x2000) != 0;

                for (int bx = 0; bx < boundsW; bx++)
                {
                    int sx = x0 + bx;
                    if (sx < 0 || sx > 255)
                        continue;

                    int tx, ty;
                    if (affine)
                    {
                        int cx = bx - boundsW / 2, cy = row - boundsH / 2;
                        tx = ((pa * cx + pb * cy) >> 8) + width / 2;
                        ty = ((pc * cx + pd * cy) >> 8) + height / 2;
                        if (tx < 0 || tx >= width || ty < 0 || ty >= height)
                            continue;
                    }
                    else
                    {
                        tx = hflip ? width - 1 - bx : bx;
                        ty = vflip ? height - 1 - row : row;
                    }

                    ushort color;
                    int alpha = -1;

                    if (mode == 3)
                    {
                        int address;
                        if (bitmap1D)
                            address = tile * bitmapBoundary + (ty * width + tx) * 2;
                        else
                        {
                            int maskX = bitmapWide2D ? 0x1F : 0x0F;
                            int stride = bitmapWide2D ? 512 : 256;
                            address = (tile & maskX) * 0x10 + (tile & ~maskX) * 0x80 + ty * stride + tx * 2;
                        }
                        color = ObjVram.Read16((uint)address);
                        if ((color & 0x8000) == 0 || palette == 0)
                            continue;
                        alpha = palette;
                    }
                    else
                    {
                        int index;
                        int tileX = tx >> 3, tileY = ty >> 3;
                        int px = tx & 7, py = ty & 7;
                        if (color256)
                        {
                            int address = oneDimensional
                                ? (tile << tileBoundaryShift) + ((tileY * (width >> 3) + tileX) * 64)
                                : ((tile & ~1) + tileY * 32 + tileX * 2) * 32;
                            index = ObjVram.Read8((uint)(address + py * 8 + px));
                            if (index == 0) continue;
                            color = extPalette
                                ? ObjExtPalette.Read16((uint)(palette * 512 + index * 2))
                                : MemoryAccess.Read16(_palette, _paletteBase + 0x200 + index * 2);
                        }
                        else
                        {
                            int address = oneDimensional
                                ? (tile << tileBoundaryShift) + ((tileY * (width >> 3) + tileX) * 32)
                                : (tile + tileY * 32 + tileX) * 32;
                            int b = ObjVram.Read8((uint)(address + py * 4 + (px >> 1)));
                            index = (px & 1) != 0 ? b >> 4 : b & 0xF;
                            if (index == 0) continue;
                            color = MemoryAccess.Read16(_palette, _paletteBase + 0x200 + (palette * 16 + index) * 2);
                        }
                    }

                    if (mode == 2)
                    {
                        _objWindow[sx] = true;
                        continue;
                    }

                    if ((_objLine[sx] & Opaque) != 0 && _objPriority[sx] <= priority)
                        continue;

                    _objLine[sx] = (ushort)(color | Opaque);
                    _objPriority[sx] = (byte)priority;
                    _objFlags[sx] = (byte)((mode == 1 ? 1 : 0) | (alpha >= 0 ? 2 : 0));
                    _objAlpha[sx] = (byte)(alpha >= 0 ? alpha + 1 : 0);
                }
            }
        }

        // ── Windows ───────────────────────────────────────────────────────────

        private void BuildWindowMask(int line, uint dispcnt)
        {
            bool win0 = (dispcnt & 0x2000) != 0;
            bool win1 = (dispcnt & 0x4000) != 0;
            bool objWin = (dispcnt & 0x8000) != 0;

            if (!win0 && !win1 && !objWin)
            {
                Array.Fill(_windowMask, (byte)0x3F);
                return;
            }

            byte outside = (byte)(Reg(REG_WINOUT) & 0x3F);
            byte objInside = (byte)((Reg(REG_WINOUT) >> 8) & 0x3F);
            byte in0 = (byte)(Reg(REG_WININ) & 0x3F);
            byte in1 = (byte)((Reg(REG_WININ) >> 8) & 0x3F);

            bool v0 = win0 && InsideVertical(Reg(REG_WIN0V), line);
            bool v1 = win1 && InsideVertical(Reg(REG_WIN1V), line);
            ushort h0 = Reg(REG_WIN0H), h1 = Reg(REG_WIN1H);

            for (int x = 0; x < 256; x++)
            {
                if (v0 && InsideHorizontal(h0, x))
                    _windowMask[x] = in0;
                else if (v1 && InsideHorizontal(h1, x))
                    _windowMask[x] = in1;
                else if (objWin && _objWindow[x])
                    _windowMask[x] = objInside;
                else
                    _windowMask[x] = outside;
            }
        }

        private static bool InsideVertical(ushort reg, int line)
        {
            int y1 = reg >> 8, y2 = reg & 0xFF;
            int y = line & 0xFF;
            return y1 <= y2 ? y >= y1 && y < y2 : y >= y1 || y < y2;
        }

        private static bool InsideHorizontal(ushort reg, int x)
        {
            int x1 = reg >> 8, x2 = reg & 0xFF;
            if (x1 == 0 && x2 == 0) return false;
            if (x2 == 0) x2 = 256;
            return x1 <= x2 ? x >= x1 && x < x2 : x >= x1 || x < x2;
        }

        // ── Composition and colour effects ────────────────────────────────────

        private void Compose(Span<ushort> rgb, uint dispcnt)
        {
            ushort backdrop = PaletteColor(0);
            ushort bldcnt = Reg(REG_BLDCNT);
            int effect = (bldcnt >> 6) & 3;
            int eva = Math.Min(Reg(REG_BLDALPHA) & 0x1F, 16);
            int evb = Math.Min((Reg(REG_BLDALPHA) >> 8) & 0x1F, 16);
            int evy = Math.Min(Reg(REG_BLDY) & 0x1F, 16);

            Span<int> bgPriority = stackalloc int[4];
            for (int bg = 0; bg < 4; bg++)
                bgPriority[bg] = BgControl(bg) & 3;

            for (int x = 0; x < 256; x++)
            {
                byte enabled = _windowMask[x];

                // Top two layers: (colour, layer id).
                int topLayer = LayerBackdrop, secondLayer = LayerBackdrop;
                ushort topColor = backdrop, secondColor = backdrop;
                int topPriority = 4, secondPriority = 4;

                for (int priority = 0; priority < 4 && secondPriority == 4; priority++)
                {
                    if ((_objLine[x] & Opaque) != 0 && _objPriority[x] == priority && (enabled & 0x10) != 0)
                        Place(LayerObj, _objLine[x], priority, ref topLayer, ref topColor, ref topPriority,
                              ref secondLayer, ref secondColor, ref secondPriority);
                    for (int bg = 0; bg < 4; bg++)
                    {
                        if (bgPriority[bg] != priority || (enabled & (1 << bg)) == 0 || (_bgLines[bg][x] & Opaque) == 0)
                            continue;
                        Place(bg, _bgLines[bg][x], priority, ref topLayer, ref topColor, ref topPriority,
                              ref secondLayer, ref secondColor, ref secondPriority);
                    }
                }

                ushort result = topColor;
                bool effectsAllowed = (enabled & 0x20) != 0;
                bool secondIsTarget = (bldcnt & (0x100 << secondLayer)) != 0;

                if (topLayer == LayerObj && (_objFlags[x] & 3) != 0 && secondIsTarget)
                {
                    // Semi-transparent and bitmap OBJs always alpha-blend onto a 2nd target.
                    int a = (_objFlags[x] & 2) != 0 ? _objAlpha[x] : eva;
                    int b = (_objFlags[x] & 2) != 0 ? 16 - a : evb;
                    result = AlphaBlend(topColor, secondColor, a, b);
                }
                else if (topLayer == LayerBg0 && IsEngineA && (DisplayControl & 8) != 0 && secondIsTarget && _bg0Alpha3D[x] < 31)
                {
                    int a = _bg0Alpha3D[x] + 1;
                    result = AlphaBlend(topColor, secondColor, a / 2, 16 - a / 2);
                }
                else if (effectsAllowed && effect != 0 && (bldcnt & (1 << topLayer)) != 0)
                {
                    switch (effect)
                    {
                        case 1:
                            if (secondIsTarget)
                                result = AlphaBlend(topColor, secondColor, eva, evb);
                            break;
                        case 2:
                            result = Brighten(topColor, evy);
                            break;
                        case 3:
                            result = Darken(topColor, evy);
                            break;
                    }
                }

                rgb[x] = (ushort)(result & 0x7FFF);
            }
        }

        private static void Place(int layer, ushort color, int priority,
            ref int topLayer, ref ushort topColor, ref int topPriority,
            ref int secondLayer, ref ushort secondColor, ref int secondPriority)
        {
            if (topPriority == 4)
            {
                topLayer = layer;
                topColor = color;
                topPriority = priority;
            }
            else if (secondPriority == 4)
            {
                secondLayer = layer;
                secondColor = color;
                secondPriority = priority;
            }
        }

        private static ushort AlphaBlend(ushort a, ushort b, int eva, int evb)
        {
            int r = Math.Min(31, ((a & 0x1F) * eva + (b & 0x1F) * evb) >> 4);
            int g = Math.Min(31, (((a >> 5) & 0x1F) * eva + ((b >> 5) & 0x1F) * evb) >> 4);
            int bl = Math.Min(31, (((a >> 10) & 0x1F) * eva + ((b >> 10) & 0x1F) * evb) >> 4);
            return (ushort)(r | (g << 5) | (bl << 10));
        }

        private static ushort Brighten(ushort c, int evy)
        {
            int r = c & 0x1F, g = (c >> 5) & 0x1F, b = (c >> 10) & 0x1F;
            r += ((31 - r) * evy) >> 4;
            g += ((31 - g) * evy) >> 4;
            b += ((31 - b) * evy) >> 4;
            return (ushort)(r | (g << 5) | (b << 10));
        }

        private static ushort Darken(ushort c, int evy)
        {
            int r = c & 0x1F, g = (c >> 5) & 0x1F, b = (c >> 10) & 0x1F;
            r -= (r * evy) >> 4;
            g -= (g * evy) >> 4;
            b -= (b * evy) >> 4;
            return (ushort)(r | (g << 5) | (b << 10));
        }
    }
}
