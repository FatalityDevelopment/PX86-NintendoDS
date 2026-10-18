using System.Numerics;
using System.Text;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Debugging;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Tools → Log (Ctrl+L), two tabs:
    ///   Emulator     what Px86 reports (<see cref="EmulatorLog"/>: game loaded, BIOS/firmware,
    ///                saves, PINE/GS connections, wireless, breakpoints, warnings, errors)
    ///   Game output  text the game prints through the emulator debug ports and in-code
    ///                messages (<see cref="DebugOutput"/>); retail games rarely print anything
    /// Both have a text filter, pause, copy and clear; Game output also filters by CPU.
    /// </summary>
    public sealed unsafe class LogWindow
    {
        private readonly UiContext _ui;
        private readonly List<DebugLine> _lines = new();
        private readonly List<int> _visible = new();
        private NdsEmulator? _source;
        private long _next;
        private bool _open;
        private bool _showArm9 = true, _showArm7 = true, _autoScroll = true, _paused, _notified;
        private string _filter = "";
        private bool _filterChanged;
        private readonly List<EmulatorLogLine> _emu = new();
        private long _emuNext;
        private bool _gameTab;
        private int _hookCpu, _hookKind;
        private string _hookAddress = "";
        private string? _hookError;

        public LogWindow(UiContext ui) => _ui = ui;

        public void Open() => _open = true;

        /// <summary>Every frame (open or not): collect new lines and say once when a game starts printing.</summary>
        public void Poll()
        {
            if (!_paused)
            {
                var fresh = EmulatorLog.Shared.Snapshot(_emuNext);
                if (fresh.Length > 0)
                {
                    _emuNext = fresh[^1].Index + 1;
                    _emu.AddRange(fresh);
                    if (_emu.Count > EmulatorLog.MaxLines)
                        _emu.RemoveRange(0, _emu.Count - EmulatorLog.MaxLines);
                }
            }
            PollGame();
        }

        private void PollGame()
        {
            var nds = _ui.Host.Emulator;
            if (nds != _source)
            {
                _source = nds;
                _lines.Clear();
                _visible.Clear();
                _next = 0;
            }
            if (nds == null || _paused)
                return;
            var fresh = nds.DebugOutput.Snapshot(_next);
            if (fresh.Length == 0)
                return;
            _next = fresh[^1].Index + 1;
            foreach (var line in fresh)
            {
                _lines.Add(line);
                if (Matches(line))
                    _visible.Add(_lines.Count - 1);
            }
            if (_lines.Count > DebugOutput.MaxLines)
            {
                _lines.RemoveRange(0, _lines.Count - DebugOutput.MaxLines);
                _filterChanged = true;
            }
            if (!_open && !_notified)
            {
                _notified = true;
                _gameTab = true;
                _ui.Notifications.Add("The game prints debug text: Tools > Log (Ctrl+L).");
            }
        }

        private bool Matches(DebugLine line) =>
            (line.Cpu == DebugCpu.Arm9 ? _showArm9 : _showArm7) &&
            (_filter.Length == 0 || line.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase));

        private static string Format(EmulatorLogLine line) =>
            $"{line.Time:HH:mm:ss} {(line.Error ? "! " : "")}{Notifications.Ascii(line.Text)}";

        private bool Matches(EmulatorLogLine line) =>
            _filter.Length == 0 || line.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase);

        private static string Format(DebugLine line) =>
            $"[{line.Frame,6}] {(line.Cpu == DebugCpu.Arm9 ? "ARM9" : "ARM7")}: {Notifications.Ascii(line.Text)}";

        public void Draw()
        {
            if (!_open)
                return;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(620, viewport.Size.X - 20), Math.Min(420, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            if (ImGui.Begin("Log", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                // Switch to Game output once when a game starts printing.
                bool selectGame = _gameTab;
                _gameTab = false;
                if (ImGui.BeginTabBar("##logtabs"))
                {
                    if (TabItem("Emulator", ImGuiTabItemFlags.None))
                    {
                        DrawEmulator();
                        ImGui.EndTabItem();
                    }
                    if (TabItem("Game output", selectGame ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
                    {
                        DrawGame();
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
            }
            ImGui.End();
        }

        // ImGui.NET's BeginTabItem overloads with flags all take "ref open", which adds a close
        // button; the native call with a null p_open gives a plain tab.
        private static bool TabItem(string label, ImGuiTabItemFlags flags)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(label + "\0");
            fixed (byte* p = utf8)
                return ImGuiNative.igBeginTabItem(p, null, flags) != 0;
        }

        private void DrawEmulator()
        {
            ImGui.SetNextItemWidth(160);
            ImGui.InputTextWithHint("##emufilter", "filter", ref _filter, 128);
            ImGui.SameLine();
            ImGui.Checkbox("Auto-scroll##emu", ref _autoScroll);
            ImGui.SameLine();
            ImGui.Checkbox("Pause##emu", ref _paused);
            ImGui.SameLine();
            var visible = _emu.Where(Matches).ToList();
            if (ImGui.Button("Copy##emu"))
                ImGui.SetClipboardText(string.Join(Environment.NewLine, visible.Select(Format)));
            ImGui.SameLine();
            if (ImGui.Button("Clear##emu"))
            {
                EmulatorLog.Shared.Clear();
                _emu.Clear();
            }
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped($"{visible.Count} of {_emu.Count} lines: what Px86 reports (also printed on the console).");
            ImGui.PopStyleColor();
            ImGui.Separator();

            if (ImGui.BeginChild("##emulines", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar))
            {
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(visible.Count);
                var warn = new Vector4(1f, 0.55f, 0.45f, 1f);
                while (clipper.Step())
                    for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                    {
                        var line = visible[row];
                        if (line.Error)
                            ImGui.TextColored(warn, Format(line));
                        else
                            ImGui.TextUnformatted(Format(line));
                    }
                clipper.End();
                clipper.Destroy();
                if (_autoScroll && !_paused && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                    ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndChild();
        }

        /// <summary>
        /// The printf/sprintf hooks of the running game: found by the scanner or added
        /// here. Added ones are kept per game code in the settings (GameHooks).
        /// </summary>
        private void DrawHooks()
        {
            if (!ImGui.BeginPopup("hooks"))
                return;
            var host = _ui.Host;
            var settings = _ui.Settings;
            string? game = host.Session?.Rom.Header.GameCode;
            if (game == null)
            {
                ImGui.TextUnformatted("Start a game first.");
                ImGui.EndPopup();
                return;
            }

            bool enabled = settings.PrintfHooks, scan = settings.PrintfAutoScan;
            if (ImGui.Checkbox("Show printf/sprintf output", ref enabled) | ImGui.Checkbox("Find printf functions in the game's code", ref scan))
            {
                settings.PrintfHooks = enabled;
                settings.PrintfAutoScan = scan;
                host.Invoke(() =>
                {
                    if (host.Emulator is not { } nds) return;
                    nds.Hooks.Enabled = enabled;
                    nds.Hooks.AutoScan = scan;
                    if (scan) nds.Hooks.ScanNow();
                });
                _ui.Launcher.TrySaveSettings();
            }
            ImGui.Separator();

            var hooks = host.Invoke(() => host.Emulator?.Hooks.List()) ?? Array.Empty<FunctionHook>();
            if (hooks.Count == 0)
                ImGui.TextDisabled("No hooks yet.");
            foreach (var hook in hooks)
            {
                ImGui.PushID(FunctionHooks.Format(hook));
                if (ImGui.SmallButton("x"))
                {
                    host.Invoke(() => host.Emulator?.Hooks.Remove(hook.Cpu, hook.Address));
                    if (settings.GameHooks.TryGetValue(game, out var saved)
                        && saved.RemoveAll(s => FunctionHooks.TryParse(s, out var c, out uint a, out _) && c == hook.Cpu && (a & ~1u) == hook.Address) > 0)
                        _ui.Launcher.TrySaveSettings();
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{(hook.Cpu == DebugCpu.Arm9 ? "ARM9" : "ARM7")} {hook.Address:X8}  {hook.Kind,-9}  {(hook.Automatic ? "found by scan" : hook.Name)}");
                ImGui.PopID();
            }
            ImGui.Separator();

            ImGui.SetNextItemWidth(70);
            ImGui.Combo("##hookcpu", ref _hookCpu, "ARM9\0ARM7\0");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputTextWithHint("##hookaddr", "address (hex)", ref _hookAddress, 16);
            ImGui.SameLine();
            var kinds = Enum.GetNames<HookKind>();
            ImGui.SetNextItemWidth(100);
            ImGui.Combo("##hookkind", ref _hookKind, string.Join('\0', kinds) + "\0");
            ImGui.SameLine();
            if (ImGui.Button("Add"))
            {
                string spec = $"{(_hookCpu == 0 ? "arm9" : "arm7")}:{_hookAddress.Trim()}:{kinds[_hookKind].ToLowerInvariant()}";
                if (FunctionHooks.TryParse(spec, out var cpu, out uint address, out var kind))
                {
                    _hookError = null;
                    host.Invoke(() => host.Emulator?.Hooks.Add(cpu, address, kind));
                    if (!settings.GameHooks.TryGetValue(game, out var saved))
                        settings.GameHooks[game] = saved = new List<string>();
                    saved.Add(spec);
                    _ui.Launcher.TrySaveSettings();
                    _hookAddress = "";
                }
                else
                    _hookError = "The address must be hexadecimal, e.g. 0201A3C4.";
            }
            if (_hookError != null)
                ImGui.TextColored(new Vector4(1, 0.55f, 0.45f, 1), _hookError);
            ImGui.TextDisabled("Hook the first instruction of the function. Saved for this game.");
            ImGui.EndPopup();
        }

        private void DrawGame()
        {
            _filterChanged |= ImGui.Checkbox("ARM9", ref _showArm9);
            ImGui.SameLine();
            _filterChanged |= ImGui.Checkbox("ARM7", ref _showArm7);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            _filterChanged |= ImGui.InputTextWithHint("##filter", "filter", ref _filter, 128);
            ImGui.Checkbox("Auto-scroll", ref _autoScroll);
            ImGui.SameLine();
            ImGui.Checkbox("Pause", ref _paused);
            ImGui.SameLine();
            if (ImGui.Button("Copy"))
            {
                var text = new StringBuilder();
                foreach (int i in _visible)
                    text.AppendLine(Format(_lines[i]));
                ImGui.SetClipboardText(text.ToString());
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                _ui.Host.Emulator?.DebugOutput.Clear();
                _lines.Clear();
                _visible.Clear();
                _next = 0;
            }
            ImGui.SameLine();
            if (ImGui.Button("Hooks..."))
                ImGui.OpenPopup("hooks");
            DrawHooks();

            if (_filterChanged)
            {
                _filterChanged = false;
                _visible.Clear();
                for (int i = 0; i < _lines.Count; i++)
                    if (Matches(_lines[i]))
                        _visible.Add(i);
            }

            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(_ui.Host.Emulator == null
                ? "Start a game first."
                : $"{_visible.Count} of {_lines.Count} lines: the no$gba debug ports (04FFFA10h-A1Ch), \"mov r12,r12\" messages, " +
                  "and the game's own printf/sprintf calls (Hooks...).");
            ImGui.PopStyleColor();
            ImGui.Separator();

            if (ImGui.BeginChild("##lines", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar))
            {
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(_visible.Count);
                while (clipper.Step())
                    for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                        ImGui.TextUnformatted(Format(_lines[_visible[row]]));
                clipper.End();
                clipper.Destroy();

                if (_ui.Host.Emulator is { } nds)
                {
                    foreach (var cpu in new[] { DebugCpu.Arm9, DebugCpu.Arm7 })
                    {
                        string pending = nds.DebugOutput.PendingText(cpu);
                        if (pending.Length > 0 && (cpu == DebugCpu.Arm9 ? _showArm9 : _showArm7))
                            ImGui.TextDisabled($"         {(cpu == DebugCpu.Arm9 ? "ARM9" : "ARM7")}: {Notifications.Ascii(pending)}...");
                    }
                }
                if (_autoScroll && !_paused && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
                    ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndChild();
        }
    }
}
