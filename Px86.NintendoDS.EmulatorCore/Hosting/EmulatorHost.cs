using System.Diagnostics;
using Px86.NintendoDS.Common.Cpu;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Hardware;
using Px86.NintendoDS.EmulatorCore.Video;
using Px86.NintendoDS.EmulatorCore.Video.Gpu3D;

namespace Px86.NintendoDS.EmulatorCore.Hosting
{
    /// <summary>Frame limiter, numbered like PCSX2's (and the PINE SetLimiterMode opcode).</summary>
    public enum LimiterMode : byte
    {
        Nominal = 0,
        Turbo = 1,
        Slomo = 2,
        Unlimited = 3,
    }

    /// <summary>Emulator status, numbered like PINE's MsgStatus reply.</summary>
    public enum HostStatus : uint
    {
        Running = 0,
        Paused = 1,
        Shutdown = 2,
    }

    /// <summary>Buttons and touch position fed to the console.</summary>
    public readonly record struct InputState(NdsButton Buttons, (int X, int Y)? Touch)
    {
        public static readonly InputState None = new(NdsButton.None, null);
    }

    /// <summary>
    /// Runs a <see cref="GameSession"/> on its own thread and lets other
    /// threads control it safely — the window, the PINE socket, the GS stream
    /// socket, or a program that embeds the emulator (such as CodeDesigner).
    ///
    /// Threading
    ///   The emulation thread runs the console in chunks of
    ///   <see cref="ChunkCycles"/> while holding <see cref="Invoke(Action)"/>'s
    ///   lock, and releases it between chunks. Requests from other threads take
    ///   the same lock, so they always see the machine between two slices
    ///   (never in the middle of an instruction) and at most one chunk late.
    ///
    /// Pacing
    ///   Frames are paced to 59.83 Hz × the <see cref="Limiter"/> speed.
    ///   <see cref="FastForward"/> (the window's hold-to-fast-forward key) runs
    ///   unthrottled while it is set.
    ///
    /// Debugging
    ///   <see cref="Debugger"/> holds breakpoints and watchpoints. A hit pauses
    ///   the host and raises <see cref="BreakHit"/>. They survive loading
    ///   another game or resetting.
    /// </summary>
    public sealed class EmulatorHost : IDisposable
    {
        public const double FrameRate = 59.8261;

        /// <summary>Work done per lock hold: 16 scanlines, about 1 ms of DS time.</summary>
        public const long ChunkCycles = LcdController.Arm9CyclesPerLine * 16;

        private readonly object _sync = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly object _inputLock = new();
        private readonly uint[] _frame = new uint[ScreenCapture.Width * ScreenCapture.Height];
        private int _waiting;
        private Thread? _thread;
        private volatile bool _running;
        private volatile bool _paused;
        private int _framesToAdvance;
        private long _framesThisSecond;
        private InputState _remoteInput = InputState.None;
        private bool _remoteInputActive;

        public EmulatorHost()
        {
            Debugger = new DebugMonitor();
        }

        // ── State ─────────────────────────────────────────────────────────────

        public DebugMonitor Debugger { get; }

        /// <summary>The running game, or null before one is loaded. Touch it only inside <see cref="Invoke(Action)"/>.</summary>
        public GameSession? Session { get; private set; }

        public NdsEmulator? Emulator => Session?.Emulator;

        public HostStatus Status => Session == null ? HostStatus.Shutdown : _paused ? HostStatus.Paused : HostStatus.Running;

        public bool IsPaused => _paused;

        public LimiterMode Limiter { get; set; }

        public double TurboSpeed { get; set; } = 2.0;

        public double SlomoSpeed { get; set; } = 0.5;

        /// <summary>Run unthrottled while true (hold-to-fast-forward).</summary>
        public bool FastForward { get; set; }

        /// <summary>Emulated frames per second, measured over the last second.</summary>
        public double Fps { get; private set; }

        /// <summary>The most recent breakpoint or watchpoint hit.</summary>
        public BreakInfo? LastBreak { get; private set; }

        /// <summary>The host's own input (keyboard, gamepad, mouse). Polled before every chunk.</summary>
        public Func<InputState>? LocalInput { get; set; }

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// A frame finished: both screens stacked, 256×384 0xAARRGGBB, plus the
        /// frame number. The buffer is reused — copy it if you keep it. Raised
        /// with the lock held, usually on the emulation thread.
        /// </summary>
        public event Action<uint[], long>? FrameReady;

