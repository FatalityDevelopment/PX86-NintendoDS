using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.Common.Cpu.Coprocessors;
using Px86.NintendoDS.EmulatorCore.Audio;
using Px86.NintendoDS.EmulatorCore.Bios;
using Px86.NintendoDS.EmulatorCore.Cartridge;
using Px86.NintendoDS.EmulatorCore.Hardware;
using Px86.NintendoDS.EmulatorCore.Hardware.Spi;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.EmulatorCore.Video.Gpu3D;
using Px86.NintendoDS.FileFormats.Rom;

namespace Px86.NintendoDS.EmulatorCore
{
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
            Cartridge = new CartridgeSlot(Scheduler, Irq9, Irq7, Dma9, Dma7);
            Lcd = new LcdController(Scheduler, Irq9, Irq7, Dma9, Dma7);
            EngineA = new Gpu2DEngine(true, Vram, Palette, Oam);
            EngineB = new Gpu2DEngine(false, Vram, Palette, Oam);
            Gpu3D = new Gpu3DEngine(Vram, Irq9, Dma9);
            EngineA.ThreeDLineProvider = Gpu3D.GetLine;
            EngineA.Alpha3DProvider = Gpu3D.GetAlpha;
            Sound = new SoundUnit();

            Hle9 = new HleBios(this, isArm9: true);
            Hle7 = new HleBios(this, isArm9: false);

            Control.ExMemChanged += () => Cartridge.Owner = Control.NdsSlotOwner;
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

        // ── Main loop ─────────────────────────────────────────────────────────

        private long _arm9Time;
        private long _arm7Time;
        private bool _frameDone;

        /// <summary>Run until the next VBlank (one 59.83 Hz frame).</summary>
        public void RunFrame()
        {
            _frameDone = false;
            long limit = Scheduler.Now + LcdController.Arm9CyclesPerFrame * 2;
            while (!_frameDone && Scheduler.Now < limit)
                RunSlice();
        }

        /// <summary>Run approximately <paramref name="arm9Cycles"/> ARM9 cycles (tests, debugging).</summary>
        public void RunCycles(long arm9Cycles)
        {
            long end = Scheduler.Now + arm9Cycles;
            while (Scheduler.Now < end)
                RunSlice(end);
        }

        private void RunSlice(long hardLimit = long.MaxValue)
        {
            long target = System.Math.Min(Scheduler.NextEventTime, Scheduler.Now + MaxSliceCycles);
            target = System.Math.Min(target, hardLimit);
            if (target <= Scheduler.Now)
                target = Scheduler.Now + 1;

            // ARM9
            WakeIfPending(Arm9, Irq9);
            while (_arm9Time < target)
            {
                if (Arm9.Halted && !(Arm9.IrqLine && (Arm9.Cpsr & ArmCpu.FLAG_I) == 0))
                {
                    _arm9Time = target;
                    break;
                }
                Scheduler.CurrentTime = _arm9Time;
                _arm9Time += Arm9.Step();
                if (Dma9.PendingCycles != 0)
                {
                    _arm9Time += Dma9.PendingCycles;
                    Dma9.PendingCycles = 0;
                }
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
                Scheduler.CurrentTime = _arm7Time;
                _arm7Time += Arm7.Step() * 2;
                if (Dma7.PendingCycles != 0)
                {
                    _arm7Time += Dma7.PendingCycles * 2;
                    Dma7.PendingCycles = 0;
                }
            }

            Scheduler.AdvanceTo(target);
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
            _frameDone = true;
            FrameCompleted?.Invoke();
        }
    }
}
