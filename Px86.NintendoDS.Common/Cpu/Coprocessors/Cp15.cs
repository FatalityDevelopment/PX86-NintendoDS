using Px86.NintendoDS.Common.Interfaces;

namespace Px86.NintendoDS.Common.Cpu.Coprocessors
{
    /// <summary>
    /// ARM946E-S system control coprocessor (CP15), ARM9 only.
    ///
    /// Registers (CRn, CRm, opc2) — opc1 is always 0 on the ARM946E-S:
    ///   C0,C0,0   Main ID            0x41059461 (read-only)
    ///   C0,C0,1   Cache type         0x0F0D2112 (read-only)
    ///   C0,C0,2   TCM size           0x00140180 (ITCM 32 KB, DTCM 16 KB)
    ///   C1,C0,0   Control register
    ///               bit  0  Protection unit enable
    ///               bit  2  Data cache enable
    ///               bits 3–6 fixed 1
    ///               bit  7  Big-endian (fixed 0 on the DS)
    ///               bit 12  Instruction cache enable
    ///               bit 13  Exception vectors at 0xFFFF0000
    ///               bit 15  Pre-ARMv5 mode (LDR PC doesn't interwork) — ignored
    ///               bit 16  DTCM enable        bit 17  DTCM load mode (write-only)
    ///               bit 18  ITCM enable        bit 19  ITCM load mode (write-only)
    ///   C2,C0,0/1 Cachability bits (data/instruction)
    ///   C3,C0,0   Write-buffer control
    ///   C5,C0,0–3 Access permission
    ///   C6,C0–C7  Protection region base/size
    ///   C7,*,*    Cache operations — no-op, except C7,C0,4 and C7,C8,2 = wait for interrupt
    ///   C9,C1,0   DTCM region: base = bits 31–12, size = 512 &lt;&lt; bits 5–1
    ///   C9,C1,1   ITCM region: base fixed at 0 on the DS, size = 512 &lt;&lt; bits 5–1
    ///   C13,*     Process ID / trace process ID
    ///
    /// The ARM9 memory bus reads <see cref="DtcmBase"/>, <see cref="DtcmSize"/>,
    /// <see cref="ItcmSize"/> and the enable/load-mode flags, and listens to
    /// <see cref="TcmConfigurationChanged"/> to rebuild its fast paths.
    ///
    /// Reference: GBATEK "ARM CP15 System Control Coprocessor".
    /// </summary>
    public sealed class Cp15 : ICoprocessor
    {
        public const uint MAIN_ID       = 0x41059461u;
        public const uint CACHE_TYPE    = 0x0F0D2112u;
        public const uint TCM_SIZE      = 0x00140180u;

        public const uint CTRL_PU_ENABLE     = 1u << 0;
        public const uint CTRL_DCACHE        = 1u << 2;
        public const uint CTRL_ICACHE        = 1u << 12;
        public const uint CTRL_HIGH_VECTORS  = 1u << 13;
        public const uint CTRL_DTCM_ENABLE   = 1u << 16;
        public const uint CTRL_DTCM_LOAD     = 1u << 17;
        public const uint CTRL_ITCM_ENABLE   = 1u << 18;
        public const uint CTRL_ITCM_LOAD     = 1u << 19;

        private const uint CTRL_FIXED_ONES   = 0x00000078u;
        private const uint CTRL_WRITABLE     = 0x000FF085u;

        private readonly ArmCpu _cpu;
        private readonly uint[] _protectionRegions = new uint[8];

        private uint _control;
        private uint _dtcmRegion;
        private uint _itcmRegion;

        public Cp15(ArmCpu cpu)
        {
            _cpu = cpu;
            Reset();
        }

        /// <summary>Raised whenever the TCM base/size/enable/load-mode state changes.</summary>
        public event Action? TcmConfigurationChanged;

        public uint Control => _control;
        public uint DtcmRegion => _dtcmRegion;
        public uint ItcmRegion => _itcmRegion;

        public uint DtcmBase { get; private set; }
        public uint DtcmSize { get; private set; }
        public uint ItcmSize { get; private set; }

        public bool DtcmEnabled => (_control & CTRL_DTCM_ENABLE) != 0;
        public bool ItcmEnabled => (_control & CTRL_ITCM_ENABLE) != 0;

        /// <summary>Load mode: the TCM is write-only (reads go to the bus behind it).</summary>
        public bool DtcmLoadMode => (_control & CTRL_DTCM_LOAD) != 0;
        public bool ItcmLoadMode => (_control & CTRL_ITCM_LOAD) != 0;

        public uint DataCacheBits { get; private set; }
        public uint InstructionCacheBits { get; private set; }
        public uint WriteBufferBits { get; private set; }
        public uint DataAccessPermission { get; private set; }
        public uint InstructionAccessPermission { get; private set; }
        public uint ProcessId { get; private set; }

        public void Reset()
        {
            _control = CTRL_FIXED_ONES;
            _dtcmRegion = 0;
            _itcmRegion = 0;
            Array.Clear(_protectionRegions);
            DataCacheBits = InstructionCacheBits = WriteBufferBits = 0;
            DataAccessPermission = InstructionAccessPermission = 0;
            ProcessId = 0;
            Apply();
        }

