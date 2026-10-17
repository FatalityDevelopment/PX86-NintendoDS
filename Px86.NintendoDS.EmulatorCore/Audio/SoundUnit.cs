using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.Common.Interfaces;

namespace Px86.NintendoDS.EmulatorCore.Audio
{
    /// <summary>
    /// The ARM7 sound processor: 16 channels, mixer and 2 capture units.
    ///
    ///   04000400h + 10h·n  SOUNDxCNT  0–6 volume, 8–9 divider (÷1, ÷2, ÷4, ÷16), 15 hold,
    ///                                 16–22 pan, 24–26 PSG duty, 27–28 repeat (manual, loop, one-shot),
    ///                                 29–30 format (PCM8, PCM16, IMA-ADPCM, PSG/noise), 31 start/busy
    ///                      SOUNDxSAD  source address        SOUNDxTMR  timer (rate = 16.76 MHz / (10000h − TMR))
    ///                      SOUNDxPNT  loop start (words)    SOUNDxLEN  length after the loop start (words)
    ///   04000500h  SOUNDCNT    0–6 master volume, 8–11 output routing, 12/13 ch1/ch3 to mixer, 15 enable
    ///   04000504h  SOUNDBIAS   0–9 bias (usually 200h)
    ///   04000508h  SNDCAPxCNT  (8-bit ×2) 0 add, 1 source, 2 one-shot, 3 PCM8, 7 start
    ///   04000510h  SNDCAPxDAD / 04000514h SNDCAPxLEN (and +8 for capture 1)
    ///
    /// Output: one stereo sample every 1024 system cycles (~32.73 kHz) into a
    /// ring buffer drained with <see cref="ReadSamples"/>. Channels use
    /// nearest-sample playback at their own timer rate.
    ///
    /// Reference: GBATEK "DS Sound Channels 0..15", "DS Sound Control Registers",
    /// "DS Sound Capture", "DS Sound Notes".
    /// </summary>
    public sealed class SoundUnit
    {
        public const uint REG_BASE = 0x400;
        public const uint REG_END  = 0x520;
        public const uint REG_SOUNDCNT  = 0x500;
        public const uint REG_SOUNDBIAS = 0x504;

        /// <summary>System cycles per output sample.</summary>
        public const int CyclesPerSample = 1024;

        private const int BufferFrames = 32768;

        private static readonly int[] AdpcmIndexTable = { -1, -1, -1, -1, 2, 4, 6, 8 };

        private static readonly ushort[] AdpcmStepTable =
        {
            0x0007, 0x0008, 0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x000E, 0x0010, 0x0011, 0x0013, 0x0015,
            0x0017, 0x0019, 0x001C, 0x001F, 0x0022, 0x0025, 0x0029, 0x002D, 0x0032, 0x0037, 0x003C, 0x0042,
            0x0049, 0x0050, 0x0058, 0x0061, 0x006B, 0x0076, 0x0082, 0x008F, 0x009D, 0x00AD, 0x00BE, 0x00D1,
            0x00E6, 0x00FD, 0x0117, 0x0133, 0x0151, 0x0173, 0x0198, 0x01C1, 0x01EE, 0x0220, 0x0256, 0x0292,
            0x02D4, 0x031C, 0x036C, 0x03C3, 0x0424, 0x048E, 0x0502, 0x0583, 0x0610, 0x06AB, 0x0756, 0x0812,
            0x08E0, 0x09C3, 0x0ABD, 0x0BD0, 0x0CFF, 0x0E4C, 0x0FBA, 0x114C, 0x1307, 0x14EE, 0x1706, 0x1954,
            0x1BDC, 0x1EA5, 0x21B6, 0x2515, 0x28CA, 0x2CDF, 0x315B, 0x364B, 0x3BB9, 0x41B2, 0x4844, 0x4F7E,
            0x5771, 0x602F, 0x69CE, 0x7462, 0x7FFF,
        };

        private sealed class Channel
        {
            public uint Control;
            public uint Source;
            public ushort Timer;
            public ushort LoopStart;   // words
            public uint Length;        // words

