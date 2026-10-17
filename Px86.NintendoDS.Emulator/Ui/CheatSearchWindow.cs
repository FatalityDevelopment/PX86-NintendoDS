using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore.Cheats;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Tools → Cheat search: find the main-RAM address of a value (lives,
    /// money…). Start a search, play, then narrow it down with a comparison;
    /// repeat until a few addresses are left and turn one into a cheat.
    /// </summary>
    public sealed class CheatSearchWindow
    {
        private static readonly string[] Sizes = { "1 byte", "2 bytes", "4 bytes" };
        private static readonly (string Label, SearchCompare Compare, bool NeedsValue)[] Filters =
        {
            ("= value", SearchCompare.EqualTo, true), ("!= value", SearchCompare.NotEqualTo, true),
            ("> value", SearchCompare.GreaterThan, true), ("< value", SearchCompare.LessThan, true),
            ("changed", SearchCompare.Changed, false), ("unchanged", SearchCompare.Unchanged, false),
            ("increased", SearchCompare.Increased, false), ("decreased", SearchCompare.Decreased, false),
            ("increased by", SearchCompare.IncreasedBy, true), ("decreased by", SearchCompare.DecreasedBy, true),
        };

        private readonly UiContext _ui;
        private readonly CheatsWindow _cheats;
        private readonly CheatSearch _search = new();
        private bool _open;
        private int _size = 1;
        private bool _signed;
        private string _value = "";
        private bool _hex;
        private string _newValue = "";
        private List<(uint Address, long Previous, long Current)> _results = new();

        public CheatSearchWindow(UiContext ui, CheatsWindow cheats)
        {
            _ui = ui;
            _cheats = cheats;
        }

        public void Open() => _open = true;

        public void Draw()
        {
            if (!_open)
                return;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(460, viewport.Size.X - 20), Math.Min(480, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            if (ImGui.Begin("Cheat search", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                if (_ui.Host.Status == EmulatorCore.Hosting.HostStatus.Shutdown)
                    ImGui.TextDisabled("Start a game first.");
                else
                    Body();
            }
            ImGui.End();
        }

        private void Body()
        {
            if (!_search.IsActive)
            {
                ImGui.Combo("Value size", ref _size, Sizes, Sizes.Length);
                ImGui.Checkbox("Signed", ref _signed);
                if (ImGui.Button("Start search"))
                {
                    _ui.Host.Invoke(() => _search.Start(_ui.Host.Emulator!.MainRam, 1 << _size, _signed));
                    Refresh();
                }
                ImGui.TextDisabled("Takes a snapshot of main RAM (4 MB); every address is a candidate at first.");
                return;
            }

            ImGui.TextUnformatted($"{_search.Count:N0} candidates after {_search.Scans} scan(s)  ({_search.Size}-byte {(_search.Signed ? "signed" : "unsigned")})");
            ImGui.SetNextItemWidth(160);
            ImGui.InputText("Value", ref _value, 32);
            ImGui.SameLine();
            ImGui.Checkbox("hex", ref _hex);

            for (int i = 0; i < Filters.Length; i++)
            {
                var (label, compare, needsValue) = Filters[i];
                if (i % 4 != 0) ImGui.SameLine();
                if (ImGui.Button(label))
                {
                    if (needsValue && !TryParse(_value, out _))
                    {
                        _ui.Notifications.Add("Type a value first");
                    }
                    else
                    {
                        TryParse(_value, out long v);
                        _ui.Host.Invoke(() => _search.Filter(_ui.Host.Emulator!.MainRam, compare, v));
                        Refresh();
                    }
                }
            }
            if (ImGui.Button("Refresh values"))
                Refresh();
            ImGui.SameLine();
            if (ImGui.Button("New search"))
            {
                _search.Reset();
                _results.Clear();
            }

            ImGui.Separator();
            ImGui.SetNextItemWidth(160);
            ImGui.InputText("New value for a cheat", ref _newValue, 32);
            if (_search.Count > _results.Count)
                ImGui.TextDisabled($"Showing the first {_results.Count}; narrow it down further.");
            if (ImGui.BeginTable("##results", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV, new Vector2(0, -1)))
            {
                ImGui.TableSetupColumn("Address");
                ImGui.TableSetupColumn("Previous");
                ImGui.TableSetupColumn("Now");
                ImGui.TableSetupColumn("");
                ImGui.TableHeadersRow();
                foreach (var (address, previous, current) in _results)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{address:X8}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(Format(previous));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(Format(current));
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Make cheat##{address:X8}"))
                    {
                        long value = TryParse(_newValue, out long v) ? v : current;
                        _cheats.Add(_search.MakeCheat(address, value, $"Hold {address:X8} = {Format(value)}"));
                    }
                }
                ImGui.EndTable();
            }
        }

        private void Refresh() =>
            _results = _ui.Host.Invoke(() => _search.Results(_ui.Host.Emulator!.MainRam, 300));

        private string Format(long v) => _hex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture);

        private bool TryParse(string text, out long value)
        {
            text = text.Trim();
            if (_hex || text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
                                     NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
