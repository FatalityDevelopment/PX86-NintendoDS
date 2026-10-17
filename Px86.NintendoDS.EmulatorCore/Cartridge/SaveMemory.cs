using Px86.NintendoDS.EmulatorCore.Hardware.Spi;

namespace Px86.NintendoDS.EmulatorCore.Cartridge
{
    /// <summary>Cartridge backup chip types.</summary>
    public enum SaveType
    {
        Auto,
        None,
        Eeprom512,
        Eeprom8K,
        Eeprom64K,
        Eeprom128K,
        Fram32K,
        Flash256K,
        Flash512K,
        Flash1M,
        Flash8M,
    }

    /// <summary>
    /// Cartridge backup memory on the AUX SPI bus (EEPROM, FRAM or FLASH).
    ///
    ///   EEPROM/FRAM  06h WREN, 04h WRDI, 05h RDSR, 01h WRSR,
    ///                03h READ, 02h WRITE (0.5K EEPROM: 0Bh/0Ah select the upper 256 bytes)
    ///   FLASH        as the firmware flash: 03h READ, 0Ah PW, 02h PP, DBh PE, D8h SE, 9Fh RDID
    ///
    /// Address width: 0.5K EEPROM = 1 byte, 8K/64K EEPROM and FRAM = 2 bytes,
    /// 128K EEPROM and FLASH = 3 bytes.
    ///
    /// Detection: an existing save file decides the type by its size. Otherwise
    /// the first READ/WRITE is examined: Nintendo's SDK sends the command and
    /// address bytes from one loop, so counting the bytes written by the same
    /// instruction (<see cref="CallerPc"/>) gives the address width (GBATEK
    /// "DS Cartridge Backup", "Detection (in emulators)"). FLASH-only commands
    /// select FLASH directly.
    /// </summary>
    public sealed class SaveMemory : ISpiDevice
    {
        private byte[] _data = Array.Empty<byte>();
        private int _addressBytes;
        private bool _isFlash;

        private int _command;
        private int _step;
        private uint _address;
        private bool _writeEnabled;
        private byte _status;

        // Auto-detection state
        private uint _commandPc;
        private int _bytesAtCommandPc;
        private readonly List<byte> _pendingTransfer = new();

        public SaveMemory(SaveType type = SaveType.Auto)
        {
            SetType(type);
        }

        public SaveType Type { get; private set; }

        public byte[] Data => _data;

        /// <summary>PC of the instruction performing the current AUXSPIDATA write (for detection).</summary>
        public Func<uint>? CallerPc { get; set; }

        /// <summary>Raised when the contents change so the host can write the .sav file.</summary>
        public event Action? Modified;

        public static int SizeOf(SaveType type) => type switch
        {
            SaveType.Eeprom512 => 512,
            SaveType.Eeprom8K => 8 * 1024,
            SaveType.Eeprom64K => 64 * 1024,
            SaveType.Eeprom128K => 128 * 1024,
            SaveType.Fram32K => 32 * 1024,
            SaveType.Flash256K => 256 * 1024,
            SaveType.Flash512K => 512 * 1024,
            SaveType.Flash1M => 1024 * 1024,
            SaveType.Flash8M => 8 * 1024 * 1024,
            _ => 0,
        };

        public static SaveType TypeForSize(int size) => size switch
        {
            512 => SaveType.Eeprom512,
            8 * 1024 => SaveType.Eeprom8K,
            32 * 1024 => SaveType.Fram32K,
            64 * 1024 => SaveType.Eeprom64K,
            128 * 1024 => SaveType.Eeprom128K,
            256 * 1024 => SaveType.Flash256K,
            512 * 1024 => SaveType.Flash512K,
            1024 * 1024 => SaveType.Flash1M,
            8 * 1024 * 1024 => SaveType.Flash8M,
            _ => SaveType.Auto,
        };

        public void SetType(SaveType type)
        {
            Type = type;
            int size = SizeOf(type);
            var data = new byte[size];
            Array.Fill(data, (byte)0xFF);
            if (_data.Length > 0)
                Array.Copy(_data, data, Math.Min(_data.Length, size));
            _data = data;

            _isFlash = type is SaveType.Flash256K or SaveType.Flash512K or SaveType.Flash1M or SaveType.Flash8M;
            _addressBytes = type switch
            {
                SaveType.Eeprom512 => 1,
                SaveType.Eeprom8K or SaveType.Eeprom64K or SaveType.Fram32K => 2,
                SaveType.Auto or SaveType.None => 0,
                _ => 3,
            };
            _status = type == SaveType.Eeprom512 ? (byte)0xF0 : (byte)0x00;
        }

        /// <summary>Load an existing .sav file; its size selects the chip type.</summary>
        public void Load(byte[] contents)
        {
            var type = TypeForSize(contents.Length);
            if (type == SaveType.Auto)
            {
                // Unusual size (e.g. padded by another emulator): pick the next size up.
                type = contents.Length <= 512 ? SaveType.Eeprom512
                    : contents.Length <= 8192 ? SaveType.Eeprom8K
                    : contents.Length <= 65536 ? SaveType.Eeprom64K
                    : contents.Length <= 262144 ? SaveType.Flash256K
                    : contents.Length <= 524288 ? SaveType.Flash512K
                    : SaveType.Flash1M;
            }
            _data = Array.Empty<byte>();
            SetType(type);
            Array.Copy(contents, _data, Math.Min(contents.Length, _data.Length));
        }

