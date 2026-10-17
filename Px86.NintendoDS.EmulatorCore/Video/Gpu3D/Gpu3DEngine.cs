using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.EmulatorCore.Hardware;

namespace Px86.NintendoDS.EmulatorCore.Video.Gpu3D
{
    /// <summary>
    /// The 3D engine's register window: ARM9 04000060h (DISP3DCNT) and
    /// 04000320h–040006A3h. Owns the <see cref="GeometryEngine"/> and the
    /// <see cref="SoftwareRasterizer"/>.
    ///
    ///   04000060h DISP3DCNT     04000320h RDLINES_COUNT   04000330h EDGE_COLOR ×8
    ///   04000340h ALPHA_TEST_REF  04000350h CLEAR_COLOR  04000354h CLEAR_DEPTH
    ///   04000356h CLRIMAGE_OFFSET 04000358h FOG_COLOR    0400035Ch FOG_OFFSET
    ///   04000360h FOG_TABLE ×32   04000380h TOON_TABLE ×32
    ///   04000400h GXFIFO   04000440h–040005FFh command ports
    ///   04000600h GXSTAT   04000604h RAM_COUNT   04000610h DISP_1DOT_DEPTH
    ///   04000620h POS_RESULT   04000630h VEC_RESULT
    ///   04000640h CLIPMTX_RESULT   04000680h VECMTX_RESULT
    ///
    /// The frame is rendered at VBlank, right after a pending swap, and shown
    /// as engine A's BG0 during the following frame.
    ///
    /// Reference: GBATEK "DS 3D I/O Map", "DS 3D Display Control", "DS 3D Status".
    /// </summary>
    public sealed class Gpu3DEngine
    {
        public const uint REG_DISP3DCNT = 0x060;
        public const uint REG_GXFIFO    = 0x400;
        public const uint REG_GXSTAT    = 0x600;

        private readonly InterruptController _irq;
        private readonly DmaController _dma;
        private readonly SoftwareRasterizer.RenderSettings _settings = new();
        private uint _gxstatIrqMode;

        public Gpu3DEngine(VramController vram, InterruptController arm9Irq, DmaController arm9Dma)
        {
            _irq = arm9Irq;
            _dma = arm9Dma;
            Geometry = new GeometryEngine();
            Rasterizer = new SoftwareRasterizer(vram);
            Geometry.FifoDrained += OnFifoDrained;
        }

        public GeometryEngine Geometry { get; }
        public SoftwareRasterizer Rasterizer { get; }

        public ushort Disp3DControl => _settings.Disp3DControl;

        /// <summary>True once a frame has been rendered (so engine A shows 3D output).</summary>
        public bool HasOutput { get; private set; }

        public void Reset()
        {
            Geometry.Reset();
            _settings.Disp3DControl = 0;
            _settings.ClearColor = 0;
            _settings.ClearDepth = 0x7FFF;
            _settings.ClearImageOffset = 0;
            _settings.AlphaTestRef = 0;
            _settings.FogColor = 0;
            _settings.FogOffset = 0;
            Array.Clear(_settings.FogTable);
            Array.Clear(_settings.ToonTable);
            Array.Clear(_settings.EdgeColors);
            _gxstatIrqMode = 0;
            HasOutput = false;
        }

        public static bool Contains(uint offset) => offset >= 0x320 && offset < 0x6A4;

        // ── DISP3DCNT ─────────────────────────────────────────────────────────

        public ushort ReadDisp3DControl() =>
            (ushort)(_settings.Disp3DControl | (Geometry.RamOverflow ? 0x2000 : 0));

        public void WriteDisp3DControl(ushort value, ushort mask = 0xFFFF)
        {
            if ((value & mask & 0x2000) != 0)
                Geometry.RamOverflow = false; // write 1 to acknowledge
            ushort writable = 0x4FFF;
            _settings.Disp3DControl = (ushort)((_settings.Disp3DControl & ~(mask & writable)) | (value & mask & writable));
        }

        // ── Register access ───────────────────────────────────────────────────

        public uint Read32(uint offset)
        {
            switch (offset)
            {
                case 0x320: return 46; // RDLINES_COUNT: renderer never falls behind
                case 0x600: return ReadGxStat();
                case 0x604: return (uint)(Geometry.PolygonCount | (Geometry.VertexCount << 16));
            }
            if (offset >= 0x620 && offset < 0x630)
                return (uint)Geometry.PositionTestResult[(offset - 0x620) / 4];
            if (offset >= 0x630 && offset < 0x636)
            {
                int i = (int)(offset - 0x630) / 2;
                uint lo = (ushort)Geometry.VectorTestResult[i];
                uint hi = i + 1 < 3 ? (ushort)Geometry.VectorTestResult[i + 1] : 0u;
                return lo | (hi << 16);
            }
            if (offset >= 0x640 && offset < 0x680)
                return (uint)Geometry.ClipMatrix[(offset - 0x640) / 4];
            if (offset >= 0x680 && offset < 0x6A4)
            {
                int i = (int)(offset - 0x680) / 4;
                return (uint)Geometry.VectorMatrix[(i / 3) * 4 + i % 3];
            }
            return 0; // the rest of the window is write-only
        }

