using Px86.NintendoDS.Emulator;

namespace Px86.NintendoDS.TestProject.Frontend
{
    [TestClass]
    public class TestEmulatorLog
    {
        [TestMethod]
        public void TeeKeepsTheConsoleOutputAndCollectsWholeLines()
        {
            var log = new EmulatorLog();
            var console = new StringWriter();
            var tee = new EmulatorLog.TeeWriter(console, log, error: false);

            tee.Write("Loaded ");
            tee.Write("POKEMON PL");
            Assert.AreEqual(0, log.Snapshot(0).Length, "no line until the line feed");
            tee.WriteLine(" [CPUE]");
            tee.Write("PINE listening.\r\nGS stream listening.\n\n");

            var lines = log.Snapshot(0);
            CollectionAssert.AreEqual(new[] { "Loaded POKEMON PL [CPUE]", "PINE listening.", "GS stream listening." }, lines.Select(l => l.Text).ToArray(), "empty lines dropped");
            StringAssert.Contains(console.ToString(), "Loaded POKEMON PL [CPUE]", "the real console still gets everything");
            Assert.AreEqual(1, log.Snapshot(lines[1].Index + 1).Length, "snapshot from an index");
        }

        [TestMethod]
        public void ErrorsAreMarkedAndTheLogIsBounded()
        {
            var log = new EmulatorLog();
            new EmulatorLog.TeeWriter(TextWriter.Null, log, error: true).WriteLine("Could not open PINE on port 28013.");
            Assert.IsTrue(log.Snapshot(0)[0].Error);

            for (int i = 0; i < EmulatorLog.MaxLines + 10; i++)
                log.Add($"line {i}");
            var all = log.Snapshot(0);
            Assert.AreEqual(EmulatorLog.MaxLines, all.Length, "oldest lines dropped");
            Assert.AreEqual($"line {EmulatorLog.MaxLines + 9}", all[^1].Text);
            log.Clear();
            Assert.AreEqual(0, log.Snapshot(0).Length);
        }
    }
}
