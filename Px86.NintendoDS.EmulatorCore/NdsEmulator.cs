using Px86.NintendoDS.Common.Core;
using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.Common.Cpu.Coprocessors;
using Px86.NintendoDS.EmulatorCore.Audio;
using Px86.NintendoDS.EmulatorCore.Bios;
using Px86.NintendoDS.EmulatorCore.Cartridge;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Hardware;
using Px86.NintendoDS.EmulatorCore.Hardware.Spi;
using Px86.NintendoDS.EmulatorCore.Hardware.Wifi;
using Px86.NintendoDS.EmulatorCore.Jit;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.EmulatorCore.Video.Gpu3D;
using Px86.NintendoDS.FileFormats.Rom;

namespace Px86.NintendoDS.EmulatorCore
{
    /// <summary>Why <see cref="NdsEmulator.Run"/> returned.</summary>
    public enum RunResult
    {
        FrameCompleted,
        BudgetUsed,
        Break,
    }

    /// <summary>
    /// The whole Nintendo DS: both CPUs, their buses, memory and every
    /// peripheral, plus the main loop.
    ///
    /// Execution model
    ///   Time advances in slices of at most <see cref="MaxSliceCycles"/> ARM9
    ///   cycles, ending early at the next scheduler event. In each slice the
    ///   ARM9 runs, then the ARM7 runs for the same span (the ARM7 clock is half
    ///   the ARM9 clock, so one ARM7 cycle counts as two). Then due events fire
    ///   (scanlines, timers, cartridge words…). Short slices keep the two CPUs
    ///   close enough for IPC handshakes.
    ///
    /// Boot
    ///   <see cref="LoadRom"/> inserts a cartridge; <see cref="Boot"/> then
    ///   either direct-boots it (default, see <see cref="DirectBoot"/>) or, if
    ///   real BIOS + firmware dumps were supplied and <see cref="UseRealBios"/>
    ///   is set, starts at the BIOS reset vectors like a real console.
    /// </summary>
    public sealed class NdsEmulator
    {
        public const int MainRamSize = 4 * 1024 * 1024;
        public const uint MainRamMask = MainRamSize - 1;
        public const int MaxSliceCycles = 64;

