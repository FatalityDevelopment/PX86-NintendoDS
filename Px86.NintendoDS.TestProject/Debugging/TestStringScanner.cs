using System.Text;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.TestProject.Emulator;

namespace Px86.NintendoDS.TestProject.Debugging
{
    /// <summary>The string dumper (Tools → Strings): the scan rules, and finding a ROM's literals in a running console.</summary>
    [TestClass]
    public class TestStringScanner
    {
        [TestMethod]
        public void RunsOfPrintableBytesBecomeStrings()
        {
            var data = Encoding.ASCII.GetBytes("\0\0Hello\0ab\0World!!\n2nd\x01" + "ABCD");
            var found = StringScanner.Scan(data, 0x02001000, 4);

            Assert.AreEqual(3, found.Count, string.Join(" | ", found));
            Assert.AreEqual(new FoundString(0x02001002, 5, "Hello"), found[0]);
            Assert.AreEqual(new FoundString(0x0200100B, 11, "World!!\\n2nd"), found[1], "a line feed stays inside the string");
            Assert.AreEqual(new FoundString((uint)(0x02001000 + data.Length - 4), 4, "ABCD"), found[2], "no terminator needed at the end");
        }

        [TestMethod]
        public void ControlCharactersDontStartOrPadStrings()
        {
            var data = Encoding.ASCII.GetBytes("\0\r`\t!\0\r\nline1\r\nline2\0");
            var found = StringScanner.Scan(data, 0, 4);
            Assert.AreEqual(1, found.Count, "\"`\\t!\" has only 2 printable bytes");
            Assert.AreEqual(new FoundString(8, 12, "line1\\r\\nline2"), found[0], "leading CR/LF trimmed");
        }

        [TestMethod]
        public void MinimumLengthAndLongRuns()
        {
            var data = Encoding.ASCII.GetBytes("abc\0" + new string('x', 300) + "\0abcdef");
            Assert.AreEqual(2, StringScanner.Scan(data, 0, 4).Count);
            Assert.AreEqual(3, StringScanner.Scan(data, 0, 3).Count, "\"abc\" counts at minimum length 3");

            var longRun = StringScanner.Scan(data, 0, 4)[0];
            Assert.AreEqual(4u, longRun.Address);
            Assert.AreEqual(StringScanner.MaxLength, longRun.Length, "cut at the maximum");
            Assert.AreEqual(new FoundString(305, 6, "abcdef"), StringScanner.Scan(data, 0, 4)[1], "the rest of the long run is skipped");
        }

        [TestMethod]
        public void FindsTheGamesLiteralsAtTheirAddresses()
        {
            var nds = TestRomRunner.Boot("debugmsg");
            for (int frame = 0; frame < 10; frame++)
                nds.RunFrame();

            AssertFound(nds, DebugCpu.Arm9, "r0=%r0%");
            AssertFound(nds, DebugCpu.Arm7, "hello from arm7");
        }

        private static void AssertFound(NdsEmulator nds, DebugCpu cpu, string literal)
        {
            foreach (var region in StringScanner.Regions(nds, cpu))
            {
                var data = StringScanner.Snapshot(nds, cpu, region);
                foreach (var found in StringScanner.Scan(data, region.Start))
                {
                    // Text bytes just before a literal (code, a literal pool) join the string; its zero ends it.
                    if (!found.Text.EndsWith(literal, StringComparison.Ordinal))
                        continue;
                    var bytes = new byte[literal.Length];
                    DebugMemory.ReadBlock(nds, cpu, found.Address + (uint)(found.Length - literal.Length), bytes);
                    Assert.AreEqual(literal, Encoding.ASCII.GetString(bytes), $"{cpu} reads the string at {found.Address:X8} ({region.Name})");
                    return;
                }
            }
            Assert.Fail($"{cpu}: \"{literal}\" not found");
        }

        [TestMethod]
        public void RegionsFollowTheCpu()
        {
            var nds = TestRomRunner.Boot("debugmsg");
            var arm9 = StringScanner.Regions(nds, DebugCpu.Arm9).Select(r => r.Name).ToArray();
            var arm7 = StringScanner.Regions(nds, DebugCpu.Arm7).Select(r => r.Name).ToArray();
            CollectionAssert.Contains(arm9, "DTCM");
            CollectionAssert.DoesNotContain(arm7, "DTCM");
            CollectionAssert.DoesNotContain(arm7, "ITCM");
            CollectionAssert.Contains(arm7, "ARM7 WRAM");
            Assert.AreEqual(nds.Cp15.DtcmBase, StringScanner.Regions(nds, DebugCpu.Arm9).Single(r => r.Name == "DTCM").Start);
        }
    }
}
