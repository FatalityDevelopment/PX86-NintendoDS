using Silk.NET.SDL;
using Px86.NintendoDS.EmulatorCore.Hardware.Spi;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Records the PC's microphone through SDL (16 kHz, mono, 16-bit) into a
    /// <see cref="MicrophoneInput"/>, which games read through the touch-screen
    /// controller's AUX channel. The window polls <see cref="Pump"/> once per
    /// frame. Without a capture device the microphone stays silent (the blow
    /// key still works).
    /// </summary>
    public sealed unsafe class MicrophoneCapture : IDisposable
    {
        private const int SampleRate = 16000;
        private const uint InitAudio = 0x00000010;  // SDL_INIT_AUDIO
        private const ushort AudioS16Lsb = 0x8010;  // AUDIO_S16LSB

        private readonly Sdl _sdl;
        private readonly uint _device;
        private readonly short[] _buffer = new short[4096];

        private MicrophoneCapture(Sdl sdl, uint device, string name, MicrophoneInput input)
        {
            _sdl = sdl;
            _device = device;
            DeviceName = name;
            Input = input;
            input.SampleRate = SampleRate;
        }

        /// <summary>Where the recorded samples go.</summary>
        public MicrophoneInput Input { get; }

        public string DeviceName { get; }

        /// <summary>Capture device names (index 0 = the system default is "").</summary>
        public static List<string> ListDevices()
        {
            var names = new List<string>();
            try
            {
                var sdl = Sdl.GetApi();
                if (sdl.InitSubSystem(InitAudio) != 0)
                    return names;
                int count = sdl.GetNumAudioDevices(1);
                for (int i = 0; i < count; i++)
                {
                    string? name = sdl.GetAudioDeviceNameS(i, 1);
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            return names;
        }

        /// <summary>Open <paramref name="deviceName"/> (null = default). Null if there is no capture device.</summary>
        public static MicrophoneCapture? TryOpen(string? deviceName, MicrophoneInput input, out string? error)
        {
            error = null;
            try
            {
                var sdl = Sdl.GetApi();
                if (sdl.InitSubSystem(InitAudio) != 0)
                {
                    error = sdl.GetErrorS();
                    return null;
                }
                var desired = new AudioSpec { Freq = SampleRate, Format = AudioS16Lsb, Channels = 1, Samples = 512 };
                AudioSpec obtained;
                uint device = sdl.OpenAudioDevice(string.IsNullOrEmpty(deviceName) ? null : deviceName, 1, &desired, &obtained, 0);
                if (device == 0)
                {
                    error = sdl.GetErrorS();
                    return null;
                }
                sdl.PauseAudioDevice(device, 0);
                return new MicrophoneCapture(sdl, device, deviceName ?? "default", input);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Move everything SDL has recorded into <see cref="Input"/>.</summary>
        public void Pump()
        {
            fixed (short* buffer = _buffer)
            {
                while (true)
                {
                    uint bytes = _sdl.DequeueAudio(_device, buffer, (uint)(_buffer.Length * 2));
                    if (bytes == 0)
                        break;
                    Input.Push(_buffer.AsSpan(0, (int)bytes / 2));
                    if (bytes < _buffer.Length * 2)
                        break;
                }
            }
        }

        public void Dispose() => _sdl.CloseAudioDevice(_device);
    }
}