        private uint ReadGxStat()
        {
            int count = Geometry.FifoCount;
            uint value = 0;
            if (Geometry.BoxTestResult) value |= 1u << 1;
            value |= (uint)(Geometry.PositionStackPointer & 0x1F) << 8;
            value |= (uint)(Geometry.ProjectionStackPointer & 1) << 13;
            if (Geometry.StackError) value |= 1u << 15;
            value |= (uint)Math.Min(count, 256) << 16;
            if (count < GeometryEngine.FifoSize / 2) value |= 1u << 25;
            if (count == 0) value |= 1u << 26;
            if (Geometry.SwapPending || count > 0) value |= 1u << 27;
            value |= _gxstatIrqMode << 30;
            return value;
        }

        public void Write32(uint offset, uint value, uint mask = 0xFFFFFFFFu)
        {
            if (offset >= 0x400 && offset < 0x440)
            {
                Geometry.WriteFifo(value);
                return;
            }
            if (offset >= 0x440 && offset < 0x600)
            {
                Geometry.WritePort(offset, value);
                return;
            }

            switch (offset)
            {
                case 0x340:
                    if ((mask & 0xFF) != 0) _settings.AlphaTestRef = (byte)(value & 0x1F);
                    return;
                case 0x350:
                    _settings.ClearColor = (_settings.ClearColor & ~mask) | (value & mask);
                    return;
                case 0x354:
                    if ((mask & 0xFFFF) != 0) _settings.ClearDepth = (ushort)((_settings.ClearDepth & ~mask) | (value & mask & 0x7FFF));
                    if ((mask & 0xFFFF0000u) != 0) _settings.ClearImageOffset = (ushort)(value >> 16);
                    return;
                case 0x358:
                    _settings.FogColor = (_settings.FogColor & ~mask) | (value & mask);
                    return;
                case 0x35C:
                    if ((mask & 0xFFFF) != 0) _settings.FogOffset = (ushort)(value & 0x7FFF);
                    return;
                case 0x600:
                    if ((mask & 0x8000) != 0 && (value & 0x8000) != 0)
                        Geometry.StackError = false;
                    if ((mask & 0xC0000000u) != 0)
                        _gxstatIrqMode = (value >> 30) & 3;
                    CheckFifoIrq();
                    return;
                case 0x610:
                    return; // DISP_1DOT_DEPTH: 1-dot culling not applied
            }

            if (offset >= 0x330 && offset < 0x340)
            {
                int i = (int)(offset - 0x330) / 2;
                if ((mask & 0xFFFF) != 0) _settings.EdgeColors[i] = (ushort)value;
                if ((mask & 0xFFFF0000u) != 0) _settings.EdgeColors[i + 1] = (ushort)(value >> 16);
                return;
            }
            if (offset >= 0x360 && offset < 0x380)
            {
                for (int b = 0; b < 4; b++)
                    if ((mask & (0xFFu << (b * 8))) != 0)
                        _settings.FogTable[offset - 0x360 + b] = (byte)(value >> (b * 8));
                return;
            }
            if (offset >= 0x380 && offset < 0x3C0)
            {
                int i = (int)(offset - 0x380) / 2;
                if ((mask & 0xFFFF) != 0) _settings.ToonTable[i] = (ushort)value;
                if ((mask & 0xFFFF0000u) != 0) _settings.ToonTable[i + 1] = (ushort)(value >> 16);
            }
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("Gpu3D");
            s.Sync(ref _settings.Disp3DControl);
            s.Sync(ref _settings.ClearColor);
            s.Sync(ref _settings.ClearDepth);
            s.Sync(ref _settings.ClearImageOffset);
            s.Sync(ref _settings.AlphaTestRef);
            s.Sync(ref _settings.FogColor);
            s.Sync(ref _settings.FogOffset);
            s.Sync(_settings.FogTable);
            s.Sync(_settings.ToonTable);
            s.Sync(_settings.EdgeColors);
            s.Sync(ref _gxstatIrqMode);
            Geometry.Serialize(s);
            if (s.IsReading && HasOutput)
                Rasterizer.Render(Geometry.RenderList, Geometry.RenderManualSort, _settings);
        }

        // ── FIFO IRQ / DMA ────────────────────────────────────────────────────

        private void OnFifoDrained()
        {
            CheckFifoIrq();
            // GXFIFO DMA: refill while the FIFO is less than half full.
            int guard = 0;
            while (_dma.IsWaiting(DmaTiming.GeometryFifo) && Geometry.FifoCount < GeometryEngine.FifoSize / 2 && guard++ < 4096)
                _dma.Trigger(DmaTiming.GeometryFifo);
        }

        public void CheckFifoIrq()
        {
            int count = Geometry.FifoCount;
            bool raise = _gxstatIrqMode switch
            {
                1 => count < GeometryEngine.FifoSize / 2,
                2 => count == 0,
                _ => false,
            };
            if (raise)
                _irq.Request(InterruptController.IRQ_GXFIFO);
        }

        /// <summary>A GXFIFO DMA channel was enabled: start feeding it.</summary>
        public void OnDmaStarted() => OnFifoDrained();

        // ── Frame ─────────────────────────────────────────────────────────────

        /// <summary>VBlank: swap buffers if requested, then render the current list.</summary>
        public void OnVBlank(bool renderEnabled)
        {
            Geometry.OnVBlank();
            if (!renderEnabled)
                return;
            Rasterizer.Render(Geometry.RenderList, Geometry.RenderManualSort, _settings);
            HasOutput = true;
            OnFifoDrained();
        }

        /// <summary>One line of 3D output for engine A's BG0.</summary>
        public ushort[]? GetLine(int line) => HasOutput ? Rasterizer.OutputLines[line] : null;

        public int GetAlpha(int line, int x) => Rasterizer.OutputAlpha[line * 256 + x];
    }
}
