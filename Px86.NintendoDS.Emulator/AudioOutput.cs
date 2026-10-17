using Silk.NET.OpenAL;
using Px86.NintendoDS.EmulatorCore;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Streams the SPU output through OpenAL Soft: a ring of small buffers
    /// refilled once per emulated frame from <see cref="EmulatorCore.Audio.SoundUnit.ReadSamples"/>.
    /// If OpenAL can't be opened the emulator simply runs silent.
    /// </summary>
    public sealed unsafe class AudioOutput : IDisposable
    {
        private const int BufferCount = 6;
        private const int MaxBacklogFrames = 2048;

        private readonly AL _al;
        private readonly ALContext _alc;
        private readonly Device* _device;
        private readonly Context* _context;
        private readonly uint _source;
        private readonly Queue<uint> _freeBuffers = new();
        private readonly NdsEmulator _nds;
        private readonly short[] _samples = new short[8192];

        private AudioOutput(NdsEmulator nds, AL al, ALContext alc, Device* device, Context* context)
        {
            _nds = nds;
            _al = al;
            _alc = alc;
            _device = device;
            _context = context;
            _source = _al.GenSource();
            foreach (uint buffer in _al.GenBuffers(BufferCount))
                _freeBuffers.Enqueue(buffer);
        }

        public static AudioOutput? TryCreate(NdsEmulator nds)
        {
            try
            {
                var alc = ALContext.GetApi(soft: true);
                var al = AL.GetApi(soft: true);
                Device* device = alc.OpenDevice("");
                if (device == null)
                    return null;
                Context* context = alc.CreateContext(device, null);
                alc.MakeContextCurrent(context);
                return new AudioOutput(nds, al, alc, device, context);
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or FileNotFoundException)
            {
                Console.Error.WriteLine($"[Audio] OpenAL unavailable ({ex.Message}); running without sound.");
                return null;
            }
        }

        /// <summary>Emulation thread, after each frame: move new samples into OpenAL.</summary>
        public void OnFrame()
        {
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            if (processed > 0)
            {
                var done = new uint[processed];
                fixed (uint* p = done)
                    _al.SourceUnqueueBuffers(_source, processed, p);
                foreach (uint b in done)
                    _freeBuffers.Enqueue(b);
            }

            while (_freeBuffers.Count > 0)
            {
                int frames = _nds.Sound.ReadSamples(_samples);
                if (frames == 0)
                    break;
                uint buffer = _freeBuffers.Dequeue();
                fixed (short* data = _samples)
                    _al.BufferData(buffer, BufferFormat.Stereo16, data, frames * 4, _nds.Sound.SampleRate);
                _al.SourceQueueBuffers(_source, 1, &buffer);
            }

            // Keep latency bounded: if OpenAL is behind (all buffers queued), drop the excess.
            while (_nds.Sound.BufferedFrames > MaxBacklogFrames)
                _nds.Sound.ReadSamples(_samples);

            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if ((SourceState)state != SourceState.Playing)
                _al.SourcePlay(_source);
        }

        public void Dispose()
        {
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
            foreach (uint b in _freeBuffers)
                _al.DeleteBuffer(b);
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
        }
    }
}
