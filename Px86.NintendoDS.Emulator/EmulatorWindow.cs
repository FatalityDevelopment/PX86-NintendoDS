using System.Diagnostics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// The desktop window: an SDL window with an OpenGL context showing both
    /// screens stacked vertically, keyboard/gamepad input and mouse touch.
    ///
    /// Threads
    ///   Emulation thread  runs <see cref="NdsEmulator.RunFrame"/> paced to
    ///                     59.8261 Hz (unpaced while fast-forwarding), applies
    ///                     the latest input at the start of each frame and
    ///                     publishes finished frames into a shared buffer.
    ///   Window thread     Silk.NET's loop: polls input, uploads the newest
    ///                     frame to a texture and draws it letterboxed.
    /// </summary>
    public sealed class EmulatorWindow : IDisposable
    {
        public const double FrameRate = 59.8261;

        private readonly GameSession _session;
        private readonly EmulatorSettings _settings;
        private readonly IWindow _window;
        private readonly InputMapper _input;
        private readonly AudioOutput? _audio;

        private readonly uint[] _sharedFrame = new uint[ScreenCapture.Width * ScreenCapture.Height];
        private readonly object _frameLock = new();
        private bool _frameReady;

        private Thread? _emulationThread;
        private volatile bool _running;
        private volatile bool _paused;

        private ScreenRenderer? _renderer;
        private IInputContext? _inputContext;
        private long _framesShown;
        private readonly Stopwatch _titleTimer = Stopwatch.StartNew();
        private long _framesAtLastTitle;

        public EmulatorWindow(GameSession session, EmulatorSettings settings)
        {
            _session = session;
            _settings = settings;
            _input = new InputMapper(settings);

            NativeLibraries.Configure();
            SdlWindowing.Use();
            Silk.NET.Input.Sdl.SdlInput.RegisterPlatform();

            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(ScreenCapture.Width * settings.Scale, ScreenCapture.Height * settings.Scale),
                Title = WindowTitle(0),
                VSync = true,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            };
            _window = Window.Create(options);
            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.FramebufferResize += size => _renderer?.Resize(size.X, size.Y);
            _window.Closing += OnClosing;

            if (settings.AudioEnabled)
                _audio = AudioOutput.TryCreate(session.Emulator);
        }

        public void Run() => _window.Run();

        private string WindowTitle(double fps)
        {
            var header = _session.Rom.Header;
            string title = $"Px86.NintendoDS — {header.Title} [{header.GameCode}]";
            if (fps > 0) title += $" — {fps:F0} fps";
            if (_paused) title += " — paused";
            if (_input.FastForward) title += " — fast-forward";
            return title;
        }

        // ── Window thread ─────────────────────────────────────────────────────

        private void OnLoad()
        {
            var gl = GL.GetApi(_window);
            _renderer = new ScreenRenderer(gl);
            _renderer.Resize(_window.FramebufferSize.X, _window.FramebufferSize.Y);

            _inputContext = _window.CreateInput();
            foreach (var keyboard in _inputContext.Keyboards)
            {
                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyUp += (_, key, _) => _input.KeyUp(key);
            }
            foreach (var mouse in _inputContext.Mice)
            {
                mouse.MouseDown += (m, button) => { if (button == MouseButton.Left) UpdateTouch(m.Position, true); };
                mouse.MouseUp += (m, button) => { if (button == MouseButton.Left) _input.SetTouch(null); };
                mouse.MouseMove += (m, position) => { if (m.IsButtonPressed(MouseButton.Left)) UpdateTouch(position, true); };
            }

            _running = true;
            _emulationThread = new Thread(EmulationLoop) { Name = "Px86 emulation", IsBackground = true };
            _emulationThread.Start();
        }

        private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
        {
            switch (key)
            {
                case Key.Escape:
                    _window.Close();
                    return;
                case Key.P:
                    _paused = !_paused;
                    return;
                case Key.F11:
                    _window.WindowState = _window.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;
                    return;
                case Key.F12:
                    SaveScreenshot();
                    return;
            }
            _input.KeyDown(key);
        }

        private void UpdateTouch(System.Numerics.Vector2 position, bool down)
        {
            if (_renderer == null)
                return;
            // Mouse positions are in window units; the renderer works in framebuffer pixels (HiDPI).
            float sx = _window.FramebufferSize.X / (float)Math.Max(_window.Size.X, 1);
            float sy = _window.FramebufferSize.Y / (float)Math.Max(_window.Size.Y, 1);
            var point = _renderer.WindowToScreen(position.X * sx, position.Y * sy);
            if (point is { } p && p.Y >= 192)
                _input.SetTouch((p.X, p.Y - 192));
            else if (down)
                _input.SetTouch(null);
        }

        private void OnRender(double delta)
        {
            if (_inputContext != null)
                _input.PollGamepads(_inputContext.Gamepads);

            lock (_frameLock)
            {
                if (_frameReady)
                {
                    _renderer!.Upload(_sharedFrame);
                    _frameReady = false;
                    _framesShown++;
                }
            }
            _renderer!.Draw();

            if (_titleTimer.Elapsed.TotalSeconds >= 1)
            {
                double fps = (_framesShown - _framesAtLastTitle) / _titleTimer.Elapsed.TotalSeconds;
                _framesAtLastTitle = _framesShown;
                _titleTimer.Restart();
                _window.Title = WindowTitle(fps);
            }
        }

        private void SaveScreenshot()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Px86.NintendoDS");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"{_session.Rom.Header.GameCode}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            lock (_frameLock)
                Px86.NintendoDS.FileFormats.Images.PngWriter.Write(path, ScreenCapture.Width, ScreenCapture.Height, _sharedFrame);
            Console.WriteLine($"Screenshot: {path}");
        }

        private void OnClosing()
        {
            _running = false;
            _emulationThread?.Join(2000);
            _session.FlushSave();
        }

        // ── Emulation thread ──────────────────────────────────────────────────

        private void EmulationLoop()
        {
            var nds = _session.Emulator;
            var frame = new uint[ScreenCapture.Width * ScreenCapture.Height];
            var clock = Stopwatch.StartNew();
            double frameSeconds = 1.0 / FrameRate;
            double nextFrame = 0;

            try
            {
                while (_running)
                {
                    if (_paused)
                    {
                        Thread.Sleep(10);
                        nextFrame = clock.Elapsed.TotalSeconds;
                        continue;
                    }

                    _input.Apply(nds);
                    nds.RunFrame();
                    _audio?.OnFrame();

                    ScreenCapture.Compose(nds, frame);
                    lock (_frameLock)
                    {
                        frame.CopyTo(_sharedFrame, 0);
                        _frameReady = true;
                    }

                    if (_input.FastForward)
                    {
                        nextFrame = clock.Elapsed.TotalSeconds;
                        continue;
                    }

                    nextFrame += frameSeconds;
                    double wait = nextFrame - clock.Elapsed.TotalSeconds;
                    if (wait > 0.002)
                        Thread.Sleep(TimeSpan.FromSeconds(wait - 0.001));
                    while (clock.Elapsed.TotalSeconds < nextFrame)
                        Thread.SpinWait(50);
                    if (wait < -0.25)
                        nextFrame = clock.Elapsed.TotalSeconds; // fell far behind: don't try to catch up
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Emulation stopped: {ex}");
                Console.Error.WriteLine($"ARM9: {nds.Arm9}\nARM7: {nds.Arm7}");
            }
        }

        public void Dispose()
        {
            _running = false;
            _audio?.Dispose();
            _renderer?.Dispose();
            _inputContext?.Dispose();
            _window.Dispose();
        }
    }
}
