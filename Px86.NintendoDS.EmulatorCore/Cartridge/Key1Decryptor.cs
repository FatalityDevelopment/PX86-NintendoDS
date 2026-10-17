using System.Buffers.Binary;

namespace Px86.NintendoDS.EmulatorCore.Cartridge
{
    /// <summary>
    /// KEY1 (Blowfish) cartridge encryption, needed only when booting through
    /// a real BIOS + firmware: the BIOS sends KEY1-encrypted commands to read
    /// the secure area (ROM 4000h–7FFFh), and expects the first 2 KB of it to
    /// arrive encrypted.
    ///
    /// The 1048h-byte key table comes from the user's ARM7 BIOS dump at
    /// offset 30h, and is keyed by the game code.
    ///
    ///   Commands: level-2 keys (decrypt each 8-byte command, MSB first)
    ///   Secure area: dumps usually store it decrypted (first word E7FFDEFFh);
    ///   it is re-encrypted once here — "encryObj" tag, level 3 over 2 KB,
    ///   then level 2 over the first 8 bytes — the inverse of what the BIOS does.
    ///
    /// Reference: GBATEK "DS Encryption by Gamecode/Idcode (KEY1)".
    /// </summary>
    public sealed class Key1Decryptor
    {
        public const int KeyTableOffset = 0x30;
        public const int KeyTableSize = 0x1048;

        private readonly uint[] _biosKeys = new uint[KeyTableSize / 4];
        private readonly uint[] _level2 = new uint[KeyTableSize / 4];
        private readonly uint[] _level3 = new uint[KeyTableSize / 4];
        private byte[]? _encryptedSecureArea;

        public Key1Decryptor(ReadOnlySpan<byte> arm7Bios, uint gameCode, ReadOnlySpan<byte> rom)
        {
            for (int i = 0; i < _biosKeys.Length; i++)
                _biosKeys[i] = BinaryPrimitives.ReadUInt32LittleEndian(arm7Bios[(KeyTableOffset + i * 4)..]);

            InitKeycode(_level2, gameCode, 2, 8);
            InitKeycode(_level3, gameCode, 3, 8);

            if (rom.Length >= 0x8000)
                _encryptedSecureArea = BuildEncryptedSecureArea(rom);
        }

        // ── Blowfish core ─────────────────────────────────────────────────────

        private static void Encrypt(uint[] key, ref uint lo, ref uint hi)
        {
            uint y = lo, x = hi;
            for (int i = 0; i <= 0x0F; i++)
            {
                uint z = key[i] ^ x;
                x = key[0x012 + (int)(z >> 24)];
                x = key[0x112 + (int)((z >> 16) & 0xFF)] + x;
                x = key[0x212 + (int)((z >> 8) & 0xFF)] ^ x;
                x = key[0x312 + (int)(z & 0xFF)] + x;
                x = y ^ x;
                y = z;
            }
            lo = x ^ key[0x10];
            hi = y ^ key[0x11];
        }

        private static void Decrypt(uint[] key, ref uint lo, ref uint hi)
        {
            uint y = lo, x = hi;
            for (int i = 0x11; i >= 0x02; i--)
            {
                uint z = key[i] ^ x;
                x = key[0x012 + (int)(z >> 24)];
                x = key[0x112 + (int)((z >> 16) & 0xFF)] + x;
                x = key[0x212 + (int)((z >> 8) & 0xFF)] ^ x;
                x = key[0x312 + (int)(z & 0xFF)] + x;
                x = y ^ x;
                y = z;
            }
            lo = x ^ key[0x01];
            hi = y ^ key[0x00];
        }

        private void InitKeycode(uint[] key, uint idCode, int level, int modulo)
        {
            Array.Copy(_biosKeys, key, key.Length);
            var keycode = new uint[] { idCode, idCode / 2, idCode * 2 };
            if (level >= 1) ApplyKeycode(key, keycode, modulo);
            if (level >= 2) ApplyKeycode(key, keycode, modulo);
            keycode[1] *= 2;
            keycode[2] /= 2;
            if (level >= 3) ApplyKeycode(key, keycode, modulo);
        }

        private static void ApplyKeycode(uint[] key, uint[] keycode, int modulo)
        {
            Encrypt(key, ref keycode[1], ref keycode[2]);
            Encrypt(key, ref keycode[0], ref keycode[1]);

            for (int i = 0; i <= 0x44; i += 4)
                key[i / 4] ^= BinaryPrimitives.ReverseEndianness(keycode[(i % modulo) / 4]);

            uint lo = 0, hi = 0;
            for (int i = 0; i <= 0x1040; i += 8)
            {
                Encrypt(key, ref lo, ref hi);
                key[i / 4] = hi;
                key[i / 4 + 1] = lo;
            }
        }

        // ── Cartridge use ─────────────────────────────────────────────────────

        /// <summary>Decrypt an 8-byte command register value (command[0] is the MSB).</summary>
        public byte[] DecryptCommand(byte[] command)
        {
            uint lo = (uint)(command[7] | command[6] << 8 | command[5] << 16 | command[4] << 24);
            uint hi = (uint)(command[3] | command[2] << 8 | command[1] << 16 | command[0] << 24);
            Decrypt(_level2, ref lo, ref hi);
            return new[]
            {
                (byte)(hi >> 24), (byte)(hi >> 16), (byte)(hi >> 8), (byte)hi,
                (byte)(lo >> 24), (byte)(lo >> 16), (byte)(lo >> 8), (byte)lo,
            };
        }

        /// <summary>Replace the first 2 KB of a secure-area block read with its encrypted form.</summary>
        public void EncryptSecureArea(Span<byte> data, uint blockAddress)
        {
            if (_encryptedSecureArea == null || blockAddress != 0x4000)
                return;
            int length = Math.Min(data.Length, 0x800);
            _encryptedSecureArea.AsSpan(0, length).CopyTo(data);
        }

        private byte[] BuildEncryptedSecureArea(ReadOnlySpan<byte> rom)
        {
            var area = rom.Slice(0x4000, 0x800).ToArray();
            bool decrypted = BinaryPrimitives.ReadUInt32LittleEndian(area) == 0xE7FFDEFFu &&
                             BinaryPrimitives.ReadUInt32LittleEndian(area.AsSpan(4)) == 0xE7FFDEFFu;
            if (!decrypted)
                return area; // already encrypted in the dump

            "encryObj"u8.CopyTo(area);
            for (int i = 0; i < 0x800; i += 8)
            {
                uint lo = BinaryPrimitives.ReadUInt32LittleEndian(area.AsSpan(i));
                uint hi = BinaryPrimitives.ReadUInt32LittleEndian(area.AsSpan(i + 4));
                Encrypt(_level3, ref lo, ref hi);
                BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(i), lo);
                BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(i + 4), hi);
            }
            {
                uint lo = BinaryPrimitives.ReadUInt32LittleEndian(area);
                uint hi = BinaryPrimitives.ReadUInt32LittleEndian(area.AsSpan(4));
                Encrypt(_level2, ref lo, ref hi);
                BinaryPrimitives.WriteUInt32LittleEndian(area, lo);
                BinaryPrimitives.WriteUInt32LittleEndian(area.AsSpan(4), hi);
            }
            return area;
        }
    }
}
