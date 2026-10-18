using Px86.NintendoDS.Emulator;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Jit;
using System.Text.Json.Nodes;

namespace Px86.NintendoDS.TestProject.Frontend
{
    [TestClass]
    public class TestSettingsJson
    {
        [TestMethod]
        public void ExportListsTheRemoteSettingsOnly()
        {
            var obj = JsonNode.Parse(SettingsJson.Export(new EmulatorSettings()))!.AsObject();
            Assert.AreEqual("Px86", (string?)obj["Nickname"], "nickname");
            Assert.AreEqual("Jit", (string?)obj["CpuMode"], "enums as names");
            Assert.AreEqual("", (string?)obj["Bios9Path"], "unset path is empty");
            Assert.IsFalse(obj.ContainsKey("KeyBindings"), "window-only settings left out");
            Assert.IsFalse(obj.ContainsKey("Volume"), "window-only settings left out");
        }

        [TestMethod]
        public void MergeChangesOnlyTheKeysGivenAndClamps()
        {
            var s = new EmulatorSettings();
            var changed = SettingsJson.Merge(s, """
                {"nickname":"ABCDEFGHIJKLMN","Language":9,"CpuMode":"interpreter","Bios9Path":"  ","Unknown":1,"FavoriteColor":11}
                """);
            CollectionAssert.AreEquivalent(new[] { "Nickname", "Language", "CpuMode" }, changed.ToArray(), "changed keys (case-insensitive; same value and empty path unchanged)");
            Assert.AreEqual("ABCDEFGHIJ", s.Nickname, "nickname cut to 10");
            Assert.AreEqual(5, s.Language, "language clamped to 0-5");
            Assert.AreEqual(CpuMode.Interpreter, s.CpuMode, "enum parsed");
            Assert.IsNull(s.Bios9Path, "empty path means not set");
            Assert.AreEqual(200, s.TurboPercent, "keys not given are kept");
        }

        [TestMethod]
        public void BadValuesAreRejected()
        {
            var s = new EmulatorSettings();
            Assert.ThrowsException<InvalidDataException>(() => SettingsJson.Merge(s, "nope"), "not JSON");
            Assert.ThrowsException<InvalidDataException>(() => SettingsJson.Merge(s, "[1]"), "not an object");
            Assert.ThrowsException<InvalidDataException>(() => SettingsJson.Merge(s, """{"CpuMode":"turbo"}"""), "unknown enum name");
            Assert.ThrowsException<InvalidDataException>(() => SettingsJson.Merge(s, """{"Language":"two"}"""), "wrong type");
            Assert.ThrowsException<InvalidDataException>(() => SettingsJson.Merge(s, """{"GsCodec":"gif"}"""), "unknown codec");
        }

        [TestMethod]
        public void ReportSortsKeysByWhenTheyTakeEffect()
        {
            var report = JsonNode.Parse(SettingsJson.Report(new[] { "CpuMode", "Nickname", "PinePort" }))!;
            Assert.AreEqual("CpuMode", (string?)report["applied"]![0]);
            Assert.AreEqual("Nickname", (string?)report["nextLoad"]![0]);
            Assert.AreEqual("PinePort", (string?)report["restart"]![0]);
        }

        [TestMethod]
        public void RemoteSettingsSavesOnlyTheChangeAndAppliesTurbo()
        {
            string folder = Path.Combine(Path.GetTempPath(), "px86-settings-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "settings.json");
            try
            {
                new EmulatorSettings { Bios9Path = "disk-bios9.bin" }.Save(path);
                // The running settings carry a command-line override that must not reach the file.
                var live = EmulatorSettings.Load(path);
                live.Bios9Path = "override-bios9.bin";
                using var host = new EmulatorHost();
                var remote = new RemoteSettings(live, host, wireless: null, path);

                var report = JsonNode.Parse(remote.Set("""{"TurboPercent":300,"Nickname":"Tyler"}"""))!;
                Assert.AreEqual("TurboPercent", (string?)report["applied"]![0], "turbo applied now");
                Assert.AreEqual(3.0, host.TurboSpeed, 1e-9, "host turbo speed");
                Assert.AreEqual("Tyler", (string?)JsonNode.Parse(remote.Get())!["Nickname"], "live value");

                var disk = EmulatorSettings.Load(path);
                Assert.AreEqual("Tyler", disk.Nickname, "change saved");
                Assert.AreEqual(300, disk.TurboPercent, "change saved");
                Assert.AreEqual("disk-bios9.bin", disk.Bios9Path, "override not saved");
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
        }
    }
}
