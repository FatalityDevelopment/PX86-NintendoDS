using Px86.NintendoDS.EmulatorCore.Cheats;

namespace Px86.NintendoDS.TestProject.Cheats
{
    /// <summary>Byte-addressed sparse memory for engine tests.</summary>
    internal sealed class FakeMemory : ICheatMemory
    {
        public readonly Dictionary<uint, byte> Bytes = new();

        public byte Read8(uint a) => Bytes.TryGetValue(a, out var b) ? b : (byte)0;
        public ushort Read16(uint a) => (ushort)(Read8(a & ~1u) | (Read8((a & ~1u) + 1) << 8));
        public uint Read32(uint a) => (uint)(Read16(a & ~3u) | (Read16((a & ~3u) + 2) << 16));
        public void Write8(uint a, byte v) => Bytes[a] = v;
        public void Write16(uint a, ushort v) { a &= ~1u; Write8(a, (byte)v); Write8(a + 1, (byte)(v >> 8)); }
        public void Write32(uint a, uint v) { a &= ~3u; Write16(a, (ushort)v); Write16(a + 2, (ushort)(v >> 16)); }
    }

    [TestClass]
    public class TestActionReplay
    {
        private static FakeMemory Run(string code, FakeMemory? memory = null, int frames = 1)
        {
            memory ??= new FakeMemory();
            var cheat = new Cheat { Code = code };
            var (words, error) = cheat.ParseWords();
            Assert.IsNull(error, error);
            uint counter = 0;
            for (int i = 0; i < frames; i++)
                ActionReplayEngine.Execute(words, memory, ref counter);
            return memory;
        }

        [TestMethod]
        public void WritesOfEachSize()
        {
            var m = Run("""
                02000000 11223344
                12000010 0000AABB
                22000020 000000CC
                """);
            Assert.AreEqual(0x11223344u, m.Read32(0x02000000), "word");
            Assert.AreEqual((ushort)0xAABB, m.Read16(0x02000010), "half");
            Assert.AreEqual((byte)0xCC, m.Read8(0x02000020), "byte");
        }

        [TestMethod]
        public void ConditionsGateTheCodesUntilEndif()
        {
            var m = new FakeMemory();
            m.Write32(0x02000100, 5);
            m.Write16(0x02000200, 0x00F0);
            Run("""
                52000100 00000005   ; IF 5 == word -> true
                02000000 00000001
                62000100 00000005   ; IF 5 != word -> false (nested)
                02000004 00000002   ; skipped
                32000100 00000001   ; nested IF inside a false block still needs its ENDIF
                02000008 00000003   ; skipped
                D0000000 00000000
                D0000000 00000000
                0200000C 00000004   ; back in the true block
                D0000000 00000000
                92000200 FF0F00F0   ; IF 00F0 == (~FF0F & half=00F0) -> true
                02000010 00000005
                D0000000 00000000
                42000100 00000004   ; IF 4 < 5 -> true
                02000014 00000006
                D0000000 00000000
                """, m);
            Assert.AreEqual(1u, m.Read32(0x02000000), "true IF runs its code");
            Assert.AreEqual(0u, m.Read32(0x02000004), "false IF skips");
            Assert.AreEqual(0u, m.Read32(0x02000008), "nested IF in a false block skips");
            Assert.AreEqual(4u, m.Read32(0x0200000C), "ENDIFs matched up correctly");
            Assert.AreEqual(5u, m.Read32(0x02000010), "masked halfword compare");
            Assert.AreEqual(6u, m.Read32(0x02000014), "unsigned less-than");
        }

        [TestMethod]
        public void PointerCodeUsesTheLoadedOffset()
        {
            var m = new FakeMemory();
            m.Write32(0x02100000, 0x02200000);   // a pointer
            Run("""
                B2100000 00000000   ; offset = [02100000]
                00000010 0000BEEF   ; word[10 + offset]
                D2000000 00000000
                02000000 00000001   ; offset is 0 again after the flush
                """, m);
            Assert.AreEqual(0xBEEFu, m.Read32(0x02200010), "write through the pointer");
            Assert.AreEqual(1u, m.Read32(0x02000000), "flush reset the offset");
        }

        [TestMethod]
        public void LoopsWithDataRegister()
        {
            var m = Run("""
                D3000000 02000000   ; offset = 02000000
                D5000000 00000000   ; data = 0
                C0000000 00000003   ; repeat 4 times
                D4000000 00000001   ;   data += 1
                D6000000 00000000   ;   word[offset] = data, offset += 4
                D2000000 00000000
                """);
            for (uint i = 0; i < 4; i++)
                Assert.AreEqual(i + 1, m.Read32(0x02000000 + i * 4), $"loop iteration {i}");
            Assert.AreEqual(0u, m.Read32(0x02000010), "exactly four iterations");
        }

