namespace Px86.NintendoDS.EmulatorCore.Video.Gpu3D
{
    /// <summary>
    /// Renders one frame of 3D polygons into a 256×192 colour/depth/attribute
    /// buffer, then post-processes edge marking and fog.
    ///
    /// Order: rear plane → opaque polygons → translucent polygons (Y-sorted
    /// unless SWAP_BUFFERS requested manual sort) → edge marking → fog.
    ///
    /// Per pixel: depth test (less, or "equal" within ±200h), texture fetch
    /// (all 7 formats), blending by polygon mode (modulate, decal, toon /
    /// highlight, shadow), alpha test, alpha blending with the framebuffer for
    /// translucent pixels, and depth/ID/fog updates following POLYGON_ATTR.
    /// Colours are 6-bit per channel, alpha 5-bit, depth 24-bit.
    ///
    /// Attributes are interpolated perspective-correctly with 1/w; Z-buffer
    /// depth is interpolated linearly in screen space.
    ///
    /// Reference: GBATEK "DS 3D Texture Formats", "DS 3D Texture Blending",
    /// "DS 3D Toon, Edge, Fog, Alpha-Blending, Anti-Aliasing", "DS 3D Rear-Plane",
    /// "DS 3D Shadow Polygons".
    /// </summary>
    public sealed class SoftwareRasterizer
    {
        private const int Width = 256;
        private const int Height = 192;

        private readonly VramController _vram;

        // Frame buffers
        private readonly int[] _r = new int[Width * Height];
        private readonly int[] _g = new int[Width * Height];
        private readonly int[] _b = new int[Width * Height];
        private readonly int[] _a = new int[Width * Height];
        private readonly int[] _depth = new int[Width * Height];
        private readonly byte[] _opaqueId = new byte[Width * Height];
        private readonly byte[] _translucentId = new byte[Width * Height];
        private readonly bool[] _fog = new bool[Width * Height];
        private readonly bool[] _edge = new bool[Width * Height];
        private readonly bool[] _stencil = new bool[Width * Height];
        private readonly bool[] _translucentWritten = new bool[Width * Height];

        /// <summary>Output lines: RGB555, bit 15 = opaque (alpha &gt; 0).</summary>
        public ushort[][] OutputLines { get; } = CreateLines();

        /// <summary>Per-pixel 5-bit alpha of the output.</summary>
        public byte[] OutputAlpha { get; } = new byte[Width * Height];

        private static ushort[][] CreateLines()
        {
            var lines = new ushort[Height][];
            for (int i = 0; i < Height; i++) lines[i] = new ushort[Width];
            return lines;
        }

        public SoftwareRasterizer(VramController vram)
        {
            _vram = vram;
        }

        /// <summary>Render state taken from the 3D registers at render time.</summary>
        public sealed class RenderSettings
        {
            public ushort Disp3DControl;
            public uint ClearColor;
            public ushort ClearDepth;
            public ushort ClearImageOffset;
            public byte AlphaTestRef;
            public uint FogColor;
            public ushort FogOffset;
            public readonly byte[] FogTable = new byte[32];
            public readonly ushort[] ToonTable = new ushort[32];
            public readonly ushort[] EdgeColors = new ushort[8];

            public bool Texturing => (Disp3DControl & 0x0001) != 0;
            public bool Highlight => (Disp3DControl & 0x0002) != 0;
            public bool AlphaTest => (Disp3DControl & 0x0004) != 0;
            public bool AlphaBlend => (Disp3DControl & 0x0008) != 0;
            public bool EdgeMarking => (Disp3DControl & 0x0020) != 0;
            public bool FogAlphaOnly => (Disp3DControl & 0x0040) != 0;
            public bool FogEnabled => (Disp3DControl & 0x0080) != 0;
            public int FogShift => (Disp3DControl >> 8) & 0xF;
            public bool RearBitmap => (Disp3DControl & 0x4000) != 0;
        }