        public NdsEmulator()
        {
            Scheduler = new Scheduler();

            MainRam = new byte[MainRamSize];
            SharedWram = new byte[32 * 1024];
            Arm7Wram = new byte[64 * 1024];
            Palette = new byte[2 * 1024];
            Oam = new byte[2 * 1024];
            Bios9 = BiosStub.CreateArm9();
            Bios7 = BiosStub.CreateArm7();
            Vram = new VramController();

            Arm9Bus = new Arm9MemoryBus(this);
            Arm7Bus = new Arm7MemoryBus(this);
            Arm9 = new ArmCpu(ArmArchitecture.ARMv5TE, Arm9Bus);
            Arm7 = new ArmCpu(ArmArchitecture.ARMv4T, Arm7Bus);
            Cp15 = new Cp15(Arm9);
            Arm9.Cp15 = Cp15;
            Cp15.TcmConfigurationChanged += Arm9Bus.UpdateTcm;

            Code = new CodeTracker(MainRam, SharedWram, Arm7Wram, Arm9Bus.Itcm);
            _blocks9 = new BlockCache(Arm9, Code, Arm9Bus.LocateCode);
            _blocks7 = new BlockCache(Arm7, Code, Arm7Bus.LocateCode);
            Cp15.TcmConfigurationChanged += FlushCode;

            Irq9 = new InterruptController(Arm9, isArm9: true);
            Irq7 = new InterruptController(Arm7, isArm9: false);
            Timers9 = new TimerUnit(Scheduler, Irq9, isArm9: true);
            Timers7 = new TimerUnit(Scheduler, Irq7, isArm9: false);
            Dma9 = new DmaController(Arm9Bus, Irq9, isArm9: true);
            Dma7 = new DmaController(Arm7Bus, Irq7, isArm9: false);
            Ipc = new IpcController(Irq9, Irq7);
            Math = new MathUnit();
            Keypad = new Keypad(Irq9, Irq7);
            Rtc = new RealTimeClock();
            Control = new SystemControl();
            Spi = new SpiBus(Irq7, new PowerManager(), FirmwareFlash.CreateDefault(), new TouchScreen());
            Wifi = new WifiController(Scheduler, Irq7);
            DebugOutput = new DebugOutput(this);
            Arm9.NoCashMessage = DebugOutput.OnNoCashMessage;
            Arm7.NoCashMessage = DebugOutput.OnNoCashMessage;
            Hooks = new FunctionHooks(this);
            Hooks.Changed += FlushCode;
            _blocks9.EndBefore = address => Hooks.IsHooked(0, address);
            _blocks7.EndBefore = address => Hooks.IsHooked(1, address);
            Spi.Touch.Clock = () => Scheduler.CurrentTime;
            Spi.Touch.Amplifier = () => (Spi.Power.MicrophoneAmplifierOn, Spi.Power.MicrophoneGain);
            Cartridge = new CartridgeSlot(Scheduler, Irq9, Irq7, Dma9, Dma7);
            Lcd = new LcdController(Scheduler, Irq9, Irq7, Dma9, Dma7);
            EngineA = new Gpu2DEngine(true, Vram, Palette, Oam);
            EngineB = new Gpu2DEngine(false, Vram, Palette, Oam);
            Gpu3D = new Gpu3DEngine(Vram, Irq9, Dma9);
            EngineA.ThreeDLineProvider = Gpu3D.GetLine;
            EngineA.Alpha3DProvider = Gpu3D.GetAlpha;
            Sound = new SoundUnit(Scheduler, Arm7Bus);
            Sound.Powered = () => Control.SoundEnabled;

            Hle9 = new HleBios(this, isArm9: true);
            Hle7 = new HleBios(this, isArm9: false);

            Control.ExMemChanged += () => Cartridge.Owner = Control.NdsSlotOwner;
            Control.WramMappingChanged += FlushCode;
            Dma9.Started += timing =>
            {
                if (timing == DmaTiming.Cartridge) Cartridge.OnDmaStarted(0);
                else if (timing == DmaTiming.GeometryFifo) Gpu3D.OnDmaStarted();
            };
            Dma7.Started += timing => { if (timing == DmaTiming.Cartridge) Cartridge.OnDmaStarted(1); };
            Lcd.ScanlineReady += RenderScanline;
            Lcd.VBlankStarted += OnVBlank;
            Lcd.LineStarted += _ => Gpu3D.CheckFifoIrq();

            Reset();
        }

        // ── Components ────────────────────────────────────────────────────────

        public Scheduler Scheduler { get; }

        /// <summary>Which RAM pages hold cached code (written by the buses).</summary>
        public CodeTracker Code { get; }

        private readonly BlockCache _blocks9;
        private readonly BlockCache _blocks7;

        /// <summary>Interpreter or cached blocks. Breakpoints and watchpoints always use the interpreter.</summary>
        public CpuMode CpuMode { get; set; } = CpuMode.Interpreter;

        /// <summary>Drop every cached block (memory was remapped, reset, state loaded).</summary>
        public void FlushCode()
        {
            Code.Clear();
            _blocks9.Clear();
            _blocks7.Clear();
        }

        /// <summary>Number of blocks built so far (both CPUs), for diagnostics.</summary>
        public long BlocksCompiled => _blocks9.Compiles + _blocks7.Compiles;

        /// <summary>Blocks compiled to native code (JIT mode).</summary>
        public long JitCompiles { get; private set; }
        public ArmCpu Arm9 { get; }
        public ArmCpu Arm7 { get; }
        public Cp15 Cp15 { get; }
        public Arm9MemoryBus Arm9Bus { get; }
        public Arm7MemoryBus Arm7Bus { get; }