        [TestMethod]
        public void LoadsAndOffsetArithmetic()
        {
            var m = new FakeMemory();
            m.Write32(0x02000040, 0x12345678);
            Run("""
                D3000000 02000000
                D9000000 00000040   ; data = word[offset + 40]
                DC000000 00000100   ; offset += 100
                D7000000 00000000   ; half[offset] = data, offset += 2
                D8000000 00000000   ; byte[offset] = data, offset += 1
                DA000000 FFFFFF3D   ; data = half[offset - C3] = half[02000040]
                C6000000 02000300   ; word[02000300] = offset
                """, m);
            Assert.AreEqual((ushort)0x5678, m.Read16(0x02000100), "halfword store");
            Assert.AreEqual((byte)0x78, m.Read8(0x02000102), "byte store after +2");
            Assert.AreEqual(0x02000103u, m.Read32(0x02000300), "C6 stores the offset");
        }

        [TestMethod]
        public void BlockCopiesAndSkippedParameters()
        {
            var m = new FakeMemory();
            m.Write32(0x02000500, 0xCAFEBABE);
            Run("""
                E2000000 0000000A   ; copy 10 parameter bytes to 02000000
                44332211 88776655
                0000AA99 00000000
                52000500 00000000   ; IF 0 == word[500] -> false
                E2000100 00000004   ; skipped, but its parameter line must be skipped too
                02000200 00000001   ; (looks like a write: must NOT run as code)
                D0000000 00000000
                D3000000 02000500
                F2000600 00000004   ; copy 4 bytes from [offset] to 02000600
                """, m);
            Assert.AreEqual(0x44332211u, m.Read32(0x02000000), "parameter bytes 1-4");
            Assert.AreEqual(0x88776655u, m.Read32(0x02000004), "parameter bytes 5-8");
            Assert.AreEqual((ushort)0xAA99, m.Read16(0x02000008), "parameter bytes 9-10");
            Assert.AreEqual((byte)0, m.Read8(0x0200000A), "no more than 10 bytes");
            Assert.AreEqual(0u, m.Read32(0x02000100), "skipped copy");
            Assert.AreEqual(0u, m.Read32(0x02000200), "parameter of a skipped copy not executed");
            Assert.AreEqual(0xCAFEBABEu, m.Read32(0x02000600), "F copy");
        }

        [TestMethod]
        public void CounterConditionPersistsBetweenFrames()
        {
            // C5: counter++, IF (counter & 1) == 0 → every second frame; D4 adds 1 then.
            var m = Run("""
                C5000000 00000001
                D3000000 02000000
                D9000000 00000000
                D4000000 00000001
                D6000000 00000000
                D0000000 00000000
                """, frames: 6);
            Assert.AreEqual(3u, m.Read32(0x02000000), "ran on frames 2, 4 and 6");
        }

        [TestMethod]
        public void HookLinesAreIgnored()
        {
            var m = Run("""
                00000000 02345678
                02000000 00000007
                """);
            Assert.AreEqual(7u, m.Read32(0x02000000), "real code still runs");
            Assert.AreEqual(0u, m.Read32(0x00000000), "hook line wrote nothing");
        }

        [TestMethod]
        public void ParsingAcceptsCommonFormats()
        {
            var cheat = new Cheat { Code = "0x02000000-00000001  // comment\n12000004 0000FFFF # another\n\n" };
            var (words, error) = cheat.ParseWords();
            Assert.IsNull(error, "dashes, 0x and comments are fine");
            CollectionAssert.AreEqual(new uint[] { 0x02000000, 1, 0x12000004, 0xFFFF }, words);
            Assert.IsNotNull(new Cheat { Code = "02000000" }.Validate(), "odd word count");
            Assert.IsNotNull(new Cheat { Code = "0200000 00000001" }.Validate(), "7-digit word");

            var raw = new Cheat { Type = CheatType.Raw, Code = "02000000 FF\n02000002 1234\n02000004:89ABCDEF" };
            var (writes, rawError) = raw.ParseRaw();
            Assert.IsNull(rawError, rawError);
            CollectionAssert.AreEqual(new[] { (0x02000000u, 0xFFu, 1), (0x02000002u, 0x1234u, 2), (0x02000004u, 0x89ABCDEFu, 4) }, writes);
            Assert.IsNotNull(new Cheat { Type = CheatType.Raw, Code = "02000000 123456789" }.Validate(), "9-digit value");
        }
    }
}
