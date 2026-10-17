using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hardware.Spi;
using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Starts games with the user's settings (BIOS/firmware dumps, save
    /// folder, generated-firmware profile) — shared by the command line, the
    /// File menu, drag &amp; drop and the PINE LoadRom opcode.
    /// </summary>
    public sealed class GameLauncher
    {
        private readonly EmulatorHost _host;
        private readonly EmulatorSettings _settings;

        public GameLauncher(EmulatorHost host, EmulatorSettings settings)
        {
            _host = host;
            _settings = settings;
        }

        /// <summary>Messages for the user (shown in the window and on the console).</summary>
        public event Action<string>? Message;

        public GameLoadOptions OptionsFor(string romPath, string? savePath = null) => new()
        {
            RomPath = romPath,
            SavePath = savePath ?? SavePathFor(romPath),
            Bios9Path = NullIfEmpty(_settings.Bios9Path),
            Bios7Path = NullIfEmpty(_settings.Bios7Path),
            FirmwarePath = NullIfEmpty(_settings.FirmwarePath),
            UseRealBios = _settings.UseRealBios,
            Profile = new FirmwareFlash.UserProfile
            {
                Nickname = string.IsNullOrWhiteSpace(_settings.Nickname) ? "Px86" : _settings.Nickname,
                Message = _settings.Message,
                BirthMonth = Math.Clamp(_settings.BirthMonth, 1, 12),
                BirthDay = Math.Clamp(_settings.BirthDay, 1, 31),
                Language = Math.Clamp(_settings.Language, 0, 5),
                FavoriteColor = Math.Clamp(_settings.FavoriteColor, 0, 15),
            },
        };

        /// <summary>Start a game on the host (replacing the running one). Reports problems through <see cref="Message"/>.</summary>
        public bool Load(string romPath, string? savePath = null)
        {
            var session = Start(OptionsFor(romPath, savePath));
            if (session == null)
                return false;
            session.Emulator.CpuMode = _settings.CpuMode;
            _host.Attach(session);
            _settings.AddRecentRom(romPath);
            TrySaveSettings();
            return true;
        }

        /// <summary>Create a session without attaching it (command line batch mode).</summary>
        public GameSession? Start(GameLoadOptions options)
        {
            try
            {
                var session = GameSession.Start(options);
                var header = session.Rom.Header;
                Message?.Invoke($"{header.Title} [{header.GameCode}]");
                foreach (var message in session.Messages)
                    Message?.Invoke(message);
                return session;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                Message?.Invoke($"Could not start {Path.GetFileName(options.RomPath)}: {ex.Message}");
                return null;
            }
        }

        public void TrySaveSettings()
        {
            try
            {
                _settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Message?.Invoke($"Could not save settings: {ex.Message}");
            }
        }

        private string? SavePathFor(string romPath)
        {
            if (string.IsNullOrEmpty(_settings.SaveDirectory))
                return null;
            Directory.CreateDirectory(_settings.SaveDirectory);
            return Path.Combine(_settings.SaveDirectory, Path.GetFileNameWithoutExtension(romPath) + ".sav");
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