        public byte[] MainRam { get; }
        public byte[] SharedWram { get; }
        public byte[] Arm7Wram { get; }
        public byte[] Palette { get; }
        public byte[] Oam { get; }
        public byte[] Bios9 { get; private set; }
        public byte[] Bios7 { get; private set; }
        public VramController Vram { get; }

        public InterruptController Irq9 { get; }
        public InterruptController Irq7 { get; }
        public TimerUnit Timers9 { get; }
        public TimerUnit Timers7 { get; }
        public DmaController Dma9 { get; }
        public DmaController Dma7 { get; }
        public IpcController Ipc { get; }
        public MathUnit Math { get; }
        public Keypad Keypad { get; }
        public RealTimeClock Rtc { get; }
        public SystemControl Control { get; }
        public SpiBus Spi { get; }

        /// <summary>The ARM7's Wifi hardware; set <see cref="WifiController.Medium"/> to connect it.</summary>
        public WifiController Wifi { get; }

        /// <summary>Text the game prints through the emulator debug ports and messages.</summary>
        public DebugOutput DebugOutput { get; }

        /// <summary>printf/sprintf hooks that print what the game formats to <see cref="DebugOutput"/>.</summary>
        public FunctionHooks Hooks { get; }
        public CartridgeSlot Cartridge { get; }
        public LcdController Lcd { get; }
        public Gpu2DEngine EngineA { get; }
        public Gpu2DEngine EngineB { get; }
        public Gpu3DEngine Gpu3D { get; }
        public SoundUnit Sound { get; }

        public HleBios Hle9 { get; }
        public HleBios Hle7 { get; }

        // ── Configuration ─────────────────────────────────────────────────────

        /// <summary>True once real BIOS dumps have been loaded with <see cref="LoadBios"/>.</summary>
        public bool HasRealBios { get; private set; }

        /// <summary>Boot through the real BIOS and firmware instead of direct boot.</summary>
        public bool UseRealBios { get; set; }

        public NdsRom? Rom => Cartridge.Rom;

        public long FrameCount { get; private set; }

        /// <summary>Raised after each completed frame (both screens rendered).</summary>
        public event Action? FrameCompleted;

        // ── Screens ───────────────────────────────────────────────────────────

        /// <summary>Top screen (6-bit channels per <see cref="Gpu2DEngine.Framebuffer"/>).</summary>
        public uint[] TopScreen => Control.DisplaySwap ? EngineA.Framebuffer : EngineB.Framebuffer;

        /// <summary>Bottom (touch) screen.</summary>
        public uint[] BottomScreen => Control.DisplaySwap ? EngineB.Framebuffer : EngineA.Framebuffer;

        // ── Setup ─────────────────────────────────────────────────────────────

        /// <summary>Use real BIOS dumps (bios9: 4 KB, bios7: 16 KB). Disables HLE SWIs.</summary>
        public void LoadBios(byte[] bios9, byte[] bios7)
        {
            if (bios9.Length < 0x1000 || bios7.Length < 0x4000)
                throw new InvalidDataException("ARM9 BIOS must be 4 KB and ARM7 BIOS 16 KB.");
            Bios9 = bios9[..0x1000];
            Bios7 = bios7[..0x4000];
            HasRealBios = true;
        }

        public void LoadFirmware(byte[] firmware) => Spi.ReplaceFirmware(new FirmwareFlash(firmware));

        public void LoadRom(NdsRom rom, SaveMemory? save = null)
        {
            save ??= new SaveMemory();
            save.CallerPc = () => Cartridge.Owner == 0 ? Arm9.Pc : Arm7.Pc;
            Cartridge.Insert(rom, save);
        }