        // ── Frame ─────────────────────────────────────────────────────────────

        public void Render(IReadOnlyList<Polygon3D> polygons, bool manualSort, RenderSettings settings)
        {
            ClearRearPlane(settings);

            foreach (var polygon in polygons)
                if (!polygon.Translucent)
                    RenderPolygon(polygon, settings);

            IEnumerable<Polygon3D> translucent = polygons.Where(p => p.Translucent);
            if (!manualSort)
                translucent = translucent.OrderBy(p => p.MaxY).ThenBy(p => p.MinY);
            foreach (var polygon in translucent)
                RenderPolygon(polygon, settings);

            if (settings.EdgeMarking)
                ApplyEdgeMarking(settings);
            if (settings.FogEnabled)
                ApplyFog(settings);

            WriteOutput();
        }

        private static int Expand(int five) => five == 0 ? 0 : five * 2 + 1;

        private void ClearRearPlane(RenderSettings s)
        {
            uint clear = s.ClearColor;
            int polyId = (int)(clear >> 24) & 0x3F;
            Array.Fill(_opaqueId, (byte)polyId);
            Array.Fill(_translucentId, (byte)0xFF);
            Array.Clear(_edge);
            Array.Clear(_stencil);
            Array.Clear(_translucentWritten);

            if (s.RearBitmap)
            {
                int offsetX = s.ClearImageOffset & 0xFF;
                int offsetY = (s.ClearImageOffset >> 8) & 0xFF;
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int i = y * Width + x;
                        uint texel = (uint)(((y + offsetY) & 0xFF) * 512 + ((x + offsetX) & 0xFF) * 2);
                        ushort color = _vram.Texture.Read16(0x40000 + texel);
                        ushort depth = _vram.Texture.Read16(0x60000 + texel);
                        _r[i] = Expand(color & 0x1F);
                        _g[i] = Expand((color >> 5) & 0x1F);
                        _b[i] = Expand((color >> 10) & 0x1F);
                        _a[i] = (color & 0x8000) != 0 ? 31 : 0;
                        _depth[i] = ExpandDepth(depth & 0x7FFF);
                        _fog[i] = (depth & 0x8000) != 0;
                    }
                }
                return;
            }

