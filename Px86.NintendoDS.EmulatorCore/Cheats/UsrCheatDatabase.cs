using System.Buffers.Binary;
using System.Text;

namespace Px86.NintendoDS.EmulatorCore.Cheats
{
    /// <summary>
    /// Reads cheat databases in the R4 "usrcheat.dat" format (also used by
    /// DeSmuME and many flash carts).
    ///
    ///   000h  "R4 CheatCode"                  010h  database description (NUL-terminated)
    ///   100h  index: { char code[4]; u32 crc; u64 offset } per game, sorted, ended by offset 0
    ///         crc = NOT CRC-32 of the ROM's first 512 bytes
    ///   game  title (NUL), padded to 4 → u32 item count, 32 bytes of master-code settings,
    ///         then the items, each starting on a 4-byte boundary:
    ///           u32 header: bits 28–31 = 1 → folder, bits 0–23 = number of items in it
    ///                                  otherwise a cheat, bits 0–23 = its size in words − 1,
    ///                                  bit 24 = enabled by default
    ///           name (NUL), note (NUL), padded to 4
    ///           cheat only: u32 word count, then the Action Replay words
    ///
    /// Some copies are scrambled with the R4 cipher: every 512-byte block is
    /// XORed with a byte stream whose 16-bit state starts at (block index ^ 484Ah)
    /// and is stirred by each ciphertext byte. If the header doesn't read
    /// "R4 CheatCode", the file is descrambled first.
    /// </summary>
    public static class UsrCheatDatabase
    {
        public const string Magic = "R4 CheatCode";
        private const int IndexOffset = 0x100;

        /// <summary>A database game entry.</summary>
        public sealed record Game(string GameCode, uint Crc, string Title, IReadOnlyList<Cheat> Cheats);

        /// <summary>The database's ROM key: NOT CRC-32 of the first 512 bytes.</summary>
        public static uint KeyFor(ReadOnlySpan<byte> rom) =>
            ~FileFormats.Checksums.Crc32.Compute(rom[..Math.Min(512, rom.Length)]);

        /// <summary>Find a game's cheats (by game code and header CRC, or code only if no CRC matches). Null if absent.</summary>
        public static Game? Find(byte[] file, string gameCode, uint crc)
        {
            var data = Descramble(file);
            Game? codeOnly = null;
            foreach (var (code, entryCrc, offset, end) in Index(data))
            {
                if (code != gameCode)
                    continue;
                if (entryCrc == crc)
                    return ReadGame(data, code, entryCrc, offset, end);
                codeOnly ??= ReadGame(data, code, entryCrc, offset, end);
            }
            return codeOnly;
        }

        /// <summary>Every game in the database (for browsing).</summary>
        public static IEnumerable<(string GameCode, uint Crc, string Title)> List(byte[] file)
        {
            var data = Descramble(file);
            foreach (var (code, crc, offset, _) in Index(data))
                yield return (code, crc, ReadString(data, (int)offset, out _));
        }

        /// <summary>The database description from the header.</summary>
        public static string Description(byte[] file) => ReadString(Descramble(file), 0x10, out _);

        // ── Layout ────────────────────────────────────────────────────────────