        public void Reset()
        {
            FlushCode();
            Scheduler.Reset();
            Array.Clear(MainRam);
            Array.Clear(SharedWram);
            Array.Clear(Arm7Wram);
            Array.Clear(Palette);
            Array.Clear(Oam);
            Vram.Reset();

            Cp15.Reset();
            Arm9.Reset();
            Arm7.Reset();
            Arm9Bus.Reset();
            Arm7Bus.Reset();

            Irq9.Reset();
            Irq7.Reset();
            Timers9.Reset();
            Timers7.Reset();
            Dma9.Reset();
            Dma7.Reset();
            Ipc.Reset();
            Math.Reset();
            Keypad.Reset();
            Rtc.Reset();
            Control.Reset();
            Spi.Reset();
            // The firmware's RF type tells which console it came from: 2 = DS (MM3155 + RF9008), 3 = DS-Lite.
            Wifi.ChipId = Spi.Firmware.Data[0x40] == 2 ? (ushort)0x1440 : (ushort)0xC340;
            Wifi.Reset();
            DebugOutput.Reset();
            Cartridge.Reset();
            EngineA.Reset();
            EngineB.Reset();
            Gpu3D.Reset();
            Sound.Reset();
            Lcd.Reset();

            bool hle = !(HasRealBios && UseRealBios);
            Arm9.SwiHandler = hle ? Hle9.Handle : null;
            Arm7.SwiHandler = hle ? Hle7.Handle : null;

            _arm9Time = 0;
            _arm7Time = 0;
            FrameCount = 0;
        }

        /// <summary>Reset and start the inserted cartridge.</summary>
        public void Boot()
        {
            Reset();
            if (HasRealBios && UseRealBios)
            {
                if (Rom != null)
                    Cartridge.Key1 = new Key1Decryptor(Bios7, BitConverter.ToUInt32(Rom.Data, 0x0C), Rom.Data);
                Arm9.ExceptionBase = 0xFFFF0000u;
                Arm9.SetPc(0xFFFF0000u);
                Arm7.SetPc(0x00000000u);
                return;
            }

            if (Rom == null)
                throw new InvalidOperationException("No ROM loaded.");
            DirectBoot.Apply(this, Rom);
        }

        // ── Save states ───────────────────────────────────────────────────────

        public const string StateMagic = "PX86-NDS-STATE";
        public const int StateVersion = 2;

        /// <summary>Write the complete machine state (memory, CPUs, every device) to <paramref name="stream"/>.</summary>
        public void SaveState(Stream stream) => SerializeState(StateSerializer.ForWriting(stream));

        /// <summary>
        /// Restore a state written by <see cref="SaveState"/> for the same game.
        /// Throws <see cref="InvalidDataException"/> for a state from another
        /// game or version; the emulator is unchanged in that case only if the
        /// header check fails (later failures leave it partially loaded).
        /// </summary>
        public void LoadState(Stream stream)
        {
            FlushCode();
            try
            {
                SerializeState(StateSerializer.ForReading(stream));
            }
            finally
            {
                FlushCode(); // memory and mappings were replaced wholesale
            }
        }

