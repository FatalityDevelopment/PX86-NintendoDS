using System.Text.Json;
using System.Text.Json.Serialization;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// User settings, stored as JSON in the per-user application data folder
    /// (<c>%APPDATA%\Px86.NintendoDS\settings.json</c> on Windows,
    /// <c>~/.config/Px86.NintendoDS/settings.json</c> on Linux/macOS).
    /// Command-line options override these for a single run.
    /// </summary>
    public sealed class EmulatorSettings
    {
        public string? Bios9Path { get; set; }
        public string? Bios7Path { get; set; }
        public string? FirmwarePath { get; set; }

        /// <summary>Boot through the real BIOS/firmware when dumps are configured (otherwise direct boot).</summary>
        public bool UseRealBios { get; set; }

        /// <summary>Window scale (1 = 256×384).</summary>
        public int Scale { get; set; } = 2;

        public bool AudioEnabled { get; set; } = false;

        /// <summary>Folder for .sav files; empty = next to the ROM.</summary>
        public string? SaveDirectory { get; set; }

        /// <summary>Host key names (Silk.NET Key enum) mapped to DS buttons.</summary>
        public Dictionary<string, string> KeyBindings { get; set; } = new()
        {
            ["Up"] = "Up",
            ["Down"] = "Down",
            ["Left"] = "Left",
            ["Right"] = "Right",
            ["X"] = "A",
            ["Z"] = "B",
            ["S"] = "X",
            ["A"] = "Y",
            ["Q"] = "L",
            ["W"] = "R",
            ["Enter"] = "Start",
            ["Backspace"] = "Select",
            ["ShiftRight"] = "Select",
        };

        [JsonIgnore]
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Px86.NintendoDS", "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static EmulatorSettings Load(string? path = null)
        {
            path ??= DefaultPath;
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<EmulatorSettings>(File.ReadAllText(path), JsonOptions) ?? new EmulatorSettings();
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[Settings] {path} is invalid ({ex.Message}); using defaults.");
            }
            return new EmulatorSettings();
        }

        public void Save(string? path = null)
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
    }
}
