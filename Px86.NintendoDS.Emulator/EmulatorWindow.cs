using System.Diagnostics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// The desktop window: an SDL window with an OpenGL context showing both
    /// screens stacked vertically, keyboard/gamepad input and mouse touch.
    ///
    /// The console runs on the <see cref="EmulatorHost"/>'s thread. The window
    /// thread (Silk.NET's loop) polls input, uploads the newest frame from
    /// <see cref="EmulatorHost.FrameReady"/> to a texture and draws it
    /// letterboxed. Audio is fed from <see cref="EmulatorHost.FrameEnded"/>.
    /// </summary>
    public sealed class EmulatorWindow : IDisposable
    {
        private readonly EmulatorHost _host;
        private readonly EmulatorSettings _settings;
        private readonly IWindow _window;
        private readonly InputMapper _input;
        private readonly AudioOutput? _audio;

        private readonly uint[] _sharedFrame = new uint[ScreenCapture.Width * ScreenCapture.Height];
        private readonly object _frameLock = new();
        private bool _frameReady;
        private int _stateSlot;
        private volatile bool _closeRequested;

        private ScreenRenderer? _renderer;
        private IInputContext? _inputContext;
        private readonly Stopwatch _titleTimer = Stopwatch.StartNew();

        public EmulatorWindow(EmulatorHost host, EmulatorSettings settings)
        {
            _host = host;
            _settings = settings;
            _input = new InputMapper(settings);

            NativeLibraries.Configure();
            SdlWindowing.Use();
            Silk.NET.Input.Sdl.SdlInput.RegisterPlatform();

            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(ScreenCapture.Width * settings.Scale, ScreenCapture.Height * settings.Scale),
                Title = WindowTitle(),
                VSync = true,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            };
            _window = Window.Create(options);
            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.FramebufferResize += size => _renderer?.Resize(size.X, size.Y);
            _window.Closing += OnClosing;

            if (settings.AudioEnabled)
                _audio = AudioOutput.TryCreate();

            _host.LocalInput = _input.Snapshot;
            _host.FrameReady += OnFrameReady;
            if (_audio != null)
                _host.FrameEnded += _audio.OnFrame;
        }

        public void Run() => _window.Run();

        /// <summary>Close the window from any thread (PINE Shutdown).</summary>
        public void RequestClose() => _closeRequested = true;

        private string WindowTitle()
        {
            string title = "Px86.NintendoDS";
            if (_host.Invoke(() => _host.Emulator?.Rom?.Header) is { } header)
                title += $" — {header.Title} [{header.GameCode}]";
            if (_host.Fps > 0) title += $" — {_host.Fps:F0} fps";
            title += $" — slot {_stateSlot}";
            if (_host.IsPaused) title += " — paused";
            if (_host.FastForward) title += " — fast-forward";
            return title;
        }

        /// <summary>Emulation thread: keep the newest frame for the next redraw.</summary>
        private void OnFrameReady(uint[] frame, long frameNumber)
        {
            lock (_frameLock)
            {
                frame.CopyTo(_sharedFrame, 0);
                _frameReady = true;
            }
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
        }

        private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
        {
            switch (key)
            {
                case Key.Escape:
                    _window.Close();
                    return;
                case Key.P:
                    _host.TogglePause();
                    return;
                case Key.F11:
                    _window.WindowState = _window.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;
                    return;
                case Key.F12:
                    SaveScreenshot();
                    return;
                case Key.F5:
                    RunStateAction(() => _host.SaveState(_stateSlot), $"Saved state slot {_stateSlot}");
                    return;
                case Key.F8:
                    RunStateAction(() => _host.LoadState(_stateSlot), $"Loaded state slot {_stateSlot}", $"State slot {_stateSlot} is empty");
                    return;
                case Key.F6:
                case Key.F7:
                    _stateSlot = (_stateSlot + (key == Key.F7 ? 1 : GameSession.StateSlots - 1)) % GameSession.StateSlots;
                    Console.WriteLine($"State slot {_stateSlot}");
                    return;
            }
            _input.KeyDown(key);
        }

        private static void RunStateAction(Func<bool> action, string done, string? notDone = null)
        {
            try
            {
                Console.WriteLine(action() ? done : notDone ?? "No game running");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Save state failed: {ex.Message}");
            }
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
            if (_closeRequested)
            {
                _window.Close();
                return;
            }
            if (_inputContext != null)
                _input.PollGamepads(_inputContext.Gamepads);
            _host.FastForward = _input.FastForward;

            lock (_frameLock)
            {
                if (_frameReady)
                {
                    _renderer!.Upload(_sharedFrame);
                    _frameReady = false;
                }
            }
            _renderer!.Draw();

            if (_titleTimer.Elapsed.TotalSeconds >= 1)
            {
                _titleTimer.Restart();
                _window.Title = WindowTitle();
            }
        }

        private void SaveScreenshot()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Px86.NintendoDS");
            Directory.CreateDirectory(folder);
            string code = _host.Invoke(() => _host.Emulator?.Rom?.Header.GameCode) ?? "NDS";
            string path = Path.Combine(folder, $"{code}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            lock (_frameLock)
                Px86.NintendoDS.FileFormats.Images.PngWriter.Write(path, ScreenCapture.Width, ScreenCapture.Height, _sharedFrame);
            Console.WriteLine($"Screenshot: {path}");
        }

        private void OnClosing()
        {
            _host.FrameReady -= OnFrameReady;
            if (_audio != null)
                _host.FrameEnded -= _audio.OnFrame;
            _host.LocalInput = null;
            _host.Stop();
            _host.Invoke(() => _host.Session?.FlushSave());

            // GL objects must go while the context still exists.
            _renderer?.Dispose();
            _renderer = null;
        }

        public void Dispose()
        {
            _audio?.Dispose();
            _renderer?.Dispose();
            _inputContext?.Dispose();
            _window.Dispose();
        }
    }
}