        private static IEnumerable<(string Code, uint Crc, long Offset, long End)> Index(byte[] data)
        {
            for (int pos = IndexOffset; pos + 32 <= data.Length; pos += 16)
            {
                long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 8));
                if (offset == 0)
                    yield break;
                long next = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 24));
                long end = next > offset ? next : data.Length;
                if (offset >= data.Length)
                    yield break;
                yield return (Encoding.ASCII.GetString(data, pos, 4), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4)), offset, Math.Min(end, data.Length));
            }
        }

        private static Game ReadGame(byte[] data, string code, uint crc, long start, long end)
        {
            int pos = (int)start;
            string title = ReadString(data, pos, out int afterTitle);
            pos = Align4(afterTitle);
            uint itemCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) & 0x0FFFFFFF;
            pos += 4 + 32;

            var cheats = new List<Cheat>();
            string? folder = null;
            int folderLeft = 0;
            for (uint item = 0; item < itemCount && pos + 4 <= end; item++)
            {
                int itemStart = pos;
                uint header = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
                string name = ReadString(data, pos + 4, out int afterName);
                string note = ReadString(data, afterName, out int afterNote);
                int body = Align4(afterNote);

                if ((header & 0xF0000000) == 0x10000000)
                {
                    folder = name;
                    folderLeft = (int)(header & 0x00FFFFFF);
                    pos = body;
                    continue;
                }

                int wordCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body));
                var words = new uint[Math.Max(0, Math.Min(wordCount, (int)((end - body - 4) / 4)))];
                for (int w = 0; w < words.Length; w++)
                    words[w] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 4 + w * 4));
                cheats.Add(new Cheat
                {
                    Name = folder != null ? $"{folder}: {name}" : name,
                    Note = note,
                    Enabled = false, // never switch database cheats on behind the user's back
                    Type = CheatType.ActionReplay,
                    Code = Cheat.FormatWords(words),
                });

                pos = itemStart + (int)((header & 0x00FFFFFF) + 1) * 4;
                if (folder != null && --folderLeft <= 0)
                    folder = null;
            }
            return new Game(code, crc, title, cheats);
        }

        private static int Align4(int pos) => (pos + 3) & ~3;

        private static string ReadString(byte[] data, int pos, out int after)
        {
            int end = pos;
            while (end < data.Length && data[end] != 0)
                end++;
            after = Math.Min(end + 1, data.Length);
            if (pos >= data.Length)
                return "";
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data, pos, end - pos);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(data, pos, end - pos); // older databases use legacy code pages
            }
        }

        // ── R4 cipher ─────────────────────────────────────────────────────────

        private static byte[] Descramble(byte[] file)
        {
            if (file.Length >= Magic.Length && Encoding.ASCII.GetString(file, 0, Magic.Length) == Magic)
                return file;
            var copy = (byte[])file.Clone();
            Transform(copy, decrypt: true);
            if (Encoding.ASCII.GetString(copy, 0, Math.Min(Magic.Length, copy.Length)) != Magic)
                throw new InvalidDataException("Not an R4 usrcheat.dat cheat database.");
            return copy;
        }

        /// <summary>Apply the R4 cipher in place (block by block). Encrypting and decrypting differ only in which byte stirs the state.</summary>
        public static void Transform(byte[] data, bool decrypt)
        {
            for (int block = 0; block * 512 < data.Length; block++)
            {
                ushort state = (ushort)(block ^ 0x484A);
                int end = Math.Min(data.Length, block * 512 + 512);
                for (int i = block * 512; i < end; i++)
                {
                    byte mask = MaskOf(state);
                    byte cipher = decrypt ? data[i] : (byte)(data[i] ^ mask);
                    data[i] ^= mask;
                    state = Stir(state, cipher);
                }
            }
        }

        private static bool Bit(uint v, int n) => ((v >> n) & 1) != 0;

        /// <summary>The XOR byte: eight chosen state bits.</summary>
        private static byte MaskOf(ushort s)
        {
            int m = 0;
            int[] from = { 0, 1, 6, 7, 9, 11, 12, 14 }; // state bit for result bits 0..7
            for (int bit = 0; bit < 8; bit++)
                if (Bit(s, from[bit])) m |= 1 << bit;
            return (byte)m;
        }

        /// <summary>Next state from the current one and the ciphertext byte.</summary>
        private static ushort Stir(ushort state, byte cipher)
        {
            uint k = (uint)(((cipher << 8) ^ state) & 0xFFFF) << 16;
            uint x = k;
            for (int j = 1; j < 32; j++)
                x ^= k >> j;

            int s = 0;
            void Set(int bit, bool on) { if (on) s |= 1 << bit; }
            Set(15, Bit(x, 23));
            for (int bit = 14; bit >= 10; bit--)
                Set(bit, Bit(k, bit + 8));                  // bits 14..10 ← k 22..18
            Set(9, Bit(k, 17) != Bit(x, 31));
            Set(8, Bit(k, 16) != Bit(x, 30));
            for (int bit = 7; bit >= 2; bit--)
                Set(bit, Bit(k, bit + 23) != Bit(k, bit + 22)); // k 30^29 … 25^24
            Set(1, Bit(k, 25) != Bit(x, 26));
            Set(0, Bit(k, 24) != Bit(x, 25));
            return (ushort)s;
        }
    }
}