        /// <summary>Raised after <see cref="FrameReady"/>, for per-frame work such as audio.</summary>
        public event Action<NdsEmulator>? FrameEnded;

        /// <summary>A breakpoint or watchpoint paused the host. Raised without the lock.</summary>
        public event Action<BreakInfo>? BreakHit;

        /// <summary>A new game was attached (also raised for <see cref="Reset"/>).</summary>
        public event Action<GameSession>? GameStarted;

        // ── Lifetime ──────────────────────────────────────────────────────────

        /// <summary>Start the emulation thread. Nothing runs until a game is loaded.</summary>
        public void Start(bool paused = false)
        {
            if (_thread != null)
                throw new InvalidOperationException("The host is already running.");
            _paused = paused;
            _running = true;
            _thread = new Thread(RunLoop) { Name = "Px86 emulation", IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _wake.Set();
            if (_thread != null && _thread != Thread.CurrentThread)
                _thread.Join(2000);
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
            Invoke(() =>
            {
                Session?.Dispose();
                Session = null;
            });
            _wake.Dispose();
        }

        // ── Thread-safe access ────────────────────────────────────────────────

        /// <summary>Run <paramref name="action"/> while the emulator is stopped between slices.</summary>
        public void Invoke(Action action)
        {
            Interlocked.Increment(ref _waiting);
            lock (_sync)
            {
                Interlocked.Decrement(ref _waiting);
                action();
            }
        }

        /// <inheritdoc cref="Invoke(Action)"/>
        public T Invoke<T>(Func<T> func)
        {
            Interlocked.Increment(ref _waiting);
            lock (_sync)
            {
                Interlocked.Decrement(ref _waiting);
                return func();
            }
        }

        // ── Games ─────────────────────────────────────────────────────────────

        /// <summary>Start a game (replacing the current one). Throws if the files can't be loaded.</summary>
        public GameSession LoadGame(GameLoadOptions options)
        {
            var session = GameSession.Start(options);
            Attach(session);
            return session;
        }

        /// <summary>Run an already started session. The host owns it from now on.</summary>
        public void Attach(GameSession session)
        {
            Invoke(() =>
            {
                if (Session != null)
                {
                    Session.Emulator.FrameCompleted -= OnEmulatorFrame;
                    Session.Emulator.Debugger = null;
                    Session.Dispose();
                }
                Session = session;
                session.Emulator.Debugger = Debugger;
                session.Emulator.FrameCompleted += OnEmulatorFrame;
                Debugger.PendingBreak = null;
                _framesToAdvance = 0;
            });
            GameStarted?.Invoke(session);
            _wake.Set();
        }

        /// <summary>Restart the current game from boot (the save file is kept).</summary>
        public bool Reset()
        {
            var session = Invoke(() =>
            {
                if (Session == null)
                    return null;
                Session.FlushSave();
                Session.Emulator.Boot();
                Debugger.PendingBreak = null;
                return Session;
            });
            if (session == null)
                return false;
            GameStarted?.Invoke(session);
            return true;
        }

        // ── Run control ───────────────────────────────────────────────────────

        public void Pause() => _paused = true;

        /// <summary>Continue. A core sitting on a breakpoint runs that instruction instead of stopping again.</summary>
        public void Resume()
        {
            Invoke(() =>
            {
                if (Emulator is { } nds)
                {
                    Debugger.SkipOnce(DebugCpu.Arm9, nds.Arm9.Pc);
                    Debugger.SkipOnce(DebugCpu.Arm7, nds.Arm7.Pc);
                }
                _framesToAdvance = 0;
                _paused = false;
            });
            _wake.Set();
        }

        public void TogglePause()
        {
            if (_paused)
                Resume();
            else
                Pause();
        }

        /// <summary>Run <paramref name="frames"/> frames, then pause.</summary>
        public void FrameAdvance(int frames)
        {
            if (frames < 1)
                throw new ArgumentOutOfRangeException(nameof(frames));
            Invoke(() =>
            {
                _framesToAdvance = frames;
                _paused = false;
            });
            _wake.Set();
        }

        /// <summary>Execute one instruction on <paramref name="cpu"/>. Only while paused; returns the new PC, or null.</summary>
        public uint? Step(DebugCpu cpu)
        {
            BreakInfo? hit = null;
            uint? pc = Invoke<uint?>(() =>
            {
                if (Emulator is not { } nds || !_paused)
                    return null;
                if (!nds.StepInstruction(cpu))
                    hit = TakeBreak();
                Debugger.PendingBreak = null;
                return nds.Cpu(cpu).Pc;
            });
            if (hit is { } h)
                BreakHit?.Invoke(h);
            return pc;
        }

        /// <summary>
        /// Like <see cref="Step"/>, but a call (BL, BLX, SWI) runs to the
        /// instruction after it. The emulator keeps running until then, so
        /// this returns before the call has finished.
        /// </summary>
        public bool StepOver(DebugCpu cpu)
        {
            uint? next = Invoke<uint?>(() =>
            {
                if (Emulator is not { } nds || !_paused)
                    return null;
                return CallReturnAddress(nds.Cpu(cpu));
            });
            if (next == null)
                return Step(cpu) != null;
            Invoke(() => Debugger.AddTemporaryBreakpoint(cpu, next.Value));
            Resume();
            return true;
        }

        /// <summary>
        /// Run until the current function returns, using LR as the return
        /// address. Reliable in leaf functions and before LR is pushed; ARM has
        /// no frame chain to do better.
        /// </summary>
        public bool StepOut(DebugCpu cpu)
        {
            bool ok = Invoke(() =>
            {
                if (Emulator is not { } nds || !_paused)
                    return false;
                Debugger.AddTemporaryBreakpoint(cpu, nds.Cpu(cpu).R[14]);
                return true;
            });
            if (ok)
                Resume();
            return ok;
        }

        /// <summary>Address after the call at the core's PC, or null if the instruction isn't a call.</summary>
        public static uint? CallReturnAddress(ArmCpu core)
        {
            uint pc = core.Pc;
            if (core.IsThumb)
            {
                ushort op = core.Bus.Fetch16(pc);
                if ((op & 0xF800) == 0xF000)
                    return pc + 4;                  // BL/BLX prefix + suffix
                if ((op & 0xFF87) == 0x4780 || (op & 0xFF00) == 0xDF00)
                    return pc + 2;                  // BLX Rm, SWI
                return null;
            }

            uint arm = core.Bus.Fetch32(pc);
            bool bl = (arm & 0x0F000000) == 0x0B000000;                 // BL (and BLX imm when cond = 1111)
            bool blxReg = (arm & 0x0FFFFFF0) == 0x012FFF30;
            bool swi = (arm & 0x0F000000) == 0x0F000000 && arm >> 28 != 0xF;
            return bl || blxReg || swi ? pc + 4 : null;
        }

        // ── Save states ───────────────────────────────────────────────────────

        public bool SaveState(int slot) => Invoke(() =>
        {
            if (Session == null) return false;
            Session.SaveState(slot);
            return true;
        });

        public bool LoadState(int slot) => Invoke(() => Session?.LoadState(slot) ?? false);

        public bool SaveStateFile(string path) => Invoke(() =>
        {
            if (Session == null) return false;
            Session.SaveStateFile(path);
            return true;
        });

        public bool LoadStateFile(string path) => Invoke(() => Session?.LoadStateFile(path) ?? false);

        // ── GX capture ────────────────────────────────────────────────────────

        /// <summary>
        /// Record the 3D commands of the next frame to <paramref name="path"/>
        /// (see <see cref="GxCapture"/>). The file is written at the next buffer
        /// swap; <paramref name="done"/> gets the path (or the error). False if no game runs.
        /// </summary>
        public bool CaptureGx(string path, Action<string, Exception?>? done = null) => Invoke(() =>
        {
            if (Emulator is not { } nds)
                return false;
            var recorder = new GxCaptureRecorder(nds);
            recorder.Completed += bytes =>
            {
                recorder.Detach();
                try
                {
                    File.WriteAllBytes(path, bytes);
                    done?.Invoke(path, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    done?.Invoke(path, ex);
                }
            };
            recorder.Start();
            return true;
        });

        // ── Remote input ──────────────────────────────────────────────────────

        /// <summary>
        /// Input from a remote controller (PINE). Buttons are OR'ed with
        /// <see cref="LocalInput"/>; a remote touch wins over a local one.
        /// </summary>
        public InputState RemoteInput
        {
            get { lock (_inputLock) return _remoteInput; }
            set
            {
                lock (_inputLock)
                {
                    _remoteInput = value;
                    _remoteInputActive = value != InputState.None;
                }
            }
        }

        private void ApplyInput(NdsEmulator nds)
        {
            var local = LocalInput?.Invoke() ?? InputState.None;
            InputState remote;
            bool remoteActive;
            lock (_inputLock)
            {
                remote = _remoteInput;
                remoteActive = _remoteInputActive;
            }
            if (LocalInput == null && !remoteActive)
                return; // nobody drives input: leave whatever was set directly

            var buttons = local.Buttons | remote.Buttons;
            var touch = remote.Touch ?? local.Touch;
            nds.Keypad.SetButtons(buttons);
            if (touch is { } p)
            {
                nds.Spi.Touch.SetTouch(p.X, p.Y);
                nds.Keypad.SetPen(true);
            }
            else
            {
                nds.Spi.Touch.ReleaseTouch();
                nds.Keypad.SetPen(false);
            }
        }

        // ── Emulation thread ──────────────────────────────────────────────────

        private BreakInfo? TakeBreak()
        {
            var hit = Debugger.PendingBreak;
            Debugger.PendingBreak = null;
            if (hit != null)
            {
                LastBreak = hit;
                _paused = true;
                _framesToAdvance = 0;
            }
            return hit;
        }

        private void OnEmulatorFrame()
        {
            var nds = Session!.Emulator;
            ScreenCapture.Compose(nds, _frame);
            FrameReady?.Invoke(_frame, nds.FrameCount);
            FrameEnded?.Invoke(nds);
            Interlocked.Increment(ref _framesThisSecond);
            if (_framesToAdvance > 0 && --_framesToAdvance == 0)
                _paused = true;
        }

        private void RunLoop()
        {
            var clock = Stopwatch.StartNew();
            double nextFrame = 0;
            double fpsWindowStart = 0;

            while (_running)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now - fpsWindowStart >= 1.0)
                {
                    Fps = Interlocked.Exchange(ref _framesThisSecond, 0) / (now - fpsWindowStart);
                    fpsWindowStart = now;
                }

                if (_paused || Session == null)
                {
                    _wake.WaitOne(50);
                    nextFrame = clock.Elapsed.TotalSeconds;
                    continue;
                }

                RunResult result = RunResult.BudgetUsed;
                BreakInfo? hit = null;
                try
                {
                    lock (_sync)
                    {
                        if (Session is { } session && !_paused)
                        {
                            ApplyInput(session.Emulator);
                            result = session.Emulator.Run(ChunkCycles);
                            if (result == RunResult.Break)
                                hit = TakeBreak();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _paused = true;
                    Console.Error.WriteLine($"Emulation stopped: {ex}");
                    if (Emulator is { } nds)
                        Console.Error.WriteLine($"ARM9: {nds.Arm9}\nARM7: {nds.Arm7}");
                    continue;
                }

                if (hit is { } h)
                    BreakHit?.Invoke(h);

                // Let threads queued on the lock in before taking it again.
                while (Volatile.Read(ref _waiting) > 0)
                    Thread.Yield();

                if (result == RunResult.FrameCompleted)
                    nextFrame = Pace(clock, nextFrame);
            }
        }

        private double Pace(Stopwatch clock, double nextFrame)
        {
            double speed = FastForward ? 0 : Limiter switch
            {
                LimiterMode.Turbo => TurboSpeed,
                LimiterMode.Slomo => SlomoSpeed,
                LimiterMode.Unlimited => 0,
                _ => 1,
            };
            double now = clock.Elapsed.TotalSeconds;
            if (speed <= 0)
                return now;

            nextFrame += 1.0 / (FrameRate * speed);
            double wait = nextFrame - now;
            if (wait < -0.25)
                return now; // fell far behind: don't try to catch up
            if (wait > 0.002)
                _wake.WaitOne(TimeSpan.FromSeconds(wait - 0.001));
            while (clock.Elapsed.TotalSeconds < nextFrame && !_paused && _running)
                Thread.SpinWait(50);
            return nextFrame;
        }
    }
}
