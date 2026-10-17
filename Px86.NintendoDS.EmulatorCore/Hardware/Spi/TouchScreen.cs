using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Spi
{
    /// <summary>
    /// TSC2046 touch-screen controller (also the microphone ADC).
    ///
    /// Control byte: bit 7 start, bits 6–4 channel, bit 3 mode (0 = 12-bit, 1 = 8-bit),
    /// bits 1–0 power-down mode. The conversion result is shifted out MSB first
    /// after one dummy bit, so a 12-bit value V arrives as two bytes:
    /// (V &gt;&gt; 5) and (V &lt;&lt; 3).
    ///
    ///   Channel 1 = Y, 5 = X (12-bit ADC), 3/4 = Z1/Z2 pressure, 6 = microphone, 0/7 = temperature.
    ///   Released pen: X = 0, Y = 0xFFF.
    ///
    /// The default firmware calibrates ADC = 0x100 + pixel × 14 (X) and
    /// 0x0B0 + pixel × 19 (Y), roughly matching real hardware ranges.
    ///
    /// Reference: GBATEK "DS Touch Screen Controller (TSC)".
    /// </summary>
    public sealed class TouchScreen : ISpiDevice
    {
        private int _x;
        private int _y;
        private bool _penDown;
        private ushort _result;
        private int _byteIndex;

        public static int PixelToAdcX(int x) => 0x100 + x * 14;
        public static int PixelToAdcY(int y) => 0x0B0 + y * 19;

        /// <summary>Microphone level 0–4095 (2048 = silence).</summary>
        public int MicrophoneLevel { get; set; } = 0x800;

        public void SetTouch(int x, int y)
        {
            _penDown = true;
            _x = Math.Clamp(x, 0, 255);
            _y = Math.Clamp(y, 0, 191);
        }

        public void ReleaseTouch() => _penDown = false;

        public bool PenDown => _penDown;

        public byte Transfer(byte value)
        {
            byte output = _byteIndex switch
            {
                0 => (byte)(_result >> 5),
                1 => (byte)(_result << 3),
                _ => 0,
            };
            _byteIndex++;

            if ((value & 0x80) != 0)
            {
                int channel = (value >> 4) & 7;
                int conversion = channel switch
                {
                    1 => _penDown ? PixelToAdcY(_y) : 0xFFF,
                    5 => _penDown ? PixelToAdcX(_x) : 0,
                    3 => _penDown ? 0x100 : 0,
                    4 => _penDown ? 0xF00 : 0xFFF,
                    6 => MicrophoneLevel,
                    0 => 0x320,
                    7 => 0x3A0,
                    _ => 0,
                };
                if ((value & 0x08) != 0)
                    conversion >>= 4; // 8-bit mode
                _result = (ushort)conversion;
                _byteIndex = 0;
            }
            return output;
        }

        public void Deselect() { }

        public void Serialize(StateSerializer s)
        {
            s.Section("Touch");
            s.Sync(ref _x); s.Sync(ref _y); s.Sync(ref _penDown); s.Sync(ref _result); s.Sync(ref _byteIndex);
        }
    }
}