            public bool Active;
            public int TimerAccumulator;
            public uint Position;      // bytes from Source (nibbles ×2 for ADPCM: tracked separately)
            public int AdpcmNibble;    // 0/1: low or high nibble of the current byte
            public int Sample;         // current output sample (16-bit)

            public int AdpcmValue, AdpcmIndex;
            public int LoopAdpcmValue, LoopAdpcmIndex;
            public bool LoopStateSaved;

            public int PsgStep;
            public int NoiseLfsr;

            public int Format => (int)(Control >> 29) & 3;
            public int Repeat => (int)(Control >> 27) & 3;
        }

        private sealed class Capture
        {
            public byte Control;
            public uint Destination;
            public ushort Length;      // words
            public bool Active;
            public uint Position;      // bytes
            public int TimerAccumulator;
        }

        private readonly Scheduler _scheduler;
        private readonly IArmMemoryBus _bus;
        private readonly Channel[] _channels = new Channel[16];
        private readonly Capture[] _captures = { new(), new() };
        private ushort _soundControl;
        private ushort _bias;

        private readonly short[] _ring = new short[BufferFrames * 2];
        private int _ringRead, _ringWrite, _ringCount;
        private readonly object _ringLock = new();

        public SoundUnit(Scheduler scheduler, IArmMemoryBus arm7Bus)
        {
            _scheduler = scheduler;
            _bus = arm7Bus;
            for (int i = 0; i < 16; i++)
                _channels[i] = new Channel();
            scheduler.SetHandler(SchedulerEvent.SpuSample, OnSample);
        }

        /// <summary>Output rate of <see cref="ReadSamples"/> (system clock / 1024).</summary>
        public int SampleRate => (int)(Scheduler.SystemClockHz / CyclesPerSample);

        /// <summary>When false (POWCNT2 bit 0 clear) the unit outputs silence.</summary>
        public Func<bool> Powered { get; set; } = () => true;

        public void Reset()
        {
            for (int i = 0; i < 16; i++)
                _channels[i] = new Channel();
            _captures[0] = new Capture();
            _captures[1] = new Capture();
            _soundControl = 0;
            _bias = 0;
            lock (_ringLock)
                _ringRead = _ringWrite = _ringCount = 0;
            _scheduler.ScheduleAt(SchedulerEvent.SpuSample, _scheduler.Now + Scheduler.SystemCyclesToArm9(CyclesPerSample));
        }

        public static bool Contains(uint offset) => offset >= REG_BASE && offset < REG_END;

        // ── Registers ─────────────────────────────────────────────────────────

        public uint Read32(uint offset)
        {
            if (offset < 0x500)
            {
                var c = _channels[(offset - 0x400) >> 4];
                return (offset & 0xF) == 0 ? (c.Control & 0x7FFFFFFFu) | (c.Active ? 0x80000000u : 0) : 0;
            }
            switch (offset)
            {
                case 0x500: return _soundControl;
                case 0x504: return _bias;
                case 0x508:
                    return (uint)(CaptureControl(0) | (CaptureControl(1) << 8));
                case 0x510: return _captures[0].Destination;
                case 0x518: return _captures[1].Destination;
                default: return 0;
            }
        }

        private byte CaptureControl(int i) => (byte)((_captures[i].Control & 0x7F) | (_captures[i].Active ? 0x80 : 0));

