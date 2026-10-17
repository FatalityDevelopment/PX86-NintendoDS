using Px86.NintendoDS.EmulatorCore.Jit;
using System.Numerics;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore.Hardware;
using Silk.NET.Input;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Config → Settings: paths and boot mode, video layout, audio, key
    /// bindings, the generated firmware's user profile and the remote-control
    /// sockets. Changes apply immediately where they can (video, audio, input,
    /// sockets) and are saved when the window closes; BIOS/firmware/profile
    /// changes apply to the next game started.
    /// </summary>
    public sealed class SettingsWindow
    {
        private static readonly string[] Arrangements = { "Vertical (top above bottom)", "Horizontal (side by side)", "Top screen only", "Bottom screen only" };
        private static readonly string[] Rotations = { "0 deg", "90 deg", "180 deg", "270 deg" };
        private static readonly string[] Languages = { "Japanese", "English", "French", "German", "Italian", "Spanish" };
        private static readonly string[] Codecs = { "jpeg", "png", "raw" };
        private static readonly string[] Buttons =
        {
            nameof(NdsButton.Up), nameof(NdsButton.Down), nameof(NdsButton.Left), nameof(NdsButton.Right), nameof(NdsButton.A), nameof(NdsButton.B),
            nameof(NdsButton.X), nameof(NdsButton.Y), nameof(NdsButton.L), nameof(NdsButton.R), nameof(NdsButton.Start), nameof(NdsButton.Select),
            InputMapper.MicBinding,
        };

        private readonly UiContext _ui;
        private bool _open;
        private string? _rebinding;

        private List<string>? _micDevices;

        public SettingsWindow(UiContext ui) => _ui = ui;

        public bool IsOpen => _open;

        /// <summary>True while waiting for a key press to bind.</summary>
        public bool CapturingKey => _rebinding != null;

        public void Open() => _open = true;

        /// <summary>The window forwards key presses here while <see cref="CapturingKey"/>.</summary>
        public void OnKeyCaptured(Key key)
        {
            if (_rebinding is not { } button)
                return;
            _rebinding = null;
            if (key == Key.Escape)
                return;
            var bindings = _ui.Settings.KeyBindings;
            foreach (var stale in bindings.Where(b => b.Value.Equals(button, StringComparison.OrdinalIgnoreCase) || b.Key == key.ToString()).Select(b => b.Key).ToList())
                bindings.Remove(stale);
            bindings[key.ToString()] = button;
            _ui.ApplyInput();
        }

        public void Draw()
        {
            if (!_open)
                return;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(520, viewport.Size.X - 20), Math.Min(460, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            bool open = _open;
            if (ImGui.Begin("Settings", ref open, ImGuiWindowFlags.NoCollapse))
            {
                if (ImGui.BeginTabBar("##settings"))
                {
                    Tab("Paths & boot", PathsTab);
                    Tab("Video", VideoTab);
                    Tab("Audio", AudioTab);
                    Tab("Input", InputTab);
                    Tab("Firmware user", FirmwareTab);
                    Tab("Emulation", EmulationTab);
                    Tab("Network", NetworkTab);
                    Tab("Remote", RemoteTab);
                    ImGui.EndTabBar();
                }
            }
            ImGui.End();
            if (_open && !open)
            {
                _rebinding = null;
                _ui.Launcher.TrySaveSettings();
            }
            _open = open;
        }

        private static void Tab(string name, Action body)
        {
            if (ImGui.BeginTabItem(name))
            {
                body();
                ImGui.EndTabItem();
            }
        }

        // ── Tabs ──────────────────────────────────────────────────────────────

        private void PathsTab()
        {
            var s = _ui.Settings;
            ImGui.TextWrapped("BIOS and firmware dumps are optional. Without them the built-in HLE BIOS and a generated firmware are used. " +
                              "Changes apply to the next game you start.");
            ImGui.Spacing();
            PathField("ARM9 BIOS (4 KB)", s.Bios9Path, v => s.Bios9Path = v, new[] { ".bin", ".rom" });
            PathField("ARM7 BIOS (16 KB)", s.Bios7Path, v => s.Bios7Path = v, new[] { ".bin", ".rom" });
            PathField("Firmware (256 KB)", s.FirmwarePath, v => s.FirmwarePath = v, new[] { ".bin", ".rom" });
            bool realBios = s.UseRealBios;
            if (ImGui.Checkbox("Boot through the real BIOS and firmware (needs all three dumps)", ref realBios))
                s.UseRealBios = realBios;
            ImGui.Spacing();
            ImGui.TextUnformatted("Save folder");
            string saves = s.SaveDirectory ?? "";
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##saves", ref saves, 1024))
                s.SaveDirectory = string.IsNullOrWhiteSpace(saves) ? null : saves;
            ImGui.TextDisabled("Empty = .sav files next to the ROM.");
        }

        private void PathField(string label, string? value, Action<string?> set, string[] extensions)
        {
            ImGui.TextUnformatted(label);
            string text = value ?? "";
            float buttons = ImGui.CalcTextSize("BrowseClear").X + ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X * 2;
            ImGui.SetNextItemWidth(-buttons);
            if (ImGui.InputText("##" + label, ref text, 1024))
                set(string.IsNullOrWhiteSpace(text) ? null : text);
            ImGui.SameLine();
            if (ImGui.Button("Browse##" + label))
                _ui.Browser.Open(label, Path.GetDirectoryName(value) ?? _ui.Settings.RomFolder, extensions, path => set(path));
            ImGui.SameLine();
            if (ImGui.Button("Clear##" + label))
                set(null);
        }

        private void VideoTab()
        {
            var s = _ui.Settings;
            bool changed = false;

            int arrangement = (int)s.ScreenArrangement;
            if (ImGui.Combo("Layout", ref arrangement, Arrangements, Arrangements.Length))
            {
                s.ScreenArrangement = (ScreenArrangement)arrangement;
                changed = true;
            }
            int rotation = s.ScreenRotation / 90;
            if (ImGui.Combo("Rotation", ref rotation, Rotations, Rotations.Length))
            {
                s.ScreenRotation = rotation * 90;
                changed = true;
            }
            int gap = s.ScreenGap;
            if (ImGui.SliderInt("Gap between screens", ref gap, 0, 96))
            {
                s.ScreenGap = gap;
                changed = true;
            }
            bool swap = s.SwapScreens;
            if (ImGui.Checkbox("Swap screens (touch screen first)", ref swap))
            {
                s.SwapScreens = swap;
                changed = true;
            }
            bool integer = s.IntegerScale;
            if (ImGui.Checkbox("Integer scaling (sharpest pixels)", ref integer))
            {
                s.IntegerScale = integer;
                changed = true;
            }
            bool linear = s.LinearFilter;
            if (ImGui.Checkbox("Smooth (linear) filtering", ref linear))
            {
                s.LinearFilter = linear;
                changed = true;
            }
            int scale = s.Scale;
            if (ImGui.SliderInt("Window scale", ref scale, 1, 6))
                s.Scale = scale;
            ImGui.SameLine();
            if (ImGui.Button("Resize window"))
                _ui.ResizeWindow(s.Scale);
            if (changed)
                _ui.ApplyVideo();
        }

        private void AudioTab()
        {
            var s = _ui.Settings;
            bool enabled = s.AudioEnabled;
            if (ImGui.Checkbox("Sound", ref enabled))
            {
                s.AudioEnabled = enabled;
                _ui.ApplyAudio();
            }
            int volume = s.Volume;
            if (ImGui.SliderInt("Volume", ref volume, 0, 100, "%d%%"))
            {
                s.Volume = volume;
                _ui.ApplyAudio();
            }

            ImGui.Spacing();
            ImGui.SeparatorText("Microphone");
            bool mic = s.MicrophoneEnabled;
            if (ImGui.Checkbox("Use the PC microphone", ref mic))
            {
                s.MicrophoneEnabled = mic;
                _ui.ApplyAudio();
            }
            _micDevices ??= MicrophoneCapture.ListDevices();
            var devices = new[] { "System default" }.Concat(_micDevices.Select(Notifications.Ascii)).ToArray();
            int device = Math.Max(0, _micDevices.IndexOf(s.MicrophoneDevice ?? "") + 1);
            if (ImGui.Combo("Device", ref device, devices, devices.Length))
            {
                s.MicrophoneDevice = device == 0 ? null : _micDevices[device - 1];
                _ui.ApplyAudio();
            }
            int gain = s.MicrophoneGain;
            if (ImGui.SliderInt("Input gain", ref gain, 25, 400, "%d%%"))
            {
                s.MicrophoneGain = gain;
                _ui.ApplyAudio();
            }
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(_ui.MicrophoneStatus());
            ImGui.TextWrapped("No microphone? Hold the Mic (blow) key, default B, or the left trigger.");
            ImGui.PopStyleColor();
        }

        private void InputTab()
        {
            ImGui.TextWrapped("Click a button, then press the key to use for it (Esc cancels). Gamepads use the standard layout " +
                              "(D-pad/left stick, face buttons in Nintendo positions, shoulders, Start/Back); the right trigger fast-forwards.");
            ImGui.Spacing();
            var bindings = _ui.Settings.KeyBindings;
            if (ImGui.BeginTable("##keys", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            {
                foreach (var button in Buttons)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(button == InputMapper.MicBinding ? "Mic (blow)" : button);
                    ImGui.TableNextColumn();
                    var keys = bindings.Where(b => b.Value.Equals(button, StringComparison.OrdinalIgnoreCase)).Select(b => b.Key).ToList();
                    string label = _rebinding == button ? "press a key..." : keys.Count == 0 ? "(none)" : string.Join(", ", keys);
                    if (ImGui.Button($"{label}##{button}", new Vector2(-1, 0)))
                        _rebinding = button;
                }
                ImGui.EndTable();
            }
            if (ImGui.Button("Restore defaults"))
            {
                _ui.Settings.KeyBindings = new EmulatorSettings().KeyBindings;
                _ui.ApplyInput();
            }
            ImGui.TextDisabled("Tab = fast-forward, P = pause, F5/F8 = save/load state, F9 = GX capture, F12 = screenshot.");
            ImGui.TextDisabled("Mic (blow) and the gamepad's left trigger send loud noise into the microphone.");
        }

        private void FirmwareTab()
        {
            var s = _ui.Settings;
            ImGui.TextWrapped("Used for the generated firmware when no firmware dump is set. Applies to the next game you start.");
            ImGui.Spacing();
            string nickname = s.Nickname;
            if (ImGui.InputText("Nickname", ref nickname, 10))
                s.Nickname = nickname;
            string message = s.Message;
            if (ImGui.InputText("Message", ref message, 26))
                s.Message = message;
            int month = s.BirthMonth, day = s.BirthDay;
            if (ImGui.SliderInt("Birthday month", ref month, 1, 12)) s.BirthMonth = month;
            if (ImGui.SliderInt("Birthday day", ref day, 1, 31)) s.BirthDay = day;
            int language = s.Language;
            if (ImGui.Combo("Language", ref language, Languages, Languages.Length))
                s.Language = language;
            int color = s.FavoriteColor;
            if (ImGui.SliderInt("Favourite colour", ref color, 0, 15))
                s.FavoriteColor = color;
        }

        private void EmulationTab()
        {
            var s = _ui.Settings;
            int turbo = s.TurboPercent;
            if (ImGui.SliderInt("Turbo speed", ref turbo, 110, 800, "%d%%"))
            {
                s.TurboPercent = turbo;
                _ui.Host.TurboSpeed = turbo / 100.0;
            }
            ImGui.TextDisabled("Emulation > Speed > Turbo uses this; hold Tab for unlimited speed.");

            ImGui.Spacing();
            int mode = (int)s.CpuMode;
            if (ImGui.Combo("CPU", ref mode, CpuModes, CpuModes.Length))
            {
                s.CpuMode = (CpuMode)mode;
                _ui.Host.Invoke(() => { if (_ui.Host.Emulator is { } nds) nds.CpuMode = s.CpuMode; });
            }
            ImGui.TextDisabled("JIT compiles hot code to native code (about 2x faster). All modes give identical results;");
            ImGui.TextDisabled("the debugger always interprets. Switch to Interpreter if a game misbehaves only with the JIT.");
        }

        private static readonly string[] CpuModes = { "Interpreter", "Cached", "JIT" };

        private static readonly string[] WirelessModes = { "Off", "Local wireless", "Local wireless + online" };

        private void NetworkTab()
        {
            var s = _ui.Settings;
            var wireless = _ui.Remote.Wireless;
            ImGui.TextWrapped("Local wireless links emulator windows for trades, battles and DS Download Play: start the " +
                              "game in two windows (on this PC, or on the LAN with the option below) with the same port.");
            ImGui.TextWrapped("Online adds a built-in access point, \"" + EmulatorCore.Network.SoftAccessPoint.Ssid + "\". Nintendo's own " +
                              "WFC servers are closed: set the DNS server of a replacement service. With your own firmware, " +
                              "pick the access point once in the game's Nintendo WFC Settings (Search for an Access Point).");
            ImGui.Spacing();

            int mode = (int)s.WirelessMode;
            ImGui.Combo("Mode", ref mode, WirelessModes, WirelessModes.Length);
            s.WirelessMode = (WirelessMode)mode;
            int port = s.WirelessPort;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("UDP port", ref port))
                s.WirelessPort = Math.Clamp(port, 1024, 65535);
            bool lan = s.WirelessLan;
            if (ImGui.Checkbox("Also reach emulators on other PCs in the local network", ref lan))
                s.WirelessLan = lan;
            string dns = s.OnlineDns ?? "";
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputText("DNS server (online)", ref dns, 64))
                s.OnlineDns = string.IsNullOrWhiteSpace(dns) ? null : dns.Trim();
            ImGui.TextDisabled("Empty = this PC's DNS. Example format: 1.2.3.4");

            if (ImGui.Button("Apply"))
            {
                if (s.WirelessMode == WirelessMode.Online && s.OnlineDns != null && EmulatorCore.Network.OnlineWifiMedium.ParseDns(s.OnlineDns) == null)
                    _ui.Notifications.Add("The DNS server must be an IP address like 1.2.3.4.");
                else
                {
                    wireless.Apply(s.WirelessMode, s.WirelessPort, s.WirelessLan, s.OnlineDns);
                    _ui.Launcher.TrySaveSettings();
                }
            }

            ImGui.Separator();
            ImGui.TextUnformatted($"Now: {WirelessModes[(int)wireless.Mode]}. This is emulator window {WirelessServices.InstanceNumber + 1}" +
                                  (WirelessServices.InstanceNumber > 0 ? " (it uses its own MAC address)." : "."));
            if (wireless.Mode != WirelessMode.Off)
                ImGui.TextDisabled($"Other consoles heard recently: {wireless.PeerCount()}.");
            if (wireless.Online is { } online)
                ImGui.TextDisabled($"Access point: {online.AccessPoint.AssociatedStations} console(s) connected, {online.Router.OpenConnections} open connection(s).");
            if (_ui.Host.Emulator is { } nds)
                ImGui.TextDisabled($"Wifi frames sent {nds.Wifi.FramesSent}, received {nds.Wifi.FramesReceived}.");
        }

        private void RemoteTab()
        {
            var s = _ui.Settings;
            var remote = _ui.Remote;
            ImGui.TextWrapped("Lets CodeDesigner, debuggers and scripts control the emulator over local TCP sockets (127.0.0.1 only). " +
                              "See docs/pine-protocol.md.");
            ImGui.Spacing();

            bool pineOn = remote.Pine != null;
            int pinePort = s.PinePort;
            if (ImGui.Checkbox("PINE socket", ref pineOn))
            {
                s.PineEnabled = pineOn;
                if (pineOn) remote.StartPine(s.PinePort); else remote.StopPine();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Port##pine", ref pinePort))
                s.PinePort = Math.Clamp(pinePort, 1, 65535);
            if (remote.Pine is { } pine)
                ImGui.TextDisabled($"Listening on 127.0.0.1:{pine.Port}, {pine.ClientCount} client(s).");

            bool gsOn = remote.Gs != null;
            int gsPort = s.GsPort;
            if (ImGui.Checkbox("GS frame stream", ref gsOn))
            {
                s.GsEnabled = gsOn;
                if (gsOn) remote.StartGs(s.GsPort, RemoteServices.ParseCodec(s.GsCodec), s.GsQuality); else remote.StopGs();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Port##gs", ref gsPort))
                s.GsPort = Math.Clamp(gsPort, 1, 65535);
            int codec = Math.Max(0, Array.IndexOf(Codecs, s.GsCodec.ToLowerInvariant()));
            if (ImGui.Combo("Format", ref codec, Codecs, Codecs.Length))
                s.GsCodec = Codecs[codec];
            int quality = s.GsQuality;
            if (ImGui.SliderInt("JPEG quality", ref quality, 10, 100))
                s.GsQuality = quality;
            if (remote.Gs is { } gs)
                ImGui.TextDisabled($"Listening on 127.0.0.1:{gs.Port}, {gs.SubscriberCount} subscriber(s), {gs.FramesDelivered} frames sent.");
            ImGui.TextDisabled("Port/format changes apply when a socket is (re)opened.");
        }
    }
}
