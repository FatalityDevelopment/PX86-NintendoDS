namespace Px86.NintendoDS.EmulatorCore.Jit
{
    /// <summary>
    /// Remembers which 256-byte pages of executable RAM (main RAM, shared
    /// WRAM, ARM7 WRAM, ITCM) hold cached blocks, so a write over a block's
    /// bytes — self-modifying code, an overlay loaded by DMA or the cartridge,
    /// a cheat — invalidates exactly that block. The buses call
    /// <see cref="OnWrite"/> on their RAM write paths; the check is one array
    /// lookup when the page holds no code. Data that shares a page with code
    /// (small variables, literal pools) only costs a scan of that page's blocks.
    /// </summary>
    public sealed class CodeTracker
    {
        public const int PageShift = 8;
        public const int PageSize = 1 << PageShift;

        private sealed class Region
        {
            public readonly byte[] Memory;
            public readonly bool[] HasCode;
            public readonly List<CachedBlock>?[] Blocks;

            public Region(byte[] memory)
            {
                Memory = memory;
                HasCode = new bool[(memory.Length + PageSize - 1) / PageSize];
                Blocks = new List<CachedBlock>?[HasCode.Length];
            }
        }

        private readonly Region[] _regions;

        public CodeTracker(params byte[][] executableMemories)
        {
            _regions = executableMemories.Select(m => new Region(m)).ToArray();
        }

        private Region? Find(byte[] memory)
        {
            foreach (var r in _regions)
                if (ReferenceEquals(r.Memory, memory))
                    return r;
            return null;
        }

        /// <summary>True for memory whose writes are tracked (code there can be cached).</summary>
        public bool IsTracked(byte[] memory) => Find(memory) != null;

        /// <summary>Record that <paramref name="block"/> was built from <paramref name="memory"/>[<paramref name="index"/>…].</summary>
        public void Register(byte[] memory, int index, CachedBlock block)
        {
            var region = Find(memory);
            if (region == null)
                return; // read-only (BIOS): never invalidated
            int page = index >> PageShift;
            region.HasCode[page] = true;
            block.MemoryStart = index;
            (region.Blocks[page] ??= new List<CachedBlock>()).Add(block);
        }

        /// <summary>
        /// A write of up to 4 bytes hit <paramref name="memory"/>[<paramref name="index"/>…]
        /// (index aligned to the access size, so the bytes never cross a page).
        /// </summary>
        public void OnWrite(byte[] memory, int index)
        {
            var region = Find(memory);
            if (region == null)
                return;
            int page = index >> PageShift;
            if ((uint)page >= (uint)region.HasCode.Length || !region.HasCode[page])
                return;
            var blocks = region.Blocks[page];
            if (blocks == null)
            {
                region.HasCode[page] = false;
                return;
            }
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                var b = blocks[i];
                if (index < b.MemoryStart + b.ByteLength && index + 4 > b.MemoryStart)
                {
                    b.Valid = false;
                    blocks.RemoveAt(i);
                }
            }
            if (blocks.Count == 0)
            {
                region.Blocks[page] = null;
                region.HasCode[page] = false;
            }
        }

        /// <summary>Fast path for main RAM writes (the overwhelmingly common case).</summary>
        public bool MainRamPageHasCode(int index) => _regions[0].HasCode[index >> PageShift];

        /// <summary>Forget everything (reset, state load, memory remapping).</summary>
        public void Clear()
        {
            foreach (var r in _regions)
            {
                for (int i = 0; i < r.HasCode.Length; i++)
                {
                    if (r.Blocks[i] is { } blocks)
                        foreach (var b in blocks)
                            b.Valid = false;
                    r.Blocks[i] = null;
                    r.HasCode[i] = false;
                }
            }
        }
    }
}
