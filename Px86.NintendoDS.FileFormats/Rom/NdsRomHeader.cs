using System.Buffers.Binary;
using System.Text;
using Px86.NintendoDS.FileFormats.Checksums;

namespace Px86.NintendoDS.FileFormats.Rom
{
    /// <summary>
    /// The 0x200-byte header at the start of every .nds image (the firmware
    /// copies it to main RAM at 0x027FFE00 on boot).
    ///
    ///   000h  12  Game title          020h  ARM9 rom offset    030h  ARM7 rom offset
    ///   00Ch   4  Game code           024h  ARM9 entry         034h  ARM7 entry
    ///   010h   2  Maker code          028h  ARM9 RAM address   038h  ARM7 RAM address
    ///   012h   1  Unit code           02Ch  ARM9 size          03Ch  ARM7 size
    ///   014h   1  Capacity (128K &lt;&lt; n) 040h/044h FNT offset/size, 048h/04Ch FAT offset/size
    ///   050h–05Fh overlay tables      060h/064h ROMCTRL settings (normal/KEY1)
    ///   068h   Icon/title offset      06Ch Secure-area CRC     080h total used ROM size
    ///   0C0h   Nintendo logo          15Ch logo CRC (CF56h)    15Eh header CRC of 000h–15Dh
    ///
    /// Reference: GBATEK "DS Cartridge Header".
    /// </summary>
    public sealed class NdsRomHeader
    {
        public const int Size = 0x200;
        public const ushort LogoChecksum = 0xCF56;

        private readonly byte[] _raw;

        public NdsRomHeader(ReadOnlySpan<byte> data)
        {
            if (data.Length < Size)
                throw new InvalidDataException($"NDS header needs {Size} bytes, got {data.Length}.");
            _raw = data[..Size].ToArray();
        }

        public ReadOnlySpan<byte> Raw => _raw;

        private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_raw.AsSpan(offset));
        private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_raw.AsSpan(offset));
        private string Ascii(int offset, int length) => Encoding.ASCII.GetString(_raw, offset, length).TrimEnd('\0', ' ');

        public string Title => Ascii(0x000, 12);
        public string GameCode => Ascii(0x00C, 4);
        public string MakerCode => Ascii(0x010, 2);
        public byte UnitCode => _raw[0x012];
        public byte EncryptionSeed => _raw[0x013];
        public long Capacity => 0x20000L << _raw[0x014];
        public byte RomVersion => _raw[0x01E];

        public uint Arm9RomOffset => U32(0x020);
        public uint Arm9EntryAddress => U32(0x024);
        public uint Arm9RamAddress => U32(0x028);
        public uint Arm9Size => U32(0x02C);
        public uint Arm7RomOffset => U32(0x030);
        public uint Arm7EntryAddress => U32(0x034);
        public uint Arm7RamAddress => U32(0x038);
        public uint Arm7Size => U32(0x03C);

        public uint FntOffset => U32(0x040);
        public uint FntSize => U32(0x044);
        public uint FatOffset => U32(0x048);
        public uint FatSize => U32(0x04C);
        public uint Arm9OverlayOffset => U32(0x050);
        public uint Arm9OverlaySize => U32(0x054);
        public uint Arm7OverlayOffset => U32(0x058);
        public uint Arm7OverlaySize => U32(0x05C);

        public uint NormalCardControl => U32(0x060);
        public uint Key1CardControl => U32(0x064);
        public uint IconTitleOffset => U32(0x068);
        public ushort SecureAreaCrc => U16(0x06C);
        public ushort SecureAreaDelay => U16(0x06E);
        public uint TotalUsedRomSize => U32(0x080);
        public uint HeaderSize => U32(0x084);
        public ushort NintendoLogoCrc => U16(0x15C);
        public ushort HeaderCrc => U16(0x15E);

        /// <summary>CRC16 of bytes 000h–15Dh, as stored at 15Eh.</summary>
        public ushort ComputeHeaderCrc() => Crc16.Compute(_raw.AsSpan(0, 0x15E));

        public bool IsHeaderCrcValid => ComputeHeaderCrc() == HeaderCrc;

        /// <summary>Homebrew built with devkitPro leaves the game code as "####" or zero.</summary>
        public bool IsHomebrew => GameCode.Length == 0 || GameCode == "####" || Arm9RomOffset < 0x4000;

        public override string ToString() => $"{Title} [{GameCode}] rev {RomVersion}";
    }
}