        public void Write32(uint offset, uint value, uint mask = 0xFFFFFFFFu)
        {
            if (offset < 0x500)
            {
                int index = (int)(offset - 0x400) >> 4;
                var c = _channels[index];
                switch (offset & 0xF)
                {
                    case 0x0:
                    {
                        bool wasActive = c.Active;
                        c.Control = (c.Control & ~mask) | (value & mask & 0xFF7F837Fu);
                        if ((c.Control & 0x80000000u) != 0 && !wasActive)
                            Start(index);
                        else if ((c.Control & 0x80000000u) == 0)
                            c.Active = false;
                        break;
                    }
                    case 0x4: c.Source = ((c.Source & ~mask) | (value & mask)) & 0x07FFFFFCu; break;
                    case 0x8:
                        if ((mask & 0xFFFF) != 0) c.Timer = (ushort)value;
                        if ((mask & 0xFFFF0000u) != 0) c.LoopStart = (ushort)(value >> 16);
                        break;
                    case 0xC: c.Length = ((c.Length & ~mask) | (value & mask)) & 0x003FFFFFu; break;
                }
                return;
            }

            switch (offset)
            {
                case 0x500:
                    if ((mask & 0xFFFF) != 0) _soundControl = (ushort)(((_soundControl & ~mask) | (value & mask)) & 0xBF7F);
                    return;
                case 0x504:
                    if ((mask & 0xFFFF) != 0) _bias = (ushort)(((_bias & ~mask) | (value & mask)) & 0x3FF);
                    return;
                case 0x508:
                    if ((mask & 0x00FF) != 0) WriteCaptureControl(0, (byte)value);
                    if ((mask & 0xFF00) != 0) WriteCaptureControl(1, (byte)(value >> 8));
                    return;
                case 0x510: _captures[0].Destination = ((_captures[0].Destination & ~mask) | (value & mask)) & 0x07FFFFFCu; return;
                case 0x514: if ((mask & 0xFFFF) != 0) _captures[0].Length = (ushort)value; return;
                case 0x518: _captures[1].Destination = ((_captures[1].Destination & ~mask) | (value & mask)) & 0x07FFFFFCu; return;
                case 0x51C: if ((mask & 0xFFFF) != 0) _captures[1].Length = (ushort)value; return;
            }
        }

        private void WriteCaptureControl(int i, byte value)
        {
            var cap = _captures[i];
            bool wasActive = cap.Active;
            cap.Control = (byte)(value & 0x8F);
            if ((value & 0x80) != 0 && !wasActive)
            {
                cap.Active = true;
                cap.Position = 0;
                cap.TimerAccumulator = 0;
            }
            else if ((value & 0x80) == 0)
            {
                cap.Active = false;
            }
        }

        private void Start(int index)
        {
            var c = _channels[index];
            c.Active = true;
            c.TimerAccumulator = 0;
            c.Position = 0;
            c.AdpcmNibble = 0;
            c.Sample = 0;
            c.PsgStep = 0;
            c.NoiseLfsr = 0x7FFF;
            c.LoopStateSaved = false;

            if (c.Format == 2)
            {
                uint header = _bus.Read32(c.Source);
                c.AdpcmValue = (short)header;
                c.AdpcmIndex = Math.Min((int)(header >> 16) & 0x7F, 88);
                c.Position = 4;
                c.Sample = c.AdpcmValue;
            }
        }

        // ── Sample generation ─────────────────────────────────────────────────

