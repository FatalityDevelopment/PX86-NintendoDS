using Px86.NintendoDS.EmulatorCore.Jit;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// The settings a remote client (the PINE GetSettings/SetSettings opcodes,
    /// CodeDesigner's settings page) may read and change, as JSON with the
    /// <see cref="EmulatorSettings"/> property names. Window-only settings
    /// (layout, audio, microphone, key bindings) are left out: a headless
    /// emulator has none of them.
    /// </summary>
    /// <remarks>
    /// Values are clamped with the same limits as the settings window. Each key
    /// says when it takes effect: <c>applied</c> (now), <c>nextLoad</c> (the next
    /// LoadRom, because <see cref="GameLauncher.OptionsFor"/> reads it) or
    /// <c>restart</c> (sockets opened at startup).
    /// </remarks>
    public static class SettingsJson
    {
        public enum Effect { Applied, NextLoad, Restart }

        private sealed record Field(string Name, Effect Effect, Func<EmulatorSettings, JsonNode?> Get, Func<EmulatorSettings, JsonNode, bool> Set);

        // ── Fields ────────────────────────────────────────────────────────────

        private static readonly Field[] Fields =
        {
            Path(nameof(EmulatorSettings.Bios9Path), s => s.Bios9Path, (s, v) => s.Bios9Path = v),
            Path(nameof(EmulatorSettings.Bios7Path), s => s.Bios7Path, (s, v) => s.Bios7Path = v),
            Path(nameof(EmulatorSettings.FirmwarePath), s => s.FirmwarePath, (s, v) => s.FirmwarePath = v),
            Bool(nameof(EmulatorSettings.UseRealBios), Effect.NextLoad, s => s.UseRealBios, (s, v) => s.UseRealBios = v),
            Enum(nameof(EmulatorSettings.CpuMode), Effect.Applied, s => s.CpuMode, (s, v) => s.CpuMode = v),
            Int(nameof(EmulatorSettings.TurboPercent), Effect.Applied, 10, 1000, s => s.TurboPercent, (s, v) => s.TurboPercent = v),
            Path(nameof(EmulatorSettings.SaveDirectory), s => s.SaveDirectory, (s, v) => s.SaveDirectory = v),

            // Generated firmware profile; limits as in the DS firmware's user settings.
            Text(nameof(EmulatorSettings.Nickname), 10, s => s.Nickname, (s, v) => s.Nickname = v),
            Text(nameof(EmulatorSettings.Message), 26, s => s.Message, (s, v) => s.Message = v),
            Int(nameof(EmulatorSettings.BirthMonth), Effect.NextLoad, 1, 12, s => s.BirthMonth, (s, v) => s.BirthMonth = v),
            Int(nameof(EmulatorSettings.BirthDay), Effect.NextLoad, 1, 31, s => s.BirthDay, (s, v) => s.BirthDay = v),
            Int(nameof(EmulatorSettings.Language), Effect.NextLoad, 0, 5, s => s.Language, (s, v) => s.Language = v),
            Int(nameof(EmulatorSettings.FavoriteColor), Effect.NextLoad, 0, 15, s => s.FavoriteColor, (s, v) => s.FavoriteColor = v),

            Enum(nameof(EmulatorSettings.WirelessMode), Effect.Applied, s => s.WirelessMode, (s, v) => s.WirelessMode = v),
            Int(nameof(EmulatorSettings.WirelessPort), Effect.Applied, 1, 65535, s => s.WirelessPort, (s, v) => s.WirelessPort = v),
            Bool(nameof(EmulatorSettings.WirelessLan), Effect.Applied, s => s.WirelessLan, (s, v) => s.WirelessLan = v),
            Str(nameof(EmulatorSettings.OnlineDns), Effect.Applied, s => s.OnlineDns, (s, v) => s.OnlineDns = v),

            Int(nameof(EmulatorSettings.PinePort), Effect.Restart, 1, 65535, s => s.PinePort, (s, v) => s.PinePort = v),
            Int(nameof(EmulatorSettings.GsPort), Effect.Restart, 1, 65535, s => s.GsPort, (s, v) => s.GsPort = v),
            Choice(nameof(EmulatorSettings.GsCodec), Effect.Restart, new[] { "jpeg", "png", "raw" }, s => s.GsCodec, (s, v) => s.GsCodec = v),
            Int(nameof(EmulatorSettings.GsQuality), Effect.Restart, 1, 100, s => s.GsQuality, (s, v) => s.GsQuality = v),
        };

        public static IReadOnlyList<string> Names { get; } = Fields.Select(f => f.Name).ToArray();

        public static Effect EffectOf(string name) =>
            Fields.First(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Effect;

        // ── Export / merge ────────────────────────────────────────────────────

        public static string Export(EmulatorSettings settings)
        {
            var obj = new JsonObject();
            foreach (var f in Fields)
                obj[f.Name] = f.Get(settings);
            return obj.ToJsonString();
        }

        /// <summary>
        /// Apply the keys present in <paramref name="json"/> (case-insensitive;
        /// unknown keys are ignored) and return the names of those that changed.
        /// </summary>
        /// <exception cref="InvalidDataException">Not a JSON object, or a value of the wrong type.</exception>
        public static IReadOnlyList<string> Merge(EmulatorSettings settings, string json)
        {
            JsonObject obj;
            try
            {
                obj = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Settings must be a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Invalid settings JSON: {ex.Message}");
            }

            var changed = new List<string>();
            foreach (var (key, value) in obj)
            {
                var field = Fields.FirstOrDefault(f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                    continue;
                try
                {
                    if (field.Set(settings, value ?? JsonValue.Create("")!))
                        changed.Add(field.Name);
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
                {
                    throw new InvalidDataException($"{field.Name}: {ex.Message}");
                }
            }
            return changed;
        }

        /// <summary><c>{"applied":[…],"nextLoad":[…],"restart":[…]}</c> for the changed keys.</summary>
        public static string Report(IEnumerable<string> changed)
        {
            var obj = new JsonObject { ["applied"] = new JsonArray(), ["nextLoad"] = new JsonArray(), ["restart"] = new JsonArray() };
            foreach (var name in changed)
            {
                string bucket = EffectOf(name) switch { Effect.Applied => "applied", Effect.NextLoad => "nextLoad", _ => "restart" };
                obj[bucket]!.AsArray().Add(name);
            }
            return obj.ToJsonString();
        }

        // ── Field builders ────────────────────────────────────────────────────

        private static bool Assign<T>(T current, T next, Action<T> set)
        {
            if (EqualityComparer<T>.Default.Equals(current, next))
                return false;
            set(next);
            return true;
        }

        // An empty path means "not set" (direct boot / next to the ROM).
        private static Field Path(string name, Func<EmulatorSettings, string?> get, Action<EmulatorSettings, string?> set) =>
            Str(name, Effect.NextLoad, get, set);

        private static Field Str(string name, Effect effect, Func<EmulatorSettings, string?> get, Action<EmulatorSettings, string?> set) =>
            new(name, effect, s => get(s) ?? "", (s, v) =>
            {
                string text = v.GetValue<string>().Trim();
                return Assign(get(s) ?? "", text, t => set(s, t.Length == 0 ? null : t));
            });

        private static Field Text(string name, int maxLength, Func<EmulatorSettings, string> get, Action<EmulatorSettings, string> set) =>
            new(name, Effect.NextLoad, s => get(s), (s, v) =>
            {
                string text = v.GetValue<string>();
                return Assign(get(s), text.Length > maxLength ? text[..maxLength] : text, t => set(s, t));
            });

        private static Field Bool(string name, Effect effect, Func<EmulatorSettings, bool> get, Action<EmulatorSettings, bool> set) =>
            new(name, effect, s => get(s), (s, v) => Assign(get(s), v.GetValue<bool>(), b => set(s, b)));

        private static Field Int(string name, Effect effect, int min, int max, Func<EmulatorSettings, int> get, Action<EmulatorSettings, int> set) =>
            new(name, effect, s => get(s), (s, v) => Assign(get(s), Math.Clamp(v.GetValue<int>(), min, max), n => set(s, n)));

        private static Field Enum<T>(string name, Effect effect, Func<EmulatorSettings, T> get, Action<EmulatorSettings, T> set) where T : struct, System.Enum =>
            new(name, effect, s => get(s).ToString(), (s, v) =>
            {
                string text = v.GetValue<string>();
                if (!System.Enum.TryParse<T>(text, ignoreCase: true, out var value) || !System.Enum.IsDefined(value))
                    throw new FormatException($"'{text}' is not one of {string.Join(", ", System.Enum.GetNames<T>())}.");
                return Assign(get(s), value, e => set(s, e));
            });

        private static Field Choice(string name, Effect effect, string[] choices, Func<EmulatorSettings, string> get, Action<EmulatorSettings, string> set) =>
            new(name, effect, s => get(s), (s, v) =>
            {
                string text = v.GetValue<string>().ToLowerInvariant();
                if (!choices.Contains(text))
                    throw new FormatException($"'{text}' is not one of {string.Join(", ", choices)}.");
                return Assign(get(s), text, c => set(s, c));
            });
    }
}
