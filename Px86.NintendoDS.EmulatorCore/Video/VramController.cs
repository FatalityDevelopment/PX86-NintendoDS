using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Video
{
    /// <summary>
    /// The nine VRAM banks (A–I, 656 KB total) and their mapping into the
    /// CPU-visible and engine-visible address spaces.
    ///
    /// Each VRAMCNT_x register (0x04000240–0x04000249, ARM9) selects a bank's
    /// MST (purpose) and OFS (slot). Every destination is modelled as a
    /// <see cref="VramRegion"/>: a table of 16 KB pages, each listing the banks
    /// mapped there. Overlapping banks are legal: reads OR the banks together,
    /// writes go to all of them.
    ///
    ///   Region            CPU address    Size   Banks (MST)
    ///   Lcdc              06800000h      656K   A–I (0)
    ///   BgA               06000000h      512K   A–D (1, OFS 0–3), E (1), F/G (1)
    ///   ObjA              06400000h      256K   A/B (2), E (2), F/G (2)
    ///   BgB               06200000h      128K   C (4), H (1), I (1)
    ///   ObjB              06600000h      128K   D (4), I (2)
    ///   Arm7              06000000h (ARM7)  256K   C/D (2, OFS 0–1)
    ///   Texture           (3D only)      512K   A–D (3, slot = OFS)
    ///   TexturePalette    (3D only)      96K    E (3), F/G (3)
    ///   BgExtPaletteA     (2D only)      32K    E (4), F/G (4)
    ///   ObjExtPaletteA    (2D only)      8K     F/G (5)
    ///   BgExtPaletteB     (2D only)      32K    H (2)
    ///   ObjExtPaletteB    (2D only)      8K     I (3)
    ///
    /// Reference: GBATEK "DS Memory Control - VRAM".
    /// </summary>
    public sealed class VramController
    {
        public const int BANK_A = 0, BANK_B = 1, BANK_C = 2, BANK_D = 3, BANK_E = 4,
                         BANK_F = 5, BANK_G = 6, BANK_H = 7, BANK_I = 8;

        public const int PageSize = 0x4000;

        private static readonly int[] BankSizes =
        {
            128 * 1024, 128 * 1024, 128 * 1024, 128 * 1024, 64 * 1024,
            16 * 1024, 16 * 1024, 32 * 1024, 16 * 1024,
        };

        // Offset of each bank inside the LCDC window (06800000h).
        private static readonly int[] LcdcOffsets =
        {
            0x00000, 0x20000, 0x40000, 0x60000, 0x80000, 0x90000, 0x94000, 0x98000, 0xA0000,
        };

        private readonly byte[][] _banks = new byte[9][];
        private readonly byte[] _control = new byte[9];

        public VramController()
        {
            for (int i = 0; i < 9; i++)
                _banks[i] = new byte[BankSizes[i]];

            Lcdc = new VramRegion("LCDC", 0x100000, _banks);
            BgA = new VramRegion("BG-A", 0x80000, _banks);
            ObjA = new VramRegion("OBJ-A", 0x40000, _banks);
            BgB = new VramRegion("BG-B", 0x20000, _banks);
            ObjB = new VramRegion("OBJ-B", 0x20000, _banks);
            Arm7 = new VramRegion("ARM7", 0x40000, _banks);
            Texture = new VramRegion("Texture", 0x80000, _banks);
            TexturePalette = new VramRegion("TexPal", 0x18000, _banks);
            BgExtPaletteA = new VramRegion("BgExtPal-A", 0x8000, _banks);
            ObjExtPaletteA = new VramRegion("ObjExtPal-A", 0x4000, _banks);
            BgExtPaletteB = new VramRegion("BgExtPal-B", 0x8000, _banks);
            ObjExtPaletteB = new VramRegion("ObjExtPal-B", 0x4000, _banks);
        }

        public VramRegion Lcdc { get; }
        public VramRegion BgA { get; }
        public VramRegion ObjA { get; }
        public VramRegion BgB { get; }
        public VramRegion ObjB { get; }
        public VramRegion Arm7 { get; }
        public VramRegion Texture { get; }
        public VramRegion TexturePalette { get; }
        public VramRegion BgExtPaletteA { get; }
        public VramRegion ObjExtPaletteA { get; }
        public VramRegion BgExtPaletteB { get; }
        public VramRegion ObjExtPaletteB { get; }

        /// <summary>Raw storage of a bank (for save states and the debugger).</summary>
        public byte[] Bank(int index) => _banks[index];

        /// <summary>Raised after any remap; the 3D engine uses it to drop texture caches.</summary>
        public event Action? MappingChanged;

        // ── Registers ─────────────────────────────────────────────────────────

        public byte ReadControl(int bank) => _control[bank];

        public void WriteControl(int bank, byte value)
        {
            if (_control[bank] == value)
                return;
            _control[bank] = value;
            Remap();
        }

        /// <summary>ARM7 VRAMSTAT (04000240h): bit 0 = C mapped to ARM7, bit 1 = D mapped to ARM7.</summary>
        public byte Vramstat
        {
            get
            {
                byte value = 0;
                if ((_control[BANK_C] & 0x87) == 0x82) value |= 1;
                if ((_control[BANK_D] & 0x87) == 0x82) value |= 2;
                return value;
            }
        }

        public void Reset()
        {
            foreach (var bank in _banks)
                Array.Clear(bank);
            Array.Clear(_control);
            Remap();
        }

        // ── Mapping ───────────────────────────────────────────────────────────

        private void Remap()
        {
            foreach (var region in AllRegions())
                region.Clear();

            for (int bank = 0; bank < 9; bank++)
            {
                byte control = _control[bank];
                if ((control & 0x80) == 0)
                    continue;
                MapBank(bank, control & 7, (control >> 3) & 3);
            }

            MappingChanged?.Invoke();
        }

        private IEnumerable<VramRegion> AllRegions()
        {
            yield return Lcdc; yield return BgA; yield return ObjA; yield return BgB; yield return ObjB;
            yield return Arm7; yield return Texture; yield return TexturePalette;
            yield return BgExtPaletteA; yield return ObjExtPaletteA; yield return BgExtPaletteB; yield return ObjExtPaletteB;
        }

        private void MapBank(int bank, int mst, int ofs)
        {
            int size = BankSizes[bank];

            switch (bank)
            {
                case BANK_A:
                case BANK_B:
                case BANK_C:
                case BANK_D:
                    // MST 4 only exists for C (BG-B) and D (OBJ-B); A/B ignore MST bit 2.
                    if (bank >= BANK_C && (mst & 4) != 0)
                    {
                        if (mst == 4)
                        {
                            if (bank == BANK_C) BgB.Map(bank, 0, size);
                            else ObjB.Map(bank, 0, size);
                        }
                        break;
                    }
                    switch (mst & 3)
                    {
                        case 0: Lcdc.Map(bank, LcdcOffsets[bank], size); break;
                        case 1: BgA.Map(bank, 0x20000 * ofs, size); break;
                        case 2:
                            if (bank <= BANK_B) ObjA.Map(bank, 0x20000 * (ofs & 1), size);
                            else Arm7.Map(bank, 0x20000 * (ofs & 1), size);
                            break;
                        case 3: Texture.Map(bank, 0x20000 * ofs, size); break;
                    }
                    break;

                case BANK_E:
                    switch (mst)
                    {
                        case 0: Lcdc.Map(bank, LcdcOffsets[bank], size); break;
                        case 1: BgA.Map(bank, 0, size); break;
                        case 2: ObjA.Map(bank, 0, size); break;
                        case 3: TexturePalette.Map(bank, 0, size); break;
                        case 4: BgExtPaletteA.Map(bank, 0, 0x8000); break;
                    }
                    break;

                case BANK_F:
                case BANK_G:
                {
                    int offset = 0x4000 * (ofs & 1) + 0x10000 * (ofs >> 1);
                    switch (mst)
                    {
                        case 0: Lcdc.Map(bank, LcdcOffsets[bank], size); break;
                        case 1: BgA.Map(bank, offset, size); break;
                        case 2: ObjA.Map(bank, offset, size); break;
                        case 3: TexturePalette.Map(bank, 0x4000 * ((ofs & 1) + (ofs >> 1) * 4), size); break;
                        case 4: BgExtPaletteA.Map(bank, 0x4000 * (ofs & 1), size); break;
                        case 5: ObjExtPaletteA.Map(bank, 0, size); break;
                    }
                    break;
                }

                case BANK_H:
                    switch (mst & 3)
                    {
                        case 0: Lcdc.Map(bank, LcdcOffsets[bank], size); break;
                        case 1: BgB.Map(bank, 0, size); break;
                        case 2: BgExtPaletteB.Map(bank, 0, size); break;
                    }
                    break;

                case BANK_I:
                    switch (mst & 3)
                    {
                        case 0: Lcdc.Map(bank, LcdcOffsets[bank], size); break;
                        case 1: BgB.Map(bank, 0x8000, size); break;
                        case 2: ObjB.Map(bank, 0, size); break;
                        case 3: ObjExtPaletteB.Map(bank, 0, size); break;
                    }
                    break;
            }
        }

        // ── ARM9 CPU access (0x06000000–0x06FFFFFF) ───────────────────────────

        public VramRegion RegionForArm9(uint address) => ((address >> 21) & 7) switch
        {
            0 => BgA,
            1 => BgB,
            2 => ObjA,
            3 => ObjB,
            _ => Lcdc,
        };

        public uint Read32(uint address) => RegionForArm9(address).Read32(address);
        public ushort Read16(uint address) => RegionForArm9(address).Read16(address);
        public byte Read8(uint address) => RegionForArm9(address).Read8(address);
        public void Write32(uint address, uint value) => RegionForArm9(address).Write32(address, value);
        public void Write16(uint address, ushort value) => RegionForArm9(address).Write16(address, value);
    }

    /// <summary>
    /// One VRAM destination, as a table of 16 KB pages. Addresses are taken
    /// modulo the region's window size, which mirrors the region the way the
    /// hardware does.
    /// </summary>
    public sealed class VramRegion
    {
        private readonly byte[][] _banks;
        private readonly int _pageCount;
        private readonly int _windowMask;

        // Fast path: the first bank mapped on each page (-1 = unmapped) and its byte offset.
        private readonly int[] _firstBank;
        private readonly int[] _firstOffset;

        // Slow path for pages with several overlapping banks.
        private readonly List<(int Bank, int Offset)>?[] _extra;

        public VramRegion(string name, int windowSize, byte[][] banks)
        {
            Name = name;
            _banks = banks;
            _pageCount = windowSize / VramController.PageSize;
            _windowMask = windowSize - 1;
            if ((windowSize & _windowMask) != 0)
                _windowMask = -1;
            WindowSize = windowSize;
            _firstBank = new int[_pageCount];
            _firstOffset = new int[_pageCount];
            _extra = new List<(int, int)>?[_pageCount];
            Clear();
        }

        public string Name { get; }

        public int WindowSize { get; }

        internal void Clear()
        {
            Array.Fill(_firstBank, -1);
            Array.Clear(_firstOffset);
            Array.Clear(_extra);
        }

        internal void Map(int bank, int regionOffset, int size)
        {
            for (int offset = 0; offset < size; offset += VramController.PageSize)
            {
                int page = ((regionOffset + offset) / VramController.PageSize) % _pageCount;
                if (_firstBank[page] < 0)
                {
                    _firstBank[page] = bank;
                    _firstOffset[page] = offset;
                }
                else
                {
                    (_extra[page] ??= new List<(int, int)>()).Add((bank, offset));
                }
            }
        }

        private int PageIndex(uint address, out int pageOffset)
        {
            int local = _windowMask >= 0 ? (int)(address & (uint)_windowMask) : (int)(address % (uint)WindowSize);
            pageOffset = local & (VramController.PageSize - 1);
            return local / VramController.PageSize;
        }

        public bool IsMapped(uint address) => _firstBank[PageIndex(address, out _)] >= 0;

        public byte Read8(uint address)
        {
            int page = PageIndex(address, out int offset);
            int bank = _firstBank[page];
            if (bank < 0) return 0;
            byte value = _banks[bank][_firstOffset[page] + offset];
            if (_extra[page] is { } extra)
                foreach (var (b, o) in extra) value |= _banks[b][o + offset];
            return value;
        }

        public ushort Read16(uint address)
        {
            int page = PageIndex(address & ~1u, out int offset);
            int bank = _firstBank[page];
            if (bank < 0) return 0;
            ushort value = MemoryAccess.Read16(_banks[bank], _firstOffset[page] + offset);
            if (_extra[page] is { } extra)
                foreach (var (b, o) in extra) value |= MemoryAccess.Read16(_banks[b], o + offset);
            return value;
        }

        public uint Read32(uint address)
        {
            int page = PageIndex(address & ~3u, out int offset);
            int bank = _firstBank[page];
            if (bank < 0) return 0;
            uint value = MemoryAccess.Read32(_banks[bank], _firstOffset[page] + offset);
            if (_extra[page] is { } extra)
                foreach (var (b, o) in extra) value |= MemoryAccess.Read32(_banks[b], o + offset);
            return value;
        }

        public void Write8(uint address, byte value)
        {
            int page = PageIndex(address, out int offset);
            int bank = _firstBank[page];
            if (bank < 0) return;
            _banks[bank][_firstOffset[page] + offset] = value;
            if (_extra[page] is { } extra)
                foreach (var (b, o) in extra) _banks[b][o + offset] = value;
        }

        public void Write16(uint address, ushort value)
        {
            int page = PageIndex(address & ~1u, out int offset);
            int bank = _firstBank[page];
            if (bank < 0) return;
            MemoryAccess.Write16(_banks[bank], _firstOffset[page] + offset, value);
            if (_extra[page] is { } extra)
                foreach (var (b, o) in extra) MemoryAccess.Write16(_banks[b], o + offset, value);
        }

        public void Write32(uint address, uint value)
        {
            int page = PageIndex(address & ~3u, out int offset);
            int bank = _firstBank[page];
            if (bank < 0) return;
            MemoryAccess.Write32(_banks[bank], _firstOffset[page] + offset, value);
            if (_extra[page] is { } extra)
                foreach (var (b, o) in extra) MemoryAccess.Write32(_banks[b], o + offset, value);
        }
    }
}
