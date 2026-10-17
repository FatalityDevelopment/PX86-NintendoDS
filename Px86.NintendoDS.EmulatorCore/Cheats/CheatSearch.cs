using System.Buffers.Binary;
using System.Collections;

namespace Px86.NintendoDS.EmulatorCore.Cheats
{
    public enum SearchCompare
    {
        EqualTo,
        NotEqualTo,
        GreaterThan,
        LessThan,
        Changed,
        Unchanged,
        Increased,
        Decreased,
        IncreasedBy,
        DecreasedBy,
    }

    /// <summary>
    /// A memory scanner over main RAM (02000000h–023FFFFFh) for finding the
    /// addresses behind lives, money, timers…: start with every address of
    /// the chosen size, then narrow the candidates down with comparisons
    /// against a value or against the previous scan, playing in between.
    /// Values are 1, 2 or 4 bytes, aligned to their size, signed or unsigned.
    /// </summary>
    public sealed class CheatSearch
    {
        public const uint RamBase = 0x02000000;

        private byte[] _previous = Array.Empty<byte>();
        private BitArray _candidates = new(0);

        public int Size { get; private set; } = 4;
        public bool Signed { get; private set; }
        public bool IsActive => _previous.Length > 0;
        public int Scans { get; private set; }

        /// <summary>How many addresses are still candidates.</summary>
        public int Count { get; private set; }

        /// <summary>Take the first snapshot: every aligned address is a candidate.</summary>
        public void Start(ReadOnlySpan<byte> mainRam, int size, bool signed)
        {
            if (size is not (1 or 2 or 4))
                throw new ArgumentOutOfRangeException(nameof(size), "1, 2 or 4 bytes");
            Size = size;
            Signed = signed;
            _previous = mainRam.ToArray();
            _candidates = new BitArray(mainRam.Length / size, true);
            Count = _candidates.Length;
            Scans = 0;
        }

        public void Reset()
        {
            _previous = Array.Empty<byte>();
            _candidates = new BitArray(0);
            Count = 0;
            Scans = 0;
        }

        /// <summary>Keep the candidates whose current value passes <paramref name="compare"/>, then remember the current values.</summary>
        public void Filter(ReadOnlySpan<byte> mainRam, SearchCompare compare, long value = 0)
        {
            if (!IsActive)
                throw new InvalidOperationException("Start a search first.");
            if (mainRam.Length != _previous.Length)
                throw new ArgumentException("RAM size changed.", nameof(mainRam));

            int count = 0;
            for (int i = 0; i < _candidates.Length; i++)
            {
                if (!_candidates[i])
                    continue;
                long now = Value(mainRam, i * Size), before = Value(_previous, i * Size);
                bool keep = compare switch
                {
                    SearchCompare.EqualTo => now == value,
                    SearchCompare.NotEqualTo => now != value,
                    SearchCompare.GreaterThan => now > value,
                    SearchCompare.LessThan => now < value,
                    SearchCompare.Changed => now != before,
                    SearchCompare.Unchanged => now == before,
                    SearchCompare.Increased => now > before,
                    SearchCompare.Decreased => now < before,
                    SearchCompare.IncreasedBy => now - before == value,
                    SearchCompare.DecreasedBy => before - now == value,
                    _ => false,
                };
                if (keep) count++;
                else _candidates[i] = false;
            }
            mainRam.CopyTo(_previous);
            Count = count;
            Scans++;
        }

        /// <summary>The first <paramref name="max"/> candidates: address, value at the last scan, current value.</summary>
        public List<(uint Address, long Previous, long Current)> Results(ReadOnlySpan<byte> mainRam, int max = 500)
        {
            var results = new List<(uint, long, long)>();
            for (int i = 0; i < _candidates.Length && results.Count < max; i++)
            {
                if (_candidates[i])
                    results.Add((RamBase + (uint)(i * Size), Value(_previous, i * Size), Value(mainRam, i * Size)));
            }
            return results;
        }

        private long Value(ReadOnlySpan<byte> ram, int offset) => (Size, Signed) switch
        {
            (1, false) => ram[offset],
            (1, true) => (sbyte)ram[offset],
            (2, false) => BinaryPrimitives.ReadUInt16LittleEndian(ram[offset..]),
            (2, true) => BinaryPrimitives.ReadInt16LittleEndian(ram[offset..]),
            (_, false) => BinaryPrimitives.ReadUInt32LittleEndian(ram[offset..]),
            _ => BinaryPrimitives.ReadInt32LittleEndian(ram[offset..]),
        };

        /// <summary>A raw cheat that holds <paramref name="address"/> at <paramref name="value"/>.</summary>
        public Cheat MakeCheat(uint address, long value, string name) => new()
        {
            Name = name,
            Type = CheatType.Raw,
            Enabled = true,
            Code = Size switch
            {
                1 => $"{address:X8} {(byte)value:X2}",
                2 => $"{address:X8} {(ushort)value:X4}",
                _ => $"{address:X8} {(uint)value:X8}",
            },
        };
    }
}
