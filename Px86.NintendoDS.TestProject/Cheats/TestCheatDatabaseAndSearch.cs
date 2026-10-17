using System.Buffers.Binary;
using System.Text;
using Px86.NintendoDS.EmulatorCore.Cheats;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Cheats
{
    [TestClass]
    public class TestCheatDatabaseAndSearch
    {
        // ── usrcheat.dat ──────────────────────────────────────────────────────

        private sealed record Item(string Name, string Note, uint[]? Words, int FolderItems = 0);

        /// <summary>Build a database following the documented layout (independent of the reader).</summary>
        private static byte[] BuildDatabase(params (string Code, uint Crc, string Title, Item[] Items)[] games)
        {
            var body = new List<byte>();
            var offsets = new List<long>();
            int indexSize = (games.Length + 1) * 16;
            long dataStart = 0x100 + indexSize;
            foreach (var game in games)
            {
                while ((dataStart + body.Count) % 4 != 0) body.Add(0);
                offsets.Add(dataStart + body.Count);
                body.AddRange(Encoding.UTF8.GetBytes(game.Title)); body.Add(0);
                while ((dataStart + body.Count) % 4 != 0) body.Add(0);
                body.AddRange(BitConverter.GetBytes((uint)game.Items.Length));
                body.AddRange(new byte[32]);
                foreach (var item in game.Items)
                {
                    int start = body.Count;
                    body.AddRange(new byte[4]); // header, patched below
                    body.AddRange(Encoding.UTF8.GetBytes(item.Name)); body.Add(0);
                    body.AddRange(Encoding.UTF8.GetBytes(item.Note)); body.Add(0);
                    while ((dataStart + body.Count) % 4 != 0) body.Add(0);
                    uint header;
                    if (item.Words == null)
                    {
                        header = 0x10000000u | (uint)item.FolderItems;
                    }
                    else
                    {
                        body.AddRange(BitConverter.GetBytes((uint)item.Words.Length));
                        foreach (var w in item.Words) body.AddRange(BitConverter.GetBytes(w));
                        header = (uint)((body.Count - start) / 4 - 1);
                    }
                    var h = BitConverter.GetBytes(header);
                    for (int i = 0; i < 4; i++) body[start + i] = h[i];
                }
            }

            var file = new byte[dataStart + body.Count];
            Encoding.ASCII.GetBytes(UsrCheatDatabase.Magic).CopyTo(file, 0);
            Encoding.ASCII.GetBytes("Test database").CopyTo(file, 0x10);
            for (int g = 0; g < games.Length; g++)
            {
                int pos = 0x100 + g * 16;
                Encoding.ASCII.GetBytes(games[g].Code).CopyTo(file, pos);
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(pos + 4), games[g].Crc);
                BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(pos + 8), (ulong)offsets[g]);
            }
            body.ToArray().CopyTo(file, dataStart);
            return file;
        }

        private static byte[] SampleDatabase() => BuildDatabase(
            ("AAAA", 0x11111111, "Other game", new[] { new Item("Nothing", "", new uint[] { 0x02000000, 1 }) }),
            ("CPUE", 0xDEADBEEF, "Pokemon Platinum", new[]
            {
                new Item("Money", "max", new uint[] { 0x22000000, 0xFF }),
                new Item("Items", "", null, FolderItems: 2),
                new Item("Master Ball", "slot 1", new uint[] { 0x12000000, 1, 0x12000002, 99 }),
                new Item("Rare Candy", "", new uint[] { 0x02000010, 50 }),
                new Item("Walk through walls", "hold R", new uint[] { 0x94000130, 0xFEFF0000, 0x02000020, 1, 0xD0000000, 0 }),
            }));

        [TestMethod]
        public void ReadsFoldersAndCodes()
        {
            var game = UsrCheatDatabase.Find(SampleDatabase(), "CPUE", 0xDEADBEEF);
            Assert.IsNotNull(game, "found by code + CRC");
            Assert.AreEqual("Pokemon Platinum", game.Title, "title");
            CollectionAssert.AreEqual(new[] { "Money", "Items: Master Ball", "Items: Rare Candy", "Walk through walls" },
                game.Cheats.Select(c => c.Name).ToArray(), "folder names prefix their cheats");
            Assert.AreEqual("02000010 00000032", game.Cheats[2].Code, "code words");
            Assert.AreEqual("hold R", game.Cheats[3].Note, "note");
            Assert.IsTrue(game.Cheats.All(c => c.Validate() == null && !c.Enabled), "parseable, imported switched off");
            Assert.AreEqual("Test database", UsrCheatDatabase.Description(SampleDatabase()), "description");
            Assert.AreEqual(2, UsrCheatDatabase.List(SampleDatabase()).Count(), "two games listed");
        }

        [TestMethod]
        public void FallsBackToTheGameCodeAndMissesUnknownGames()
        {
            Assert.AreEqual("Pokemon Platinum", UsrCheatDatabase.Find(SampleDatabase(), "CPUE", 0x12345678)?.Title, "other revision: code only");
            Assert.IsNull(UsrCheatDatabase.Find(SampleDatabase(), "ZZZZ", 0), "unknown game");
        }

        [TestMethod]
        public void ReadsTheScrambledVariant()
        {
            var plain = SampleDatabase();
            var scrambled = (byte[])plain.Clone();
            UsrCheatDatabase.Transform(scrambled, decrypt: false);
            Assert.AreNotEqual(UsrCheatDatabase.Magic, Encoding.ASCII.GetString(scrambled, 0, 12), "really scrambled");

            var back = (byte[])scrambled.Clone();
            UsrCheatDatabase.Transform(back, decrypt: true);
            CollectionAssert.AreEqual(plain, back, "descrambling inverts scrambling");

            var game = UsrCheatDatabase.Find(scrambled, "CPUE", 0xDEADBEEF);
            Assert.AreEqual(4, game?.Cheats.Count, "same cheats from the scrambled file");
        }

        [TestMethod]
        public void RejectsOtherFiles()
        {
            Assert.ThrowsException<InvalidDataException>(() => UsrCheatDatabase.Find(new byte[1024], "CPUE", 0));
        }

        // ── Search ────────────────────────────────────────────────────────────

        [TestMethod]
        public void SearchNarrowsDownToTheChangingValue()
        {
            var ram = new byte[0x1000];
            void Set(int offset, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(ram.AsSpan(offset), v);
            Set(0x100, 100); Set(0x200, 100); Set(0x300, 7);

            var search = new CheatSearch();
            search.Start(ram, 2, signed: false);
            Assert.AreEqual(0x800, search.Count, "every halfword at first");
            search.Filter(ram, SearchCompare.EqualTo, 100);
            Assert.AreEqual(2, search.Count, "two hundreds");

            Set(0x100, 90); // "took damage"
            search.Filter(ram, SearchCompare.DecreasedBy, 10);
            var results = search.Results(ram);
            Assert.AreEqual(1, results.Count, "one address left");
            Assert.AreEqual((CheatSearch.RamBase + 0x100u, 90L, 90L), results[0], "address and values");

            search.Filter(ram, SearchCompare.Unchanged);
            Assert.AreEqual(1, search.Count, "unchanged since the last scan");
            var cheat = search.MakeCheat(results[0].Address, 999, "HP");
            Assert.AreEqual("02000100 03E7", cheat.Code, "raw cheat at the found address");
        }

        [TestMethod]
        public void SignedComparisons()
        {
            var ram = new byte[16];
            ram[4] = 0xFF; // -1 signed, 255 unsigned
            var search = new CheatSearch();
            search.Start(ram, 1, signed: true);
            search.Filter(ram, SearchCompare.LessThan, 0);
            Assert.AreEqual(1, search.Count, "only the -1");
            search.Start(ram, 1, signed: false);
            search.Filter(ram, SearchCompare.GreaterThan, 200);
            Assert.AreEqual(1, search.Count, "255 unsigned");
        }

        // ── On a running console ──────────────────────────────────────────────

        [TestMethod]
        public void CheatsApplyEveryFrameOnTheConsole()
        {
            var nds = TestRomRunner.Boot("bench");
            var list = new CheatList();
            list.Cheats.Add(new Cheat { Name = "AR", Enabled = true, Code = "023F0000 0000ABCD" });
            list.Cheats.Add(new Cheat { Name = "raw", Enabled = true, Type = CheatType.Raw, Code = "023F0010 1234" });
            list.Cheats.Add(new Cheat { Name = "off", Enabled = false, Code = "023F0020 00000001" });
            list.Cheats.Add(new Cheat { Name = "broken", Enabled = true, Code = "not hex" });
            string? failed = null;
            list.CheatFailed += (c, _) => failed = c.Name;

            list.Apply(new EmulatorCheatMemory(nds));
            Assert.AreEqual(0xABCDu, nds.Arm9Bus.BusRead32(0x023F0000), "AR write landed in main RAM");
            Assert.AreEqual((ushort)0x1234, nds.Arm9Bus.BusRead16(0x023F0010), "raw write");
            Assert.AreEqual(0u, nds.Arm9Bus.BusRead32(0x023F0020), "disabled cheat not applied");
            Assert.AreEqual("broken", failed, "bad code reported");
            Assert.IsFalse(list.Cheats[3].Enabled, "bad code switched off");

            nds.Arm9Bus.BusWrite32(0x023F0000, 0);
            list.Apply(new EmulatorCheatMemory(nds));
            Assert.AreEqual(0xABCDu, nds.Arm9Bus.BusRead32(0x023F0000), "re-applied next frame");

            string path = Path.Combine(Path.GetTempPath(), $"px86-cheats-{Guid.NewGuid():N}.json");
            try
            {
                list.Save(path);
                var loaded = CheatList.Load(path);
                Assert.AreEqual(4, loaded.Cheats.Count, "saved and loaded");
                Assert.AreEqual(CheatType.Raw, loaded.Cheats[1].Type, "type survives");
                Assert.AreEqual("023F0000 0000ABCD", loaded.Cheats[0].Code, "code survives");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
