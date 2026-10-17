using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware
{
    /// <summary>
    /// The four 16-bit timers of one CPU.
    ///
    ///   04000100h + 4n  TMnCNT_L  read: current counter, write: reload value
    ///   04000102h + 4n  TMnCNT_H  bits 0–1 prescaler (F/1, F/64, F/256, F/1024)
    ///                             bit 2 count-up (cascade from timer n−1; not timer 0)
    ///                             bit 6 IRQ on overflow, bit 7 start
    ///
    /// F = 33.513982 MHz on both CPUs. Free-running timers are not ticked one
    /// by one: the counter is derived from the time it was last (re)based, and
    /// the overflow is a scheduler event. Cascaded timers tick only when the
    /// timer below them overflows.
    ///
    /// Reference: GBATEK "DS Timers" / "GBA Timers".
    /// </summary>
    public sealed class TimerUnit
    {
        public const uint REG_BASE = 0x100;

        private static readonly int[] PrescalerShift = { 0, 6, 8, 10 };

        private readonly Scheduler _scheduler;
        private readonly InterruptController _irq;
        private readonly SchedulerEvent _firstEvent;
        private readonly Timer[] _timers = new Timer[4];

        private sealed class Timer
        {
            public ushort Reload;
            public ushort Control;
            public ushort Counter;   // value at BaseTime
            public long BaseTime;

            public bool Running => (Control & 0x80) != 0;
            public bool CountUp => (Control & 0x04) != 0;
        }

        public TimerUnit(Scheduler scheduler, InterruptController irq, bool isArm9)
        {
            _scheduler = scheduler;
            _irq = irq;
            _firstEvent = isArm9 ? SchedulerEvent.Timer9_0 : SchedulerEvent.Timer7_0;
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                _timers[i] = new Timer();
                scheduler.SetHandler(_firstEvent + i, () => OnOverflowEvent(index));
            }
        }

        public void Reset()
        {
            for (int i = 0; i < 4; i++)
            {
                _timers[i] = new Timer();
                _scheduler.Cancel(_firstEvent + i);
            }
        }

        /// <summary>One counter tick in ARM9 cycles.</summary>
        private long Period(int index) => 2L << PrescalerShift[_timers[index].Control & 3];

        private bool IsFreeRunning(int index) =>
            _timers[index].Running && !(index > 0 && _timers[index].CountUp);

        public ushort ReadCounter(int index)
        {
            var t = _timers[index];
            if (!IsFreeRunning(index))
                return t.Counter;
            // The ARM7 runs its half of a slice after the ARM9, so it can read
            // a timer the ARM9 rebased at a later point in the same slice.
            long ticks = Math.Max(0, _scheduler.CurrentTime - t.BaseTime) / Period(index);
            return (ushort)(t.Counter + ticks);
        }

        public ushort ReadControl(int index) => _timers[index].Control;

        public void WriteReload(int index, ushort value) => _timers[index].Reload = value;

        public void WriteControl(int index, ushort value)
        {
            var t = _timers[index];
            ushort counter = ReadCounter(index);
            bool wasRunning = t.Running;

            t.Control = (ushort)(value & (index == 0 ? 0x00C3 : 0x00C7));
            t.Counter = !wasRunning && t.Running ? t.Reload : counter;
            t.BaseTime = _scheduler.CurrentTime;
            Reschedule(index);
        }

        private void Reschedule(int index)
        {
            var t = _timers[index];
            if (!IsFreeRunning(index))
            {
                _scheduler.Cancel(_firstEvent + index);
                return;
            }
            long ticksToOverflow = 0x10000 - t.Counter;
            _scheduler.ScheduleAt(_firstEvent + index, t.BaseTime + ticksToOverflow * Period(index));
        }

        private void OnOverflowEvent(int index)
        {
            var t = _timers[index];
            t.BaseTime = _scheduler.TimeOf(_firstEvent + index);
            t.Counter = t.Reload;
            Overflow(index);
            Reschedule(index);
        }

        private void Overflow(int index)
        {
            var t = _timers[index];
            if ((t.Control & 0x40) != 0)
                _irq.Request(InterruptController.IRQ_TIMER0 + index);

            Overflowed?.Invoke(index);

            if (index == 3)
                return;

            var next = _timers[index + 1];
            if (next.Running && next.CountUp)
            {
                if (next.Counter == 0xFFFF)
                {
                    next.Counter = next.Reload;
                    Overflow(index + 1);
                }
                else
                {
                    next.Counter++;
                }
            }
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("Timers");
            foreach (var t in _timers)
            {
                s.Sync(ref t.Reload);
                s.Sync(ref t.Control);
                s.Sync(ref t.Counter);
                s.Sync(ref t.BaseTime);
            }
        }

        /// <summary>Raised on every overflow (the SPU and DMA can listen).</summary>
        public event Action<int>? Overflowed;
    }
}