        private void OnSample()
        {
            _scheduler.ScheduleAt(SchedulerEvent.SpuSample,
                _scheduler.TimeOf(SchedulerEvent.SpuSample) + Scheduler.SystemCyclesToArm9(CyclesPerSample));

            // Each output sample spans 512 ticks of the 16.76 MHz channel timers.
            const int TicksPerSample = CyclesPerSample / 2;
            Span<int> left = stackalloc int[16];
            Span<int> right = stackalloc int[16];

            for (int i = 0; i < 16; i++)
            {
                var c = _channels[i];
                if (!c.Active)
                {
                    left[i] = right[i] = 0;
                    continue;
                }

                int period = 0x10000 - c.Timer;
                if (period <= 0) period = 1;
                c.TimerAccumulator += TicksPerSample;
                while (c.TimerAccumulator >= period && c.Active)
                {
                    c.TimerAccumulator -= period;
                    Advance(i, c);
                }

                int volume = (int)(c.Control & 0x7F);
                if (volume == 127) volume = 128;
                int shift = ((c.Control >> 8) & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
                int pan = (int)(c.Control >> 16) & 0x7F;
                if (pan == 127) pan = 128;

                int sample = (c.Sample * volume >> 7) >> shift;
                left[i] = sample * (128 - pan) >> 7;
                right[i] = sample * pan >> 7;
            }

            int mixL = 0, mixR = 0;
            for (int i = 0; i < 16; i++)
            {
                if ((i == 1 && (_soundControl & 0x1000) != 0) || (i == 3 && (_soundControl & 0x2000) != 0))
                    continue;
                mixL += left[i];
                mixR += right[i];
            }

            RunCaptures(mixL, mixR, left, right);

            int outL = SelectOutput((_soundControl >> 8) & 3, mixL, left);
            int outR = SelectOutput((_soundControl >> 10) & 3, mixR, right);

            int master = _soundControl & 0x7F;
            if (master == 127) master = 128;
            bool enabled = (_soundControl & 0x8000) != 0 && Powered();
            if (!enabled)
                outL = outR = 0;

            // 16-bit mix × master → 10-bit, plus bias, clipped to the 10-bit DAC range.
            int dacL = Math.Clamp(((outL * master) >> 7 >> 6) + _bias, 0, 0x3FF);
            int dacR = Math.Clamp(((outR * master) >> 7 >> 6) + _bias, 0, 0x3FF);
            Push((short)((dacL - 0x200) << 6), (short)((dacR - 0x200) << 6));
        }

        private static int SelectOutput(int select, int mixer, ReadOnlySpan<int> channels) => select switch
        {
            0 => mixer,
            1 => channels[1],
            2 => channels[3],
            _ => channels[1] + channels[3],
        };

        private void Advance(int index, Channel c)
        {
            switch (c.Format)
            {
                case 0: // PCM8
                    if (!InRange(c)) return;
                    c.Sample = (sbyte)_bus.Read8(c.Source + c.Position) << 8;
                    c.Position++;
                    break;
                case 1: // PCM16
                    if (!InRange(c)) return;
                    c.Sample = (short)_bus.Read16(c.Source + c.Position);
                    c.Position += 2;
                    break;
                case 2: // IMA-ADPCM
                    AdvanceAdpcm(c);
                    break;
                default:
                    if (index >= 14)
                    {
                        // Noise: 15-bit LFSR.
                        bool carry = (c.NoiseLfsr & 1) != 0;
                        c.NoiseLfsr >>= 1;
                        if (carry)
                        {
                            c.NoiseLfsr ^= 0x6000;
                            c.Sample = -0x7FFF;
                        }
                        else
                        {
                            c.Sample = 0x7FFF;
                        }
                    }
                    else if (index >= 8)
                    {
                        int duty = (int)(c.Control >> 24) & 7;
                        c.Sample = c.PsgStep < 7 - duty ? -0x7FFF : 0x7FFF;
                        c.PsgStep = (c.PsgStep + 1) & 7;
                    }
                    else
                    {
                        c.Sample = 0;
                    }
                    break;
            }
        }

        /// <summary>Handle the end of the sample data: loop or stop. Returns false when the channel stopped.</summary>
        private bool InRange(Channel c)
        {
            uint end = (c.LoopStart + c.Length) * 4u;
            if (c.Position < end)
                return true;

            if (c.Repeat == 2 || c.Length == 0)
            {
                c.Active = false;
                c.Control &= 0x7FFFFFFFu;
                if ((c.Control & 0x8000) == 0)
                    c.Sample = 0;
                return false;
            }
            c.Position = c.LoopStart * 4u;
            return true;
        }

        private void AdvanceAdpcm(Channel c)
        {
            uint loopStart = c.LoopStart * 4u;
            if (c.Position == loopStart && c.AdpcmNibble == 0 && !c.LoopStateSaved)
            {
                c.LoopAdpcmValue = c.AdpcmValue;
                c.LoopAdpcmIndex = c.AdpcmIndex;
                c.LoopStateSaved = true;
            }

            uint end = (c.LoopStart + c.Length) * 4u;
            if (c.Position >= end)
            {
                if (c.Repeat == 2 || c.Length == 0)
                {
                    c.Active = false;
                    c.Control &= 0x7FFFFFFFu;
                    if ((c.Control & 0x8000) == 0)
                        c.Sample = 0;
                    return;
                }
                c.Position = loopStart;
                c.AdpcmNibble = 0;
                c.AdpcmValue = c.LoopAdpcmValue;
                c.AdpcmIndex = c.LoopAdpcmIndex;
            }

            byte data = _bus.Read8(c.Source + c.Position);
            int nibble = c.AdpcmNibble == 0 ? data & 0xF : data >> 4;
            if (c.AdpcmNibble == 1)
                c.Position++;
            c.AdpcmNibble ^= 1;

            int step = AdpcmStepTable[c.AdpcmIndex];
            int diff = step >> 3;
            if ((nibble & 1) != 0) diff += step >> 2;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 4) != 0) diff += step;
            c.AdpcmValue = (nibble & 8) != 0
                ? Math.Max(c.AdpcmValue - diff, -0x7FFF)
                : Math.Min(c.AdpcmValue + diff, 0x7FFF);
            c.AdpcmIndex = Math.Clamp(c.AdpcmIndex + AdpcmIndexTable[nibble & 7], 0, 88);
            c.Sample = c.AdpcmValue;
        }

