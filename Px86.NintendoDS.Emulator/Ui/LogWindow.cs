using System.Numerics;
using System.Text;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Debugging;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Tools → Log (Ctrl+L): text the game prints through the emulator debug
    /// ports and in-code messages (<see cref="DebugOutput"/>), with CPU and
    /// text filters, pause, copy and clear.
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

        public LogWindow(UiContext ui) => _ui = ui;

        public void Open() => _open = true;

        /// <summary>Every frame (open or not): collect new lines and say once when a game starts printing.</summary>
        public void Poll()
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
                _ui.Notifications.Add("The game prints debug text: Tools > Log (Ctrl+L).");
            }
        }

        private bool Matches(DebugLine line) =>
            (line.Cpu == DebugCpu.Arm9 ? _showArm9 : _showArm7) &&
            (_filter.Length == 0 || line.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase));

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
                    : $"{_visible.Count} of {_lines.Count} lines. Programs print with the no$gba debug ports (04FFFA10h-A1Ch) or \"mov r12,r12\" messages.");
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
            ImGui.End();
        }
    }
}
