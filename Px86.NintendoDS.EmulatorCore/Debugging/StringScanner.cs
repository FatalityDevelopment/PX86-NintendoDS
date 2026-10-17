using System.Text;

namespace Px86.NintendoDS.EmulatorCore.Debugging
{
    /// <summary>A string found in memory, at its CPU address.</summary>
    public readonly record struct FoundString(uint Address, int Length, string Text);

    /// <summary>A memory region the string dumper can scan.</summary>
    public readonly record struct ScanRegion(string Name, uint Start, uint Size);

    /// <summary>
    /// The string dumper behind Tools → Strings: finds ASCII text in a CPU's
    /// memory, like the Unix "strings" tool.
    ///
    /// A string is a run of printable bytes (20h–7Eh, plus tab, CR and LF) with
    /// at least <c>minLength</c> printable ones; it needn't end with a zero byte.
    /// Tab/CR/LF inside a string stay (game text often spans lines) and show
    /// as "\t", "\r", "\n". A string never starts with one: in code and
    /// data, a stray 0Dh before a literal is far more common than a real one.
    /// Runs longer than <see cref="MaxLength"/> are cut there (filler such as
    /// a screen of spaces) and their rest is skipped.
    /// </summary>
    public static class StringScanner
    {
        public const int MaxLength = 256;
        public const int DefaultMinLength = 4;

        public static List<FoundString> Scan(ReadOnlySpan<byte> data, uint baseAddress, int minLength = DefaultMinLength)
        {
            var found = new List<FoundString>();
            var text = new StringBuilder(MaxLength * 2);
            int i = 0;
            while (i < data.Length)
            {
                if (!IsPrintable(data[i]))
                {
                    i++;
                    continue;
                }
                int start = i, printable = 0;
                for (; i < data.Length && IsText(data[i]); i++)
                    if (IsPrintable(data[i]))
                        printable++;
                if (printable < minLength)
                    continue;
                int length = i - start;
                length = Math.Min(length, MaxLength);
                text.Clear();
                foreach (byte b in data.Slice(start, length))
                {
                    switch (b)
                    {
                        case (byte)'\n': text.Append("\\n"); break;
                        case (byte)'\r': text.Append("\\r"); break;
                        case (byte)'\t': text.Append("\\t"); break;
                        default: text.Append((char)b); break;
                    }
                }
                found.Add(new FoundString(baseAddress + (uint)start, length, text.ToString()));
            }
            return found;
        }

        private static bool IsPrintable(byte b) => b is >= 0x20 and < 0x7F;

        private static bool IsText(byte b) => b is >= 0x20 and < 0x7F or (byte)'\t' or (byte)'\n' or (byte)'\r';

        /// <summary>The RAM a CPU sees (no I/O, no BIOS). The ARM9's DTCM is where CP15 put it.</summary>
        public static IReadOnlyList<ScanRegion> Regions(NdsEmulator nds, DebugCpu cpu) => cpu == DebugCpu.Arm9
            ? new ScanRegion[]
            {
                new("Main RAM", 0x02000000, NdsEmulator.MainRamSize),
                new("ITCM", 0x01FF8000, 32 * 1024),
                new("DTCM", nds.Cp15.DtcmBase, Arm9MemoryBus.DtcmSize),
                new("Shared WRAM", 0x03000000, 32 * 1024),
                new("VRAM (LCDC)", 0x06800000, 656 * 1024),
            }
            : new ScanRegion[]
            {
                new("Main RAM", 0x02000000, NdsEmulator.MainRamSize),
                new("Shared WRAM", 0x03000000, 32 * 1024),
                new("ARM7 WRAM", 0x03800000, 64 * 1024),
            };

        /// <summary>
        /// Copy a region as the CPU sees it, without side effects. Main RAM is
        /// copied directly (one bus read per word would be a million calls).
        /// Call on the emulation thread or inside <c>EmulatorHost.Invoke</c>.
        /// </summary>
        public static byte[] Snapshot(NdsEmulator nds, DebugCpu cpu, ScanRegion region)
        {
            if (region.Start == 0x02000000 && region.Size == NdsEmulator.MainRamSize)
                return nds.MainRam.ToArray();
            var data = new byte[region.Size];
            DebugMemory.ReadBlock(nds, cpu, region.Start, data);
            return data;
        }
    }
}
