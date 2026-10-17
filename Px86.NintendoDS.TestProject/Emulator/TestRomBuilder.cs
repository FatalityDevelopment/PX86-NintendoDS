using System.Buffers.Binary;
using System.Text;
using Px86.NintendoDS.FileFormats.Checksums;
using Px86.NintendoDS.FileFormats.Rom;

namespace Px86.NintendoDS.TestProject.Emulator
{
    /// <summary>
    /// Builds minimal .nds images in memory: header + ARM9 binary at ROM 4000h
    /// (loaded to 02000000h) + ARM7 binary at ROM 8000h (loaded to 02380000h).
    /// </summary>
    internal static class TestRomBuilder
    {
        public const uint Arm9Address = 0x02000000;
        public const uint Arm7Address = 0x02380000;

        public static readonly uint[] Arm7IdleLoop = { 0xEAFFFFFEu }; // b .

        public static NdsRom Build(uint[] arm9, uint[]? arm7 = null, string gameCode = "TEST")
        {
            arm7 ??= Arm7IdleLoop;
            var rom = new byte[0x10000];

            Encoding.ASCII.GetBytes("PX86TEST").CopyTo(rom, 0);
            Encoding.ASCII.GetBytes(gameCode).CopyTo(rom, 0x0C);
            rom[0x14] = 0; // 128 KB capacity

            WriteBinary(rom, 0x4000, arm9);
            WriteBinary(rom, 0x8000, arm7);

            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x20), 0x4000);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x24), Arm9Address);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x28), Arm9Address);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x2C), (uint)(arm9.Length * 4));
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x30), 0x8000);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x34), Arm7Address);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x38), Arm7Address);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x3C), (uint)(arm7.Length * 4));
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x60), 0x00586000);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x80), (uint)rom.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x84), 0x4000);
            BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x15C), NdsRomHeader.LogoChecksum);
            BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x15E), Crc16.Compute(rom.AsSpan(0, 0x15E)));

            return new NdsRom(rom);
        }

        private static void WriteBinary(byte[] rom, int offset, uint[] code)
        {
            for (int i = 0; i < code.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(offset + i * 4), code[i]);
        }
    }
}
