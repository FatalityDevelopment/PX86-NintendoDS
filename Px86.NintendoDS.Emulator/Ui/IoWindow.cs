using System.Numerics;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>Tools → I/O registers: the most useful registers of both CPUs, live, with key fields decoded.</summary>
    public sealed class IoWindow
    {
        private sealed record Register(string Name, uint Address, int Size, Func<uint, string>? Decode = null);

        private static readonly string[] IrqNames =
        {
            "VBlank", "HBlank", "VCount", "Timer0", "Timer1", "Timer2", "Timer3", "RTC", "DMA0", "DMA1", "DMA2", "DMA3",
            "Keypad", "GBA", "", "", "IPCSync", "IPCSend", "IPCRecv", "CartDone", "CartIREQ", "GXFIFO", "Hinge", "SPI", "Wifi",
        };

        private static string Irqs(uint v) =>
            string.Join(" ", Enumerable.Range(0, IrqNames.Length).Where(i => (v >> i & 1) != 0 && IrqNames[i] != "").Select(i => IrqNames[i]));

        private static readonly Register[] Arm9 =
        {
            new("DISPCNT A", 0x04000000, 4, v => $"mode {v & 7}{((v & 8) != 0 ? " 3D" : "")} BG{string.Concat(Enumerable.Range(0, 4).Where(i => (v >> (8 + i) & 1) != 0))}{((v & 0x1000) != 0 ? " OBJ" : "")} disp {v >> 16 & 3}"),
            new("DISPSTAT", 0x04000004, 2, v => $"{((v & 1) != 0 ? "vblank " : "")}{((v & 2) != 0 ? "hblank " : "")}irq {(v >> 3) & 7:X}"),
            new("VCOUNT", 0x04000006, 2, v => $"line {v}"),
            new("DISPCNT B", 0x04001000, 4, v => $"mode {v & 7} BG{string.Concat(Enumerable.Range(0, 4).Where(i => (v >> (8 + i) & 1) != 0))}{((v & 0x1000) != 0 ? " OBJ" : "")}"),
            new("POWCNT1", 0x04000304, 2, v => $"{((v & 1) != 0 ? "LCD " : "")}{((v & 2) != 0 ? "2D-A " : "")}{((v & 4) != 0 ? "3D-render " : "")}{((v & 8) != 0 ? "3D-geom " : "")}{((v & 0x200) != 0 ? "2D-B " : "")}{((v & 0x8000) != 0 ? "A-top" : "A-bottom")}"),
            new("VRAMCNT A-D", 0x04000240, 4), new("VRAMCNT E-G", 0x04000244, 4), new("VRAMCNT H-I", 0x04000248, 2),
            new("WRAMCNT", 0x04000247, 1),
            new("IME", 0x04000208, 4), new("IE", 0x04000210, 4, Irqs), new("IF", 0x04000214, 4, Irqs),
            new("DMA0CNT", 0x040000B8, 4), new("DMA1CNT", 0x040000C4, 4), new("DMA2CNT", 0x040000D0, 4), new("DMA3CNT", 0x040000DC, 4),
            new("TM0", 0x04000100, 4), new("TM1", 0x04000104, 4), new("TM2", 0x04000108, 4), new("TM3", 0x0400010C, 4),
            new("KEYINPUT", 0x04000130, 2), new("IPCSYNC", 0x04000180, 2), new("IPCFIFOCNT", 0x04000184, 2),
            new("ROMCTRL", 0x040001A4, 4), new("DISP3DCNT", 0x04000060, 2), new("GXSTAT", 0x04000600, 4, v => $"fifo {(v >> 16) & 0x1FF}{((v & 0x08000000) != 0 ? " busy" : "")}"),
        };

        private static readonly Register[] Arm7 =
        {
            new("IME", 0x04000208, 4), new("IE", 0x04000210, 4, Irqs), new("IF", 0x04000214, 4, Irqs),
            new("DMA0CNT", 0x040000B8, 4), new("DMA1CNT", 0x040000C4, 4), new("DMA2CNT", 0x040000D0, 4), new("DMA3CNT", 0x040000DC, 4),
            new("TM0", 0x04000100, 4), new("TM1", 0x04000104, 4), new("TM2", 0x04000108, 4), new("TM3", 0x0400010C, 4),
            new("EXTKEYIN", 0x04000136, 2, v => $"{((v & 0x40) == 0 ? "pen down " : "")}{((v & 0x80) != 0 ? "hinge closed" : "")}"),
            new("RTC", 0x04000138, 2), new("IPCSYNC", 0x04000180, 2), new("SPICNT", 0x040001C0, 2),
            new("SOUNDCNT", 0x04000500, 2, v => $"vol {v & 0x7F}{((v & 0x8000) != 0 ? " on" : " off")}"), new("POWCNT2", 0x04000304, 2),
            new("HALTCNT/POSTFLG", 0x04000300, 2),
        };

        private readonly UiContext _ui;
        private bool _open;

        public IoWindow(UiContext ui) => _ui = ui;

        public void Open() => _open = true;

        public void Draw()
        {
            if (!_open)
                return;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(560, viewport.Size.X - 20), Math.Min(520, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            if (ImGui.Begin("I/O registers", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                if (_ui.Host.Status == HostStatus.Shutdown)
                {
                    ImGui.TextDisabled("Start a game first.");
                }
                else if (ImGui.BeginTabBar("##io"))
                {
                    if (ImGui.BeginTabItem("ARM9")) { Table(DebugCpu.Arm9, Arm9); ImGui.EndTabItem(); }
                    if (ImGui.BeginTabItem("ARM7")) { Table(DebugCpu.Arm7, Arm7); ImGui.EndTabItem(); }
                    ImGui.EndTabBar();
                }
            }
            ImGui.End();
        }

        private void Table(DebugCpu cpu, Register[] registers)
        {
            var values = _ui.Host.Invoke(() =>
            {
                var nds = _ui.Host.Emulator!;
                return registers.Select(r => r.Size switch
                {
                    1 => DebugMemory.Read8(nds, cpu, r.Address),
                    2 => DebugMemory.Read16(nds, cpu, r.Address),
                    _ => DebugMemory.Read32(nds, cpu, r.Address),
                }).ToArray();
            });
            if (!ImGui.BeginTable("##regs", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY))
                return;
            ImGui.TableSetupColumn("Register");
            ImGui.TableSetupColumn("Address");
            ImGui.TableSetupColumn("Value");
            ImGui.TableSetupColumn("Meaning", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            for (int i = 0; i < registers.Length; i++)
            {
                var r = registers[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Name);
                ImGui.TableNextColumn(); ImGui.TextDisabled($"{r.Address:X8}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Size switch { 1 => $"{values[i]:X2}", 2 => $"{values[i]:X4}", _ => $"{values[i]:X8}" });
                ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Decode?.Invoke(values[i]) ?? "");
            }
            ImGui.EndTable();
        }
    }
}
