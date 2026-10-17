using System.Globalization;
using System.Text.Json.Serialization;

namespace Px86.NintendoDS.EmulatorCore.Cheats
{
    public enum CheatType
    {
        /// <summary>Action Replay DS code: pairs of 32-bit words ("XXXXXXXX YYYYYYYY" per line).</summary>
        ActionReplay,

        /// <summary>
        /// Plain writes applied every frame, one per line: "ADDRESS VALUE" in hex.
        /// The value's digit count picks the width: 2 = byte, 4 = halfword, 8 = word.
        /// </summary>
        Raw,
    }

    /// <summary>One cheat: a name, its code text and whether it is applied every frame.</summary>
    public sealed class Cheat
    {
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
        public bool Enabled { get; set; }
        public CheatType Type { get; set; } = CheatType.ActionReplay;

        /// <summary>The code as the user typed it (hex words, whitespace/dashes/comments allowed).</summary>
        public string Code { get; set; } = "";

        /// <summary>The C5 counter of an Action Replay code persists between frames.</summary>
        [JsonIgnore]
        internal uint Counter;

        // ── Parsing ───────────────────────────────────────────────────────────

        /// <summary>The Action Replay words, or an error message for the first bad token.</summary>
        public (uint[] Words, string? Error) ParseWords()
        {
            var words = new List<uint>();
            foreach (var token in Tokens(Code))
            {
                if (token.Length != 8 || !uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint word))
                    return (Array.Empty<uint>(), $"'{token}' is not an 8-digit hex word");
                words.Add(word);
            }
            if (words.Count % 2 != 0)
                return (Array.Empty<uint>(), "an Action Replay code needs an even number of words");
            return (words.ToArray(), null);
        }

        /// <summary>The raw writes (address, value, size in bytes), or an error message.</summary>
        public (List<(uint Address, uint Value, int Size)> Writes, string? Error) ParseRaw()
        {
            var writes = new List<(uint, uint, int)>();
            foreach (var raw in Code.Split('\n'))
            {
                var line = StripComment(raw);
                if (line.Length == 0)
                    continue;
                var parts = line.Split(new[] { ' ', '\t', ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                    return (new(), $"'{line}': expected ADDRESS VALUE");
                if (!uint.TryParse(Hex(parts[0]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint address))
                    return (new(), $"'{parts[0]}' is not a hex address");
                string value = Hex(parts[1]);
                int size = value.Length switch { <= 2 => 1, <= 4 => 2, <= 8 => 4, _ => 0 };
                if (size == 0 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                    return (new(), $"'{parts[1]}' is not a 2-, 4- or 8-digit hex value");
                writes.Add((address, v, size));
            }
            return (writes, null);
        }

        /// <summary>Null if the code parses, else what's wrong with it.</summary>
        public string? Validate() => Type == CheatType.ActionReplay ? ParseWords().Error : ParseRaw().Error;

        private static string Hex(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;

        private static string StripComment(string line)
        {
            int comment = line.IndexOfAny(new[] { ';', '#' });
            if (comment >= 0)
                line = line[..comment];
            int slash = line.IndexOf("//", StringComparison.Ordinal);
            if (slash >= 0)
                line = line[..slash];
            return line.Trim();
        }

        private static IEnumerable<string> Tokens(string code)
        {
            foreach (var raw in code.Split('\n'))
            {
                var line = StripComment(raw).Replace("-", " ");
                foreach (var token in line.Split(new[] { ' ', '\t', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    yield return Hex(token);
            }
        }

        /// <summary>Format words as "XXXXXXXX YYYYYYYY" lines.</summary>
        public static string FormatWords(IReadOnlyList<uint> words)
        {
            var lines = new List<string>();
            for (int i = 0; i + 1 < words.Count; i += 2)
                lines.Add($"{words[i]:X8} {words[i + 1]:X8}");
            return string.Join("\n", lines);
        }
    }
}
