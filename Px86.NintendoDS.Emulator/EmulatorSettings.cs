using Px86.NintendoDS.EmulatorCore.Jit;
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

        // ── Video ─────────────────────────────────────────────────────────────

        public ScreenArrangement ScreenArrangement { get; set; } = ScreenArrangement.Vertical;

        /// <summary>Gap between the screens, in DS pixels.</summary>
        public int ScreenGap { get; set; }

        public bool SwapScreens { get; set; }

        /// <summary>0, 90, 180 or 270 degrees clockwise.</summary>
        public int ScreenRotation { get; set; }

        public bool LinearFilter { get; set; }

        public bool IntegerScale { get; set; }

        [JsonIgnore]
        public ScreenLayout Layout => new()
        {
            Arrangement = ScreenArrangement,
            Gap = Math.Clamp(ScreenGap, 0, 192),
            Swap = SwapScreens,
            Rotation = ScreenRotation is 90 or 180 or 270 ? ScreenRotation : 0,
            IntegerScale = IntegerScale,
        };

        // ── Audio ─────────────────────────────────────────────────────────────

        public bool AudioEnabled { get; set; } = true;

        /// <summary>Output volume, 0–100.</summary>
        public int Volume { get; set; } = 100;

        // ── Emulation ─────────────────────────────────────────────────────────

        /// <summary>Speed of the "turbo" limiter mode, in percent.</summary>
        public int TurboPercent { get; set; } = 200;

        /// <summary>How the CPUs run code. The JIT gives the same results, faster; the debugger always interprets.</summary>
        public CpuMode CpuMode { get; set; } = CpuMode.Jit;

        /// <summary>Most recently opened ROMs, newest first.</summary>
        public List<string> RecentRoms { get; set; } = new();

        /// <summary>Where the Open ROM browser starts.</summary>
        public string? RomFolder { get; set; }

        public void AddRecentRom(string path)
        {
            path = Path.GetFullPath(path);
            RecentRoms.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RecentRoms.Insert(0, path);
            if (RecentRoms.Count > 10)
                RecentRoms.RemoveRange(10, RecentRoms.Count - 10);
            RomFolder = Path.GetDirectoryName(path);
        }

        /// <summary>Last usrcheat.dat imported from (Tools → Cheats).</summary>
        public string? CheatDatabasePath { get; set; }

        // ── Generated firmware (used when no firmware dump is configured) ─────

        public string Nickname { get; set; } = "Px86";
        public string Message { get; set; } = "";
        public int BirthMonth { get; set; } = 1;
        public int BirthDay { get; set; } = 1;

        /// <summary>0 Japanese, 1 English, 2 French, 3 German, 4 Italian, 5 Spanish.</summary>
        public int Language { get; set; } = 1;

        /// <summary>Favourite colour 0–15.</summary>
        public int FavoriteColor { get; set; } = 11;

        /// <summary>Folder for .sav files; empty = next to the ROM.</summary>
        public string? SaveDirectory { get; set; }

        // ── Remote control (CodeDesigner, debuggers) ──────────────────────────

        /// <summary>Open the PINE socket in the window too (headless server mode always opens it).</summary>
        public bool PineEnabled { get; set; }

        public int PinePort { get; set; } = 28013;

        /// <summary>Open the GS frame-stream socket in the window too.</summary>
        public bool GsEnabled { get; set; }

        public int GsPort { get; set; } = 28014;

        /// <summary>GS stream picture format: jpeg, png or raw.</summary>
        public string GsCodec { get; set; } = "jpeg";

        public int GsQuality { get; set; } = 85;

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