        private void RunCaptures(int mixL, int mixR, ReadOnlySpan<int> left, ReadOnlySpan<int> right)
        {
            for (int i = 0; i < 2; i++)
            {
                var cap = _captures[i];
                if (!cap.Active)
                    continue;

                // The capture timer is channel 1 / channel 3's timer.
                var timerChannel = _channels[i == 0 ? 1 : 3];
                int period = Math.Max(1, 0x10000 - timerChannel.Timer);
                cap.TimerAccumulator += CyclesPerSample / 2;

                while (cap.TimerAccumulator >= period && cap.Active)
                {
                    cap.TimerAccumulator -= period;
                    int source = (cap.Control & 0x02) != 0
                        ? (i == 0 ? left[0] + right[0] : left[2] + right[2])
                        : (i == 0 ? mixL : mixR);
                    source = Math.Clamp(source, -0x8000, 0x7FFF);

                    uint length = Math.Max(1u, cap.Length) * 4u;
                    if ((cap.Control & 0x08) != 0)
                    {
                        _bus.Write8(cap.Destination + cap.Position, (byte)(source >> 8));
                        cap.Position += 1;
                    }
                    else
                    {
                        _bus.Write16(cap.Destination + cap.Position, (ushort)source);
                        cap.Position += 2;
                    }

                    if (cap.Position >= length)
                    {
                        cap.Position = 0;
                        if ((cap.Control & 0x04) != 0)
                        {
                            cap.Active = false;
                            cap.Control &= 0x7F;
                        }
                    }
                }
            }
        }

        // ── Output buffer ─────────────────────────────────────────────────────

        private void Push(short l, short r)
        {
            lock (_ringLock)
            {
                if (_ringCount == BufferFrames)
                {
                    _ringRead = (_ringRead + 2) % _ring.Length;
                    _ringCount--;
                }
                _ring[_ringWrite] = l;
                _ring[_ringWrite + 1] = r;
                _ringWrite = (_ringWrite + 2) % _ring.Length;
                _ringCount++;
            }
        }

        /// <summary>Number of stereo frames waiting in the output buffer.</summary>
        public int BufferedFrames
        {
            get { lock (_ringLock) return _ringCount; }
        }

        /// <summary>Copy mixed stereo samples (L, R interleaved) into <paramref name="destination"/>; returns frames written.</summary>
        public int ReadSamples(Span<short> destination)
        {
            lock (_ringLock)
            {
                int frames = Math.Min(_ringCount, destination.Length / 2);
                for (int f = 0; f < frames; f++)
                {
                    destination[f * 2] = _ring[_ringRead];
                    destination[f * 2 + 1] = _ring[_ringRead + 1];
                    _ringRead = (_ringRead + 2) % _ring.Length;
                }
                _ringCount -= frames;
                return frames;
            }
        }
    }
}
