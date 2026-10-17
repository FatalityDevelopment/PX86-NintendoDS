namespace Px86.NintendoDS.FileFormats.Checksums
{
    /// <summary>
    /// The standard CRC-32 (zlib/PNG/Ethernet: reflected polynomial
    /// 0xEDB88320, initial and final value 0xFFFFFFFF). Action Replay game IDs
    /// and cheat databases identify a ROM by the CRC-32 of its first 512 bytes.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                table[i] = crc;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }
}
