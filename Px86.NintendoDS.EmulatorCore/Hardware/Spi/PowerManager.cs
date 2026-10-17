using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Spi
{
    /// <summary>
    /// Power management chip (Mitsumi 3152A / 3205B on DS-Lite).
    ///
    /// First byte: bit 7 = read, bits 6–0 = register. Second byte: data.
    ///   0 Control (sound amp, backlights, LED, bit 6 = power off)
    ///   1 Battery status (0 = good)
    ///   2 Microphone amp enable, 3 Microphone gain
    ///   4 DS-Lite backlight level / power source
    ///
    /// Reference: GBATEK "DS Power Management Device".
    /// </summary>
    public sealed class PowerManager : ISpiDevice
    {
        private readonly byte[] _registers = new byte[8];
        private int _index = -1;
        private bool _read;

        public PowerManager()
        {
            Reset();
        }

        /// <summary>Register 2 bit 0: the microphone amplifier is on.</summary>
        public bool MicrophoneAmplifierOn => (_registers[2] & 1) != 0;

        /// <summary>Register 3: amplifier gain 0–3 (20×, 40×, 80×, 160×).</summary>
        public int MicrophoneGain => _registers[3] & 3;

        /// <summary>Raised when software writes the power-off bit.</summary>
        public event Action? PowerOffRequested;

        public void Reset()
        {
            Array.Clear(_registers);
            _registers[0] = 0x0D; // amp on, both backlights on
            _registers[4] = 0x43; // max backlight, unknown bits = 4
            _index = -1;
        }

        public byte Transfer(byte value)
        {
            if (_index < 0)
            {
                _index = value & 0x07;
                _read = (value & 0x80) != 0;
                return 0;
            }

            if (_read)
                return _registers[_index];

            switch (_index)
            {
                case 0:
                    _registers[0] = (byte)(value & 0x7F);
                    if ((value & 0x40) != 0)
                        PowerOffRequested?.Invoke();
                    break;
                case 2: _registers[2] = (byte)(value & 1); break;
                case 3: _registers[3] = (byte)(value & 3); break;
                case 4: _registers[4] = (byte)((_registers[4] & 0xF8) | (value & 0x07)); break;
            }
            return 0;
        }

        public void Deselect() => _index = -1;

        public void Serialize(StateSerializer s)
        {
            s.Section("Power");
            s.Sync(_registers);
            s.Sync(ref _index);
            s.Sync(ref _read);
        }
    }
}
