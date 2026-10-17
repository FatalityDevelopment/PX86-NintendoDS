using System.Buffers.Binary;
using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Spi
{
    /// <summary>
    /// TSC2046 touch-screen controller (also the microphone ADC).
    ///
    /// Control byte: bit 7 start, bits 6–4 channel, bit 3 mode (0 = 12-bit, 1 = 8-bit),
    /// bits 1–0 power-down mode. The conversion result is shifted out MSB first
    /// after one dummy bit, so a 12-bit value V arrives as two bytes
    /// (V &gt;&gt; 5) and (V &lt;&lt; 3), an 8-bit one as (V &gt;&gt; 1) and (V &lt;&lt; 7).
    ///
    /// Microphone (channel 6): the power manager's amplifier must be on (register 2);
    /// its gain (register 3: 20×…160×) scales the input around the 800h midpoint.
    ///
    ///   Channel 1 = Y, 5 = X (12-bit ADC), 3/4 = Z1/Z2 pressure, 6 = microphone, 0/7 = temperature.
    ///   Released pen: X = 0, Y = 0xFFF.
    ///
    /// Games turn ADC values into pixels with the two calibration points in
    /// the firmware user settings (GBATEK "DS Firmware User Settings" 58h–63h):
    ///   scr.x = (adc.x − adc.x1) × (scr.x2 − scr.x1) / (adc.x2 − adc.x1) + (scr.x1 − 1)
    /// so the emulated stylus inverts that formula with the calibration of
    /// whichever firmware is loaded (a real dump or the generated one), and a
    /// tap lands exactly where the mouse clicked.
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

        /// <summary>Default mapping, used by the generated firmware: ADC = 0x100 + pixel × 14 (X), 0x0B0 + pixel × 19 (Y).</summary>
        public static int PixelToAdcX(int x) => 0x100 + x * 14;
        public static int PixelToAdcY(int y) => 0x0B0 + y * 19;

        private Calibration _calibration = Calibration.Default;

        /// <summary>One axis' two calibration points (adc1 at pixel scr1−1, adc2 at pixel scr2−1).</summary>
        public readonly record struct Axis(int Adc1, int Scr1, int Adc2, int Scr2)
        {
            public bool IsValid => Scr2 > Scr1 && Adc2 != Adc1;

            /// <summary>Pixel → ADC: the inverse of the conversion games do.</summary>
            public int ToAdc(int pixel) =>
                Math.Clamp(Adc1 + (int)Math.Round((pixel - (Scr1 - 1)) * (double)(Adc2 - Adc1) / (Scr2 - Scr1)), 0, 0xFFF);

            /// <summary>ADC → pixel, exactly as GBATEK gives it (integer division).</summary>
            public int ToPixel(int adc) => (adc - Adc1) * (Scr2 - Scr1) / (Adc2 - Adc1) + (Scr1 - 1);
        }

        public readonly record struct Calibration(Axis X, Axis Y)
        {
            public static readonly Calibration Default = new(
                new Axis(PixelToAdcX(0), 1, PixelToAdcX(255), 256),
                new Axis(PixelToAdcY(0), 1, PixelToAdcY(191), 192));

            /// <summary>Read the calibration from firmware user settings (falls back to <see cref="Default"/> if it's unusable).</summary>
            public static Calibration FromUserSettings(ReadOnlySpan<byte> settings)
            {
                if (settings.Length < 0x64)
                    return Default;
                var x = new Axis(BinaryPrimitives.ReadUInt16LittleEndian(settings[0x58..]) & 0xFFF, settings[0x5C], BinaryPrimitives.ReadUInt16LittleEndian(settings[0x5E..]) & 0xFFF, settings[0x62]);
                var y = new Axis(BinaryPrimitives.ReadUInt16LittleEndian(settings[0x5A..]) & 0xFFF, settings[0x5D], BinaryPrimitives.ReadUInt16LittleEndian(settings[0x60..]) & 0xFFF, settings[0x63]);
                return x.IsValid && y.IsValid ? new Calibration(x, y) : Default;
            }
        }

        public Calibration CurrentCalibration => _calibration;

        /// <summary>Use the calibration stored in these firmware user settings.</summary>
        public void Calibrate(ReadOnlySpan<byte> userSettings) => _calibration = Calibration.FromUserSettings(userSettings);

        /// <summary>What the microphone hears; null = silence.</summary>
        public IMicrophoneSource? Microphone { get; set; }

        /// <summary>Emulated time, for sampling the microphone.</summary>
        public Func<long> Clock { get; set; } = () => 0;

        /// <summary>Microphone amplifier state from the power manager: (on, gain 0–3).</summary>
        public Func<(bool On, int Gain)> Amplifier { get; set; } = () => (true, 0);

        private bool _eightBit;

        /// <summary>The 12-bit microphone reading right now (800h = silence).</summary>
        public int MicrophoneLevel()
        {
            var (on, gain) = Amplifier();
            if (!on || Microphone == null)
                return 0x800;
            int sample = Microphone.Sample(Clock());
            return Math.Clamp(0x800 + (sample << gain) / 32, 0, 0xFFF);
        }

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
            byte output = (_byteIndex, _eightBit) switch
            {
                (0, false) => (byte)(_result >> 5),
                (1, false) => (byte)(_result << 3),
                (0, true) => (byte)(_result >> 1),
                (1, true) => (byte)(_result << 7),
                _ => 0,
            };
            _byteIndex++;

            if ((value & 0x80) != 0)
            {
                int channel = (value >> 4) & 7;
                int conversion = channel switch
                {
                    1 => _penDown ? _calibration.Y.ToAdc(_y) : 0xFFF,
                    5 => _penDown ? _calibration.X.ToAdc(_x) : 0,
                    3 => _penDown ? 0x100 : 0,
                    4 => _penDown ? 0xF00 : 0xFFF,
                    6 => MicrophoneLevel(),
                    0 => 0x320,
                    7 => 0x3A0,
                    _ => 0,
                };
                _eightBit = (value & 0x08) != 0;
                if (_eightBit)
                    conversion >>= 4;
                _result = (ushort)conversion;
                _byteIndex = 0;
            }
            return output;
        }

        public void Deselect() { }

        public void Serialize(StateSerializer s)
        {
            s.Section("Touch");
            s.Sync(ref _x); s.Sync(ref _y); s.Sync(ref _penDown); s.Sync(ref _result); s.Sync(ref _byteIndex); s.Sync(ref _eightBit);
        }
    }
}
