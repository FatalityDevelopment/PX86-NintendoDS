using System.Text.Json;
using System.Text.Json.Serialization;
using Px86.NintendoDS.FileFormats.Checksums;
using Px86.NintendoDS.FileFormats.Rom;

namespace Px86.NintendoDS.EmulatorCore.Cheats
{
    /// <summary>
    /// The cheats of one game, applied once per frame (at VBlank, like an
    /// Action Replay). Saved as JSON, one file per game named after its game
    /// code and header CRC-32 (the ID Action Replay and cheat databases use).
    /// Not thread-safe: <see cref="Hosting.EmulatorHost"/> serialises access.
    /// </summary>
    public sealed class CheatList
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public List<Cheat> Cheats { get; set; } = new();

        /// <summary>Raised after a cheat fails to parse (its name and the error); the cheat is then disabled.</summary>
        public event Action<Cheat, string>? CheatFailed;

        /// <summary>"CODE-CRC32" of the ROM header, e.g. "CPUE-A1B2C3D4".</summary>
        public static string GameId(NdsRom rom) => $"{rom.Header.GameCode}-{HeaderCrc(rom):X8}";

        /// <summary>CRC-32 of the first 512 bytes (the cartridge header).</summary>
        public static uint HeaderCrc(NdsRom rom) => Crc32.Compute(rom.Data.AsSpan(0, Math.Min(512, rom.Data.Length)));

        /// <summary>Apply every enabled cheat once.</summary>
        public void Apply(ICheatMemory memory)
        {
            foreach (var cheat in Cheats)
            {
                if (!cheat.Enabled)
                    continue;
                if (cheat.Type == CheatType.ActionReplay)
                {
                    var (words, error) = cheat.ParseWords();
                    if (error != null)
                    {
                        Fail(cheat, error);
                        continue;
                    }
                    ActionReplayEngine.Execute(words, memory, ref cheat.Counter);
                }
                else
                {
                    var (writes, error) = cheat.ParseRaw();
                    if (error != null)
                    {
                        Fail(cheat, error);
                        continue;
                    }
                    foreach (var (address, value, size) in writes)
                    {
                        switch (size)
                        {
                            case 1: memory.Write8(address, (byte)value); break;
                            case 2: memory.Write16(address, (ushort)value); break;
                            default: memory.Write32(address, value); break;
                        }
                    }
                }
            }
        }

        private void Fail(Cheat cheat, string error)
        {
            cheat.Enabled = false;
            CheatFailed?.Invoke(cheat, error);
        }

        // ── Files ─────────────────────────────────────────────────────────────

        // Create: without it .NET returns "" while ~/.config doesn't exist (a relative path).
        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "Px86.NintendoDS", "cheats");

        public static string PathFor(NdsRom rom, string? folder = null) =>
            Path.Combine(folder ?? DefaultFolder, GameId(rom) + ".json");

        public static CheatList Load(string path)
        {
            if (!File.Exists(path))
                return new CheatList();
            return JsonSerializer.Deserialize<CheatList>(File.ReadAllText(path), Json) ?? new CheatList();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
    }
}
