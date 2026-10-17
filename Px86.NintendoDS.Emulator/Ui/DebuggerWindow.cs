using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Px86.NintendoDS.Common.Disassembler;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Tools → Debugger: run control, registers, disassembly with breakpoints,
    /// and the breakpoint/watchpoint lists, for either CPU. Everything goes
    /// through <see cref="EmulatorHost"/> and its <see cref="DebugMonitor"/>,
    /// the same machinery the PINE socket uses.
    /// </summary>
    public sealed class DebuggerWindow
    {
        private const int Lines = 48;
        private static readonly string[] Cpus = { "ARM9", "ARM7" };
        private static readonly string[] Modes = { "Follow CPU", "ARM", "Thumb" };
        private static readonly string[] RegisterNames = { "r0", "r1", "r2", "r3", "r4", "r5", "r6", "r7", "r8", "r9", "r10", "r11", "r12", "sp", "lr", "pc" };

        private readonly UiContext _ui;
        private bool _open;
        private int _cpu;
        private int _mode;
        private bool _follow = true;
        private uint _viewAddress;
        private string _gotoText = "";
        private string _breakText = "";
        private string _watchStart = "", _watchEnd = "";
        private int _watchCond = 2;
        private int _editRegister = -1;
        private string _editText = "";

        private Snapshot? _snapshot;

        private sealed record Line(uint Address, uint Opcode, string Text, bool Breakpoint);
        private sealed record Snapshot(uint[] R, uint Cpsr, uint Spsr, string Mode, bool Thumb, bool Halted, Line[] Lines, uint Pc);

        public DebuggerWindow(UiContext ui) => _ui = ui;

        private DebugCpu Cpu => (DebugCpu)_cpu;

        public void Open() => _open = true;

        /// <summary>Show the debugger at a breakpoint.</summary>
        public void OnBreak(BreakInfo info)
        {
            _open = true;
            _cpu = (int)info.Cpu;
            _follow = true;
        }

        public void Draw()
        {
            if (!_open)
                return;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(720, viewport.Size.X - 20), Math.Min(620, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            if (ImGui.Begin("Debugger", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                if (_ui.Host.Status == HostStatus.Shutdown)
                    ImGui.TextDisabled("Start a game to debug it.");
                else
                    Body();
            }
            ImGui.End();
        }

        private void Body()
        {
            var host = _ui.Host;
            Toolbar(host);
            _snapshot = TakeSnapshot();
            if (_snapshot == null)
                return;

            float registersWidth = 200;
            if (ImGui.BeginChild("##regs", new Vector2(registersWidth, -170), true))
                Registers(_snapshot);
            ImGui.EndChild();
            ImGui.SameLine();
            if (ImGui.BeginChild("##dis", new Vector2(0, -170), true))
                Disassembly(_snapshot);
            ImGui.EndChild();
            if (ImGui.BeginChild("##points", new Vector2(0, 0), true))
                Points();
            ImGui.EndChild();
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void Toolbar(EmulatorHost host)
        {
            ImGui.SetNextItemWidth(80);
            ImGui.Combo("##cpu", ref _cpu, Cpus, Cpus.Length);
            ImGui.SameLine();
            bool paused = host.IsPaused;
            if (ImGui.Button(paused ? "Continue" : "Pause"))
                host.TogglePause();
            ImGui.SameLine();
            ImGui.BeginDisabled(!paused);
            if (ImGui.Button("Step"))
            {
                host.Step(Cpu);
                _follow = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Step over"))
            {
                host.StepOver(Cpu);
                _follow = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Step out"))
            {
                host.StepOut(Cpu);
                _follow = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Frame"))
                host.FrameAdvance(1);
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.TextDisabled(paused ? "paused" : "running");
            if (host.LastBreak is { } b)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(b.Reason == BreakReason.Watchpoint
                    ? $"| {b.Cpu} {(b.IsWrite ? "write" : "read")} {b.Address:X8} at {b.Pc:X8}"
                    : $"| {b.Cpu} breakpoint {b.Pc:X8}");
            }
        }

        // ── Data ──────────────────────────────────────────────────────────────

        private Snapshot? TakeSnapshot() => _ui.Host.Invoke(() =>
        {
            var nds = _ui.Host.Emulator;
            if (nds == null)
                return null;
            var core = nds.Cpu(Cpu);
            var r = new uint[16];
            for (int i = 0; i < 15; i++) r[i] = core.R[i];
            r[15] = core.Pc;

            bool thumb = _mode == 0 ? core.IsThumb : _mode == 2;
            int size = thumb ? 2 : 4;
            if (_follow)
            {
                uint pc = core.Pc;
                uint top = _viewAddress;
                // Keep the PC in view; recentre when it leaves the window.
                if (pc < top || pc >= top + (uint)(Lines * size) || (top & (uint)(size - 1)) != 0)
                    _viewAddress = pc - (uint)(Lines / 3 * size);
            }

            var bus = DebugMemory.Bus(nds, Cpu);
            var lines = new Line[Lines];
            uint address = _viewAddress & ~(uint)(size - 1);
            for (int i = 0; i < Lines; i++, address += (uint)size)
            {
                string text;
                uint opcode;
                if (thumb)
                {
                    opcode = bus.Fetch16(address);
                    text = ThumbDisassembler.Disassemble((ushort)opcode, address, bus.Fetch16(address + 2));
                }
                else
                {
                    opcode = bus.Fetch32(address);
                    text = ArmDisassembler.Disassemble(opcode, address);
                }
                lines[i] = new Line(address, opcode, text, _ui.Host.Debugger.HasBreakpoint(Cpu, address));
            }
            return new Snapshot(r, core.Cpsr, core.Spsr, core.Mode.ToString(), core.IsThumb, core.Halted, lines, core.Pc);
        });

        // ── Registers ─────────────────────────────────────────────────────────

        private void Registers(Snapshot s)
        {
            bool paused = _ui.Host.IsPaused;
            for (int i = 0; i < 16; i++)
            {
                if (_editRegister == i)
                {
                    ImGui.SetNextItemWidth(-1);
                    ImGui.SetKeyboardFocusHere();
                    if (ImGui.InputText($"##edit{i}", ref _editText, 16, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsHexadecimal))
                    {
                        if (uint.TryParse(_editText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                        {
                            int reg = i;
                            _ui.Host.Invoke(() =>
                            {
                                if (_ui.Host.Emulator is { } nds)
                                    EmulatorCore.Remote.PineRegisters.Write(nds, Cpu, EmulatorCore.Remote.PineOpcodes.CategoryGpr, (byte)reg, value);
                            });
                        }
                        _editRegister = -1;
                    }
                    if (ImGui.IsItemDeactivated())
                        _editRegister = -1;
                    continue;
                }
                ImGui.TextUnformatted($"{RegisterNames[i],-4}{s.R[i]:X8}");
                if (paused && ImGui.IsItemClicked())
                {
                    _editRegister = i;
                    _editText = $"{s.R[i]:X8}";
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(paused ? $"{(int)s.R[i]} - click to edit" : $"{(int)s.R[i]}");
            }
            ImGui.Separator();
            uint c = s.Cpsr;
            string flags = $"{(c >> 31 != 0 ? 'N' : '-')}{((c >> 30 & 1) != 0 ? 'Z' : '-')}{((c >> 29 & 1) != 0 ? 'C' : '-')}" +
                           $"{((c >> 28 & 1) != 0 ? 'V' : '-')}{((c >> 27 & 1) != 0 ? 'Q' : '-')} {((c >> 7 & 1) != 0 ? 'I' : '-')}{((c >> 6 & 1) != 0 ? 'F' : '-')}{(s.Thumb ? 'T' : '-')}";
            ImGui.TextUnformatted($"cpsr {c:X8}");
            ImGui.TextUnformatted($"     {flags}");
            ImGui.TextUnformatted($"spsr {s.Spsr:X8}");
            ImGui.TextUnformatted($"{s.Mode}{(s.Thumb ? " Thumb" : " ARM")}{(s.Halted ? " halted" : "")}");
        }

        // ── Disassembly ───────────────────────────────────────────────────────

        private void Disassembly(Snapshot s)
        {
            ImGui.SetNextItemWidth(110);
            ImGui.Combo("##mode", ref _mode, Modes, Modes.Length);
            ImGui.SameLine();
            ImGui.Checkbox("Follow PC", ref _follow);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputText("Go to", ref _gotoText, 10, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsHexadecimal) &&
                uint.TryParse(_gotoText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint target))
            {
                _follow = false;
                _viewAddress = target;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("^")) { _follow = false; _viewAddress -= (uint)(Lines / 2 * (s.Thumb ? 2 : 4)); }
            ImGui.SameLine();
            if (ImGui.SmallButton("v")) { _follow = false; _viewAddress += (uint)(Lines / 2 * (s.Thumb ? 2 : 4)); }

            if (ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel != 0)
            {
                _follow = false;
                _viewAddress -= (uint)((int)ImGui.GetIO().MouseWheel * 3 * (s.Thumb ? 2 : 4));
            }

            foreach (var line in s.Lines)
            {
                bool atPc = line.Address == s.Pc;
                string marker = line.Breakpoint ? "*" : " ";
                string arrow = atPc ? ">" : " ";
                var color = atPc ? new Vector4(1f, 0.9f, 0.3f, 1f) : line.Breakpoint ? new Vector4(1f, 0.45f, 0.45f, 1f) : new Vector4(0.85f, 0.85f, 0.85f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                bool clicked = ImGui.Selectable($"{marker}{arrow} {line.Address:X8}  {line.Text}##{line.Address:X8}", atPc);
                ImGui.PopStyleColor();
                if (clicked)
                    ToggleBreakpoint(line.Address);
                if (ImGui.BeginPopupContextItem($"##ctx{line.Address:X8}"))
                {
                    if (ImGui.MenuItem("Toggle breakpoint"))
                        ToggleBreakpoint(line.Address);
                    if (ImGui.MenuItem("Run to here", null, false, _ui.Host.IsPaused))
                    {
                        uint address = line.Address;
                        _ui.Host.Invoke(() => _ui.Host.Debugger.AddTemporaryBreakpoint(Cpu, address));
                        _ui.Host.Resume();
                        _follow = true;
                    }
                    if (ImGui.MenuItem("Copy address"))
                        ImGui.SetClipboardText($"{line.Address:X8}");
                    ImGui.EndPopup();
                }
            }
            ImGui.TextDisabled("Click a line to toggle a breakpoint; right-click for more.");
        }

        private void ToggleBreakpoint(uint address)
        {
            _ui.Host.Invoke(() =>
            {
                if (!_ui.Host.Debugger.RemoveBreakpoint(Cpu, address))
                    _ui.Host.Debugger.AddBreakpoint(Cpu, address);
            });
        }

        // ── Breakpoints and watchpoints ───────────────────────────────────────

        private void Points()
        {
            var debugger = _ui.Host.Debugger;
            var (breakpoints, watches) = _ui.Host.Invoke(() => (debugger.Breakpoints.ToList(), debugger.Watches.ToList()));

            ImGui.TextUnformatted("Breakpoints");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputText("##bp", ref _breakText, 10, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsHexadecimal) &&
                uint.TryParse(_breakText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bp))
            {
                _ui.Host.Invoke(() => debugger.AddBreakpoint(Cpu, bp));
                _breakText = "";
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"add on {Cpus[_cpu]}");
            foreach (var (cpu, address) in breakpoints)
            {
                ImGui.TextUnformatted($"  {cpu} {address:X8}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"go##g{cpu}{address:X8}"))
                {
                    _cpu = (int)cpu;
                    _follow = false;
                    _viewAddress = address;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"x##b{cpu}{address:X8}"))
                    _ui.Host.Invoke(() => debugger.RemoveBreakpoint(cpu, address));
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Watchpoints");
            ImGui.SetNextItemWidth(90);
            ImGui.InputText("from", ref _watchStart, 10, ImGuiInputTextFlags.CharsHexadecimal);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.InputText("to (excl.)", ref _watchEnd, 10, ImGuiInputTextFlags.CharsHexadecimal);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.Combo("##cond", ref _watchCond, new[] { "-", "read", "write", "read+write" }, 4);
            ImGui.SameLine();
            if (ImGui.Button("Add watch") && TryHex(_watchStart, out uint start))
            {
                uint end = TryHex(_watchEnd, out uint e) && e > start ? e : start + 4;
                var cond = (WatchCondition)Math.Max(1, _watchCond);
                _ui.Host.Invoke(() => debugger.AddWatch(new Watchpoint(Cpu, start, end, cond)));
            }
            foreach (var w in watches)
            {
                ImGui.TextUnformatted($"  {w.Cpu} {w.Start:X8}-{w.End:X8} {w.Condition}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"x##w{w.Cpu}{w.Start:X8}{w.End:X8}"))
                    _ui.Host.Invoke(() => debugger.RemoveWatch(w.Cpu, w.Start, w.End));
            }
        }

        private static bool TryHex(string text, out uint value) =>
            uint.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
