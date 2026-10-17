using System.Buffers.Binary;
using System.Text;
using Px86.NintendoDS.FileFormats.Checksums;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Spi
{
    /// <summary>
    /// The 256 KB SPI flash holding the DS firmware, Wifi calibration and user
    /// settings (nickname, birthday, language, touch-screen calibration).
    ///
    /// Commands (GBATEK "DS Firmware Serial Flash Memory"):
    ///   06h WREN, 04h WRDI, 05h RDSR, 9Fh RDID, 03h READ, 0Bh FAST READ,
    ///   0Ah PW (page write), 02h PP (page program), DBh PE, D8h SE, B9h DP, ABh RDP
    /// All take a 3-byte big-endian address where applicable.
    ///
    /// When no firmware dump is supplied, <see cref="CreateDefault"/> builds an
    /// image with a valid header, a MAC address and two user-settings copies
    /// with correct CRCs — enough for direct boot and for games that read the
    /// user settings or MAC over SPI.
    /// </summary>
    public sealed class FirmwareFlash : ISpiDevice
    {
        public const int Size = 0x40000;
        public const int UserSettingsOffset = 0x3FE00;

        private readonly byte[] _data;
        private int _command;
        private int _step;
        private uint _address;
        private bool _writeEnabled;

        public FirmwareFlash(byte[] data)
        {
            if (data.Length < Size)
                Array.Resize(ref data, Size);
            _data = data;
        }

        public byte[] Data => _data;

        /// <summary>Raised after any program/erase, so the host can persist the image.</summary>
        public event Action? Modified;

        /// <summary>The active user-settings block (the newer of the two copies).</summary>
        public ReadOnlySpan<byte> UserSettings
        {
            get
            {
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(0x20)) * 8;
                if (offset <= 0 || offset > Size - 0x200)
                    offset = UserSettingsOffset;
                int count0 = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset + 0x70));
                int count1 = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset + 0x170));
                bool second = count1 == ((count0 + 1) & 0x7F);
                return _data.AsSpan(offset + (second ? 0x100 : 0), 0x100);
            }
        }

        public byte Transfer(byte value)
        {
            if (_step == 0)
            {
                _command = value;
                _step = 1;
                switch (value)
                {
                    case 0x06: _writeEnabled = true; break;
                    case 0x04: _writeEnabled = false; break;
                }
                return 0;
            }

            switch (_command)
            {
                case 0x05: // RDSR
                    return (byte)(_writeEnabled ? 0x02 : 0x00);

                case 0x9F: // RDID — ST M45PE20
                {
                    byte[] id = { 0x20, 0x40, 0x12 };
                    byte result = _step <= 3 ? id[_step - 1] : (byte)0;
                    _step++;
                    return result;
                }

                case 0x03: // READ
                case 0x0B: // FAST READ (one dummy byte after the address)
                case 0x0A: // PW
                case 0x02: // PP
                case 0xDB: // PE
                case 0xD8: // SE
                    if (_step <= 3)
                    {
                        _address = (_address << 8) | value;
                        _step++;
                        if (_step == 4)
                            OnAddressComplete();
                        return 0;
                    }
                    return DataPhase(value);

                default:
                    return 0;
            }
        }

        private void OnAddressComplete()
        {
            _address &= Size - 1;
            if (!_writeEnabled)
                return;
            if (_command == 0xDB)
            {
                Array.Fill(_data, (byte)0xFF, (int)(_address & ~0xFFu), 0x100);
                Modified?.Invoke();
            }
            else if (_command == 0xD8)
            {
                Array.Fill(_data, (byte)0xFF, (int)(_address & ~0xFFFFu), 0x10000);
                Modified?.Invoke();
            }
        }

        private byte DataPhase(byte value)
        {
            switch (_command)
            {
                case 0x03:
                {
                    byte result = _data[_address];
                    _address = (_address + 1) & (Size - 1);
                    return result;
                }
                case 0x0B:
                    if (_step == 4) { _step++; return 0; }
                    goto case 0x03;
                case 0x0A:
                    if (_writeEnabled)
                    {
                        _data[_address] = value;
                        _address = (_address & ~0xFFu) | ((_address + 1) & 0xFF);
                    }
                    return 0;
                case 0x02:
                    if (_writeEnabled)
                    {
                        _data[_address] &= value;
                        _address = (_address & ~0xFFu) | ((_address + 1) & 0xFF);
                    }
                    return 0;
                default:
                    return 0;
            }
        }

        public void Deselect()
        {
            if (_writeEnabled && _step >= 4 && (_command == 0x0A || _command == 0x02))
                Modified?.Invoke();
            if (_command is 0x0A or 0x02 or 0xDB or 0xD8 && _step >= 4)
                _writeEnabled = false;
            _step = 0;
            _command = 0;
            _address = 0;
        }

        // ── Default image ─────────────────────────────────────────────────────

        public sealed class UserProfile
        {
            public string Nickname { get; set; } = "Px86";
            public string Message { get; set; } = "";
            public int FavoriteColor { get; set; } = 11;
            public int BirthMonth { get; set; } = 1;
            public int BirthDay { get; set; } = 1;
            /// <summary>0 Japanese, 1 English, 2 French, 3 German, 4 Italian, 5 Spanish.</summary>
            public int Language { get; set; } = 1;
        }

        /// <summary>Build a firmware image that satisfies games when the user has no dump.</summary>
        public static FirmwareFlash CreateDefault(UserProfile? profile = null)
        {
            profile ??= new UserProfile();
            var data = new byte[Size];
            Array.Fill(data, (byte)0xFF, 0, 0x200);
            Array.Clear(data, 0x200, Size - 0x200);

            // Header
            Encoding.ASCII.GetBytes("MAC").CopyTo(data, 0x08);
            data[0x0B] = 0x68;
            data[0x1D] = 0x20; // DS-Lite
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x20), UserSettingsOffset / 8);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x22), 0x7EC0);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x24), 0x7E40);

            // Wifi calibration: length, MAC address, enabled channels, CRC.
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x2C), 0x0138);
            byte[] mac = { 0x00, 0x09, 0xBF, 0x12, 0x34, 0x56 };
            mac.CopyTo(data, 0x36);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3C), 0x3FFE);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x2A), Crc16.Compute(data.AsSpan(0x2C, 0x138), 0x0000));

            // Access points: mark all three as unconfigured.
            for (int ap = 0; ap < 3; ap++)
                data[0x3FA00 + ap * 0x100 + 0xE7] = 0xFF;

            byte[] settings = BuildUserSettings(profile);
            for (int copy = 0; copy < 2; copy++)
            {
                int offset = UserSettingsOffset + copy * 0x100;
                settings.CopyTo(data, offset);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 0x70), (ushort)copy);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 0x72), Crc16.Compute(data.AsSpan(offset, 0x70)));
                Array.Fill(data, (byte)0xFF, offset + 0x74, 0x8C);
            }

            return new FirmwareFlash(data);
        }

        private static byte[] BuildUserSettings(UserProfile profile)
        {
            var s = new byte[0x70];
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x00), 5);
            s[0x02] = (byte)profile.FavoriteColor;
            s[0x03] = (byte)profile.BirthMonth;
            s[0x04] = (byte)profile.BirthDay;

            string nick = profile.Nickname.Length > 10 ? profile.Nickname[..10] : profile.Nickname;
            Encoding.Unicode.GetBytes(nick).CopyTo(s, 0x06);
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x1A), (ushort)nick.Length);

            string message = profile.Message.Length > 26 ? profile.Message[..26] : profile.Message;
            Encoding.Unicode.GetBytes(message).CopyTo(s, 0x1C);
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x50), (ushort)message.Length);

            // Touch calibration points match TouchScreen.PixelToAdcX/Y, so the conversion is exact.
            WriteCalibrationPoint(s, 0x58, 0, 0);
            WriteCalibrationPoint(s, 0x5E, 255, 191);

            ushort flags = (ushort)((profile.Language & 7) | 0xFC00);
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x64), flags);
            s[0x66] = (byte)(DateTime.Now.Year - 2000);
            Array.Fill(s, (byte)0xFF, 0x6C, 4);
            return s;
        }

        private static void WriteCalibrationPoint(byte[] s, int offset, int x, int y)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(offset), (ushort)TouchScreen.PixelToAdcX(x));
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(offset + 2), (ushort)TouchScreen.PixelToAdcY(y));
            s[offset + 4] = (byte)(x + 1);
            s[offset + 5] = (byte)(y + 1);
        }
    }
}