            int r = Expand((int)(clear & 0x1F)), g = Expand((int)(clear >> 5) & 0x1F), b = Expand((int)(clear >> 10) & 0x1F);
            int a = (int)(clear >> 16) & 0x1F;
            bool fog = (clear & 0x8000) != 0;
            int clearDepth = ExpandDepth(s.ClearDepth & 0x7FFF);
            Array.Fill(_r, r);
            Array.Fill(_g, g);
            Array.Fill(_b, b);
            Array.Fill(_a, a);
            Array.Fill(_depth, clearDepth);
            Array.Fill(_fog, fog);
        }

        private static int ExpandDepth(int x) => x * 0x200 + ((x + 1) / 0x8000) * 0x1FF;

        // ── Polygon rasterization ─────────────────────────────────────────────

        private struct EdgePoint
        {
            public double X;
            public double InvW;
            public double RoW, GoW, BoW, SoW, ToW; // attribute / w
            public double Depth;
        }

        private void RenderPolygon(Polygon3D polygon, RenderSettings s)
        {
            var vertices = polygon.Vertices;
            int count = polygon.VertexCount;
            if (count < 3)
                return;

            int minY = polygon.MinY, maxY = polygon.MaxY;
            bool wireframe = polygon.Alpha == 0;
            int polyAlpha = wireframe ? 31 : polygon.Alpha;

            var texture = s.Texturing ? TextureSampler.Create(_vram, polygon.TextureParams, polygon.PaletteBase) : null;

            int lastLine = Math.Max(minY, maxY - 1);
            for (int y = minY; y <= lastLine && y < Height; y++)
            {
                if (y < 0) continue;
                double sampleY = maxY == minY ? minY : y + 0.5;

                bool found = false;
                EdgePoint left = default, right = default;
                for (int i = 0; i < count; i++)
                {
                    ref var a = ref vertices[i];
                    ref var b = ref vertices[(i + 1) % count];
                    int y0 = a.ScreenY, y1 = b.ScreenY;
                    if (y0 == y1)
                    {
                        if (maxY != minY) continue;
                        Consider(EdgeAt(a), ref left, ref right, ref found);
                        Consider(EdgeAt(b), ref left, ref right, ref found);
                        continue;
                    }
                    // Walk every edge from its upper vertex, so two polygons sharing an
                    // edge compute bit-identical X positions (no 1-pixel cracks between them).
                    bool down = y0 < y1;
                    ref var top = ref down ? ref a : ref b;
                    ref var bottom = ref down ? ref b : ref a;
                    if (sampleY < top.ScreenY || sampleY > bottom.ScreenY) continue;
                    double t = (sampleY - top.ScreenY) / (bottom.ScreenY - top.ScreenY);
                    Consider(Lerp(EdgeAt(top), EdgeAt(bottom), t), ref left, ref right, ref found);
                }
                if (!found)
                    continue;

                int xStart = (int)Math.Round(left.X);
                int xEnd = (int)Math.Round(right.X);
                if (xEnd <= xStart) xEnd = xStart + 1;
                xStart = Math.Max(xStart, 0);
                xEnd = Math.Min(xEnd, Width);

                bool topOrBottom = y == minY || y == lastLine;
                double spanWidth = Math.Max(right.X - left.X, 1e-9);

                for (int x = xStart; x < xEnd; x++)
                {
                    bool isEdge = topOrBottom || x == xStart || x == xEnd - 1;
                    if (wireframe && !isEdge)
                        continue;

                    double t = Math.Clamp((x + 0.5 - left.X) / spanWidth, 0, 1);
                    var p = Lerp(left, right, t);
                    double w = 1.0 / Math.Max(p.InvW, 1e-12);

                    int depth = polygon.WBuffer ? (int)Math.Clamp(w, 0, 0xFFFFFF) : (int)Math.Clamp(p.Depth, 0, 0xFFFFFF);
                    int vr = Math.Clamp((int)(p.RoW * w), 0, 63);
                    int vg = Math.Clamp((int)(p.GoW * w), 0, 63);
                    int vb = Math.Clamp((int)(p.BoW * w), 0, 63);

                    int r = vr, g = vg, b = vb, a = polyAlpha;
                    if (texture != null)
                    {
                        int ts = (int)Math.Floor(p.SoW * w) >> 4;
                        int tt = (int)Math.Floor(p.ToW * w) >> 4;
                        texture.Sample(ts, tt, out int tr, out int tg, out int tb, out int ta);
                        Blend(polygon.Mode, s, vr, vg, vb, polyAlpha, tr, tg, tb, ta, out r, out g, out b, out a);
                    }
                    else if (polygon.Mode == 2)
                    {
                        Blend(2, s, vr, vg, vb, polyAlpha, 63, 63, 63, 31, out r, out g, out b, out a);
                    }

                    if (a == 0)
                        continue;
                    if (s.AlphaTest ? a <= s.AlphaTestRef : a == 0)
                        continue;

                    WritePixel(polygon, s, y * Width + x, depth, r, g, b, a, isEdge);
                }
            }
        }

        private static EdgePoint EdgeAt(in Vertex3D v)
        {
            double invW = 1.0 / Math.Max(v.W, 1);
            return new EdgePoint
            {
                X = v.ScreenX,
                InvW = invW,
                RoW = v.R * invW, GoW = v.G * invW, BoW = v.B * invW,
                SoW = v.S * invW, ToW = v.T * invW,
                Depth = v.Depth,
            };
        }

        private static EdgePoint Lerp(in EdgePoint a, in EdgePoint b, double t) => new()
        {
            X = a.X + (b.X - a.X) * t,
            InvW = a.InvW + (b.InvW - a.InvW) * t,
            RoW = a.RoW + (b.RoW - a.RoW) * t,
            GoW = a.GoW + (b.GoW - a.GoW) * t,
            BoW = a.BoW + (b.BoW - a.BoW) * t,
            SoW = a.SoW + (b.SoW - a.SoW) * t,
            ToW = a.ToW + (b.ToW - a.ToW) * t,
            Depth = a.Depth + (b.Depth - a.Depth) * t,
        };

        private static void Consider(in EdgePoint p, ref EdgePoint left, ref EdgePoint right, ref bool found)
        {
            if (!found)
            {
                left = right = p;
                found = true;
                return;
            }
            if (p.X < left.X) left = p;
            if (p.X > right.X) right = p;
        }

        private static void Blend(int mode, RenderSettings s, int vr, int vg, int vb, int va,
                                  int tr, int tg, int tb, int ta,
                                  out int r, out int g, out int b, out int a)
        {
            switch (mode)
            {
                case 1: // decal
                    if (ta == 0) { r = vr; g = vg; b = vb; }
                    else if (ta == 31) { r = tr; g = tg; b = tb; }
                    else
                    {
                        r = (tr * ta + vr * (31 - ta)) >> 5;
                        g = (tg * ta + vg * (31 - ta)) >> 5;
                        b = (tb * ta + vb * (31 - ta)) >> 5;
                    }
                    a = va;
                    return;

                case 2: // toon / highlight: vertex red selects a toon colour
                {
                    ushort toon = s.ToonTable[vr >> 1];
                    int sr = Expand(toon & 0x1F), sg = Expand((toon >> 5) & 0x1F), sb = Expand((toon >> 10) & 0x1F);
                    r = ((tr + 1) * (sr + 1) - 1) >> 6;
                    g = ((tg + 1) * (sg + 1) - 1) >> 6;
                    b = ((tb + 1) * (sb + 1) - 1) >> 6;
                    if (s.Highlight)
                    {
                        r = Math.Min(63, r + sr);
                        g = Math.Min(63, g + sg);
                        b = Math.Min(63, b + sb);
                    }
                    a = ((ta + 1) * (va + 1) - 1) >> 5;
                    return;
                }

                default: // modulation (and shadow)
                    r = ((tr + 1) * (vr + 1) - 1) >> 6;
                    g = ((tg + 1) * (vg + 1) - 1) >> 6;
                    b = ((tb + 1) * (vb + 1) - 1) >> 6;
                    a = ((ta + 1) * (va + 1) - 1) >> 5;
                    return;
            }
        }

        private bool DepthTest(Polygon3D polygon, int index, int depth)
        {
            int old = _depth[index];
            if ((polygon.Attributes & 0x4000) != 0)
                return Math.Abs(depth - old) <= 0x200;
            return depth < old;
        }

        private void WritePixel(Polygon3D polygon, RenderSettings s, int index, int depth, int r, int g, int b, int a, bool isEdge)
        {
            uint attr = polygon.Attributes;
            int id = polygon.PolygonId;

            if (polygon.Mode == 3)
            {
                if (id == 0)
                {
                    // Shadow mask: mark pixels where the volume's back face is hidden by scene geometry.
                    if (!DepthTest(polygon, index, depth))
                        _stencil[index] = true;
                    return;
                }
                if (!_stencil[index])
                    return;
                _stencil[index] = false;
                if (_opaqueId[index] == id)
                    return;
            }

            if (!DepthTest(polygon, index, depth))
                return;

            bool translucent = a < 31;
            if (!translucent)
            {
                _r[index] = r; _g[index] = g; _b[index] = b; _a[index] = 31;
                _depth[index] = depth;
                _opaqueId[index] = (byte)id;
                _fog[index] = (attr & 0x8000) != 0;
                _edge[index] = isEdge;
                return;
            }

            // Translucent: one blend per polygon ID per pixel.
            if (_translucentWritten[index] && _translucentId[index] == id)
                return;

            if (s.AlphaBlend && _a[index] > 0)
            {
                _r[index] = (r * (a + 1) + _r[index] * (31 - a)) >> 5;
                _g[index] = (g * (a + 1) + _g[index] * (31 - a)) >> 5;
                _b[index] = (b * (a + 1) + _b[index] * (31 - a)) >> 5;
                _a[index] = Math.Max(a, _a[index]);
            }
            else
            {
                _r[index] = r; _g[index] = g; _b[index] = b; _a[index] = a;
            }

            if ((attr & 0x800) != 0)
                _depth[index] = depth;
            _translucentId[index] = (byte)id;
            _translucentWritten[index] = true;
            _fog[index] &= (attr & 0x8000) != 0;
        }

        // ── Post-processing ───────────────────────────────────────────────────

        private void ApplyEdgeMarking(RenderSettings s)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    if (!_edge[i] || _a[i] == 0)
                        continue;
                    int id = _opaqueId[i];
                    int depth = _depth[i];
                    bool mark =
                        (x > 0 && _opaqueId[i - 1] != id && depth < _depth[i - 1]) ||
                        (x < Width - 1 && _opaqueId[i + 1] != id && depth < _depth[i + 1]) ||
                        (y > 0 && _opaqueId[i - Width] != id && depth < _depth[i - Width]) ||
                        (y < Height - 1 && _opaqueId[i + Width] != id && depth < _depth[i + Width]);
                    if (!mark)
                        continue;
                    ushort color = s.EdgeColors[id >> 3];
                    _r[i] = Expand(color & 0x1F);
                    _g[i] = Expand((color >> 5) & 0x1F);
                    _b[i] = Expand((color >> 10) & 0x1F);
                }
            }
        }

        private void ApplyFog(RenderSettings s)
        {
            int fogR = Expand((int)(s.FogColor & 0x1F));
            int fogG = Expand((int)(s.FogColor >> 5) & 0x1F);
            int fogB = Expand((int)(s.FogColor >> 10) & 0x1F);
            int fogA = (int)(s.FogColor >> 16) & 0x1F;
            int step = s.FogShift >= 11 ? 0 : 0x400 >> s.FogShift;

            for (int i = 0; i < _r.Length; i++)
            {
                if (!_fog[i])
                    continue;

                // Depth in the fog registers' 15-bit units.
                int depth = _depth[i] >> 9;
                int density = FogDensity(depth, s.FogOffset, step, s.FogTable);
                if (density >= 127) density = 128;

                if (!s.FogAlphaOnly)
                {
                    _r[i] = (fogR * density + _r[i] * (128 - density)) >> 7;
                    _g[i] = (fogG * density + _g[i] * (128 - density)) >> 7;
                    _b[i] = (fogB * density + _b[i] * (128 - density)) >> 7;
                }
                _a[i] = (fogA * density + _a[i] * (128 - density)) >> 7;
            }
        }

        private static int FogDensity(int depth, int offset, int step, byte[] table)
        {
            if (step == 0)
                return depth <= offset ? table[0] & 0x7F : table[31] & 0x7F;
            int position = depth - offset - step; // boundary[0] = offset + step
            if (position < 0)
                return table[0] & 0x7F;
            int index = position / step;
            if (index >= 31)
                return table[31] & 0x7F;
            int fraction = position % step;
            int d0 = table[index] & 0x7F, d1 = table[index + 1] & 0x7F;
            return d0 + (d1 - d0) * fraction / step;
        }

        private void WriteOutput()
        {
            for (int y = 0; y < Height; y++)
            {
                var line = OutputLines[y];
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    int a = _a[i];
                    OutputAlpha[i] = (byte)a;
                    line[x] = a == 0 ? (ushort)0 : (ushort)((_r[i] >> 1) | ((_g[i] >> 1) << 5) | ((_b[i] >> 1) << 10) | 0x8000);
                }
            }
        }
    }
}
