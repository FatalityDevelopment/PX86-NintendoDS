namespace Px86.NintendoDS.EmulatorCore.Hardware.Spi
{
    /// <summary>What the microphone hears, sampled when a game reads TSC channel 6.</summary>
    public interface IMicrophoneSource
    {
        /// <summary>Signed 16-bit level at emulated time <paramref name="arm9Cycles"/>.</summary>
        short Sample(long arm9Cycles);
    }

    /// <summary>
    /// The microphone: PCM from the host's microphone, played back at
    /// emulated speed, and a "blow" switch that feeds loud noise (blowing out
    /// candles, shouting) for players without a microphone.
    ///
    /// Host samples arrive on a capture thread through <see cref="Push"/>.
    /// Reads follow emulated time: the read position advances by the elapsed
    /// emulated time, and when the emulator lags or races ahead of the capture
    /// it resynchronises to a small fixed latency instead of drifting.
    /// </summary>
    public sealed class MicrophoneInput : IMicrophoneSource
    {
        private const int BufferSize = 1 << 15;            // 32768 samples ≈ 2 s at 16 kHz
        private readonly short[] _ring = new short[BufferSize];
        private readonly object _lock = new();
        private long _written;                             // total samples pushed
        private double _readPosition;                      // in samples
        private long _lastTime = -1;
        private uint _noise = 0x12345678;

        /// <summary>Sample rate of the pushed PCM.</summary>
        public int SampleRate { get; set; } = 16000;

        /// <summary>Software gain on the host samples (1 = as captured).</summary>
        public float Gain { get; set; } = 1f;

        /// <summary>While true, the microphone hears loud noise.</summary>
        public bool Blowing { get; set; }

        /// <summary>Latency kept between capture and playback, in samples (resync target).</summary>
        public int Latency => SampleRate / 20;

        /// <summary>Add captured mono 16-bit samples (any thread).</summary>
        public void Push(ReadOnlySpan<short> samples)
        {
            lock (_lock)
            {
                foreach (short s in samples)
                    _ring[_written++ & (BufferSize - 1)] = s;
            }
        }

        public short Sample(long arm9Cycles)
        {
            if (Blowing)
            {
                _noise ^= _noise << 13;
                _noise ^= _noise >> 17;
                _noise ^= _noise << 5;
                return (short)((int)(_noise & 0xFFFF) - 0x8000);
            }
            lock (_lock)
            {
                if (_written == 0)
                    return 0;
                if (_lastTime >= 0 && arm9Cycles > _lastTime)
                    _readPosition += (arm9Cycles - _lastTime) * (double)SampleRate / Scheduler.Arm9ClockHz;
                _lastTime = arm9Cycles;
                if (_readPosition > _written || _readPosition < _written - Latency * 4)
                    _readPosition = Math.Max(0, _written - Latency);
                long index = (long)_readPosition;
                if (index >= _written)
                    index = _written - 1;
                return (short)Math.Clamp(_ring[index & (BufferSize - 1)] * Gain, short.MinValue, short.MaxValue);
            }
        }
    }
}