        // ── SPI ───────────────────────────────────────────────────────────────

        public byte Transfer(byte value)
        {
            if (Type == SaveType.Auto)
                return DetectTransfer(value);
            if (Type == SaveType.None)
                return 0xFF;

            if (_step == 0)
            {
                _command = value;
                _step = 1;
                _address = 0;
                switch (value)
                {
                    case 0x06: _writeEnabled = true; break;
                    case 0x04: _writeEnabled = false; break;
                }
                return 0xFF;
            }

            switch (_command)
            {
                case 0x05: // RDSR
                    return (byte)(_status | (_writeEnabled ? 0x02 : 0x00));
                case 0x01: // WRSR
                    if (_step == 1 && !_isFlash)
                        _status = (byte)((_status & 0xF0) | (value & 0x0C));
                    _step++;
                    return 0xFF;
                case 0x9F: // RDID
                    if (!_isFlash) return 0xFF;
                    {
                        byte[] id = { 0x20, 0x40, (byte)(Type == SaveType.Flash256K ? 0x12 : Type == SaveType.Flash512K ? 0x13 : 0x14) };
                        byte result = _step <= 3 ? id[_step - 1] : (byte)0;
                        _step++;
                        return result;
                    }
                case 0x03:
                case 0x0B:
                case 0x02:
                case 0x0A:
                case 0xDB:
                case 0xD8:
                    return AddressedTransfer(value);
                default:
                    return 0xFF;
            }
        }

        private byte AddressedTransfer(byte value)
        {
            if (_step <= _addressBytes)
            {
                _address = (_address << 8) | value;
                _step++;
                if (_step > _addressBytes)
                    OnAddressComplete();
                return 0xFF;
            }

            uint mask = (uint)_data.Length - 1;
            switch (_command)
            {
                case 0x03:
                case 0x0B:
                {
                    byte result = _data[_address & mask];
                    _address++;
                    return result;
                }
                case 0x02:
                    if (_writeEnabled)
                    {
                        if (_isFlash) _data[_address & mask] &= value;
                        else _data[_address & mask] = value;
                        _address++;
                    }
                    return 0xFF;
                case 0x0A:
                    if (_writeEnabled)
                    {
                        _data[_address & mask] = value;
                        _address++;
                    }
                    return 0xFF;
                default:
                    return 0xFF;
            }
        }

        private void OnAddressComplete()
        {
            // 0.5K EEPROM: bit 3 of the command is address bit 8.
            if (Type == SaveType.Eeprom512 && (_command == 0x0B || _command == 0x0A))
                _address |= 0x100;
            if (Type == SaveType.Eeprom512 && _command == 0x0A)
                _command = 0x02; // WRHI behaves as write

            if (!_isFlash || !_writeEnabled)
                return;

            uint mask = (uint)_data.Length - 1;
            if (_command == 0xDB)
                Array.Fill(_data, (byte)0xFF, (int)(_address & mask & ~0xFFu), 0x100);
            else if (_command == 0xD8)
                Array.Fill(_data, (byte)0xFF, (int)(_address & mask & ~0xFFFFu), Math.Min(0x10000, _data.Length));
        }

        public void Deselect()
        {
            if (Type == SaveType.Auto)
            {
                FinishDetection();
                return;
            }

            bool wrote = _step > _addressBytes && _command is 0x02 or 0x0A or 0xDB or 0xD8 && _writeEnabled;
            if (wrote)
            {
                _writeEnabled = false;
                Modified?.Invoke();
            }
            _step = 0;
            _command = 0;
        }

        // ── Auto-detection ────────────────────────────────────────────────────

        private byte DetectTransfer(byte value)
        {
            uint pc = CallerPc?.Invoke() ?? 0;
            if (_pendingTransfer.Count == 0)
            {
                _commandPc = pc;
                _bytesAtCommandPc = 0;
            }
            if (pc == _commandPc)
                _bytesAtCommandPc++;
            _pendingTransfer.Add(value);

            byte command = _pendingTransfer[0];
            if (command is 0x0A or 0xDB or 0xD8 or 0x9F)
            {
                Commit(SaveType.Flash512K);
                return 0xFF;
            }
            if (command == 0x05)
                return 0x00; // RDSR: "EEPROM/FRAM/FLASH present"
            return 0xFF;
        }

        private void FinishDetection()
        {
            if (_pendingTransfer.Count == 0)
                return;
            byte command = _pendingTransfer[0];
            if (command is 0x03 or 0x02 or 0x0B)
            {
                int addressBytes = _bytesAtCommandPc - 1;
                if (CallerPc == null || addressBytes <= 0 || addressBytes > 3)
                    addressBytes = Math.Clamp(_pendingTransfer.Count - 1, 1, 3);
                Commit(addressBytes switch
                {
                    1 => SaveType.Eeprom512,
                    2 => SaveType.Eeprom64K,
                    _ => SaveType.Flash512K,
                });
            }
            else
            {
                _pendingTransfer.Clear();
            }
        }

        /// <summary>Lock in a detected type and replay the bytes seen so far.</summary>
        private void Commit(SaveType type)
        {
            var bytes = _pendingTransfer.ToArray();
            _pendingTransfer.Clear();
            SetType(type);
            DetectedType?.Invoke(type);
            _step = 0;
            foreach (byte b in bytes)
                Transfer(b);
        }

        /// <summary>Raised once auto-detection settles on a chip type.</summary>
        public event Action<SaveType>? DetectedType;
    }
}
