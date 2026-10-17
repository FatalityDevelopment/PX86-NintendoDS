using System.Buffers.Binary;

namespace Px86.NintendoDS.FileFormats.Rom
{
    /// <summary>
    /// A loaded .nds cartridge image: the header, the raw bytes, and helpers to
    /// pull out the ARM9/ARM7 boot binaries and files from the NitroFS.
    ///
    /// The ROM is kept as one byte array so the cartridge emulation can serve
    /// arbitrary 0xB7 reads from it. Images are padded up to a multiple of
    /// 0x200 so block reads never run off the end.
    /// </summary>
    public sealed class NdsRom
    {
        public NdsRom(byte[] data)
        {
            if (data.Length < NdsRomHeader.Size)
                throw new InvalidDataException("File is too small to be a Nintendo DS ROM.");

            int padded = (data.Length + 0x1FF) & ~0x1FF;
            if (padded != data.Length)
                Array.Resize(ref data, padded);

            Data = data;
            Header = new NdsRomHeader(data);

            if (Header.Arm9RomOffset + (long)Header.Arm9Size > data.Length ||
                Header.Arm7RomOffset + (long)Header.Arm7Size > data.Length)
                throw new InvalidDataException("ARM9/ARM7 binaries extend past the end of the ROM.");
        }

        public static NdsRom Load(string path) => new(File.ReadAllBytes(path));

        public byte[] Data { get; }

        public NdsRomHeader Header { get; }

        public ReadOnlySpan<byte> Arm9Binary => Data.AsSpan((int)Header.Arm9RomOffset, (int)Header.Arm9Size);

        public ReadOnlySpan<byte> Arm7Binary => Data.AsSpan((int)Header.Arm7RomOffset, (int)Header.Arm7Size);

        /// <summary>Read a byte range with wrap-around past the end, like a real ROM chip.</summary>
        public void Read(uint address, Span<byte> destination)
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] = Data[(int)((address + (uint)i) % (uint)Data.Length)];
        }

        // ── NitroFS ───────────────────────────────────────────────────────────

        /// <summary>Byte range of file <paramref name="id"/> from the FAT.</summary>
        public (uint Start, uint End) GetFileRange(int id)
        {
            int entry = (int)Header.FatOffset + id * 8;
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(entry));
            uint end = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(entry + 4));
            return (start, end);
        }

        public int FileCount => (int)(Header.FatSize / 8);

        /// <summary>Walk the FNT and return every file path with its file id.</summary>
        public IReadOnlyList<(string Path, int Id)> ListFiles()
        {
            var result = new List<(string, int)>();
            if (Header.FntSize == 0)
                return result;
            Walk(0xF000, "", result);
            return result;
        }

        private void Walk(int directoryId, string prefix, List<(string, int)> result)
        {
            int fnt = (int)Header.FntOffset;
            int entry = fnt + (directoryId & 0xFFF) * 8;
            int subTable = fnt + (int)BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(entry));
            int fileId = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(entry + 4));

            int pos = subTable;
            while (true)
            {
                byte typeLength = Data[pos++];
                if (typeLength == 0)
                    break;
                int length = typeLength & 0x7F;
                string name = System.Text.Encoding.ASCII.GetString(Data, pos, length);
                pos += length;

                if ((typeLength & 0x80) != 0)
                {
                    int subId = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(pos));
                    pos += 2;
                    Walk(subId, prefix + name + "/", result);
                }
                else
                {
                    result.Add((prefix + name, fileId++));
                }
            }
        }
    }
}