        private void SerializeState(StateSerializer s)
        {
            string magic = StateMagic;
            int version = StateVersion;
            string game = Rom?.Header.GameCode ?? "";
            s.Sync(ref magic);
            s.Sync(ref version);
            s.Sync(ref game);
            if (s.IsReading)
            {
                if (magic != StateMagic)
                    throw new InvalidDataException("Not a Px86.NintendoDS save state.");
                if (version != StateVersion)
                    throw new InvalidDataException($"Save state version {version} is not supported (expected {StateVersion}).");
                if (game != (Rom?.Header.GameCode ?? ""))
                    throw new InvalidDataException($"Save state is for game {game}, not {Rom?.Header.GameCode}.");
            }

            s.Section("Memory");
            s.Sync(MainRam);
            s.Sync(SharedWram);
            s.Sync(Arm7Wram);
            s.Sync(Palette);
            s.Sync(Oam);

            Scheduler.Serialize(s);
            Arm9.Serialize(s);
            Arm7.Serialize(s);
            Cp15.Serialize(s);
            Arm9Bus.Serialize(s);
            Arm7Bus.Serialize(s);
            Vram.Serialize(s);
            Irq9.Serialize(s);
            Irq7.Serialize(s);
            Timers9.Serialize(s);
            Timers7.Serialize(s);
            Dma9.Serialize(s);
            Dma7.Serialize(s);
            Ipc.Serialize(s);
            Math.Serialize(s);
            Keypad.Serialize(s);
            Rtc.Serialize(s);
            Control.Serialize(s);
            Spi.Serialize(s);
            Wifi.Serialize(s);
            DebugOutput.Serialize(s);
            Cartridge.Serialize(s);
            Lcd.Serialize(s);
            EngineA.Serialize(s);
            EngineB.Serialize(s);
            Gpu3D.Serialize(s);
            Sound.Serialize(s);
            Hle9.Serialize(s);
            Hle7.Serialize(s);

            s.Section("Loop");
            s.Sync(ref _arm9Time);
            s.Sync(ref _arm7Time);
            long frames = FrameCount;
            s.Sync(ref frames);
            FrameCount = frames;
            s.Section("End");
        }

        // ── Main loop ─────────────────────────────────────────────────────────

        private long _arm9Time;
        private long _arm7Time;
        private bool _frameDone;

        /// <summary>Run until the next VBlank (one 59.83 Hz frame), or until the debugger stops a core.</summary>
        public void RunFrame() => Run(LcdController.Arm9CyclesPerFrame * 2);

        /// <summary>Run approximately <paramref name="arm9Cycles"/> ARM9 cycles (tests, debugging).</summary>
        public void RunCycles(long arm9Cycles)
        {
            long end = Scheduler.Now + arm9Cycles;
            while (Scheduler.Now < end)
            {
                if (!RunSlice(end))
                    return;
            }
        }

        /// <summary>
        /// Run until VBlank starts, <paramref name="arm9Cycles"/> have passed or
        /// the <see cref="Debugger"/> stops a core, whichever comes first. A
        /// stopped run leaves the machine between two instructions of the
        /// stopped core; calling this again continues exactly from there.
        /// </summary>
        public RunResult Run(long arm9Cycles)
        {
            _frameDone = false;
            long end = Scheduler.Now + System.Math.Max(arm9Cycles, 1);
            while (!_frameDone && Scheduler.Now < end)
            {
                if (!RunSlice())
                    return RunResult.Break;
            }
            return _frameDone ? RunResult.FrameCompleted : RunResult.BudgetUsed;
        }

        // ── Debugger ──────────────────────────────────────────────────────────

        private DebugMonitor? _debugger;

        /// <summary>Breakpoints/watchpoints. Null (the default) means no checks at all.</summary>
        public DebugMonitor? Debugger
        {
            get => _debugger;
            set
            {
                if (_debugger != null)
                    _debugger.WatchesChanged -= InstallWatchBuses;
                _debugger = value;
                if (_debugger != null)
                    _debugger.WatchesChanged += InstallWatchBuses;
                InstallWatchBuses();
            }
        }

        private void InstallWatchBuses()
        {
            var d = _debugger;
            Arm9.Bus = d != null && d.HasWatches(DebugCpu.Arm9) ? new WatchingMemoryBus(Arm9Bus, d, DebugCpu.Arm9, Arm9) : Arm9Bus;
            Arm7.Bus = d != null && d.HasWatches(DebugCpu.Arm7) ? new WatchingMemoryBus(Arm7Bus, d, DebugCpu.Arm7, Arm7) : Arm7Bus;
        }

        public ArmCpu Cpu(DebugCpu cpu) => cpu == DebugCpu.Arm9 ? Arm9 : Arm7;

