namespace Px86.NintendoDS.EmulatorCore.Video.Gpu3D
{
    /// <summary>
    /// Fetches texels for one polygon's TEXIMAGE_PARAM / PLTT_BASE.
    ///
    ///   TEXIMAGE_PARAM  0–15 VRAM offset / 8, 16/17 repeat S/T, 18/19 flip S/T,
    ///                   20–22 / 23–25 size 8 &lt;&lt; n, 26–28 format, 29 colour 0 transparent
    ///   Formats  1 A3I5   2 4-colour   3 16-colour   4 256-colour
    ///            5 4×4 compressed   6 A5I3   7 direct colour
    ///
    /// Output colours are 6-bit per channel, alpha 5-bit (0 = transparent).
    ///
    /// Reference: GBATEK "DS 3D Texture Attributes", "DS 3D Texture Formats".
    /// </summary>
    public sealed class TextureSampler
    {
        private readonly VramRegion _texture;
        private readonly VramRegion _palette;
        private readonly uint _address;
        private readonly int _width;
        private readonly int _height;
        private readonly int _format;
        private readonly bool _repeatS, _repeatT, _flipS, _flipT;
        private readonly bool _color0Transparent;
        private readonly uint _paletteAddress;

        private TextureSampler(VramController vram, uint parameters, uint paletteBase)
        {
            _texture = vram.Texture;
            _palette = vram.TexturePalette;
            _address = (parameters & 0xFFFF) * 8;
            _repeatS = (parameters & (1u << 16)) != 0;
            _repeatT = (parameters & (1u << 17)) != 0;
            _flipS = (parameters & (1u << 18)) != 0;
            _flipT = (parameters & (1u << 19)) != 0;
            _width = 8 << (int)((parameters >> 20) & 7);
            _height = 8 << (int)((parameters >> 23) & 7);
            _format = (int)(parameters >> 26) & 7;
            _color0Transparent = (parameters & (1u << 29)) != 0;
            _paletteAddress = _format == 2 ? paletteBase * 8 : paletteBase * 16;
        }

        /// <summary>A sampler, or null for "no texture" (format 0).</summary>
        public static TextureSampler? Create(VramController vram, uint parameters, uint paletteBase)
        {
            int format = (int)(parameters >> 26) & 7;
            return format == 0 ? null : new TextureSampler(vram, parameters, paletteBase);
        }

        private static int Expand(int five) => five == 0 ? 0 : five * 2 + 1;

        private static int Wrap(int coordinate, int size, bool repeat, bool flip)
        {
            if (!repeat)
                return Math.Clamp(coordinate, 0, size - 1);
            if (flip)
            {
                int period = coordinate & (size * 2 - 1);
                return period < size ? period : size * 2 - 1 - period;
            }
            return coordinate & (size - 1);
        }

        private void PaletteColor(uint index, out int r, out int g, out int b)
        {
            ushort color = _palette.Read16(_paletteAddress + index * 2);
            r = Expand(color & 0x1F);
            g = Expand((color >> 5) & 0x1F);
            b = Expand((color >> 10) & 0x1F);
        }

        public void Sample(int s, int t, out int r, out int g, out int b, out int a)
        {
            s = Wrap(s, _width, _repeatS, _flipS);
            t = Wrap(t, _height, _repeatT, _flipT);
            uint texel = (uint)(t * _width + s);
            r = g = b = 0;
            a = 31;

            switch (_format)
            {
                case 1: // A3I5
                {
                    byte value = _texture.Read8(_address + texel);
                    int alpha = value >> 5;
                    a = alpha * 4 + alpha / 2;
                    PaletteColor((uint)(value & 0x1F), out r, out g, out b);
                    return;
                }
                case 2: // 4 colours
                {
                    byte value = _texture.Read8(_address + texel / 4);
                    uint index = (uint)(value >> (int)((texel & 3) * 2)) & 3;
                    if (index == 0 && _color0Transparent) { a = 0; return; }
                    PaletteColor(index, out r, out g, out b);
                    return;
                }
                case 3: // 16 colours
                {
                    byte value = _texture.Read8(_address + texel / 2);
                    uint index = (texel & 1) != 0 ? (uint)(value >> 4) : (uint)(value & 0xF);
                    if (index == 0 && _color0Transparent) { a = 0; return; }
                    PaletteColor(index, out r, out g, out b);
                    return;
                }
                case 4: // 256 colours
                {
                    uint index = _texture.Read8(_address + texel);
                    if (index == 0 && _color0Transparent) { a = 0; return; }
                    PaletteColor(index, out r, out g, out b);
                    return;
                }
                case 5:
                    SampleCompressed(s, t, out r, out g, out b, out a);
                    return;
                case 6: // A5I3
                {
                    byte value = _texture.Read8(_address + texel);
                    a = value >> 3;
                    PaletteColor((uint)(value & 7), out r, out g, out b);
                    return;
                }
                default: // direct colour
                {
                    ushort color = _texture.Read16(_address + texel * 2);
                    r = Expand(color & 0x1F);
                    g = Expand((color >> 5) & 0x1F);
                    b = Expand((color >> 10) & 0x1F);
                    a = (color & 0x8000) != 0 ? 31 : 0;
                    return;
                }
            }
        }

        private void SampleCompressed(int s, int t, out int r, out int g, out int b, out int a)
        {
            uint block = _address + (uint)(((t >> 2) * (_width >> 2) + (s >> 2)) * 4);
            uint bits = _texture.Read32(block);
            int index = (int)(bits >> ((t & 3) * 8 + (s & 3) * 2)) & 3;

            // Palette index data in slot 1: slot 0 blocks map to its lower half, slot 2 to its upper half.
            uint infoAddress = block < 0x40000
                ? 0x20000 + (block & 0x1FFFF) / 2
                : 0x30000 + (block & 0x1FFFF) / 2;
            ushort info = _texture.Read16(infoAddress);
            uint palette = _paletteAddress + (uint)(info & 0x3FFF) * 4;
            int mode = info >> 14;
            a = 31;

            void Color(int i, out int cr, out int cg, out int cb)
            {
                ushort c = _palette.Read16(palette + (uint)i * 2);
                cr = Expand(c & 0x1F);
                cg = Expand((c >> 5) & 0x1F);
                cb = Expand((c >> 10) & 0x1F);
            }

            if (index < 2 || mode == 2 || (mode == 0 && index == 2))
            {
                Color(index, out r, out g, out b);
                return;
            }
            if (index == 3 && mode < 2)
            {
                r = g = b = 0;
                a = 0; // transparent
                return;
            }

            // Interpolated colours from palette entries 0 and 1 (modes 1 and 3).
            Color(0, out int r0, out int g0, out int b0);
            Color(1, out int r1, out int g1, out int b1);
            if (mode == 1)
            {
                r = (r0 + r1) / 2; g = (g0 + g1) / 2; b = (b0 + b1) / 2;
            }
            else if (index == 2)
            {
                r = (r0 * 5 + r1 * 3) / 8; g = (g0 * 5 + g1 * 3) / 8; b = (b0 * 5 + b1 * 3) / 8;
            }
            else
            {
                r = (r0 * 3 + r1 * 5) / 8; g = (g0 * 3 + g1 * 5) / 8; b = (b0 * 3 + b1 * 5) / 8;
            }
        }
    }
}
