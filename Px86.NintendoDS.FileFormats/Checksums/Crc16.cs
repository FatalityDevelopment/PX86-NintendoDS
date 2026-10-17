namespace Px86.NintendoDS.FileFormats.Checksums
{
    /// <summary>
    /// The CRC-16 used throughout the DS (cartridge header, secure area,
    /// firmware user settings, BIOS GetCRC16): reflected polynomial 0xA001
    /// (the BIOS's 8-entry table C0C1h, C181h, … expands to the same thing),
    /// usually started at 0xFFFF.
    ///
    /// Reference: GBATEK "BIOS Misc Functions" (GetCRC16).
    /// </summary>
    public static class Crc16
    {
        private static readonly ushort[] Table = BuildTable();

        private static ushort[] BuildTable()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int crc = i;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
                table[i] = (ushort)crc;
            }
            return table;
        }

        public static ushort Compute(ReadOnlySpan<byte> data, ushort initial = 0xFFFF)
        {
            int crc = initial;
            foreach (byte b in data)
                crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
            return (ushort)crc;
        }
    }
}
