using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore
{
    /// <summary>Every kind of timed event in the system. One slot each.</summary>
    public enum SchedulerEvent
    {
        LcdHBlank,
        LcdLineEnd,
        Timer9_0, Timer9_1, Timer9_2, Timer9_3,
        Timer7_0, Timer7_1, Timer7_2, Timer7_3,
        CartridgeTransfer,
        SpiTransfer,
        SpuSample,
        GeometryCommand,
        Count,
    }

    /// <summary>
    /// Discrete-event scheduler for everything that isn't a CPU instruction.
    ///
    /// Time is counted in ARM9 cycles (67.027964 MHz). The ARM7 and the bus
    /// devices run at half that rate (33.513982 MHz, the "system clock"); use
    /// <see cref="SystemCyclesToArm9"/> when scheduling in system-clock units.
    ///
    /// There is a fixed slot per <see cref="SchedulerEvent"/>; rescheduling an
    /// event replaces its pending time. With ~15 slots, a linear scan for the
    /// next event is cheaper than maintaining a heap.
    /// </summary>
    public sealed class Scheduler
    {
        public const long Arm9ClockHz = 67_027_964;
        public const long SystemClockHz = 33_513_982;

        private readonly long[] _times = new long[(int)SchedulerEvent.Count];
        private readonly bool[] _active = new bool[(int)SchedulerEvent.Count];
        private readonly Action[] _handlers = new Action[(int)SchedulerEvent.Count];

        /// <summary>Scheduler time in ARM9 cycles: the point up to which events have been processed.</summary>
        public long Now { get; private set; }

        /// <summary>
        /// Precise time of the code currently running: the executing CPU's own
        /// position inside the current slice, or the event time while an event
        /// handler runs. Devices read their counters against this and schedule
        /// relative to it with <see cref="ScheduleFromNow"/>.
        /// </summary>
        public long CurrentTime { get; set; }

        /// <summary>Time of the earliest pending event (long.MaxValue if none).</summary>
        public long NextEventTime { get; private set; } = long.MaxValue;

        public static long SystemCyclesToArm9(long systemCycles) => systemCycles * 2;

        public void SetHandler(SchedulerEvent id, Action handler) => _handlers[(int)id] = handler;

        public void Reset()
        {
            Now = 0;
            CurrentTime = 0;
            Array.Clear(_times);
            Array.Clear(_active);
            NextEventTime = long.MaxValue;
        }

        /// <summary>Schedule <paramref name="id"/> at <see cref="Now"/> + <paramref name="delay"/> ARM9 cycles.</summary>
        public void Schedule(SchedulerEvent id, long delay) => ScheduleAt(id, Now + delay);

        /// <summary>Schedule <paramref name="id"/> at <see cref="CurrentTime"/> + <paramref name="delay"/>.</summary>
        public void ScheduleFromNow(SchedulerEvent id, long delay) => ScheduleAt(id, CurrentTime + delay);

        public void ScheduleAt(SchedulerEvent id, long time)
        {
            bool wasActive = _active[(int)id];
            _times[(int)id] = time;
            _active[(int)id] = true;
            if (wasActive)
                RecomputeNext();
            else if (time < NextEventTime)
                NextEventTime = time;
        }

        public void Cancel(SchedulerEvent id)
        {
            if (!_active[(int)id])
                return;
            _active[(int)id] = false;
            RecomputeNext();
        }

        public bool IsScheduled(SchedulerEvent id) => _active[(int)id];

        public long TimeOf(SchedulerEvent id) => _times[(int)id];

        /// <summary>Advance time to <paramref name="time"/> and fire every event that is due, in time order.</summary>
        public void AdvanceTo(long time)
        {
            while (NextEventTime <= time)
            {
                int due = -1;
                long dueTime = long.MaxValue;
                for (int i = 0; i < _times.Length; i++)
                {
                    if (_active[i] && _times[i] < dueTime)
                    {
                        dueTime = _times[i];
                        due = i;
                    }
                }

                Now = dueTime;
                CurrentTime = dueTime;
                _active[due] = false;
                RecomputeNext();
                _handlers[due]?.Invoke();
            }
            Now = time;
            CurrentTime = time;
        }

        private void RecomputeNext()
        {
            long next = long.MaxValue;
            for (int i = 0; i < _times.Length; i++)
                if (_active[i] && _times[i] < next)
                    next = _times[i];
            NextEventTime = next;
        }

        public void Serialize(StateSerializer s)
        {
            s.Section("Scheduler");
            s.Sync(_times);
            s.Sync(_active);
            long now = Now, current = CurrentTime, next = NextEventTime;
            s.Sync(ref now);
            s.Sync(ref current);
            s.Sync(ref next);
            Now = now;
            CurrentTime = current;
            NextEventTime = next;
        }
    }
}
