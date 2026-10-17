using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.Common.Interfaces;

namespace Px86.NintendoDS.EmulatorCore.Hardware
{
    /// <summary>When a DMA channel starts, unified across both CPUs' encodings.</summary>
    public enum DmaTiming
    {
        Immediate,
        VBlank,
        HBlank,
        DisplayStart,
        MainMemoryDisplay,
        Cartridge,
        GbaSlot,
        GeometryFifo,
        Wifi,
    }

    /// <summary>
    /// The four DMA channels of one CPU.
    ///
    ///   040000B0h + 12n  DMAnSAD   source address
    ///   040000B4h + 12n  DMAnDAD   destination address
    ///   040000B8h + 12n  DMAnCNT   bits 0–20 word count (ARM9; ARM7: 14 bits, 16 for ch 3)
    ///                              bits 21–22 dest control (inc, dec, fixed, inc+reload)
    ///                              bits 23–24 source control (inc, dec, fixed)
    ///                              bit 25 repeat, bit 26 32-bit units
    ///                              bits 27–29 timing (ARM9) / 28–29 (ARM7), bit 30 IRQ, bit 31 enable
    ///   040000E0h + 4n   DMAnFILL  (ARM9 only) 32-bit scratch words, used as a fixed fill source
    ///
    ///   ARM9 timing: 0 immediate, 1 VBlank, 2 HBlank, 3 display start, 4 main-memory display,
    ///                5 DS cart, 6 GBA cart, 7 geometry FIFO
    ///   ARM7 timing: 0 immediate, 1 VBlank, 2 DS cart, 3 wifi (ch 0/2) / GBA cart (ch 1/3)
    ///
    /// Transfers run to completion as soon as they trigger (no bus-cycle
    /// interleaving with the CPU). The cost is returned to the caller so the
    /// emulator can charge it to the CPU. Geometry-FIFO DMA moves at most 112
    /// words per trigger, and cartridge DMA one word per trigger, as on hardware.
    ///
    /// Reference: GBATEK "DS DMA Transfers" / "GBA DMA Transfers".
    /// </summary>
    public sealed class DmaController
    {
        public const uint REG_BASE = 0x0B0;
        public const uint REG_FILL = 0x0E0;

        private readonly IArmMemoryBus _bus;
        private readonly InterruptController _irq;
        private readonly bool _isArm9;
        private readonly Channel[] _channels = new Channel[4];
        private readonly uint[] _fill = new uint[4];

        private sealed class Channel
        {
            public uint Source;
            public uint Destination;
            public uint Control;

            public uint CurrentSource;
            public uint CurrentDestination;
            public uint Remaining;
            public DmaTiming Timing;

            public bool Enabled => (Control & 0x80000000u) != 0;
        }

        public DmaController(IArmMemoryBus bus, InterruptController irq, bool isArm9)
        {
            _bus = bus;
            _irq = irq;
            _isArm9 = isArm9;
            for (int i = 0; i < 4; i++)
                _channels[i] = new Channel();
        }

        /// <summary>Set while a transfer runs; the ARM9 bus uses it to keep DMA out of the TCMs.</summary>
        public bool Active { get; private set; }

        /// <summary>Cycles spent in DMA since the emulator last collected them.</summary>
        public int PendingCycles { get; set; }

        public void Reset()
        {
            for (int i = 0; i < 4; i++)
                _channels[i] = new Channel();
            Array.Clear(_fill);
            PendingCycles = 0;
        }

        // ── Registers ─────────────────────────────────────────────────────────

        public uint ReadSource(int ch) => _channels[ch].Source;
        public uint ReadDestination(int ch) => _channels[ch].Destination;
        public uint ReadControl(int ch) => _channels[ch].Control;
        public uint ReadFill(int ch) => _fill[ch];

        public void WriteSource(int ch, uint value, uint mask = 0xFFFFFFFFu)
        {
            var c = _channels[ch];
            uint limit = _isArm9 || ch != 0 ? 0x0FFFFFFFu : 0x07FFFFFFu;
            c.Source = ((c.Source & ~mask) | (value & mask)) & limit;
        }

        public void WriteDestination(int ch, uint value, uint mask = 0xFFFFFFFFu)
        {
            var c = _channels[ch];
            uint limit = _isArm9 || ch == 3 ? 0x0FFFFFFFu : 0x07FFFFFFu;
            c.Destination = ((c.Destination & ~mask) | (value & mask)) & limit;
        }

        public void WriteFill(int ch, uint value, uint mask = 0xFFFFFFFFu) =>
            _fill[ch] = (_fill[ch] & ~mask) | (value & mask);

