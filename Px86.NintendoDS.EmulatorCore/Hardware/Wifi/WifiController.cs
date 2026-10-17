using System.Buffers.Binary;
using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Hardware.Wifi
{
    /// <summary>
    /// The DS Wifi MAC (ARM7 only), its 8 KB packet RAM and the two serial
    /// chips behind it (<see cref="WifiBaseband"/>, <see cref="WifiRf"/>).
    ///
    /// Address space (ARM7)
    ///   04804000h  Wifi RAM 8 KB (RX ring, TX frames, WEP keys at 5F80h)
    ///   04808000h  MAC registers (also mirrored at 04800000h); 8-bit writes are ignored
    ///
    /// Main registers (offsets from 04808000h)
    ///   000  W_ID              chip ID (1440h DS, C340h DS-Lite)
    ///   004  W_MODE_RST        0 TX master enable; 13/14 reset port groups (write-only)
    ///   010  W_IF / 012 W_IE   0 RX done, 1 TX done, 6 RX start, 7 TX start, 8/9 TX/RX buffer count,
    ///                          11 RF wakeup, 12 multiplay CMD done, 13 post-beacon, 14 beacon, 15 pre-beacon
    ///   018  W_MACADDR (3)     020 W_BSSID (3)   028 W_AID_LOW   02A W_AID_FULL
    ///   030  W_RXCNT           0 latch WR_ADDR → WRCSR, 7 REPLY1 → REPLY2, 15 RX enable
    ///   036  W_POWER_US        0 = 1 stops the µs counter
    ///   03C  W_POWERSTATE      1 request power-up (→ IRQ11), 9 powered down
    ///   040  W_POWERFORCE      15 apply bit 0 to POWERSTATE.9
    ///   044  W_RANDOM          11-bit LFSR, X = (X & 1) ^ (X rol 1)
    ///   050..064 RX ring       BEGIN, END, WRCSR, WR_ADDR, RD_ADDR, READCSR, COUNT, RD_DATA, GAP, GAPDISP
    ///   068..076 TX write port WR_ADDR, COUNT, WR_DATA, GAP, GAPDISP
    ///   080  W_TXBUF_BEACON    090 W_TXBUF_CMD  094/098 W_TXBUF_REPLY1/2  0A0/0A4/0A8 W_TXBUF_LOC1..3
    ///                          (0–11 halfword address of the TX header, 13 keep sequence number, 15 enable)
    ///   0AC/0AE/0B0 W_TXREQ    reset / set / read request bits (0 LOC1, 1 CMD, 2 LOC2, 3 LOC3)
    ///   0B4  W_TXBUF_RESET     0B6 W_TXBUSY   0B8 W_TXSTAT
    ///   0D0  W_RXFILTER        0 accept group frames of any BSSID
    ///   0E8  W_US_COUNTCNT     0EA W_US_COMPARECNT (1 = force IRQ14)  0F0..0F6 compare  0F8..0FE count (64-bit µs)
    ///   08C  W_BEACONINT       110 W_PRE_BEACON  118 W_CMD_COUNT (10 µs units)  11C W_BEACON_COUNT  134 W_POST_BEACON
    ///   158..168 baseband port, 17C..184 RF port, 19C W_RF_PINS, 214 W_RF_STATUS, 210 W_TX_SEQNO
    ///   1B0..1BF W_RXSTAT      8-bit event counters, cleared when read; 1C0 W_TX_ERR_COUNT; 1C4 W_RX_COUNT
    ///   21C  W_IF_SET          force W_IF bits
    ///
    /// Frames
    ///   TX: a 12-byte hardware header (status, rate at 8, length incl. FCS at 0Ah) followed by the IEEE frame.
    ///   RX: the hardware writes a 12-byte header (flags, rate, length excl. FCS, RSSI) and the frame into the
    ///   ring at WRCSR, then moves WRCSR past it (rounded up to 4 bytes) without passing READCSR.
    ///   Frames go to an <see cref="IWifiMedium"/>: nothing, other consoles or a software access point. ACKs are
    ///   implicit: every unicast transmit succeeds once, and frames for other stations are filtered out here.
    ///
    /// Multiplay
    ///   A master sends W_TXBUF_CMD frames, waits for one REPLY per slave in the frame body's slave mask (body
    ///   halfword 1), stores the replies in its RX ring, sends the automatic CMD ACK and raises IRQ12. A slave
    ///   (W_AID_LOW ≠ 0) receiving a CMD for it answers with REPLY2 (moved from REPLY1), or an empty reply.
    ///
    /// Timers
    ///   The µs counter runs from the scheduler. Every 1024 µs (when bits 0–9 wrap) the beacon, post-beacon and
    ///   compare logic runs and the medium is polled for incoming frames.
    ///
    /// Reference: GBATEK "DS Wifi I/O Map" through "DS Wifi Hardware Headers".
    /// </summary>
    public sealed class WifiController
    {
        // ── Register offsets (from 04808000h) ─────────────────────────────────
        public const uint W_ID = 0x000, W_MODE_RST = 0x004, W_MODE_WEP = 0x006, W_TXSTATCNT = 0x008;
        public const uint W_IF = 0x010, W_IE = 0x012, W_MACADDR = 0x018, W_BSSID = 0x020, W_AID_LOW = 0x028, W_AID_FULL = 0x02A;
        public const uint W_RXCNT = 0x030, W_POWER_US = 0x036, W_POWER_TX = 0x038, W_POWERSTATE = 0x03C, W_POWERFORCE = 0x040;
        public const uint W_RANDOM = 0x044;
        public const uint W_RXBUF_BEGIN = 0x050, W_RXBUF_END = 0x052, W_RXBUF_WRCSR = 0x054, W_RXBUF_WR_ADDR = 0x056;
        public const uint W_RXBUF_RD_ADDR = 0x058, W_RXBUF_READCSR = 0x05A, W_RXBUF_COUNT = 0x05C, W_RXBUF_RD_DATA = 0x060;
        public const uint W_RXBUF_GAP = 0x062, W_RXBUF_GAPDISP = 0x064;
        public const uint W_TXBUF_WR_ADDR = 0x068, W_TXBUF_COUNT = 0x06C, W_TXBUF_WR_DATA = 0x070, W_TXBUF_GAP = 0x074, W_TXBUF_GAPDISP = 0x076;
        public const uint W_TXBUF_BEACON = 0x080, W_LISTENCOUNT = 0x088, W_BEACONINT = 0x08C, W_LISTENINT = 0x08E;
        public const uint W_TXBUF_CMD = 0x090, W_TXBUF_REPLY1 = 0x094, W_TXBUF_REPLY2 = 0x098;
        public const uint W_TXBUF_LOC1 = 0x0A0, W_TXBUF_LOC2 = 0x0A4, W_TXBUF_LOC3 = 0x0A8;
        public const uint W_TXREQ_RESET = 0x0AC, W_TXREQ_SET = 0x0AE, W_TXREQ_READ = 0x0B0, W_TXBUF_RESET = 0x0B4;
        public const uint W_TXBUSY = 0x0B6, W_TXSTAT = 0x0B8, W_PREAMBLE = 0x0BC, W_RXFILTER = 0x0D0;
        public const uint W_US_COUNTCNT = 0x0E8, W_US_COMPARECNT = 0x0EA, W_US_COMPARE0 = 0x0F0, W_US_COUNT0 = 0x0F8;
        public const uint W_CONTENTFREE = 0x10C, W_PRE_BEACON = 0x110, W_CMD_COUNT = 0x118, W_BEACON_COUNT = 0x11C, W_POST_BEACON = 0x134;
        public const uint W_BB_CNT = 0x158, W_BB_WRITE = 0x15A, W_BB_READ = 0x15C, W_BB_BUSY = 0x15E, W_BB_MODE = 0x160, W_BB_POWER = 0x168;
        public const uint W_RF_DATA2 = 0x17C, W_RF_DATA1 = 0x17E, W_RF_BUSY = 0x180, W_RF_CNT = 0x184;
        public const uint W_TX_HDR_CNT = 0x194, W_RF_PINS = 0x19C;
        public const uint W_RXSTAT_INC_IF = 0x1A8, W_RXSTAT_OVF_IF = 0x1AC, W_RXSTAT = 0x1B0, W_TX_ERR_COUNT = 0x1C0, W_RX_COUNT = 0x1C4;
        public const uint W_CMD_STAT = 0x1D0, W_TX_SEQNO = 0x210, W_RF_STATUS = 0x214, W_IF_SET = 0x21C, W_RXTX_ADDR = 0x268;

        // ── IRQ bits ──────────────────────────────────────────────────────────
        public const int IRQ_RX_DONE = 0, IRQ_TX_DONE = 1, IRQ_RX_START = 6, IRQ_TX_START = 7, IRQ_TXBUF_COUNT = 8;
        public const int IRQ_RXBUF_COUNT = 9, IRQ_RF_WAKEUP = 11, IRQ_CMD_DONE = 12, IRQ_POST_BEACON = 13, IRQ_BEACON = 14, IRQ_PRE_BEACON = 15;

        private const int UsPerTick = 1024;

        /// <summary>The multiplay CMD ACK destination, hardcoded in the hardware.</summary>
        private static readonly byte[] CmdAckAddress = { 0x03, 0x09, 0xBF, 0x00, 0x00, 0x03 };
        private static readonly byte[] ReplyAddress = { 0x03, 0x09, 0xBF, 0x00, 0x00, 0x10 };

        private readonly Scheduler _scheduler;
        private readonly InterruptController _irq;
        private readonly ushort[] _reg = new ushort[0x800];
        private bool _irqLine;

        // µs counter: value at _usBaseTime (scheduler time), counting while enabled.
        private ulong _usBase;
        private long _usBaseTime;
        private ushort _random = 1;
        private long _randomTime;
        private bool _preBeaconFired;
        private ushort _rxDataLatch;

        // Transmit in progress (one at a time); -1 = idle.
        private int _txSlot = -1;
        private long _cmdCountTime;

        private static long _nextId = Random.Shared.NextInt64(1, long.MaxValue / 2);

        public WifiController(Scheduler scheduler, InterruptController arm7Irq)
        {
            _scheduler = scheduler;
            _irq = arm7Irq;
            scheduler.SetHandler(SchedulerEvent.WifiTick, OnTick);
            scheduler.SetHandler(SchedulerEvent.WifiTx, OnTransmitDone);
            Id = Interlocked.Increment(ref _nextId);
            Reset();
        }

        public byte[] Ram { get; } = new byte[0x2000];
        public WifiBaseband Baseband { get; } = new();
        public WifiRf Rf { get; } = new();

        /// <summary>Where frames go and come from. Change it any time (local wireless, online, nothing).</summary>
        public IWifiMedium Medium { get; set; } = NullWifiMedium.Instance;

        /// <summary>Identifies this console on a medium.</summary>
        public long Id { get; }

        /// <summary>W_ID value: 1440h for an original DS (firmware RF type 2), C340h for a DS-Lite.</summary>
        public ushort ChipId { get; set; } = 0xC340;

        /// <summary>How long (wall clock) a multiplay master waits for slaves' replies.</summary>
        public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromMilliseconds(30);

        /// <summary>Frames sent and received, for the UI and tests.</summary>
        public long FramesSent { get; private set; }
        public long FramesReceived { get; private set; }

        /// <summary>Frames that arrived but were not stored: receiver off, filtered out, or no room in the RX ring.</summary>
        public long FramesIgnored { get; private set; }
        public long FramesDropped { get; private set; }

        /// <summary>A frame left this console (after the hardware's header fix-ups).</summary>
        public event Action<WifiPacket>? Transmitted;

        public void Reset()
        {
            Array.Clear(_reg);
            Array.Clear(Ram);
            Baseband.Reset();
            Rf.Reset();
            R(W_ID) = ChipId;
            R(W_TX_RETRYLIMIT) = 0x0707;
            R(W_POWER_US) = 0x0001;
            R(W_POWER_TX) = 0x0003;
            R(W_POWERSTATE) = 0x0200;
            R(W_RXBUF_BEGIN) = 0x4000;
            R(W_RXBUF_END) = 0x4800;
            R(W_BEACONINT) = 0x0064;
            R(0x09C) = 0x0050;
            R(W_TXREQ_READ) = 0x0010;
            R(W_PREAMBLE) = 0x0001;
            R(W_RXFILTER) = 0x0401;
            R(0x0D4) = 0x0001;
            R(0x0D8) = 0x0004;
            R(0x0DA) = 0x0602;
            R(0x0E0) = 0x0008;
            R(0x0EC) = 0x3F03;
            R(0x0EE) = 0x0001;
            R(W_US_COMPARE0) = 0xFC00;
            R(W_US_COMPARE0 + 2) = 0xFFFF;
            R(W_US_COMPARE0 + 4) = 0xFFFF;
            R(W_US_COMPARE0 + 6) = 0xFFFF;
            R(0x120) = 0x0048; R(0x122) = 0x4840; R(0x126) = 0x0080; R(0x12A) = 0x1000;
            R(0x130) = 0x0142; R(0x132) = 0x8064; R(W_POST_BEACON) = 0xFFFF;
            R(0x142) = 0x2443; R(0x144) = 0x0042; R(0x146) = 0x0016; R(0x148) = 0x0016; R(0x14A) = 0x0016;
            R(0x14C) = 0x162C; R(0x150) = 0x0204; R(0x154) = 0x0058;
            R(W_BB_CNT) = 0x00B5; R(W_BB_READ) = 0x00B5; R(W_BB_MODE) = 0x0100; R(W_BB_POWER) = 0x800D;
            R(0x178) = 0x0800; R(W_RF_DATA2) = 0x0800; R(W_RF_DATA1) = 0xC008; R(W_RF_CNT) = 0x0018;
            R(W_RF_PINS) = 0x0004; R(0x1A2) = 0x0001;
            R(W_RF_STATUS) = 0x0009; R(0x224) = 0x0003; R(0x230) = 0x0047; R(0x234) = 0x0EFF;
            R(W_RXTX_ADDR) = 0x0005; R(0x278) = 0x000F; R(0x27C) = 0x000A; R(0x290) = 0xFFFF;
            _irqLine = false;
            _usBase = 0;
            _usBaseTime = 0;
            _random = 1;
            _randomTime = 0;
            _preBeaconFired = false;
            _txSlot = -1;
            _cmdCountTime = 0;
            _scheduler.Cancel(SchedulerEvent.WifiTick);
            _scheduler.Cancel(SchedulerEvent.WifiTx);
        }

        private const uint W_TX_RETRYLIMIT = 0x02C;

        private ref ushort R(uint offset) => ref _reg[(offset & 0xFFF) >> 1];

        private long Now => _scheduler.CurrentTime;

        // ── Bus access ────────────────────────────────────────────────────────

        /// <summary>16-bit read from the Wifi window (04800000h–0480FFFFh).</summary>
        public ushort Read16(uint address)
        {
            uint offset = address & 0x7FFE;
            if (offset >= 0x4000 && offset < 0x6000)
                return BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan((int)(offset - 0x4000)));
            if (offset >= 0x1000)
            {
                // 1000h, 6000h and 7000h are passive mirrors: no side effects, RD_DATA returns its latch.
                if (offset is (>= 0x1000 and < 0x2000) or >= 0x6000)
                    return (offset & 0xFFF) == W_RXBUF_RD_DATA ? _rxDataLatch : _reg[(offset & 0xFFF) >> 1];
                return 0;
            }
            return ReadRegister(offset);
        }

        /// <summary>16-bit write; 8-bit writes to the Wifi window are ignored by the hardware.</summary>
        public void Write16(uint address, ushort value)
        {
            uint offset = address & 0x7FFE;
            if (offset >= 0x4000 && offset < 0x6000)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan((int)(offset - 0x4000)), value);
                return;
            }
            if (offset < 0x1000)
                WriteRegister(offset, value);
        }

        private ushort ReadRegister(uint offset)
        {
            switch (offset)
            {
                case W_RANDOM:
                    return NextRandom();
                case W_RXBUF_RD_DATA:
                    return ReadRxData();
                case W_US_COUNT0: case W_US_COUNT0 + 2: case W_US_COUNT0 + 4: case W_US_COUNT0 + 6:
                    return (ushort)(UsCount() >> (int)((offset - W_US_COUNT0) * 8));
                case W_CMD_COUNT:
                    return CurrentCmdCount();
                case W_TXBUSY:
                {
                    int busy = R(W_TXBUSY) & 0x0010;
                    for (int slot = 0; slot < 4; slot++)
                        if ((R(W_TXREQ_READ) & (1 << slot)) != 0 && (R(SlotRegister(slot)) & 0x8000) != 0)
                            busy |= 1 << slot;
                    return (ushort)busy;
                }
                case 0x078: return R(W_TXBUF_WR_ADDR);
                case 0x178: return R(W_RF_DATA2);
                case W_BB_BUSY: case W_RF_BUSY: return 0;
                case W_BB_READ: return Baseband.LastRead;
                case W_RXSTAT_OVF_IF:
                {
                    ushort flags = 0;
                    for (int i = 0; i < 13; i++)
                        if ((RxStat(i) & 0x80) != 0) flags |= (ushort)(1 << i);
                    return flags;
                }
                case >= W_RXSTAT and <= W_RXSTAT + 0xE:
                case W_TX_ERR_COUNT: case W_RX_COUNT:
                case >= W_CMD_STAT and <= W_CMD_STAT + 0xE:
                {
                    ushort value = R(offset);
                    R(offset) = 0; // statistics clear when read
                    return value;
                }
                default:
                    return R(offset);
            }
        }

        private void WriteRegister(uint offset, ushort value)
        {
            switch (offset)
            {
                case W_ID: case W_RXBUF_WRCSR: case W_TXREQ_READ: case W_TXBUSY: case W_TXSTAT: case W_TXBUF_REPLY2:
                case W_BB_READ: case W_BB_BUSY: case W_RF_BUSY: case W_RF_PINS: case W_TX_SEQNO: case W_RF_STATUS:
                case W_RXBUF_RD_DATA: case W_RANDOM: case W_RXSTAT_INC_IF: case W_RXSTAT_OVF_IF: case W_RX_COUNT: case W_RXTX_ADDR:
                    return; // read-only

                case W_MODE_RST:
                {
                    ushort old = R(W_MODE_RST);
                    R(W_MODE_RST) = (ushort)(value & 0x9FFF);
                    if ((old & 1) == 0 && (value & 1) != 0)
                    {
                        R(0x034) = 0x0002;
                        R(W_RF_PINS) = 0x0046;
                        R(W_RF_STATUS) = 0x0009;
                        R(0x27C) = 0x0005;
                    }
                    else if ((old & 1) != 0 && (value & 1) == 0)
                    {
                        R(0x27C) = 0x000A;
                    }
                    if ((value & (1 << 13)) != 0)
                    {
                        R(W_RXBUF_WR_ADDR) = 0;
                        R(0x0C0) = 0;
                        R(0x0C4) = 0;
                        R(0x1A4) = 0;
                        R(0x278) = 0x000F;
                    }
                    if ((value & (1 << 14)) != 0)
                        ResetPortGroup14();
                    StartNextTransmit();
                    return;
                }

                case W_MODE_WEP: R(offset) = (ushort)(value & 0x7F); return;

                case W_IF:
                    R(W_IF) &= (ushort)~value;
                    UpdateIrq();
                    return;
                case W_IE:
                    R(W_IE) = value;
                    UpdateIrq();
                    return;
                case W_IF_SET:
                    R(W_IF) |= (ushort)(value & ~(1 << 10));
                    UpdateIrq();
                    return;

                case W_AID_LOW: R(offset) = (ushort)(value & 0x000F); return;
                case W_AID_FULL: R(offset) = (ushort)(value & 0x07FF); return;

                case W_RXCNT:
                    R(W_RXCNT) = (ushort)(value & 0xFF0E);
                    if ((value & 0x0001) != 0)
                        R(W_RXBUF_WRCSR) = R(W_RXBUF_WR_ADDR);
                    if ((value & 0x0080) != 0)
                    {
                        R(W_TXBUF_REPLY2) = R(W_TXBUF_REPLY1);
                        R(W_TXBUF_REPLY1) = 0;
                    }
                    UpdateTick();
                    return;

                case W_POWER_US:
                    SetCounterState(() => R(W_POWER_US) = (ushort)(value & 0x0003));
                    return;
                case W_POWER_TX: R(offset) = (ushort)(value & 0x0007); return;
                case W_POWERSTATE:
                    R(W_POWERSTATE) = (ushort)((R(W_POWERSTATE) & 0x0300) | (value & 0x0003));
                    if ((value & 0x0002) != 0 && (R(W_POWERSTATE) & 0x0200) != 0)
                    {
                        // Queued power-up completes: the RF side wakes and says so with IRQ11.
                        WakeUp();
                        RaiseIrq(IRQ_RF_WAKEUP);
                    }
                    return;
                case W_POWERFORCE:
                    R(W_POWERFORCE) = (ushort)(value & 0x8001);
                    if ((value & 0x8000) != 0)
                    {
                        if ((value & 1) != 0) PowerDown();
                        else WakeUp();
                    }
                    return;

                case W_RXBUF_WR_ADDR: case W_RXBUF_READCSR: case W_RXBUF_GAPDISP: case W_TXBUF_GAPDISP:
                case W_RXBUF_COUNT: case W_TXBUF_COUNT:
                    R(offset) = (ushort)(value & 0x0FFF);
                    return;
                case W_RXBUF_RD_ADDR: case W_RXBUF_GAP: case W_TXBUF_WR_ADDR: case W_TXBUF_GAP:
                    R(offset) = (ushort)(value & 0x1FFE);
                    return;
                case W_TXBUF_WR_DATA:
                    WriteTxData(value);
                    return;

                case W_BEACONINT: R(offset) = (ushort)(value & 0x03FF); return;
                case W_LISTENCOUNT: case W_LISTENINT: R(offset) = (ushort)(value & 0x00FF); return;
                case W_TXBUF_CMD:
                    // Bit 15 can only be set while W_CMD_COUNT is running.
                    if (CurrentCmdCount() == 0) value &= 0x7FFF;
                    R(offset) = value;
                    StartNextTransmit();
                    return;
                case W_TXBUF_BEACON: case W_TXBUF_LOC1: case W_TXBUF_LOC2: case W_TXBUF_LOC3:
                    R(offset) = value;
                    StartNextTransmit();
                    return;
                case W_TXREQ_RESET:
                    R(W_TXREQ_READ) &= (ushort)~(value & 0x000F);
                    return;
                case W_TXREQ_SET:
                    R(W_TXREQ_READ) |= (ushort)(value & 0x000F);
                    StartNextTransmit();
                    return;
                case W_TXBUF_RESET:
                    if ((value & 0x01) != 0) R(W_TXBUF_LOC1) &= 0x7FFF;
                    if ((value & 0x02) != 0) R(W_TXBUF_CMD) &= 0x7FFF;
                    if ((value & 0x04) != 0) R(W_TXBUF_LOC2) &= 0x7FFF;
                    if ((value & 0x08) != 0) R(W_TXBUF_LOC3) &= 0x7FFF;
                    if ((value & 0x40) != 0) R(W_TXBUF_REPLY2) &= 0x7FFF;
                    if ((value & 0x80) != 0) R(W_TXBUF_REPLY1) &= 0x7FFF;
                    return;

                case W_US_COUNTCNT:
                    SetCounterState(() => R(W_US_COUNTCNT) = (ushort)(value & 1));
                    return;
                case W_US_COMPARECNT:
                    R(W_US_COMPARECNT) = (ushort)(value & 1);
                    if ((value & 2) != 0)
                        BeaconInterrupt(forced: true);
                    return;
                case W_US_COUNT0: case W_US_COUNT0 + 2: case W_US_COUNT0 + 4: case W_US_COUNT0 + 6:
                {
                    int shift = (int)(offset - W_US_COUNT0) * 8;
                    ulong count = UsCount();
                    count = (count & ~(0xFFFFUL << shift)) | ((ulong)value << shift);
                    _usBase = count;
                    _usBaseTime = Now;
                    UpdateTick();
                    return;
                }
                case W_US_COMPARE0:
                    R(offset) = (ushort)(value & 0xFC01);
                    return;
                case W_CMD_COUNT:
                    R(W_CMD_COUNT) = value;
                    _cmdCountTime = Now;
                    return;

                case W_BB_CNT:
                    R(W_BB_CNT) = value;
                    if ((R(W_BB_POWER) & 0x8000) == 0)
                        Baseband.Transfer(value, R(W_BB_WRITE));
                    return;
                case W_RF_DATA2:
                {
                    R(W_RF_DATA2) = value;
                    var (d1, d2) = Rf.Transfer(R(W_RF_CNT), R(W_RF_DATA1), value);
                    R(W_RF_DATA1) = d1;
                    R(W_RF_DATA2) = d2;
                    return;
                }
                case W_RF_CNT: R(offset) = (ushort)(value & 0x413F); return;

                default:
                    R(offset) = value;
                    return;
            }
        }

        private void ResetPortGroup14()
        {
            foreach (uint r in new uint[] { W_MODE_WEP, W_TXSTATCNT, 0x00A, W_MACADDR, W_MACADDR + 2, W_MACADDR + 4,
                                      W_BSSID, W_BSSID + 2, W_BSSID + 4, W_AID_LOW, W_AID_FULL, 0x02E, 0x084, 0x194, 0x198 })
                R(r) = 0;
            R(W_TX_RETRYLIMIT) = 0x0707;
            R(W_RXBUF_BEGIN) = 0x4000;
            R(W_RXBUF_END) = 0x4800;
            R(W_PREAMBLE) = 0x0001;
            R(W_RXFILTER) = 0x0401;
            R(0x0D4) = 0x0001;
            R(0x0E0) = 0x0008;
            R(0x0EC) = 0x3F03;
            R(0x1A2) = 0x0001;
            R(0x224) = 0x0003;
            R(0x230) = 0x0047;
        }

        /// <summary>Auto-sleep after the post-beacon slot (IRQ13): like a forced power-down, but TX requests stay.</summary>
        private void Sleep()
        {
            R(0x034) = 0x0002;
            R(W_POWERSTATE) |= 0x0200;
            R(W_RF_PINS) = 0x0046;
            R(W_RF_STATUS) = 0x0009;
        }

        private void WakeUp()
        {
            R(W_POWERSTATE) &= unchecked((ushort)~0x0202);
            R(W_RF_STATUS) = 0x0001;
            R(W_RF_PINS) = 0x0084;
            StartNextTransmit();
        }

        private void PowerDown()
        {
            R(0x034) = 0x0002;
            R(W_POWERSTATE) |= 0x0200;
            R(W_TXREQ_READ) = 0;
            R(W_RF_PINS) = 0x0046;
            R(W_RF_STATUS) = 0x0009;
        }

        private bool Powered => (R(W_POWERSTATE) & 0x0200) == 0;

        private ushort NextRandom()
        {
            // The LFSR steps at 33.51 MHz and the read returns the value latched by the previous read.
            ushort latched = _random;
            long steps = (Now - _randomTime) / 2 % 0x5FD;
            _randomTime = Now;
            for (long i = 0; i <= steps; i++)
                _random = (ushort)((_random & 1) ^ (((_random << 1) & 0x7FE) | (_random >> 10)));
            return latched;
        }

        // ── Interrupts ────────────────────────────────────────────────────────

        private void RaiseIrq(int bit)
        {
            R(W_IF) |= (ushort)(1 << bit);
            UpdateIrq();
        }

        /// <summary>IF.24 is set only when (W_IF &amp; W_IE) goes from zero to non-zero.</summary>
        private void UpdateIrq()
        {
            bool line = (R(W_IF) & R(W_IE)) != 0;
            if (line && !_irqLine)
                _irq.Request(InterruptController.IRQ_WIFI);
            _irqLine = line;
        }

        // ── Microsecond counter, beacon timing ────────────────────────────────

        private bool CounterRunning => (R(W_US_COUNTCNT) & 1) != 0 && (R(W_POWER_US) & 1) == 0;

        private static long UsToCycles(long us) => us * Scheduler.Arm9ClockHz / 1_000_000;

        public ulong UsCount() =>
            CounterRunning ? _usBase + (ulong)((Now - _usBaseTime) * 1_000_000 / Scheduler.Arm9ClockHz) : _usBase;

        private void SetCounterState(Action change)
        {
            _usBase = UsCount();
            _usBaseTime = Now;
            change();
            UpdateTick();
        }

        private ushort CurrentCmdCount()
        {
            // Decrements every 10 µs while W_CMD_COUNTCNT is set, stopping at zero.
            ushort count = R(W_CMD_COUNT);
            if ((R(0x0EE) & 1) == 0 || count == 0)
                return count;
            long elapsed = (Now - _cmdCountTime) * 100_000 / Scheduler.Arm9ClockHz;
            return (ushort)Math.Max(0, count - elapsed);
        }

        /// <summary>Keep the 1024 µs tick running while the counter or receiver is on.</summary>
        private void UpdateTick()
        {
            bool needed = CounterRunning || (R(W_RXCNT) & 0x8000) != 0;
            if (!needed)
            {
                _scheduler.Cancel(SchedulerEvent.WifiTick);
                return;
            }
            long delay;
            if (CounterRunning)
            {
                ulong count = UsCount();
                ulong boundary = (count | (UsPerTick - 1)) + 1;
                delay = UsToCycles((long)(boundary - _usBase)) - (Now - _usBaseTime);
            }
            else
            {
                delay = UsToCycles(UsPerTick);
            }
            _scheduler.ScheduleFromNow(SchedulerEvent.WifiTick, Math.Max(1, delay));
        }

        private void OnTick()
        {
            if (CounterRunning)
            {
                // Re-base on the exact boundary so rounding never accumulates.
                ulong count = UsCount();
                _usBase = (count + UsPerTick / 2) & ~(ulong)(UsPerTick - 1);
                _usBaseTime = Now;
                MillisecondTick();
            }
            PollMedium();
            UpdateTick();
        }

        private void MillisecondTick()
        {
            bool compare = (R(W_US_COMPARECNT) & 1) != 0;

            // W_POST_BEACON counts down and stays at zero (IRQ13).
            if (R(W_POST_BEACON) != 0 && --R(W_POST_BEACON) == 0)
            {
                RaiseIrq(IRQ_POST_BEACON);
                if ((R(W_POWER_TX) & 2) == 0)
                    Sleep();
            }

            // W_BEACON_COUNT: IRQ15 W_PRE_BEACON µs early, IRQ14 at zero, reloaded at once.
            if (R(W_BEACON_COUNT) != 0 || R(W_BEACONINT) != 0)
            {
                ushort count = R(W_BEACON_COUNT);
                if (count != 0)
                    count--;
                R(W_BEACON_COUNT) = count;
                if (!_preBeaconFired && count * UsPerTick <= R(W_PRE_BEACON))
                {
                    _preBeaconFired = true;
                    if (compare)
                        RaiseIrq(IRQ_PRE_BEACON);
                    if ((R(W_POWER_TX) & 1) != 0)
                        WakeUp(); // auto-wakeup for the coming beacon
                }
                if (count == 0)
                {
                    if ((R(W_US_COMPARE0) & 1) == 0)
                        BeaconInterrupt(forced: false);
                    else
                        R(W_BEACON_COUNT) = R(W_BEACONINT); // blocked until W_US_COUNT = W_US_COMPARE
                }
            }

            // W_US_COMPARE (millisecond resolution).
            ulong compareValue = (ulong)R(W_US_COMPARE0) | (ulong)R(W_US_COMPARE0 + 2) << 16
                                 | (ulong)R(W_US_COMPARE0 + 4) << 32 | (ulong)R(W_US_COMPARE0 + 6) << 48;
            if (compare && ((_usBase ^ compareValue) & ~0x3FFUL) == 0)
            {
                R(W_US_COMPARE0) &= 0xFFFE;
                BeaconInterrupt(forced: false);
            }

            CheckCommandExpired();
        }

        private void BeaconInterrupt(bool forced)
        {
            if (!forced)
            {
                R(W_BEACON_COUNT) = R(W_BEACONINT);
                _preBeaconFired = false;
            }
            if ((R(W_US_COMPARECNT) & 1) == 0)
                return;
            RaiseIrq(IRQ_BEACON);
            R(W_POST_BEACON) = 0xFFFF;
            R(W_TXREQ_READ) &= 0xFFF2;
            if (R(W_LISTENCOUNT) == 0)
                R(W_LISTENCOUNT) = R(W_LISTENINT);
            if (R(W_LISTENCOUNT) != 0)
                R(W_LISTENCOUNT)--;
            if ((R(W_TXBUF_BEACON) & 0x8000) != 0 && Powered)
                TransmitBeacon();
        }

        // ── RX ring ───────────────────────────────────────────────────────────

        /// <summary>BEGIN/END are addresses in the Wifi window (4000h–6000h); END = 6000h means the end of RAM.</summary>
        private static int RingAddress(ushort value) => value >= 0x4000 ? Math.Min(value - 0x4000, 0x2000) & ~1 : value & 0x1FFE;
        private int RxBegin => RingAddress(R(W_RXBUF_BEGIN));
        private int RxEnd => RingAddress(R(W_RXBUF_END));

        /// <summary>Byte offset in Wifi RAM after advancing <paramref name="byteAddress"/> by 2 with BEGIN/END wrapping.</summary>
        private int RxAdvance(int byteAddress)
        {
            byteAddress += 2;
            if (byteAddress == RxEnd)
                byteAddress = RxBegin;
            return byteAddress & 0x1FFE;
        }

        private ushort ReadRxData()
        {
            int address = R(W_RXBUF_RD_ADDR) & 0x1FFE;
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan(address));
            _rxDataLatch = value;
            address = RxAdvance(address);
            int gap = R(W_RXBUF_GAP) & 0x1FFE;
            if (gap != 0 && address == gap)
            {
                address += R(W_RXBUF_GAPDISP) * 2;
                int end = RxEnd, begin = RxBegin;
                if (address >= end && end > begin)
                    address = address + begin - end;
                address &= 0x1FFE;
                if (ChipId == 0xC340)
                    R(W_RXBUF_GAPDISP) = 0; // the DS-Lite forgets GAPDISP after using it
            }
            R(W_RXBUF_RD_ADDR) = (ushort)address;
            CountDown(W_RXBUF_COUNT, IRQ_RXBUF_COUNT);
            return value;
        }

        private void WriteTxData(ushort value)
        {
            int address = R(W_TXBUF_WR_ADDR) & 0x1FFE;
            BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan(address), value);
            address = (address + 2) & 0x1FFE;
            int gap = R(W_TXBUF_GAP) & 0x1FFE;
            if (gap != 0 && address == gap)
            {
                address = (address + R(W_TXBUF_GAPDISP) * 2) & 0x1FFE;
                if (ChipId == 0xC340)
                    R(W_TXBUF_GAPDISP) = 0;
            }
            R(W_TXBUF_WR_ADDR) = (ushort)address;
            CountDown(W_TXBUF_COUNT, IRQ_TXBUF_COUNT);
        }

        private void CountDown(uint register, int irq)
        {
            if (R(register) == 0)
                return;
            if (--R(register) == 0)
                RaiseIrq(irq);
        }

        // W_RXSTAT counter n lives at an irregular byte port (1B1h, 1B9h and 1BBh don't exist).
        private static readonly uint[] StatPort = { 0x1B0, 0x1B2, 0x1B3, 0x1B4, 0x1B5, 0x1B6, 0x1B7, 0x1B8, 0x1BA, 0x1BC, 0x1BD, 0x1BE, 0x1BF };
        public const int STAT_RXBUF_FULL = 3, STAT_RX_OK = 6;

        /// <summary>Count an RX event: INC_IF bit, IRQ02 if enabled, IRQ04 when the counter reaches its half-overflow bit 7.</summary>
        private void IncrementStat(int index)
        {
            uint port = StatPort[index];
            ref ushort word = ref R(port & ~1u);
            int shift = (int)(port & 1) * 8;
            int counter = (word >> shift) & 0xFF;
            if (counter < 0xFF)
                word = (ushort)((word & ~(0xFF << shift)) | ((counter + 1) << shift));
            R(W_RXSTAT_INC_IF) |= (ushort)(1 << index);
            if ((R(0x1AA) & (1 << index)) != 0)
                RaiseIrq(2);
            if (counter == 0x7F && (R(0x1AE) & (1 << index)) != 0)
                RaiseIrq(4);
        }

        private int RxStat(int index)
        {
            uint port = StatPort[index];
            return (R(port & ~1u) >> (int)((port & 1) * 8)) & 0xFF;
        }

        private void PollMedium()
        {
            var medium = Medium;
            while (medium.TryReceive(Id, TimeSpan.Zero, out var packet))
                Receive(packet);
        }

        /// <summary>A frame arrived over the air: filter it, store it in the RX ring and react to multiplay CMDs.</summary>
        public void Receive(WifiPacket packet)
        {
            var frame = packet.Frame;
            if (!Powered || (R(W_RXCNT) & 0x8000) == 0 || frame.Length < 10)
            {
                FramesIgnored++;
                return;
            }

            ushort fc = packet.FrameControl;
            int type = (fc >> 2) & 3, subtype = (fc >> 4) & 0xF;
            ReadOnlySpan<byte> destination = frame.AsSpan(4, 6);
            ReadOnlySpan<byte> bssid = Bssid(frame, fc);
            bool group = (destination[0] & 1) != 0;
            bool ourBssid = !bssid.IsEmpty && bssid.SequenceEqual(RegisterBytes(W_BSSID));
            bool toUs = destination.SequenceEqual(RegisterBytes(W_MACADDR));
            if (!toUs && !(group && (ourBssid || (R(W_RXFILTER) & 1) != 0)))
            {
                FramesIgnored++;
                return;
            }

            int flags = type switch
            {
                0 => subtype == 8 ? 0x1 : 0x0,
                1 => 0x5,
                _ => fc switch { 0x0228 => 0xC, 0x0218 => 0xD, 0x0118 => 0xE, 0x0158 => 0xF, _ => 0x8 },
            };
            flags |= 0x10;
            if ((fc & 0x0400) != 0) flags |= 0x0300;
            if (ourBssid) flags |= 0x8000;

            if (!StoreReceived(frame, packet.Rate, (ushort)flags))
                return;
            FramesReceived++;

            // Multiplay slave: a CMD naming us gets the automatic reply.
            int aid = R(W_AID_LOW) & 0xF;
            if (fc == 0x0228 && aid != 0 && frame.Length >= 28)
            {
                ushort slaves = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(24 + 2));
                if ((slaves & (1 << aid)) != 0)
                    SendReply(aid);
            }
        }

        private static ReadOnlySpan<byte> Bssid(byte[] frame, ushort fc)
        {
            if (((fc >> 2) & 3) == 1)
                return frame.Length >= 16 ? frame.AsSpan(10, 6) : ReadOnlySpan<byte>.Empty; // control: TA
            if (frame.Length < 22)
                return ReadOnlySpan<byte>.Empty;
            return (fc & 0x0300) switch
            {
                0x0100 => frame.AsSpan(4, 6),   // ToDS: BSSID in address 1
                0x0200 => frame.AsSpan(10, 6),  // FromDS: address 2
                _ => frame.AsSpan(16, 6),       // address 3
            };
        }

        private byte[] RegisterBytes(uint offset)
        {
            var bytes = new byte[6];
            for (int i = 0; i < 3; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), R(offset + (uint)i * 2));
            return bytes;
        }

        /// <summary>Write RX header + frame at WRCSR. False (and a "buffer full" count) when it doesn't fit before READCSR.</summary>
        private bool StoreReceived(ReadOnlySpan<byte> frame, int rate, ushort flags)
        {
            int begin = RxBegin, end = RxEnd;
            if (end <= begin)
                return false;
            int start = (R(W_RXBUF_WRCSR) & 0xFFF) * 2;
            int read = (R(W_RXBUF_READCSR) & 0xFFF) * 2;
            int total = (12 + frame.Length + 3) & ~3;
            int free = read > start ? read - start : end - begin - (start - read);
            if (start < begin || start >= end || total >= free)
            {
                IncrementStat(STAT_RXBUF_FULL);
                FramesDropped++;
                return false;
            }

            RaiseIrq(IRQ_RX_START);
            Span<byte> header = stackalloc byte[12];
            header.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(header, flags);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 0x0040);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan(start + 4)));
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)rate);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)frame.Length);
            header[10] = 0xC1; // strong signal
            header[11] = 0x80;

            int address = start;
            void Put(byte b0, byte b1)
            {
                Ram[address] = b0;
                Ram[address + 1] = b1;
                address = RxAdvance(address);
            }
            for (int i = 0; i < 12; i += 2)
                Put(header[i], header[i + 1]);
            for (int i = 0; i < frame.Length; i += 2)
                Put(frame[i], i + 1 < frame.Length ? frame[i + 1] : (byte)0);
            if (((address - start + (address < start ? end - begin : 0)) & 3) != 0)
                Put(0, 0);

            R(W_RXBUF_WRCSR) = (ushort)(address / 2);
            BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan(0x1F6E), 0x0F01);
            IncrementStat(STAT_RX_OK);
            R(W_RX_COUNT) = (ushort)Math.Min(0xFF, (R(W_RX_COUNT) & 0xFF) + 1);
            RaiseIrq(IRQ_RX_DONE);
            return true;
        }

        // ── TX ────────────────────────────────────────────────────────────────

        // Transmit slots: index = W_TXREQ bit; LOC3 goes first, LOC1 last.
        private static readonly int[] SlotOrder = { 3, 2, 1, 0 };
        private static uint SlotRegister(int slot) => slot switch { 0 => W_TXBUF_LOC1, 1 => W_TXBUF_CMD, 2 => W_TXBUF_LOC2, _ => W_TXBUF_LOC3 };

        private void StartNextTransmit()
        {
            if (_txSlot >= 0 || (R(W_MODE_RST) & 1) == 0 || !Powered)
                return;
            foreach (int slot in SlotOrder)
            {
                if ((R(W_TXREQ_READ) & (1 << slot)) == 0 || (R(SlotRegister(slot)) & 0x8000) == 0)
                    continue;
                if (slot == 1 && CurrentCmdCount() == 0)
                    continue;
                BeginTransmit(slot);
                return;
            }
        }

        private void BeginTransmit(int slot)
        {
            int header = (R(SlotRegister(slot)) & 0xFFF) * 2;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan((header + 0xA) & 0x1FFE)) & 0x3FFF;
            int rate = Ram[(header + 8) & 0x1FFF] == 0x14 ? 2 : 1;

            _txSlot = slot;
            R(W_TXBUSY) |= (ushort)(1 << slot);
            R(W_RF_STATUS) = 0x0003;
            R(W_RF_PINS) = 0x0046;
            R(W_RXTX_ADDR) = (ushort)(header / 2 + 6);
            RaiseIrq(IRQ_TX_START);
            FixSequenceNumber(header, keep: (R(SlotRegister(slot)) & 0x2000) != 0 || Ram[(header + 4) & 0x1FFF] != 0);

            bool shortPreamble = rate == 2 && (R(W_PREAMBLE) & 4) != 0;
            long us = (shortPreamble ? 96 : 192) + length * 8 / rate;
            _scheduler.ScheduleFromNow(SchedulerEvent.WifiTx, Math.Max(1, UsToCycles(us)));
        }

        /// <summary>IRQ07: the IEEE sequence control becomes W_TX_SEQNO × 10h (unless disabled), then SEQNO increments.</summary>
        private void FixSequenceNumber(int header, bool keep)
        {
            if (keep || (R(W_TX_HDR_CNT) & 4) != 0)
                return;
            BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan((header + 12 + 22) & 0x1FFE), (ushort)(R(W_TX_SEQNO) << 4));
            R(W_TX_SEQNO) = (ushort)((R(W_TX_SEQNO) + 1) & 0x0FFF);
        }

        private WifiPacket FrameAt(int header, int extraFlags = 0)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan((header + 0xA) & 0x1FFE)) & 0x3FFF;
            int bytes = Math.Max(0, length - 4); // the FCS is generated by hardware, not stored by the receiver
            var frame = new byte[bytes];
            for (int i = 0; i < bytes; i++)
                frame[i] = Ram[(header + 12 + i) & 0x1FFF];
            if (frame.Length >= 2)
                frame[0] &= 0xFC; // protocol version forced to 0
            return new WifiPacket { Frame = frame, Rate = Ram[(header + 8) & 0x1FFF] == 0x14 ? 0x14 : 0x0A, Sender = Id, ReplyAid = extraFlags };
        }

        private void Send(WifiPacket packet)
        {
            FramesSent++;
            Medium.Send(packet);
            Transmitted?.Invoke(packet);
        }

        private void OnTransmitDone()
        {
            int slot = _txSlot;
            _txSlot = -1;
            if (slot < 0)
                return;
            uint register = SlotRegister(slot);
            int header = (R(register) & 0xFFF) * 2;
            Send(FrameAt(header));

            if (slot == 1)
            {
                FinishCommand(header);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan(header), 0x0001);
                Ram[(header + 5) & 0x1FFF] = 0;
                int which = slot switch { 2 => 1, 3 => 2, _ => 0 };
                R(W_TXSTAT) = (ushort)(0x0001 | ((R(register) & 0x1000) != 0 ? 0x0700 : 0) | (which << 12));
                R(register) &= 0x7FFF;
                R(W_TXBUSY) &= (ushort)~(1 << slot);
                R(W_RF_STATUS) = 0x0001;
                R(W_RF_PINS) = 0x0084;
                RaiseIrq(IRQ_TX_DONE);
            }
            StartNextTransmit();
        }

        /// <summary>
        /// Multiplay master after the CMD went out: collect the slaves' replies
        /// (waiting briefly in real time for consoles on other threads or
        /// machines), store them, send the CMD ACK and finish with IRQ12.
        /// </summary>
        private void FinishCommand(int header)
        {
            if ((R(W_TXSTATCNT) & 0x4000) != 0)
            {
                R(W_TXSTAT) = 0x0800;
                RaiseIrq(IRQ_TX_DONE);
            }

            int body = header + 12 + 24;
            ushort wanted = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan((body + 2) & 0x1FFE)) & 0xFFFE);
            ushort answered = 0;
            int peers = Medium.PeerCount(Id);
            var deadline = DateTime.UtcNow + (peers > 0 ? ReplyTimeout : TimeSpan.Zero);
            while ((answered & wanted) != wanted)
            {
                var left = deadline - DateTime.UtcNow;
                if (!Medium.TryReceive(Id, left > TimeSpan.Zero ? left : TimeSpan.Zero, out var packet))
                    break;
                if (packet.ReplyAid != 0 && (packet.FrameControl is 0x0118 or 0x0158))
                    answered |= (ushort)(1 << packet.ReplyAid);
                Receive(packet);
            }
            ushort missing = (ushort)(wanted & ~answered);

            // The automatic CMD ACK: FC 0218h to 03:09:BF:00:00:03, body = ?, error flags.
            var ack = new byte[24 + 4];
            BinaryPrimitives.WriteUInt16LittleEndian(ack, 0x0218);
            CmdAckAddress.CopyTo(ack, 4);
            RegisterBytes(W_MACADDR).CopyTo(ack, 10);
            RegisterBytes(W_BSSID).CopyTo(ack, 16);
            BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(22), (ushort)(R(W_TX_SEQNO) << 4));
            R(W_TX_SEQNO) = (ushort)((R(W_TX_SEQNO) + 1) & 0x0FFF);
            BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(24), 0x0046);
            BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(26), missing);
            RaiseIrq(IRQ_TX_START);
            Send(new WifiPacket { Frame = ack, Rate = 0x14, Sender = Id });
            if ((R(W_TXSTATCNT) & 0x2000) != 0)
            {
                R(W_TXSTAT) = 0x0B01;
                RaiseIrq(IRQ_TX_DONE);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan(header), missing == 0 ? (ushort)0x0001 : (ushort)0x0005);
            BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan((header + 2) & 0x1FFE), missing);
            for (int i = 1; i < 16; i++)
            {
                if ((missing & (1 << i)) == 0) continue;
                uint offset = W_CMD_STAT + (uint)i;
                ref ushort word = ref R(offset & ~1u);
                int shift = (int)(offset & 1) * 8;
                int counter = Math.Min(0xFF, ((word >> shift) & 0xFF) + 1);
                word = (ushort)((word & ~(0xFF << shift)) | (counter << shift));
            }
            R(W_TXBUF_CMD) &= 0x7FFF;
            R(W_TXBUSY) &= unchecked((ushort)~0x0002);
            R(W_RF_STATUS) = 0x0001;
            R(W_RF_PINS) = 0x0084;
            RaiseIrq(IRQ_CMD_DONE);
        }

        /// <summary>
        /// W_CMD_COUNT ran out before a pending CMD could go out: the CMD fails
        /// (TXHDR[0] = 0005h, every slave missing) and IRQ12 still fires, so the
        /// master's driver isn't left waiting.
        /// </summary>
        private void CheckCommandExpired()
        {
            if ((R(W_TXBUF_CMD) & 0x8000) == 0 || (R(W_TXREQ_READ) & 0x0002) == 0 || _txSlot == 1 || CurrentCmdCount() != 0)
                return;
            int header = (R(W_TXBUF_CMD) & 0xFFF) * 2;
            ushort wanted = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(Ram.AsSpan((header + 12 + 24 + 2) & 0x1FFE)) & 0xFFFE);
            BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan(header), 0x0005);
            BinaryPrimitives.WriteUInt16LittleEndian(Ram.AsSpan((header + 2) & 0x1FFE), wanted);
            R(W_TXBUF_CMD) &= 0x7FFF;
            R(W_TX_SEQNO) = (ushort)((R(W_TX_SEQNO) + 1) & 0x0FFF);
            RaiseIrq(IRQ_CMD_DONE);
        }

        /// <summary>Multiplay slave: REPLY1 → REPLY2, then send REPLY2 (or an empty reply).</summary>
        private void SendReply(int aid)
        {
            ushort old = R(W_TXBUF_REPLY2);
            if ((old & 0x8000) != 0)
            {
                int oldHeader = (old & 0xFFF) * 2;
                Ram[(oldHeader + 1) & 0x1FFF] = Ram[oldHeader];
                Ram[oldHeader] = 0x01;
            }
            R(W_TXBUF_REPLY2) = R(W_TXBUF_REPLY1);
            R(W_TXBUF_REPLY1) = 0;

            ushort reply = R(W_TXBUF_REPLY2);
            RaiseIrq(IRQ_TX_START);
            WifiPacket packet;
            if ((reply & 0x8000) != 0)
            {
                int header = (reply & 0xFFF) * 2;
                if (Ram[(header + 4) & 0x1FFF] < 0xFF)
                    Ram[(header + 4) & 0x1FFF]++;
                Ram[(header + 5) & 0x1FFF] = 0;
                FixSequenceNumber(header, keep: false);
                var frame = FrameAt(header, aid);
                packet = frame;
                if ((R(W_TXSTATCNT) & 0x1000) != 0)
                {
                    R(W_TXSTAT) = 0x0401;
                    RaiseIrq(IRQ_TX_DONE);
                }
            }
            else
            {
                var empty = new byte[24];
                BinaryPrimitives.WriteUInt16LittleEndian(empty, 0x0158);
                RegisterBytes(W_BSSID).CopyTo(empty, 4);   // ToDS: address 1 = BSSID
                RegisterBytes(W_MACADDR).CopyTo(empty, 10);
                ReplyAddress.CopyTo(empty, 16);            // address 3 = DA
                BinaryPrimitives.WriteUInt16LittleEndian(empty.AsSpan(22), (ushort)(R(W_TX_SEQNO) << 4));
                R(W_TX_SEQNO) = (ushort)((R(W_TX_SEQNO) + 1) & 0x0FFF);
                packet = new WifiPacket { Frame = empty, Rate = 0x14, Sender = Id, ReplyAid = aid };
            }
            Send(packet);
        }

        /// <summary>W_TXBUF_BEACON in a beacon timeslot: the hardware stamps the 64-bit timestamp with W_US_COUNT.</summary>
        private void TransmitBeacon()
        {
            int header = (R(W_TXBUF_BEACON) & 0xFFF) * 2;
            R(W_TXBUSY) |= 0x0010;
            RaiseIrq(IRQ_TX_START);
            FixSequenceNumber(header, keep: false);
            var packet = FrameAt(header);
            if (packet.Frame.Length >= 32)
                BinaryPrimitives.WriteUInt64LittleEndian(packet.Frame.AsSpan(24), UsCount());
            Send(packet);
            R(W_TXBUSY) &= unchecked((ushort)~0x0010);
            if ((R(W_TXSTATCNT) & 0x8000) != 0)
            {
                R(W_TXSTAT) = 0x0301;
                RaiseIrq(IRQ_TX_DONE);
            }
        }

        // ── Save states ───────────────────────────────────────────────────────

        public void Serialize(StateSerializer s)
        {
            s.Section("Wifi");
            s.Sync(_reg);
            s.Sync(Ram);
            s.Sync(ref _irqLine);
            s.Sync(ref _usBase);
            s.Sync(ref _usBaseTime);
            s.Sync(ref _random);
            s.Sync(ref _randomTime);
            s.Sync(ref _preBeaconFired);
            s.Sync(ref _rxDataLatch);
            s.Sync(ref _txSlot);
            s.Sync(ref _cmdCountTime);
            Baseband.Serialize(s);
            Rf.Serialize(s);
        }
    }
}