        public uint Read(int opcode1, int crn, int crm, int opcode2)
        {
            switch (crn)
            {
                case 0:
                    return opcode2 switch
                    {
                        1 => CACHE_TYPE,
                        2 => TCM_SIZE,
                        _ => MAIN_ID,
                    };
                case 1:
                    return _control;
                case 2:
                    return opcode2 == 1 ? InstructionCacheBits : DataCacheBits;
                case 3:
                    return WriteBufferBits;
                case 5:
                    return opcode2 switch
                    {
                        0 => CompactPermissions(DataAccessPermission),
                        1 => CompactPermissions(InstructionAccessPermission),
                        2 => DataAccessPermission,
                        3 => InstructionAccessPermission,
                        _ => 0,
                    };
                case 6:
                    return _protectionRegions[crm & 7];
                case 9:
                    if (crm == 1)
                        return opcode2 == 0 ? _dtcmRegion : _itcmRegion;
                    return 0;
                case 13:
                    return ProcessId;
                default:
                    return 0;
            }
        }

        public void Write(int opcode1, int crn, int crm, int opcode2, uint value)
        {
            switch (crn)
            {
                case 1:
                    if (crm == 0 && opcode2 == 0)
                    {
                        _control = (value & CTRL_WRITABLE) | CTRL_FIXED_ONES;
                        Apply();
                    }
                    break;
                case 2:
                    if (opcode2 == 1) InstructionCacheBits = value;
                    else DataCacheBits = value;
                    break;
                case 3:
                    WriteBufferBits = value;
                    break;
                case 5:
                    switch (opcode2)
                    {
                        case 0: DataAccessPermission = ExpandPermissions(value); break;
                        case 1: InstructionAccessPermission = ExpandPermissions(value); break;
                        case 2: DataAccessPermission = value; break;
                        case 3: InstructionAccessPermission = value; break;
                    }
                    break;
                case 6:
                    _protectionRegions[crm & 7] = value;
                    break;
                case 7:
                    if ((crm == 0 && opcode2 == 4) || (crm == 8 && opcode2 == 2))
                        _cpu.Halted = true;
                    break;
                case 9:
                    if (crm == 1)
                    {
                        if (opcode2 == 0) _dtcmRegion = value;
                        else if (opcode2 == 1) _itcmRegion = value;
                        Apply();
                    }
                    break;
                case 13:
                    ProcessId = value;
                    break;
            }
        }

        public void Serialize(Core.StateSerializer s)
        {
            s.Section("Cp15");
            s.Sync(ref _control);
            s.Sync(ref _dtcmRegion);
            s.Sync(ref _itcmRegion);
            s.Sync(_protectionRegions);
            s.Sync(() => DataCacheBits, v => DataCacheBits = v);
            s.Sync(() => InstructionCacheBits, v => InstructionCacheBits = v);
            s.Sync(() => WriteBufferBits, v => WriteBufferBits = v);
            s.Sync(() => DataAccessPermission, v => DataAccessPermission = v);
            s.Sync(() => InstructionAccessPermission, v => InstructionAccessPermission = v);
            s.Sync(() => ProcessId, v => ProcessId = v);
            if (s.IsReading)
                Apply();
        }

        /// <summary>Direct-boot shortcut: write the values the firmware leaves behind.</summary>
        public void SetState(uint control, uint dtcmRegion, uint itcmRegion)
        {
            _control = (control & CTRL_WRITABLE) | CTRL_FIXED_ONES;
            _dtcmRegion = dtcmRegion;
            _itcmRegion = itcmRegion;
            Apply();
        }

        private void Apply()
        {
            DtcmBase = _dtcmRegion & 0xFFFFF000u;
            DtcmSize = RegionSize(_dtcmRegion);
            ItcmSize = RegionSize(_itcmRegion);
            _cpu.ExceptionBase = (_control & CTRL_HIGH_VECTORS) != 0 ? 0xFFFF0000u : 0x00000000u;
            TcmConfigurationChanged?.Invoke();
        }

        /// <summary>Size field (bits 5–1) n → 512 &lt;&lt; n bytes, minimum 4 KB, capped at 2 GB.</summary>
        private static uint RegionSize(uint region)
        {
            int n = Math.Max((int)(region >> 1) & 0x1F, 3);
            return (uint)Math.Min(512ul << n, 0x80000000ul);
        }

        // C5 opc2 0/1 use 2 bits per region; opc2 2/3 use 4 bits per region.
        private static uint ExpandPermissions(uint compact)
        {
            uint result = 0;
            for (int i = 0; i < 8; i++)
                result |= ((compact >> (i * 2)) & 3) << (i * 4);
            return result;
        }

        private static uint CompactPermissions(uint extended)
        {
            uint result = 0;
            for (int i = 0; i < 8; i++)
                result |= ((extended >> (i * 4)) & 3) << (i * 2);
            return result;
        }
    }
}
