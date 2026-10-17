using System.Buffers.Binary;
using Px86.NintendoDS.EmulatorCore.Cartridge;

namespace Px86.NintendoDS.TestProject.Cartridge
{
    /// <summary>
    /// KEY1 round trip with a synthetic key table (the real one lives in the user's ARM7 BIOS):
    /// what the cartridge encrypts for a real-BIOS boot must decrypt back for direct boot.
    /// </summary>
    [TestClass]
    public class TestKey1
    {
        private static byte[] SyntheticBios()
        {
            var bios = new byte[0x4000];
            new Random(1234).NextBytes(bios);
            return bios;
        }

        private static byte[] RomWithDecryptedSecureArea()
        {
            var rom = new byte[0x8000];
            "TEST"u8.CopyTo(rom.AsSpan(0x0C));
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x4000), 0xE7FFDEFFu);
            BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x4004), 0xE7FFDEFFu);
            for (int i = 8; i < 0x800; i++)
                rom[0x4000 + i] = (byte)(i * 7);
            return rom;
        }

        [TestMethod]
        public void SecureAreaEncryptThenDecryptRoundTrips()
        {
            var bios = SyntheticBios();
            var rom = RomWithDecryptedSecureArea();
            uint gameCode = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x0C));
            var key1 = new Key1Decryptor(bios, gameCode, rom);

            var block = new byte[0x1000];
            key1.EncryptSecureArea(block, 0x4000);
            Assert.IsFalse(block.AsSpan(0, 0x800).SequenceEqual(rom.AsSpan(0x4000, 0x800)), "data is encrypted");

            Assert.IsTrue(key1.DecryptSecureArea(block), "encryObj tag found after decryption");
            CollectionAssert.AreEqual(rom.AsSpan(0x4000, 0x800).ToArray(), block.AsSpan(0, 0x800).ToArray());
        }

        [TestMethod]
        public void DecryptRejectsWrongKey()
        {
            var rom = RomWithDecryptedSecureArea();
            uint gameCode = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(0x0C));
            var block = new byte[0x1000];
            new Key1Decryptor(SyntheticBios(), gameCode, rom).EncryptSecureArea(block, 0x4000);

            var otherBios = SyntheticBios();
            otherBios[0x40] ^= 0xFF;
            var original = block.ToArray();
            Assert.IsFalse(new Key1Decryptor(otherBios, gameCode, rom).DecryptSecureArea(block));
            CollectionAssert.AreEqual(original, block, "left untouched on failure");
        }

        [TestMethod]
        public void SecureAreaDetection()
        {
            var rom = RomWithDecryptedSecureArea();
            Assert.IsTrue(Key1Decryptor.IsSecureAreaDecrypted(rom));
            Assert.IsFalse(Key1Decryptor.IsSecureAreaEmpty(rom));
            Assert.IsTrue(Key1Decryptor.IsSecureAreaEmpty(new byte[0x8000]));
        }
    }
}
