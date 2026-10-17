using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Tools → Memory: a hex editor over either CPU's address space. Click a
    /// byte to edit it; the selection can become a watchpoint. Reads skip the
    /// FIFO/cart data ports (see <see cref="DebugMemory"/>), writes behave
    /// like CPU writes.
    /// </summary>
    public sealed class MemoryWindow
    {
        private const int Rows = 24;
        private static readonly string[] Cpus = { "ARM9", "ARM7" };
        private static readonly (string Name, uint Address)[] Regions =
        {
            ("Main RAM", 0x02000000), ("ITCM", 0x01FF8000), ("DTCM", 0x027E0000), ("Shared WRAM", 0x03000000),
            ("ARM7 WRAM", 0x03800000), ("I/O", 0x04000000), ("Palette", 0x05000000), ("VRAM (LCDC)", 0x06800000), ("OAM", 0x07000000),
        };

        private readonly UiContext _ui;
        private bool _open;
        private int _cpu;
        private uint _base = 0x02000000;
        private string _gotoText = "";
        private uint? _selected;
        private uint _markStart, _markEnd; // bytes shown highlighted (a string from the Strings window)
        private bool _focus;
        private string _editText = "";
        private readonly byte[] _buffer = new byte[Rows * 16];

        public MemoryWindow(UiContext ui) => _ui = ui;

        public void Open() => _open = true;

        /// <summary>Open at <paramref name="address"/> and highlight <paramref name="length"/> bytes there.</summary>
        public void Show(DebugCpu cpu, uint address, int length = 1)
        {
            _open = true;
            _focus = true;
            _cpu = (int)cpu;
            _base = address & ~0xFu;
            _selected = null;
            _markStart = address;
            _markEnd = address + (uint)Math.Max(length, 1);
        }

        public void Draw()
        {
            if (!_open)
                return;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(640, viewport.Size.X - 20), Math.Min(520, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            if (_focus)
            {
                ImGui.SetNextWindowFocus();
                _focus = false;
            }
            if (ImGui.Begin("Memory", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                if (_ui.Host.Status == HostStatus.Shutdown)
                    ImGui.TextDisabled("Start a game first.");
                else
                    Body();
            }
            ImGui.End();
        }

        private void Body()
        {
            var cpu = (DebugCpu)_cpu;
            ImGui.SetNextItemWidth(70);
            ImGui.Combo("##cpu", ref _cpu, Cpus, Cpus.Length);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputText("Go to", ref _gotoText, 10, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsHexadecimal) &&
                uint.TryParse(_gotoText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint target))
                _base = target & ~0xFu;
            ImGui.SameLine();
            if (ImGui.BeginCombo("##region", "Region", ImGuiComboFlags.NoPreview))
            {
                foreach (var (name, address) in Regions)
                    if (ImGui.Selectable($"{name}  {address:X8}"))
                        _base = address;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Page up")) _base -= Rows * 16;
            ImGui.SameLine();
            if (ImGui.SmallButton("Page down")) _base += Rows * 16;

            if (ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel != 0)
                _base -= (uint)((int)ImGui.GetIO().MouseWheel * 3 * 16);

            _ui.Host.Invoke(() =>
            {
                if (_ui.Host.Emulator is { } nds)
                    DebugMemory.ReadBlock(nds, cpu, _base, _buffer);
            });

            ImGui.Separator();
            for (int row = 0; row < Rows; row++)
            {
                uint rowAddress = _base + (uint)(row * 16);
                ImGui.TextUnformatted($"{rowAddress:X8} ");
                for (int col = 0; col < 16; col++)
                {
                    uint address = rowAddress + (uint)col;
                    byte value = _buffer[row * 16 + col];
                    ImGui.SameLine(0, col == 8 ? 10 : 4);
                    if (_selected == address)
                    {
                        ImGui.SetNextItemWidth(ImGui.CalcTextSize("000").X);
                        ImGui.SetKeyboardFocusHere();
                        if (ImGui.InputText($"##e{address:X8}", ref _editText, 3, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsHexadecimal))
                        {
                            if (byte.TryParse(_editText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                                _ui.Host.Invoke(() => { if (_ui.Host.Emulator is { } nds) DebugMemory.Write8(nds, cpu, address, b); });
                            _selected = address + 1;
                            _editText = "";
                        }
                        if (ImGui.IsItemDeactivated() && !ImGui.IsItemDeactivatedAfterEdit())
                            _selected = null;
                        continue;
                    }
                    if (address - _markStart < _markEnd - _markStart)
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"{value:X2}");
                    else
                        ImGui.TextUnformatted($"{value:X2}");
                    if (ImGui.IsItemClicked())
                    {
                        _selected = address;
                        _editText = "";
                    }
                }
                ImGui.SameLine(0, 12);
                var chars = new char[16];
                for (int col = 0; col < 16; col++)
                {
                    byte b = _buffer[row * 16 + col];
                    chars[col] = b is >= 0x20 and < 0x7F ? (char)b : '.';
                }
                ImGui.TextDisabled(new string(chars));
            }

            ImGui.Separator();
            if (_selected is { } sel)
            {
                ImGui.TextUnformatted($"Selected {sel:X8}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Break on write"))
                    _ui.Host.Invoke(() => _ui.Host.Debugger.AddWatch(new Watchpoint(cpu, sel, sel + 1, WatchCondition.Write)));
                ImGui.SameLine();
                if (ImGui.SmallButton("Break on read"))
                    _ui.Host.Invoke(() => _ui.Host.Debugger.AddWatch(new Watchpoint(cpu, sel, sel + 1, WatchCondition.Read)));
            }
            else
            {
                ImGui.TextDisabled("Click a byte to edit it (type hex, Enter moves on). Scroll with the mouse wheel.");
            }
        }
    }
}