        /// <summary>
        /// Execute exactly one instruction on <paramref name="cpu"/> (or take
        /// its pending IRQ), then let the other core and the hardware catch up
        /// to the same time. A halted core is first run until it wakes. Returns
        /// false if the other core hit a breakpoint while catching up.
        /// </summary>
        public bool StepInstruction(DebugCpu cpu)
        {
            bool arm9 = cpu == DebugCpu.Arm9;
            var core = arm9 ? Arm9 : Arm7;
            var irq = arm9 ? Irq9 : Irq7;
            var dma = arm9 ? Dma9 : Dma7;
            int clock = arm9 ? 1 : 2;

            // Bounded so a core that never wakes (IRQs off) can't hang the debugger.
            for (int guard = 0; guard < 100_000; guard++)
            {
                WakeIfPending(core, irq);
                bool halted = core.Halted && !(core.IrqLine && (core.Cpsr & ArmCpu.FLAG_I) == 0);
                ref long time = ref arm9 ? ref _arm9Time : ref _arm7Time;
                if (halted)
                {
                    time = System.Math.Max(time, Scheduler.NextEventTime);
                }
                else
                {
                    Scheduler.CurrentTime = time;
                    time += core.Step() * clock;
                    if (dma.PendingCycles != 0)
                    {
                        time += dma.PendingCycles * clock;
                        dma.PendingCycles = 0;
                    }
                }

                long until = time;
                while (Scheduler.Now < until)
                {
                    if (!RunSlice(until))
                        return false;
                }
                if (!halted)
                    return true;
            }
            return true;
        }

        private bool RunSlice(long hardLimit = long.MaxValue)
        {
            long target = System.Math.Min(Scheduler.NextEventTime, Scheduler.Now + MaxSliceCycles);
            target = System.Math.Min(target, hardLimit);
            if (target <= Scheduler.Now)
                target = Scheduler.Now + 1;
            var debug = _debugger;

            bool cached = CpuMode != CpuMode.Interpreter && debug == null;
            var hooks = Hooks.Active ? Hooks : null;

            // ARM9
            WakeIfPending(Arm9, Irq9);
            while (_arm9Time < target)
            {
                if (Arm9.Halted && !(Arm9.IrqLine && (Arm9.Cpsr & ArmCpu.FLAG_I) == 0))
                {
                    _arm9Time = target;
                    break;
                }
                // Hooks see the PC before the instruction runs; with a debugger they
                // run after the breakpoint check, so a resumed break doesn't print twice.
                if (hooks != null && debug == null && !Arm9.IrqPending)
                    hooks.OnExecute(0, Arm9);
                if (cached && !Arm9.IrqPending && _blocks9.Get() is { } block9)
                {
                    _arm9Time = RunBlock(Arm9, block9, _arm9Time, target, Dma9, 1);
                    continue;
                }
                if (debug != null && debug.CheckBreakpoint(0, Arm9.Pc))
                    return false;
                if (hooks != null && debug != null && !Arm9.IrqPending)
                    hooks.OnExecute(0, Arm9);
                Scheduler.CurrentTime = _arm9Time;
                _arm9Time += Arm9.Step();
                if (Dma9.PendingCycles != 0)
                {
                    _arm9Time += Dma9.PendingCycles;
                    Dma9.PendingCycles = 0;
                }
                if (debug?.PendingBreak != null)
                    return false;
            }

            // ARM7 (one ARM7 cycle = two ARM9 cycles)
            WakeIfPending(Arm7, Irq7);
            while (_arm7Time < target)
            {
                if (Arm7.Halted && !(Arm7.IrqLine && (Arm7.Cpsr & ArmCpu.FLAG_I) == 0))
                {
                    _arm7Time = target;
                    break;
                }
                // Hooks see the PC before the instruction runs; with a debugger they
                // run after the breakpoint check, so a resumed break doesn't print twice.
                if (hooks != null && debug == null && !Arm7.IrqPending)
                    hooks.OnExecute(1, Arm7);
                if (cached && !Arm7.IrqPending && _blocks7.Get() is { } block7)
                {
                    _arm7Time = RunBlock(Arm7, block7, _arm7Time, target, Dma7, 2);
                    continue;
                }
                if (debug != null && debug.CheckBreakpoint(1, Arm7.Pc))
                    return false;
                if (hooks != null && debug != null && !Arm7.IrqPending)
                    hooks.OnExecute(1, Arm7);
                Scheduler.CurrentTime = _arm7Time;
                _arm7Time += Arm7.Step() * 2;
                if (Dma7.PendingCycles != 0)
                {
                    _arm7Time += Dma7.PendingCycles * 2;
                    Dma7.PendingCycles = 0;
                }
                if (debug?.PendingBreak != null)
                    return false;
            }

            Scheduler.AdvanceTo(target);
            return true;
        }

