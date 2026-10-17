using System.Numerics;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// The menu bar and its small windows (About, Controls).
    ///   File       open / recent / close, save and load state slots, screenshot, GX capture, exit
    ///   Emulation  pause, reset, frame advance, speed
    ///   Config     settings, video quick toggles, fullscreen
    ///   Tools      remote control sockets (more tools register through <see cref="ToolItems"/>)
    ///   Help       controls, about
    /// </summary>
    public sealed class MainMenu
    {
        private readonly UiContext _ui;
        private readonly SettingsWindow _settings;
        private bool _aboutOpen;
        private bool _controlsOpen;

        public MainMenu(UiContext ui, SettingsWindow settings)
        {
            _ui = ui;
            _settings = settings;
        }

        /// <summary>Extra Tools menu entries (cheats, debugger…): label, shortcut, action.</summary>
        public List<(string Label, string? Shortcut, Action Run)> ToolItems { get; } = new();

        /// <summary>Height of the menu bar this frame (0 when hidden).</summary>
        public float Height { get; private set; }

        public bool Visible { get; set; } = true;

        public void Draw()
        {
            Height = 0;
            if (Visible && ImGui.BeginMainMenuBar())
            {
                Height = ImGui.GetWindowSize().Y;
                FileMenu();
                EmulationMenu();
                ConfigMenu();
                ToolsMenu();
                HelpMenu();
                StatusText();
                ImGui.EndMainMenuBar();
            }
            AboutWindow();
            ControlsWindow();
        }

        private bool HasGame => _ui.Host.Status != HostStatus.Shutdown;

        private void FileMenu()
        {
            if (!ImGui.BeginMenu("File"))
                return;
            if (ImGui.MenuItem("Open ROM...", "Ctrl+O"))
                _ui.OpenRomDialog();
            if (ImGui.BeginMenu("Recent", _ui.Settings.RecentRoms.Count > 0))
            {
                foreach (var rom in _ui.Settings.RecentRoms.ToList())
                {
                    if (ImGui.MenuItem(Path.GetFileName(rom)))
                        _ui.Launcher.Load(rom);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(rom);
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Clear list"))
                    _ui.Settings.RecentRoms.Clear();
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Close ROM", null, false, HasGame))
                _ui.Host.Unload();
            ImGui.Separator();
            StateMenu("Save state", save: true);
            StateMenu("Load state", save: false);
            if (ImGui.MenuItem("Save state to file...", null, false, HasGame))
                _ui.Browser.Open("Save state as", _ui.Settings.RomFolder, new[] { ".pxstate" },
                    path => Try(() => _ui.Host.SaveStateFile(path), $"Saved {Path.GetFileName(path)}"), save: true, defaultName: "state.pxstate");
            if (ImGui.MenuItem("Load state from file...", null, false, HasGame))
                _ui.Browser.Open("Load state", _ui.Settings.RomFolder, new[] { ".pxstate", ".st0", ".st1", ".st2", ".st3", ".st4", ".st5", ".st6", ".st7", ".st8", ".st9" },
                    path => Try(() => _ui.Host.LoadStateFile(path), $"Loaded {Path.GetFileName(path)}"));
            ImGui.Separator();
            if (ImGui.MenuItem("Screenshot", "F12", false, HasGame))
                _ui.SaveScreenshot();
            if (ImGui.MenuItem("GX capture (3D bug report)", "F9", false, HasGame))
                _ui.CaptureGx();
            ImGui.Separator();
            if (ImGui.MenuItem("Exit", "Ctrl+Q"))
                _ui.Quit();
            ImGui.EndMenu();
        }

        private void StateMenu(string label, bool save)
        {
            if (!ImGui.BeginMenu(label, HasGame))
                return;
            string? rom = _ui.Host.Invoke(() => _ui.Host.Session?.RomPath);
            for (int slot = 0; slot < GameSession.StateSlots; slot++)
            {
                string when = "empty";
                if (rom != null)
                {
                    var file = new FileInfo(Path.ChangeExtension(rom, $".st{slot}"));
                    if (file.Exists) when = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                }
                string shortcut = slot == _ui.StateSlot ? (save ? "F5" : "F8") : "";
                if (ImGui.MenuItem($"Slot {slot}   {when}", shortcut, false, save || when != "empty"))
                {
                    _ui.StateSlot = slot;
                    if (save) _ui.SaveState(slot); else _ui.LoadState(slot);
                }
            }
            ImGui.EndMenu();
        }

        private void EmulationMenu()
        {
            if (!ImGui.BeginMenu("Emulation"))
                return;
            var host = _ui.Host;
            if (ImGui.MenuItem(host.IsPaused ? "Resume" : "Pause", "P", false, HasGame))
                host.TogglePause();
            if (ImGui.MenuItem("Frame advance", "N", false, HasGame))
                host.FrameAdvance(1);
            if (ImGui.MenuItem("Reset", "Ctrl+R", false, HasGame))
            {
                host.Reset();
                _ui.Notifications.Add("Reset");
            }
            ImGui.Separator();
            if (ImGui.BeginMenu("Speed"))
            {
                Limiter("Normal (100%)", LimiterMode.Nominal);
                Limiter($"Turbo ({_ui.Settings.TurboPercent}%)", LimiterMode.Turbo);
                Limiter("Slow motion (50%)", LimiterMode.Slomo);
                Limiter("Unlimited", LimiterMode.Unlimited);
                ImGui.Separator();
                ImGui.TextDisabled("Hold Tab to fast-forward");
                ImGui.EndMenu();
            }
            ImGui.EndMenu();
        }

        private void Limiter(string label, LimiterMode mode)
        {
            if (ImGui.MenuItem(label, null, _ui.Host.Limiter == mode))
                _ui.Host.Limiter = mode;
        }

        private void ConfigMenu()
        {
            if (!ImGui.BeginMenu("Config"))
                return;
            if (ImGui.MenuItem("Settings...", "Ctrl+,"))
                _settings.Open();
            ImGui.Separator();
            var s = _ui.Settings;
            if (ImGui.BeginMenu("Screen layout"))
            {
                foreach (var arrangement in Enum.GetValues<ScreenArrangement>())
                {
                    if (ImGui.MenuItem(arrangement.ToString(), null, s.ScreenArrangement == arrangement))
                    {
                        s.ScreenArrangement = arrangement;
                        _ui.ApplyVideo();
                    }
                }
                ImGui.EndMenu();
            }
            bool swap = s.SwapScreens;
            if (ImGui.MenuItem("Swap screens", null, ref swap))
            {
                s.SwapScreens = swap;
                _ui.ApplyVideo();
            }
            bool linear = s.LinearFilter;
            if (ImGui.MenuItem("Smooth filtering", null, ref linear))
            {
                s.LinearFilter = linear;
                _ui.ApplyVideo();
            }
            if (ImGui.MenuItem("Fullscreen", "F11"))
                _ui.ToggleFullscreen();
            ImGui.EndMenu();
        }

        private void ToolsMenu()
        {
            if (!ImGui.BeginMenu("Tools"))
                return;
            foreach (var (label, shortcut, run) in ToolItems)
                if (ImGui.MenuItem(label, shortcut))
                    run();
            if (ToolItems.Count > 0)
                ImGui.Separator();
            var remote = _ui.Remote;
            var s = _ui.Settings;
            bool pine = remote.Pine != null;
            if (ImGui.MenuItem($"PINE socket (port {s.PinePort})", null, ref pine))
            {
                if (pine) remote.StartPine(s.PinePort); else remote.StopPine();
            }
            bool gs = remote.Gs != null;
            if (ImGui.MenuItem($"GS frame stream (port {s.GsPort})", null, ref gs))
            {
                if (gs) remote.StartGs(s.GsPort, RemoteServices.ParseCodec(s.GsCodec), s.GsQuality); else remote.StopGs();
            }
            ImGui.EndMenu();
        }

        private void HelpMenu()
        {
            if (!ImGui.BeginMenu("Help"))
                return;
            if (ImGui.MenuItem("Controls"))
                _controlsOpen = true;
            if (ImGui.MenuItem("About"))
                _aboutOpen = true;
            ImGui.EndMenu();
        }

        private void StatusText()
        {
            var host = _ui.Host;
            string status = host.Status switch
            {
                HostStatus.Shutdown => "No game - File > Open ROM, or drop a .nds file here",
                HostStatus.Paused => $"Paused   slot {_ui.StateSlot}",
                _ => $"{host.Fps:F0} fps{(host.FastForward ? "  fast-forward" : "")}   slot {_ui.StateSlot}",
            };
            float width = ImGui.CalcTextSize(status).X;
            float x = ImGui.GetWindowWidth() - width - 12;
            if (x > ImGui.GetCursorPosX() + 8)
            {
                ImGui.SetCursorPosX(x);
                ImGui.TextDisabled(status);
            }
        }

        private void AboutWindow()
        {
            if (!_aboutOpen)
                return;
            ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
            if (ImGui.Begin("About Px86.NintendoDS", ref _aboutOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextUnformatted("Px86.NintendoDS - a Nintendo DS emulator in C#");
                ImGui.Spacing();
                ImGui.TextWrapped("ARM946E-S + ARM7TDMI interpreter, 2D/3D graphics, sound, saves, save states, " +
                                  "PINE and GS stream sockets for CodeDesigner. Hardware reference: GBATEK.");
                ImGui.Spacing();
                ImGui.TextDisabled("BIOS and firmware dumps are optional (Config > Settings > Paths & boot).");
            }
            ImGui.End();
        }

        private void ControlsWindow()
        {
            if (!_controlsOpen)
                return;
            if (ImGui.Begin("Controls", ref _controlsOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                foreach (var (key, button) in _ui.Settings.KeyBindings.OrderBy(b => b.Value))
                    ImGui.TextUnformatted($"{button,-7} {key}");
                ImGui.Separator();
                ImGui.TextUnformatted("Touch screen   left mouse button on the bottom screen");
                ImGui.TextUnformatted("Fast-forward   hold Tab (or right trigger)");
                ImGui.TextUnformatted("Pause P   Frame advance N   Reset Ctrl+R");
                ImGui.TextUnformatted("Save/load state F5/F8   slot F6/F7");
                ImGui.TextUnformatted("Screenshot F12   GX capture F9   Fullscreen F11");
                ImGui.TextUnformatted("Menu bar Esc   Open ROM Ctrl+O   Quit Ctrl+Q");
                ImGui.TextDisabled("Rebind keys in Config > Settings > Input.");
            }
            ImGui.End();
        }

        private void Try(Func<bool> action, string done)
        {
            try
            {
                _ui.Notifications.Add(action() ? done : "No game running");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _ui.Notifications.Add($"Failed: {ex.Message}");
            }
        }
    }
}
