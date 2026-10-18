using System.Text;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>One line the emulator printed.</summary>
    public readonly record struct EmulatorLogLine(long Index, DateTime Time, bool Error, string Text);

    /// <summary>
    /// What the emulator itself reports (game loaded, BIOS/firmware, saves, PINE and GS
    /// connections, wireless, breakpoints, warnings, errors) for the Log window. Everything
    /// the frontend and core say goes to the console, so <see cref="Install"/> tees
    /// Console.Out/Error into a ring buffer: nothing has to log twice, and the console
    /// (headless runs, the CodeDesigner terminal) still gets every line.
    /// </summary>
    public sealed class EmulatorLog
    {
        public const int MaxLines = 5000;

        private readonly object _lock = new();
        private readonly List<EmulatorLogLine> _lines = new();
        private long _next;

        public static EmulatorLog Shared { get; } = new();

        /// <summary>Route Console.Out and Console.Error through <see cref="Shared"/> (once).</summary>
        public static void Install()
        {
            if (Console.Out is TeeWriter)
                return;
            Console.SetOut(new TeeWriter(Console.Out, Shared, error: false));
            Console.SetError(new TeeWriter(Console.Error, Shared, error: true));
        }

        public void Add(string text, bool error = false)
        {
            lock (_lock)
            {
                _lines.Add(new EmulatorLogLine(_next++, DateTime.Now, error, text));
                if (_lines.Count > MaxLines)
                    _lines.RemoveRange(0, _lines.Count - MaxLines);
            }
        }

        /// <summary>Lines with Index ≥ <paramref name="from"/>.</summary>
        public EmulatorLogLine[] Snapshot(long from)
        {
            lock (_lock)
                return _lines.Where(l => l.Index >= from).ToArray();
        }

        public void Clear()
        {
            lock (_lock)
                _lines.Clear();
        }

        /// <summary>Writes through to the real console and collects complete lines.</summary>
        public sealed class TeeWriter(TextWriter inner, EmulatorLog log, bool error) : TextWriter
        {
            private readonly StringBuilder _pending = new();

            public override Encoding Encoding => inner.Encoding;

            public override void Write(char value)
            {
                inner.Write(value);
                lock (_pending)
                {
                    if (value == '\n')
                        Flush(_pending);
                    else if (value != '\r')
                        _pending.Append(value);
                }
            }

            public override void Write(string? value)
            {
                if (value == null)
                    return;
                inner.Write(value);
                lock (_pending)
                {
                    foreach (char c in value)
                    {
                        if (c == '\n')
                            Flush(_pending);
                        else if (c != '\r')
                            _pending.Append(c);
                    }
                }
            }

            public override void WriteLine(string? value)
            {
                Write(value);
                Write('\n');
            }

            public override void Flush() => inner.Flush();

            private void Flush(StringBuilder line)
            {
                string text = line.ToString().TrimEnd();
                line.Clear();
                if (text.Length > 0)
                    log.Add(text, error);
            }
        }
    }
}
