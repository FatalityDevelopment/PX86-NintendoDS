namespace Px86.NintendoDS.EmulatorCore.Hardware
{
    /// <summary>
    /// Seiko S-35180 real-time clock on a bit-banged 3-wire bus (ARM7 only).
    ///
    ///   04000138h  RTC  bit 0 data, 1 /SCK, 2 CS; bits 4–6 = direction of bits 0–2 (1 = output)
    ///
    /// A transfer is: CS high, command byte, parameter bytes, CS low. Bits
    /// travel LSB first and are latched on the rising clock edge. The command
    /// byte is 0110 ccc r (LSB first), or the bit-reversed form some code uses;
    /// both are accepted.
    ///
    ///   ccc 0 status 1 (1 byte)      ccc 4 status 2 (1 byte)
    ///       2 date &amp; time (7 bytes: yy mm dd wd hh mm ss, BCD)
    ///       6 time (3 bytes)         1/5 alarms (accepted, ignored)
    ///       3 clock adjust           7 free register
    ///
    /// The clock follows the host's local time. Writes to date/time keep an
    /// offset so games that set the clock see their value.
    ///
    /// Reference: GBATEK "DS Real-Time Clock (RTC)".
    /// </summary>
    public sealed class RealTimeClock
    {
        public const uint REG_RTC = 0x138;

        private ushort _register;
        private bool _selected;
        private int _bitIndex;
        private int _byteIndex;
        private int _command = -1;
        private bool _reading;
        private byte _currentByte;
        private int _outputBit;
        private readonly byte[] _buffer = new byte[8];

        private byte _status1 = 0x02;   // 24-hour mode
        private byte _status2;
        private byte _adjust;
        private byte _free;
        private TimeSpan _offset = TimeSpan.Zero;

        public void Reset()
        {
            _register = 0;
            _selected = false;
            _command = -1;
            _status1 = 0x02;
            _status2 = 0;
        }

        /// <summary>Clock source; tests replace it with a fixed time.</summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        public ushort Read()
        {
            ushort value = _register;
            if (_selected && _reading && (_register & 0x10) == 0)
                value = (ushort)((value & ~1) | _outputBit);
            return value;
        }

        public void Write(ushort value)
        {
            bool oldClock = (_register & 0x02) != 0;
            bool select = (value & 0x04) != 0 && (value & 0x40) != 0;
            bool clock = (value & 0x02) != 0;
            int dataBit = value & 1;

            // Pins configured as input keep their previous level.
            _register = (ushort)(value & 0xFF77);

            if (!select)
            {
                if (_selected)
                    EndTransfer();
                _selected = false;
                return;
            }

            if (!_selected)
            {
                _selected = true;
                _bitIndex = 0;
                _byteIndex = 0;
                _command = -1;
                _reading = false;
                _currentByte = 0;
                return;
            }

            if (oldClock || !clock)
                return; // act on rising edges only

            if (_command < 0 || !_reading)
                _currentByte |= (byte)(dataBit << _bitIndex);
            else
                _outputBit = (_buffer[_byteIndex] >> _bitIndex) & 1; // sampled by software after this edge

            _bitIndex++;
            if (_bitIndex < 8)
                return;

            _bitIndex = 0;
            if (_command < 0)
            {
                StartCommand(_currentByte);
            }
            else if (!_reading)
            {
                if (_byteIndex < _buffer.Length)
                    _buffer[_byteIndex] = _currentByte;
                _byteIndex++;
            }
            else
            {
                _byteIndex = Math.Min(_byteIndex + 1, _buffer.Length - 1);
            }
            _currentByte = 0;
        }

        private void StartCommand(byte raw)
        {
            int command = raw;
            if ((raw & 0x0F) != 0x06)
            {
                // MSB-first command transfer: reverse the bits.
                int reversed = 0;
                for (int i = 0; i < 8; i++)
                    reversed |= ((raw >> i) & 1) << (7 - i);
                command = reversed;
            }

            _command = (command >> 4) & 7;
            _reading = (command & 0x80) != 0;
            _byteIndex = 0;
            Array.Clear(_buffer);

            if (_reading)
                FillReadBuffer();
        }

        private void FillReadBuffer()
        {
            DateTime now = Clock() + _offset;
            switch (_command)
            {
                case 0:
                    _buffer[0] = _status1;
                    _status1 &= 0x0F; // interrupt/power flags clear on read
                    break;
                case 4:
                    _buffer[0] = _status2;
                    break;
                case 2:
                    _buffer[0] = Bcd(now.Year % 100);
                    _buffer[1] = Bcd(now.Month);
                    _buffer[2] = Bcd(now.Day);
                    _buffer[3] = (byte)(((int)now.DayOfWeek + 6) % 7);
                    WriteTime(now, 4);
                    break;
                case 6:
                    WriteTime(now, 0);
                    break;
                case 3:
                    _buffer[0] = _adjust;
                    break;
                case 7:
                    _buffer[0] = _free;
                    break;
            }
        }

        private void WriteTime(DateTime now, int offset)
        {
            int hour = now.Hour;
            bool pm = hour >= 12;
            if ((_status1 & 0x02) == 0)
                hour %= 12;
            _buffer[offset] = (byte)(Bcd(hour) | (pm ? 0x40 : 0));
            _buffer[offset + 1] = Bcd(now.Minute);
            _buffer[offset + 2] = Bcd(now.Second);
        }

        private void EndTransfer()
        {
            if (_command < 0 || _reading)
                return;

            switch (_command)
            {
                case 0:
                    if (_byteIndex > 0)
                    {
                        if ((_buffer[0] & 1) != 0)
                        {
                            _offset = TimeSpan.Zero;
                            _status2 = 0;
                        }
                        _status1 = (byte)((_status1 & 0xF0) | (_buffer[0] & 0x0E));
                    }
                    break;
                case 4:
                    if (_byteIndex > 0) _status2 = _buffer[0];
                    break;
                case 3:
                    if (_byteIndex > 0) _adjust = _buffer[0];
                    break;
                case 7:
                    if (_byteIndex > 0) _free = _buffer[0];
                    break;
                case 2:
                    if (_byteIndex >= 7)
                        SetDateTime(_buffer[0], _buffer[1], _buffer[2], _buffer[4], _buffer[5], _buffer[6]);
                    break;
                case 6:
                    if (_byteIndex >= 3)
                    {
                        DateTime now = Clock() + _offset;
                        SetDateTime(Bcd(now.Year % 100), Bcd(now.Month), Bcd(now.Day), _buffer[0], _buffer[1], _buffer[2]);
                    }
                    break;
            }
        }

        private void SetDateTime(byte year, byte month, byte day, byte hour, byte minute, byte second)
        {
            try
            {
                var target = new DateTime(2000 + FromBcd(year), Math.Clamp(FromBcd(month & 0x1F), 1, 12),
                    Math.Clamp(FromBcd(day & 0x3F), 1, 28), FromBcd(hour & 0x3F) % 24, FromBcd(minute & 0x7F) % 60,
                    FromBcd(second & 0x7F) % 60);
                _offset = target - Clock();
            }
            catch (ArgumentOutOfRangeException)
            {
                // Garbage date written by the game: ignore it, as the chip would.
            }
        }

        private static byte Bcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

        private static int FromBcd(int value) => ((value >> 4) & 0xF) * 10 + (value & 0xF);
    }
}
