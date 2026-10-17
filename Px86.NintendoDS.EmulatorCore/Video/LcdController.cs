using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.EmulatorCore.Hardware;

namespace Px86.NintendoDS.EmulatorCore.Video
{
    /// <summary>
    /// LCD timing: scanline counter, blanking flags and their interrupts/DMA.
    ///
    ///   04000004h  DISPSTAT (per CPU)  0 VBlank, 1 HBlank, 2 VCount match (R)
    ///                                  3/4/5 VBlank/HBlank/VCount IRQ enable
    ///                                  7 = LYC bit 8, 8–15 = LYC bits 0–7
    ///   04000006h  VCOUNT   (shared)   current line 0–262
    ///
    /// Timing (dot clock = 33.513982 MHz / 6):
    ///   355 dots per line → 2130 system cycles = 4260 ARM9 cycles
    ///   HBlank starts after 256 dots (1536 system cycles)
    ///   263 lines per frame, VBlank on lines 192–261 (the flag is clear on 262)
    ///   → 59.8261 frames per second
    ///
    /// Visible lines are rendered at the start of HBlank. VBlank start (line
    /// 192) ends the frame for <see cref="NdsEmulator.RunFrame"/>.
    ///
    /// Reference: GBATEK "DS Video Stuff" (DS Display Dimensions / Timings).
    /// </summary>
    public sealed class LcdController
    {
        public const uint REG_DISPSTAT = 0x004;
        public const uint REG_VCOUNT   = 0x006;

        public const int ScreenWidth = 256;
        public const int ScreenHeight = 192;
        public const int TotalLines = 263;

        public const long Arm9CyclesPerLine = 4260;
        public const long Arm9CyclesToHBlank = 3072;
        public const long Arm9CyclesPerFrame = Arm9CyclesPerLine * TotalLines;

        private readonly Scheduler _scheduler;
        private readonly InterruptController[] _irq;
        private readonly DmaController[] _dma;
        private readonly ushort[] _dispstat = new ushort[2];
        private long _lineStart;

        public LcdController(Scheduler scheduler, InterruptController arm9Irq, InterruptController arm7Irq,
                             DmaController arm9Dma, DmaController arm7Dma)
        {
            _scheduler = scheduler;
            _irq = new[] { arm9Irq, arm7Irq };
            _dma = new[] { arm9Dma, arm7Dma };
            scheduler.SetHandler(SchedulerEvent.LcdHBlank, OnHBlank);
            scheduler.SetHandler(SchedulerEvent.LcdLineEnd, OnLineEnd);
        }

        public int VCount { get; private set; }

        public bool InVBlank => VCount >= ScreenHeight && VCount < TotalLines - 1;

        /// <summary>Render line <c>n</c> (0–191). Raised at the start of HBlank.</summary>
        public event Action<int>? ScanlineReady;

        /// <summary>Line 192 reached: the frame is complete.</summary>
        public event Action? VBlankStarted;

        /// <summary>A new line began (after VCOUNT was incremented).</summary>
        public event Action<int>? LineStarted;

        public void Reset()
        {
            Array.Clear(_dispstat);
            VCount = 0;
            _lineStart = _scheduler.Now;
            _scheduler.ScheduleAt(SchedulerEvent.LcdHBlank, _lineStart + Arm9CyclesToHBlank);
            _scheduler.ScheduleAt(SchedulerEvent.LcdLineEnd, _lineStart + Arm9CyclesPerLine);
        }

        // ── Registers ─────────────────────────────────────────────────────────

        public ushort ReadDispstat(int cpu)
        {
            int value = _dispstat[cpu] & 0xFFB8;
            if (InVBlank) value |= 1;
            if (_scheduler.CurrentTime - _lineStart >= Arm9CyclesToHBlank) value |= 2;
            if (VCount == Lyc(cpu)) value |= 4;
            return (ushort)value;
        }

        public void WriteDispstat(int cpu, ushort value, ushort mask = 0xFFFF)
        {
            _dispstat[cpu] = (ushort)((_dispstat[cpu] & ~(mask & 0xFFB8)) | (value & mask & 0xFFB8));
        }

        private int Lyc(int cpu) => (_dispstat[cpu] >> 8) | ((_dispstat[cpu] & 0x80) << 1);

        public void WriteVCount(ushort value)
        {
            // Writable for console linking; only honoured in the VBlank window GBATEK recommends.
            if (VCount >= 202 && VCount <= 212 && value >= 202 && value <= 212)
                VCount = value;
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("Lcd");
            s.Sync(_dispstat);
            int vcount = VCount;
            s.Sync(ref vcount);
            VCount = vcount;
            s.Sync(ref _lineStart);
        }

        // ── Events ────────────────────────────────────────────────────────────

        private void OnHBlank()
        {
            for (int cpu = 0; cpu < 2; cpu++)
                if ((_dispstat[cpu] & 0x10) != 0)
                    _irq[cpu].Request(InterruptController.IRQ_HBLANK);

            if (VCount < ScreenHeight)
            {
                ScanlineReady?.Invoke(VCount);
                _dma[0].Trigger(DmaTiming.HBlank);
            }
        }

        private void OnLineEnd()
        {
            _lineStart = _scheduler.TimeOf(SchedulerEvent.LcdLineEnd);
            VCount++;
            if (VCount == TotalLines)
                VCount = 0;

            _scheduler.ScheduleAt(SchedulerEvent.LcdHBlank, _lineStart + Arm9CyclesToHBlank);
            _scheduler.ScheduleAt(SchedulerEvent.LcdLineEnd, _lineStart + Arm9CyclesPerLine);

            if (VCount == ScreenHeight)
            {
                for (int cpu = 0; cpu < 2; cpu++)
                {
                    if ((_dispstat[cpu] & 0x08) != 0)
                        _irq[cpu].Request(InterruptController.IRQ_VBLANK);
                    _dma[cpu].Trigger(DmaTiming.VBlank);
                }
                VBlankStarted?.Invoke();
            }

            for (int cpu = 0; cpu < 2; cpu++)
                if ((_dispstat[cpu] & 0x20) != 0 && VCount == Lyc(cpu))
                    _irq[cpu].Request(InterruptController.IRQ_VCOUNT);

            if (VCount >= 2 && VCount < ScreenHeight + 2)
                _dma[0].Trigger(DmaTiming.DisplayStart);

            LineStarted?.Invoke(VCount);
        }
    }
}