        /// <summary>
        /// Run a cached block: the same per-instruction work as the
        /// interpreter loop above (device time, DMA stalls, IRQ/halt checks),
        /// stopping at the slice end, a jump, a halt, a pending IRQ, or when
        /// the block's code was overwritten.
        /// </summary>
        private long RunBlock(ArmCpu cpu, CachedBlock block, long time, long target, DmaController dma, int clock)
        {
            if (block.Compiled != null)
                return block.Compiled(cpu, block, Scheduler, dma, time, target);
            if (CpuMode == CpuMode.Jit && !block.NoJit && ++block.Runs >= CachedBlock.JitThreshold)
            {
                // A compiler bug must not stop the game: such a block stays interpreted.
                // Invalid IL is reported on the first call, before any instruction runs.
                try
                {
                    var compiled = JitCompiler.Compile(block, clock);
                    long end = compiled(cpu, block, Scheduler, dma, time, target);
                    block.Compiled = compiled;
                    JitCompiles++;
                    return end;
                }
                catch (InvalidProgramException)
                {
                    block.NoJit = true;
                }
            }
            uint expected = block.Address;
            uint size = block.Thumb ? 2u : 4u;
            var opcodes = block.Opcodes;
            for (int i = 0; i < block.Count; i++)
            {
                Scheduler.CurrentTime = time;
                int cycles = block.Thumb
                    ? cpu.ExecuteDecodedThumb(opcodes[i], block.ThumbHandlers![i])
                    : cpu.ExecuteDecodedArm(opcodes[i], block.ArmHandlers![i]);
                time += cycles * clock;
                if (dma.PendingCycles != 0)
                {
                    time += dma.PendingCycles * clock;
                    dma.PendingCycles = 0;
                }
                expected += size;
                if (time >= target || cpu.Pc != expected || cpu.Halted || cpu.IrqPending || !block.Valid || cpu.IsThumb != block.Thumb)
                    break;
            }
            return time;
        }

        /// <summary>HALT ends as soon as (IE &amp; IF) != 0, even if the IRQ was already pending when the CPU halted.</summary>
        private static void WakeIfPending(ArmCpu cpu, InterruptController irq)
        {
            if (cpu.Halted && (irq.Ie & irq.If) != 0)
                cpu.Halted = false;
        }

        // ── Video hooks ───────────────────────────────────────────────────────

        private void RenderScanline(int line)
        {
            bool lcd = Control.LcdEnabled;
            EngineA.RenderScanline(line, lcd && Control.EngineAEnabled);
            EngineB.RenderScanline(line, lcd && Control.EngineBEnabled);
        }

        private void OnVBlank()
        {
            // POWCNT1 bits 2–3: 3D rendering and geometry engines powered.
            Gpu3D.OnVBlank(renderEnabled: (Control.PowerControl9 & 0x000C) == 0x000C);
            EngineA.OnVBlank();
            EngineB.OnVBlank();
            FrameCount++;
            Hooks.OnFrame();
            _frameDone = true;
            FrameCompleted?.Invoke();
        }
    }
}
