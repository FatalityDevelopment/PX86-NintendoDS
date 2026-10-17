using System.Diagnostics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl;
using Px86.NintendoDS.Emulator.Ui;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Px86.NintendoDS.EmulatorCore.Video;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// The desktop window: an SDL window with an OpenGL context showing both
    /// screens (placed by <see cref="ScreenLayout"/>), a Dear ImGui menu bar
    /// and tool windows, keyboard/gamepad input and mouse touch.
    ///
    /// The console runs on the <see cref="EmulatorHost"/>'s thread. The window
    /// thread (Silk.NET's loop) polls input, uploads the newest frame from
    /// <see cref="EmulatorHost.FrameReady"/> to a texture, draws it and then the
    /// UI. Audio is fed from <see cref="EmulatorHost.FrameEnded"/>.
    /// </summary>
    public sealed unsafe class EmulatorWindow : IDisposable
    {
        private readonly EmulatorHost _host;
        private readonly EmulatorSettings _settings;
        private readonly GameLauncher _launcher;
        private readonly RemoteServices _remote;
        private readonly IWindow _window;
        private readonly InputMapper _input;
        private readonly Notifications _notifications = new();
        private readonly FileBrowser _browser = new();
        private AudioOutput? _audio;
        private readonly EmulatorCore.Hardware.Spi.MicrophoneInput _micInput = new();
        private MicrophoneCapture? _mic;
        private string? _micDevice;
        private string? _micError;

        private readonly uint[] _sharedFrame = new uint[ScreenCapture.Width * ScreenCapture.Height];
        private readonly object _frameLock = new();
        private bool _frameReady;
        private volatile bool _closeRequested;

        private GL? _gl;
        private ScreenRenderer? _renderer;
        private ImGuiController? _imgui;
        private IInputContext? _inputContext;
        private UiContext? _ui;
        private MainMenu? _menu;
        private SettingsWindow? _settingsWindow;
        private CheatsWindow? _cheatsWindow;
        private CheatSearchWindow? _searchWindow;
        private DebuggerWindow? _debugger;
        private MemoryWindow? _memory;
        private IoWindow? _io;
        private LogWindow? _log;
        private StringsWindow? _strings;
        private VideoWindow? _video;
        private ScreenLayout _layout;
        private bool _touching;
        private readonly Stopwatch _titleTimer = Stopwatch.StartNew();
        private static readonly Silk.NET.SDL.Sdl Sdl = Silk.NET.SDL.Sdl.GetApi();

        public EmulatorWindow(EmulatorHost host, EmulatorSettings settings, GameLauncher launcher, RemoteServices remote)
        {
            _host = host;
            _settings = settings;
            _launcher = launcher;
            _remote = remote;
            _input = new InputMapper(settings);
            _layout = settings.Layout;

            NativeLibraries.Configure();
            SdlWindowing.Use();
            Silk.NET.Input.Sdl.SdlInput.RegisterPlatform();

            var (width, height) = _layout.WindowSize(settings.Scale);
            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(width, height + 22),
                Title = "Px86.NintendoDS",
                VSync = true,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            };
            _window = Window.Create(options);
            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.FileDrop += OnFileDrop;
            _window.Closing += OnClosing;

            _launcher.Message += _notifications.Add;
            _remote.Log += _notifications.Add;
            _host.LocalInput = _input.Snapshot;
            _host.FrameReady += OnFrameReady;
            _host.FrameEnded += OnFrameEnded;
            _host.GameStarted += session => session.Emulator.Spi.Touch.Microphone = _micInput;
            _host.Invoke(() => { if (_host.Emulator is { } nds) nds.Spi.Touch.Microphone = _micInput; });
            _host.TurboSpeed = settings.TurboPercent / 100.0;
            ApplyAudio();
        }

        public void Run() => _window.Run();

        /// <summary>Close the window from any thread (PINE Shutdown).</summary>
        public void RequestClose() => _closeRequested = true;

        /// <summary>Emulation thread: keep the newest frame for the next redraw.</summary>
        private void OnFrameReady(uint[] frame, long frameNumber)
        {
            lock (_frameLock)
            {
                frame.CopyTo(_sharedFrame, 0);
                _frameReady = true;
            }
        }

        private void OnFrameEnded(NdsEmulator nds) => _audio?.OnFrame(nds);

        // ── Settings ──────────────────────────────────────────────────────────

        private void ApplyVideo()
        {
            _layout = _settings.Layout;
            _renderer?.SetFilter(_settings.LinearFilter);
        }

        private void ApplyAudio()
        {
            if (_settings.AudioEnabled && _audio == null)
            {
                _audio = AudioOutput.TryCreate();
            }
            else if (!_settings.AudioEnabled && _audio != null)
            {
                var old = _audio;
                _audio = null;
                _host.Invoke(() => { }); // let a frame in progress finish with the old output
                old.Dispose();
            }
            _audio?.SetVolume(_settings.Volume / 100f);

            // Microphone: (re)open the capture device when it is turned on or changed.
            _micInput.Gain = _settings.MicrophoneGain / 100f;
            if (!_settings.MicrophoneEnabled || _micDevice != _settings.MicrophoneDevice)
            {
                _mic?.Dispose();
                _mic = null;
                _micError = null;
            }
            if (_settings.MicrophoneEnabled && _mic == null && _micError == null)
            {
                _micDevice = _settings.MicrophoneDevice;
                _mic = MicrophoneCapture.TryOpen(_micDevice, _micInput, out _micError);
                _micError ??= _mic == null ? "no capture device" : null;
            }
        }

        private string MicrophoneStatus() =>
            _mic != null ? $"Recording from: {Notifications.Ascii(_mic.DeviceName)}."
            : _settings.MicrophoneEnabled ? $"No microphone ({Notifications.Ascii(_micError ?? "none found")}); the blow key still works."
            : "PC microphone off.";

        private void ResizeWindow(int scale)
        {
            var (w, h) = _layout.WindowSize(scale);
            _window.Size = new Vector2D<int>(w, h + (int)(_menu?.Height ?? 0));
        }

        private void ToggleFullscreen() =>
            _window.WindowState = _window.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;

        // ── Window thread ─────────────────────────────────────────────────────

        private void OnLoad()
        {
            _gl = GL.GetApi(_window);
            _renderer = new ScreenRenderer(_gl);
            _renderer.SetFilter(_settings.LinearFilter);

            _inputContext = _window.CreateInput();
            _imgui = new ImGuiController(_gl, _window, _inputContext);
            ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            ImGui.GetIO().NativePtr->IniFilename = null; // don't litter imgui.ini next to the exe

            _ui = new UiContext
            {
                Host = _host,
                Settings = _settings,
                Launcher = _launcher,
                Remote = _remote,
                Notifications = _notifications,
                Browser = _browser,
                ApplyVideo = ApplyVideo,
                ApplyAudio = ApplyAudio,
                ApplyInput = () => _input.LoadBindings(_settings),
                ResizeWindow = ResizeWindow,
                ToggleFullscreen = ToggleFullscreen,
                SaveScreenshot = SaveScreenshot,
                CaptureGx = CaptureGx,
                Quit = () => _window.Close(),
                MicrophoneStatus = MicrophoneStatus,
            };
            _settingsWindow = new SettingsWindow(_ui);
            _menu = new MainMenu(_ui, _settingsWindow);
            _cheatsWindow = new CheatsWindow(_ui);
            _searchWindow = new CheatSearchWindow(_ui, _cheatsWindow);
            _menu.ToolItems.Add(("Cheats...", "Ctrl+C", _cheatsWindow.Open));
            _menu.ToolItems.Add(("Cheat search...", null, _searchWindow.Open));
            _host.CheatFailed += (name, error) => _notifications.Add($"Cheat '{name}' switched off: {error}");

            _debugger = new DebuggerWindow(_ui);
            _memory = new MemoryWindow(_ui);
            _io = new IoWindow(_ui);
            _log = new LogWindow(_ui);
            _strings = new StringsWindow(_ui, _memory);
            _video = new VideoWindow(_ui, _gl);
            _menu.ToolItems.Add(("Debugger...", "Ctrl+D", _debugger.Open));
            _menu.ToolItems.Add(("Memory...", "Ctrl+M", _memory.Open));
            _menu.ToolItems.Add(("I/O registers...", null, _io.Open));
            _menu.ToolItems.Add(("Video viewers...", null, _video.Open));
            _menu.ToolItems.Add(("Log...", "Ctrl+L", _log.Open));
            _menu.ToolItems.Add(("Strings...", "Ctrl+T", _strings.Open));
            _host.BreakHit += OnBreakHit;

            foreach (var keyboard in _inputContext.Keyboards)
            {
                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyUp += (_, key, _) => _input.KeyUp(key);
            }
            foreach (var mouse in _inputContext.Mice)
            {
                mouse.MouseDown += (m, button) => { if (button == MouseButton.Left) BeginTouch(m.Position); };
                mouse.MouseUp += (m, button) => { if (button == MouseButton.Left) EndTouch(); };
                mouse.MouseMove += (m, position) => { if (_touching) UpdateTouch(position); };
            }

            if (_host.Status == HostStatus.Shutdown)
                _notifications.Add("Open a ROM with File > Open ROM (Ctrl+O) or drop a .nds file on the window.");
        }

        private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
        {
            if (_settingsWindow!.CapturingKey)
            {
                _settingsWindow.OnKeyCaptured(key);
                return;
            }

            bool ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
            if (ctrl)
            {
                switch (key)
                {
                    case Key.O: _ui!.OpenRomDialog(); return;
                    case Key.Q: _window.Close(); return;
                    case Key.R: _host.Reset(); _notifications.Add("Reset"); return;
                    case Key.Comma: _settingsWindow.Open(); return;
                    case Key.C when !ImGui.GetIO().WantTextInput: _cheatsWindow!.Open(); return;
                    case Key.D: _debugger!.Open(); return;
                    case Key.M: _memory!.Open(); return;
                    case Key.L: _log!.Open(); return;
                    case Key.T: _strings!.Open(); return;
                }
            }

            // A focused text field or the file browser gets the keyboard, not the game.
            if (ImGui.GetIO().WantTextInput || _browser.IsOpen)
                return;

            switch (key)
            {
                case Key.Escape:
                    // With a menu or popup open, Esc is ImGui's (close it); otherwise it hides/shows the bar.
                    if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
                        _menu!.Visible = !_menu.Visible;
                    return;
                case Key.F11:
                    ToggleFullscreen();
                    return;
                case Key.F12:
                    SaveScreenshot();
                    return;
                case Key.F9:
                    CaptureGx();
                    return;
                case Key.F5:
                    _ui!.SaveState(_ui.StateSlot);
                    return;
                case Key.F8:
                    _ui!.LoadState(_ui.StateSlot);
                    return;
                case Key.F6:
                case Key.F7:
                    _ui!.StateSlot = (_ui.StateSlot + (key == Key.F7 ? 1 : GameSession.StateSlots - 1)) % GameSession.StateSlots;
                    _notifications.Add($"State slot {_ui.StateSlot}");
                    return;
                case Key.P when !_input.IsBound(key):
                    _host.TogglePause();
                    return;
                case Key.N when !_input.IsBound(key):
                    _host.FrameAdvance(1);
                    return;
            }
            _input.KeyDown(key);
        }

        // ── Touch ─────────────────────────────────────────────────────────────

        private ScreenRect GameArea()
        {
            float scaleY = _window.FramebufferSize.Y / (float)Math.Max(_window.Size.Y, 1);
            float menu = (_menu?.Height ?? 0) * scaleY;
            return new ScreenRect(0, menu, _window.FramebufferSize.X, Math.Max(_window.FramebufferSize.Y - menu, 1));
        }

        private (int X, int Y)? TouchPoint(System.Numerics.Vector2 position)
        {
            // Mouse positions are in window units; the layout works in framebuffer pixels (HiDPI).
            float sx = _window.FramebufferSize.X / (float)Math.Max(_window.Size.X, 1);
            float sy = _window.FramebufferSize.Y / (float)Math.Max(_window.Size.Y, 1);
            return _layout.ToTouch(GameArea(), position.X * sx, position.Y * sy);
        }

        private void BeginTouch(System.Numerics.Vector2 position)
        {
            if (ImGui.GetIO().WantCaptureMouse)
                return; // the click is for a menu or window
            if (TouchPoint(position) is { } p)
            {
                _touching = true;
                _input.SetTouch(p);
            }
        }

        private void UpdateTouch(System.Numerics.Vector2 position)
        {
            // Dragging off the screen keeps the last point instead of lifting the stylus.
            if (TouchPoint(position) is { } p)
                _input.SetTouch(p);
        }

        private void EndTouch()
        {
            _touching = false;
            _input.SetTouch(null);
        }

        /// <summary>Emulation thread: a breakpoint or watchpoint paused the game.</summary>
        private void OnBreakHit(EmulatorCore.Debugging.BreakInfo info)
        {
            _debugger?.OnBreak(info);
            _notifications.Add(info.Reason == EmulatorCore.Debugging.BreakReason.Watchpoint
                ? $"{info.Cpu}: {(info.IsWrite ? "write to" : "read of")} {info.Address:X8} at {info.Pc:X8}"
                : $"{info.Cpu}: breakpoint at {info.Pc:X8}");
        }

        private void OnFileDrop(string[] paths)
        {
            var rom = paths.FirstOrDefault(p => p.EndsWith(".nds", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".srl", StringComparison.OrdinalIgnoreCase));
            if (rom != null)
                _launcher.Load(rom);
            else if (paths.FirstOrDefault(p => p.EndsWith(".gxcap", StringComparison.OrdinalIgnoreCase)) is { } gx)
                _notifications.Add($"Replay GX captures from the command line: --replay-gx \"{gx}\"");
        }

        // ── Rendering ─────────────────────────────────────────────────────────

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
            _micInput.Blowing = _input.Blowing;
            _mic?.Pump();

            lock (_frameLock)
            {
                if (_frameReady)
                {
                    _renderer!.Upload(_sharedFrame);
                    _frameReady = false;
                }
            }

            // Silk throws from FramebufferSize when SDL's error string is set, even by an
            // unrelated failed call (e.g. an unsupported cursor shape on some X servers).
            Sdl.ClearError();
            _imgui!.Update((float)delta);
            _menu!.Draw();
            _settingsWindow!.Draw();
            _cheatsWindow!.Draw();
            _searchWindow!.Draw();
            _debugger!.Draw();
            _memory!.Draw();
            _io!.Draw();
            _log!.Poll();
            _log.Draw();
            _strings!.Draw();
            _video!.Draw();
            _browser.Draw();
            _notifications.Draw();

            var area = _host.Status == HostStatus.Shutdown ? new ScreenRect(0, 0, 0, 0) : GameArea();
            _renderer!.Draw(_window.FramebufferSize.X, _window.FramebufferSize.Y, _layout, area);
            _imgui.Render();

            if (_titleTimer.Elapsed.TotalSeconds >= 1)
            {
                _titleTimer.Restart();
                _window.Title = WindowTitle();
            }
        }

        private string WindowTitle()
        {
            string title = "Px86.NintendoDS";
            if (_host.Invoke(() => _host.Emulator?.Rom?.Header) is { } header)
                title += $" - {header.Title} [{header.GameCode}]";
            if (_host.IsPaused && _host.Status != HostStatus.Shutdown) title += " - paused";
            return title;
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private static string PicturesFolder()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Px86.NintendoDS");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private string GameCode() => _host.Invoke(() => _host.Emulator?.Rom?.Header.GameCode) ?? "NDS";

        private void SaveScreenshot()
        {
            if (_host.Status == HostStatus.Shutdown)
                return;
            string path = Path.Combine(PicturesFolder(), $"{GameCode()}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            lock (_frameLock)
                FileFormats.Images.PngWriter.Write(path, ScreenCapture.Width, ScreenCapture.Height, _sharedFrame);
            _notifications.Add($"Screenshot: {path}");
        }

        /// <summary>F9: record the next 3D frame for bug reports (see docs/emulator.md).</summary>
        private void CaptureGx()
        {
            string path = Path.Combine(PicturesFolder(), $"{GameCode()}-{DateTime.Now:yyyyMMdd-HHmmss}{EmulatorCore.Video.Gpu3D.GxCapture.FileExtension}");
            if (!_host.CaptureGx(path, (file, error) =>
                    _notifications.Add(error == null ? $"GX capture: {file}" : $"GX capture failed: {error.Message}")))
                _notifications.Add("GX capture needs a running game.");
        }

        private void OnClosing()
        {
            _host.FrameReady -= OnFrameReady;
            _host.FrameEnded -= OnFrameEnded;
            _host.BreakHit -= OnBreakHit;
            _host.LocalInput = null;
            _host.Stop();
            _host.Invoke(() => _host.Session?.FlushSave());
            _launcher.TrySaveSettings();

            // GL objects must go while the context still exists.
            _video?.Dispose();
            _imgui?.Dispose();
            _imgui = null;
            _renderer?.Dispose();
            _renderer = null;
        }

        public void Dispose()
        {
            _audio?.Dispose();
            _mic?.Dispose();
            _inputContext?.Dispose();
            _window.Dispose();
        }
    }
}
