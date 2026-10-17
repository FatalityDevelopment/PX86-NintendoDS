using Px86.NintendoDS.Common.Core;
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

        /// <summary>MAC address of the generated firmware (Nintendo's 00:09:BF prefix).</summary>
        public static readonly byte[] DefaultMac = { 0x00, 0x09, 0xBF, 0x12, 0x34, 0x56 };

        /// <summary>The console's MAC address (firmware[036h]).</summary>
        public byte[] MacAddress => _data.AsSpan(0x36, 6).ToArray();

        /// <summary>
        /// Give this console another MAC address (in memory only). Two emulator
        /// windows started from the same firmware would otherwise share one, and
        /// filter out each other's frames in local wireless play.
        /// </summary>
        public void SetMacAddress(ReadOnlySpan<byte> mac)
        {
            mac[..6].CopyTo(_data.AsSpan(0x36));
            UpdateWifiCrc(_data);
        }

        private static void UpdateWifiCrc(byte[] data)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x2C));
            if (length is <= 0 or > 0x1D0)
                length = 0x138;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x2A), Crc16.Compute(data.AsSpan(0x2C, length), 0x0000));
        }

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

        public void Serialize(StateSerializer s)
        {
            s.Section("Firmware");
            s.Sync(_data);
            s.Sync(ref _command); s.Sync(ref _step); s.Sync(ref _address); s.Sync(ref _writeEnabled);
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

            // Wifi calibration (GBATEK "DS Firmware Wifi Calibration Data"), DS-Lite style: RF type 3.
            data[0x2E] = 0x00;
            data[0x2F] = 0x05;                           // version: v6 (DS-Lite)
            data.AsSpan(0x30, 6).Fill(0xFF);
            data[0x35] = 0x00;
            DefaultMac.CopyTo(data, 0x36);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3C), 0x3FFE); // channels 1–13
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x3E), 0xFFFF);
            data[0x40] = 0x03;                           // RF chip type 3 (MM3218)
            data[0x41] = 0x94;                           // 20-bit RF transfers, flag set → W_RF_CNT = 0114h
            data[0x42] = 0x29;                           // 29h initial RF values at 0CEh
            data[0x43] = 0x02;                           // two RF indices per channel
            ushort[] config =
            {
                0x0002, 0x0017, 0x0026, 0x1818, 0x0048, 0x4840, 0x0058, 0x0042, // W_CONFIG_146h…144h
                0x0140, 0x8064, 0xE0E0, 0x2443, 0x0003, 0x0032, 0x01F4, 0x0101, // 130h…150h, W_POWER_TX
            };
            for (int i = 0; i < config.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x44 + i * 2), config[i]);
            data.AsSpan(0x64, 0x69).Clear();             // BB[0..68h]
            data[0x64 + 0x01] = 0x9E;
            data[0x64 + 0x1E] = 0xBB;
            data[0x64 + 0x35] = 0x1F;
            data[0xCD] = 0x00;
            data.AsSpan(0xCE, 0x29).Clear();             // RF[0..28h]
            data[0xF7] = 0x02;                           // two BB indices per channel: 1Eh and 35h
            data[0xF8] = 0x1E; data.AsSpan(0xF9, 14).Fill(0xBB);
            data[0x107] = 0x35; data.AsSpan(0x108, 14).Fill(0x1F);
            data[0x116] = 0x00; data.AsSpan(0x117, 14).Clear();
            data[0x125] = 0x01; data.AsSpan(0x126, 14).Clear();
            data[0x162] = 0x1A;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x2C), 0x0138);
            UpdateWifiCrc(data);

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

            // Calibration points inset from the corners like a real console's, on the
            // TouchScreen.PixelToAdcX/Y line. Pixel positions are stored 1-based.
            WriteCalibrationPoint(s, 0x58, 31, 31);
            WriteCalibrationPoint(s, 0x5E, 223, 159);

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
