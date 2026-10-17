using System.Diagnostics;
using System.Numerics;
using System.Text;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Hosting;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Tools → Strings (Ctrl+T): the ASCII strings in a CPU's memory
    /// (<see cref="StringScanner"/>). Clicking one shows it in the Memory window.
    /// The list is a snapshot of when Scan was pressed.
    /// </summary>
    public sealed unsafe class StringsWindow
    {
        private static readonly string[] Cpus = { "ARM9", "ARM7" };

        private readonly UiContext _ui;
        private readonly MemoryWindow _memory;
        private bool _open;
        private int _cpu, _region, _minLength = StringScanner.DefaultMinLength;
        private string _filter = "";
        private bool _filterChanged;
        private NdsEmulator? _source;

        private List<(FoundString String, string Region)> _results = new();
        private readonly List<int> _visible = new();
        private DebugCpu _resultCpu;
        private int _selected = -1;
        private Task<(List<(FoundString, string)> Results, double Ms)>? _scan;
        private string _status = "Press Scan to list the strings in memory.";

        public StringsWindow(UiContext ui, MemoryWindow memory)
        {
            _ui = ui;
            _memory = memory;
        }

        public void Open() => _open = true;

        public void Draw()
        {
            if (_ui.Host.Emulator != _source)
            {
                _source = _ui.Host.Emulator;
                _results = new();
                _visible.Clear();
                _selected = -1;
                _status = "Press Scan to list the strings in memory.";
            }
            if (_scan is { IsCompleted: true } done)
            {
                _scan = null;
                if (done.IsCompletedSuccessfully)
                {
                    _results = done.Result.Results;
                    _status = $"{_results.Count} strings ({done.Result.Ms:F0} ms). Click one to show it in the Memory window.";
                }
                else
                    _status = $"Scan failed: {done.Exception?.InnerException?.Message}";
                _selected = -1;
                _filterChanged = true;
            }
            if (!_open)
                return;

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(620, viewport.Size.X - 20), Math.Min(460, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            if (ImGui.Begin("Strings", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                if (_ui.Host.Status == HostStatus.Shutdown || _ui.Host.Emulator is not { } nds)
                    ImGui.TextDisabled("Start a game first.");
                else
                    Body(nds);
            }
            ImGui.End();
        }

        private void Body(NdsEmulator nds)
        {
            var regions = StringScanner.Regions(nds, (DebugCpu)_cpu);
            _region = Math.Min(_region, regions.Count);

            ImGui.SetNextItemWidth(70);
            ImGui.Combo("##cpu", ref _cpu, Cpus, Cpus.Length);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(130);
            if (ImGui.BeginCombo("##region", _region == 0 ? "All RAM" : regions[_region - 1].Name))
            {
                if (ImGui.Selectable("All RAM", _region == 0))
                    _region = 0;
                for (int i = 0; i < regions.Count; i++)
                    if (ImGui.Selectable($"{regions[i].Name}  {regions[i].Start:X8}", _region == i + 1))
                        _region = i + 1;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("Min length", ref _minLength))
                _minLength = Math.Clamp(_minLength, 2, 32);
            ImGui.SameLine();
            ImGui.BeginDisabled(_scan != null);
            if (ImGui.Button(_scan != null ? "Scanning..." : "Scan"))
                StartScan(regions);
            ImGui.EndDisabled();

            ImGui.SetNextItemWidth(200);
            _filterChanged |= ImGui.InputTextWithHint("##filter", "filter", ref _filter, 128);
            ImGui.SameLine();
            if (ImGui.Button("Copy"))
            {
                var text = new StringBuilder();
                foreach (int i in _visible)
                    text.Append($"{_results[i].String.Address:X8}  ").AppendLine(_results[i].String.Text);
                ImGui.SetClipboardText(text.ToString());
            }

            if (_filterChanged)
            {
                _filterChanged = false;
                _visible.Clear();
                for (int i = 0; i < _results.Count; i++)
                    if (_filter.Length == 0 || _results[i].String.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                        _visible.Add(i);
            }

            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(_filter.Length > 0 && _results.Count > 0 ? $"{_visible.Count} of {_status}" : _status);
            ImGui.PopStyleColor();
            ImGui.Separator();

            if (ImGui.BeginTable("##strings", 2, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("00000000").X + 8);
                ImGui.TableSetupColumn("Text", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(_visible.Count);
                while (clipper.Step())
                {
                    for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                    {
                        int index = _visible[row];
                        var (found, region) = _results[index];
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        if (ImGui.Selectable($"{found.Address:X8}##s{index}", _selected == index, ImGuiSelectableFlags.SpanAllColumns))
                        {
                            _selected = index;
                            _memory.Show(_resultCpu, found.Address, found.Length);
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"{region}, {found.Address:X8}-{found.Address + (uint)found.Length - 1:X8}, {found.Length} bytes");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(found.Text);
                    }
                }
                clipper.End();
                clipper.Destroy();
                ImGui.EndTable();
            }
        }

        private void StartScan(IReadOnlyList<ScanRegion> regions)
        {
            var cpu = (DebugCpu)_cpu;
            var chosen = _region == 0 ? regions.ToArray() : new[] { regions[_region - 1] };
            int minLength = _minLength;
            // Copy under the host lock (the game pauses only for the copy), scan on the thread pool.
            var snapshots = _ui.Host.Invoke(() => _ui.Host.Emulator is { } nds
                ? chosen.Select(r => (Region: r, Data: StringScanner.Snapshot(nds, cpu, r))).ToArray()
                : Array.Empty<(ScanRegion Region, byte[] Data)>());
            _resultCpu = cpu;
            _status = "Scanning...";
            _scan = Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var results = new List<(FoundString, string)>();
                foreach (var (region, data) in snapshots)
                    foreach (var found in StringScanner.Scan(data, region.Start, minLength))
                        results.Add((found, region.Name));
                return (results, watch.Elapsed.TotalMilliseconds);
            });
        }
    }
}