        public void WriteControl(int ch, uint value, uint mask = 0xFFFFFFFFu)
        {
            var c = _channels[ch];
            bool wasEnabled = c.Enabled;
            c.Control = (c.Control & ~mask) | (value & mask);

            if (!c.Enabled || wasEnabled)
                return;

            c.CurrentSource = c.Source;
            c.CurrentDestination = c.Destination;
            c.Remaining = WordCount(ch);
            c.Timing = DecodeTiming(ch, c.Control);

            if (c.Timing == DmaTiming.Immediate)
                Run(ch);
            else if (c.Timing == DmaTiming.GeometryFifo || c.Timing == DmaTiming.Cartridge)
                Started?.Invoke(c.Timing);
        }

        /// <summary>Raised when a FIFO-paced channel is enabled, so the device can trigger it right away if ready.</summary>
        public event Action<DmaTiming>? Started;

        private uint WordCount(int ch)
        {
            uint count = _channels[ch].Control;
            if (_isArm9)
            {
                count &= 0x1FFFFF;
                return count == 0 ? 0x200000u : count;
            }
            if (ch == 3)
            {
                count &= 0xFFFF;
                return count == 0 ? 0x10000u : count;
            }
            count &= 0x3FFF;
            return count == 0 ? 0x4000u : count;
        }

        private DmaTiming DecodeTiming(int ch, uint control)
        {
            if (_isArm9)
                return (DmaTiming)((control >> 27) & 7);

            return ((control >> 28) & 3) switch
            {
                0 => DmaTiming.Immediate,
                1 => DmaTiming.VBlank,
                2 => DmaTiming.Cartridge,
                _ => (ch & 1) == 0 ? DmaTiming.Wifi : DmaTiming.GbaSlot,
            };
        }

        /// <summary>True if any enabled channel waits on <paramref name="timing"/>.</summary>
        public bool IsWaiting(DmaTiming timing)
        {
            foreach (var c in _channels)
                if (c.Enabled && c.Timing == timing)
                    return true;
            return false;
        }

        /// <summary>Start every enabled channel waiting for <paramref name="timing"/>.</summary>
        public void Trigger(DmaTiming timing)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                var c = _channels[ch];
                if (c.Enabled && c.Timing == timing)
                    Run(ch);
            }
        }

        // ── Transfer ──────────────────────────────────────────────────────────

        private void Run(int ch)
        {
            var c = _channels[ch];
            bool wide = (c.Control & (1u << 26)) != 0;
            int unit = wide ? 4 : 2;
            int destControl = (int)(c.Control >> 21) & 3;
            int sourceControl = (int)(c.Control >> 23) & 3;
            int destStep = destControl switch { 0 or 3 => unit, 1 => -unit, _ => 0 };
            int sourceStep = sourceControl switch { 0 => unit, 1 => -unit, _ => 0 };

            uint count = c.Remaining;
            if (c.Timing == DmaTiming.GeometryFifo)
                count = Math.Min(count, 112u);
            else if (c.Timing == DmaTiming.Cartridge)
                count = Math.Min(count, 1u);

            Active = true;
            uint src = c.CurrentSource;
            uint dst = c.CurrentDestination;
            for (uint i = 0; i < count; i++)
            {
                if (wide)
                    _bus.Write32(dst & ~3u, _bus.Read32(src & ~3u));
                else
                    _bus.Write16(dst & ~1u, _bus.Read16(src & ~1u));
                src = (uint)(src + sourceStep);
                dst = (uint)(dst + destStep);
            }
            Active = false;

            c.CurrentSource = src;
            c.CurrentDestination = dst;
            c.Remaining -= count;
            PendingCycles += (int)count * (wide ? 2 : 1) + 2;

            if (c.Remaining > 0)
                return; // FIFO-paced: wait for the next trigger

            if ((c.Control & (1u << 30)) != 0)
                _irq.Request(InterruptController.IRQ_DMA0 + ch);

            bool repeat = (c.Control & (1u << 25)) != 0 && c.Timing != DmaTiming.Immediate;
            if (repeat)
            {
                c.Remaining = WordCount(ch);
                if (destControl == 3)
                    c.CurrentDestination = c.Destination;
            }
            else
            {
                c.Control &= ~0x80000000u;
            }
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("Dma");
            foreach (var c in _channels)
            {
                s.Sync(ref c.Source);
                s.Sync(ref c.Destination);
                s.Sync(ref c.Control);
                s.Sync(ref c.CurrentSource);
                s.Sync(ref c.CurrentDestination);
                s.Sync(ref c.Remaining);
                s.SyncEnum(ref c.Timing);
            }
            s.Sync(_fill);
            int pending = PendingCycles;
            s.Sync(ref pending);
            PendingCycles = pending;
        }
    }
}
