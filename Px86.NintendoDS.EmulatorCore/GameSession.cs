using Px86.NintendoDS.EmulatorCore.Cartridge;
using Px86.NintendoDS.FileFormats.Rom;

namespace Px86.NintendoDS.EmulatorCore
{
    /// <summary>Everything needed to start a game.</summary>
    public sealed class GameLoadOptions
    {
        public required string RomPath { get; init; }
        public string? SavePath { get; init; }
        public string? Bios9Path { get; init; }
        public string? Bios7Path { get; init; }
        public string? FirmwarePath { get; init; }
        public bool UseRealBios { get; init; }
    }

    /// <summary>
    /// A running game: the emulator plus its files. Loads the ROM, optional
    /// BIOS/firmware dumps and the save file (<c>game.sav</c> next to the ROM
    /// by default), and writes the save back shortly after the game changes it.
    /// </summary>
    public sealed class GameSession : IDisposable
    {
        private const int SaveFlushDelayFrames = 60;

        private int _framesSinceSaveChange = -1;

        private GameSession(NdsEmulator emulator, NdsRom rom, SaveMemory save, string romPath, string savePath)
        {
            RomPath = romPath;
            Emulator = emulator;
            Rom = rom;
            Save = save;
            SavePath = savePath;
        }

        public NdsEmulator Emulator { get; }
        public NdsRom Rom { get; }
        public SaveMemory Save { get; }
        public string RomPath { get; }
        public string SavePath { get; }

        /// <summary>Human-readable notes about how the game was started (missing dumps, save type…).</summary>
        public List<string> Messages { get; } = new();

        public static GameSession Start(GameLoadOptions options)
        {
            var emulator = new NdsEmulator();
            var rom = NdsRom.Load(options.RomPath);
            var session = new GameSession(emulator, rom, new SaveMemory(), options.RomPath,
                options.SavePath ?? Path.ChangeExtension(options.RomPath, ".sav"));

            if (options.Bios9Path != null && options.Bios7Path != null)
            {
                emulator.LoadBios(File.ReadAllBytes(options.Bios9Path), File.ReadAllBytes(options.Bios7Path));
                emulator.UseRealBios = options.UseRealBios;
                session.Messages.Add(options.UseRealBios ? "Booting through the real BIOS." : "BIOS dumps loaded (direct boot).");
            }
            else
            {
                session.Messages.Add("No BIOS dumps: using the built-in HLE BIOS and direct boot.");
            }

            if (options.FirmwarePath != null)
                emulator.LoadFirmware(File.ReadAllBytes(options.FirmwarePath));
            else if (options.UseRealBios)
                session.Messages.Add("Real-BIOS boot needs a firmware dump; falling back to direct boot.");

            if (emulator.UseRealBios && options.FirmwarePath == null)
                emulator.UseRealBios = false;

            if (File.Exists(session.SavePath))
            {
                session.Save.Load(File.ReadAllBytes(session.SavePath));
                session.Messages.Add($"Save: {session.SavePath} ({session.Save.Type}).");
            }
            else
            {
                session.Messages.Add($"No save file yet; the type is detected when the game first saves ({session.SavePath}).");
            }

            session.Save.Modified += () => session._framesSinceSaveChange = 0;
            session.Save.DetectedType += type => session.Messages.Add($"Detected save type: {type}.");

            emulator.LoadRom(rom, session.Save);
            emulator.Boot();

            switch (DirectBoot.SecureAreaStatus)
            {
                case DirectBoot.SecureArea.EncryptedNoBios:
                    session.Messages.Add("WARNING: this ROM's secure area is encrypted. Provide your bios7.bin/bios9.bin " +
                                         "(--bios7/--bios9) so it can be decrypted, or the game may crash.");
                    break;
                case DirectBoot.SecureArea.DecryptedWithBios:
                    session.Messages.Add("Decrypted the ROM's secure area with the ARM7 BIOS key table.");
                    break;
                case DirectBoot.SecureArea.DecryptionFailed:
                    session.Messages.Add("WARNING: the secure area could not be decrypted (bad BIOS dump or corrupt ROM).");
                    break;
            }
            emulator.FrameCompleted += session.OnFrame;
            return session;
        }

        private void OnFrame()
        {
            if (_framesSinceSaveChange < 0)
                return;
            if (++_framesSinceSaveChange >= SaveFlushDelayFrames)
                FlushSave();
        }

        /// <summary>Write the save file now if the game changed it.</summary>
        public void FlushSave()
        {
            if (_framesSinceSaveChange < 0 || Save.Data.Length == 0)
                return;
            _framesSinceSaveChange = -1;
            string temp = SavePath + ".tmp";
            File.WriteAllBytes(temp, Save.Data);
            File.Move(temp, SavePath, overwrite: true);
        }

        // ── Save states ───────────────────────────────────────────────────────

        public const int StateSlots = 10;

        /// <summary>State files sit next to the ROM: <c>game.st0</c> … <c>game.st9</c>.</summary>
        public string StatePath(int slot) => Path.ChangeExtension(RomPath, $".st{slot}");

        /// <summary>
        /// Save the whole machine to a slot. Call it between frames on the
        /// thread that runs the emulator. The state includes the cartridge save
        /// memory, so loading it later also rolls that back; the .sav file is
        /// only rewritten when the game saves again.
        /// </summary>
        public void SaveState(int slot) => SaveStateFile(StatePath(slot));

        /// <summary>Load a slot. Returns false when the slot is empty; throws <see cref="InvalidDataException"/> for a bad file.</summary>
        public bool LoadState(int slot) => LoadStateFile(StatePath(slot));

        /// <summary>Save the whole machine to any file (written to a temporary file first, then moved into place).</summary>
        public void SaveStateFile(string path)
        {
            string temp = path + ".tmp";
            using (var file = File.Create(temp))
                Emulator.SaveState(file);
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>Load a state file. Returns false when it doesn't exist; a bad file throws and leaves the machine as it was.</summary>
        public bool LoadStateFile(string path)
        {
            if (!File.Exists(path))
                return false;
            // Read it whole first, so a truncated file fails before the machine is touched.
            var data = File.ReadAllBytes(path);
            var backup = new MemoryStream();
            Emulator.SaveState(backup);
            try
            {
                Emulator.LoadState(new MemoryStream(data, writable: false));
            }
            catch
            {
                backup.Position = 0;
                Emulator.LoadState(backup);
                throw;
            }
            return true;
        }

        public void Dispose() => FlushSave();
    }
}
